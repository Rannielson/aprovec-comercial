using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class CommissionTests(ApiFixture api)
{
    public sealed record RuleTotalDto(string RuleType, int? Level, Guid? GroupId, string? GroupName, decimal Rate, decimal Base, decimal Amount);
    public sealed record BeneficiaryDto(Guid UserId, string? Name, decimal Total, List<RuleTotalDto> ByRule);
    public sealed record CommissionsDto(string Competencia, string Status, string Source, decimal Total, List<BeneficiaryDto> Beneficiaries);
    public sealed record EntryDto(Guid BoletoId, Guid OriginUserId, string? OriginName, string RuleType, int? Level, decimal Rate,
        decimal Base, decimal Amount, string? AssociadoNome, string? Placa, DateOnly? PagoEm);
    public sealed record DownlineDto(Guid UserId, string Name, int Level, decimal Rate,
        decimal Base, decimal Amount, int Pagamentos);
    public sealed record LostDto(bool Available, decimal Total, decimal EmAtraso, decimal Cancelados, int Boletos);
    public sealed record CreatedPlanDto(Guid Id);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    // Seeds a frozen (status = 'confirmado') fechamento for `competencia`, with one
    // fechamento_detalhes "own" row per (beneficiary, boleto owner) pair given. Inserts happen
    // while the fechamento is still 'apuracao' (fechamento_detalhes_guard forbids inserting into
    // an already-frozen fechamento), then the status is flipped to 'confirmado' afterwards.
    private async Task<Guid> SeedFrozenFechamentoAsync(SeededTenant s, DateOnly competencia, params (Guid Beneficiary, Guid Owner)[] rows)
    {
        var fechamentoId = Guid.NewGuid();
        await api.SqlAsync(
            "insert into fechamentos (id, tenant_id, competencia) values (@fechamentoId, @tenant, @competencia)",
            new { fechamentoId, tenant = s.TenantId, competencia });

        foreach (var (beneficiary, owner) in rows)
        {
            await api.SqlAsync(
                """
                insert into fechamento_detalhes
                  (tenant_id, fechamento_id, beneficiario_id, origem_participante_id, boleto_id, rule_id, rule_type, rate, base, valor)
                select @tenant, @fechamentoId, @beneficiary, @owner, b.id, @ruleId, 'own', 0.07, 1000, 70
                  from boletos b
                 where b.tenant_id = @tenant and b.participante_id = @owner
                 limit 1
                """,
                new { tenant = s.TenantId, fechamentoId, beneficiary, owner, ruleId = Guid.NewGuid() });
        }

        await api.SqlAsync(
            "update fechamentos set status = 'confirmado', confirmado_em = now(), confirmado_por = @admin where id = @fechamentoId",
            new { fechamentoId, admin = s.Admin });
        return fechamentoId;
    }

    [Fact]
    public async Task Admin_can_review_reimported_data_without_changing_confirmed_payouts()
    {
        var s=await api.SeedAsync();
        var closing=await SeedFrozenFechamentoAsync(s,new DateOnly(2026,9,1),(s.Joao,s.Joao));
        var admin=await LoginAsync(s,"admin");
        var review=await admin.GetJsonAsync<CommissionsDto>("/commissions/2026-09?recalculate=true");
        Assert.Equal("live_review",review.Source);
        Assert.Equal(1600m,review.Beneficiaries.Single(b=>b.UserId==s.Joao).Total);
        var saved=await admin.GetJsonAsync<CommissionsDto>("/commissions/2026-09");
        Assert.Equal("snapshot",saved.Source);
        Assert.Equal(70m,saved.Beneficiaries.Single(b=>b.UserId==s.Joao).Total);
        Assert.Equal("confirmado",await api.SqlScalarAsync<string>("select status from fechamentos where id=@closing",new {closing}));
        var consultant=await LoginAsync(s,"joao");
        var forbidden=await consultant.GetAsync("/commissions/2026-09?recalculate=true");
        Assert.Equal(HttpStatusCode.Forbidden,forbidden.StatusCode);
        var current = await consultant.GetJsonAsync<CommissionsDto>("/commissions/2026-09/current");
        Assert.Equal("current", current.Source);
        Assert.Equal("apuracao", current.Status);
        Assert.Single(current.Beneficiaries);
        Assert.Equal(s.Joao, current.Beneficiaries.Single().UserId);
        Assert.Equal(1600m, current.Beneficiaries.Single().Total);
        var savedAgain = await admin.GetJsonAsync<CommissionsDto>("/commissions/2026-09");
        Assert.Equal(70m, savedAgain.Beneficiaries.Single(b=>b.UserId==s.Joao).Total);
        Assert.Equal("confirmado", savedAgain.Status);
    }

    [Fact]
    public async Task Consultor_sees_own_and_first_level_commission()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CommissionsDto>("/commissions/2026-09");

        Assert.Equal(("2026-09", "apuracao", "live", 1600.00m), (result.Competencia, result.Status, result.Source, result.Total));
        var joao = Assert.Single(result.Beneficiaries);
        Assert.Equal(("João Silva", 1600.00m), (joao.Name, joao.Total));
        Assert.Equal(
            new[]
            {
                new RuleTotalDto("own", null, null, null, 0.07m, 20000m, 1400.00m),
                new RuleTotalDto("upline", 1, null, null, 0.02m, 10000m, 200.00m),
            },
            joao.ByRule);
    }

    [Fact]
    public async Task Contract_cutoff_is_selected_per_consultant_and_uses_vehicle_contract_date()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var consultant = await LoginAsync(s, "joao");
        var cutoff = new { cutoffDate = "2026-09-30" };
        await ApiClient.ExpectAsync(
            await admin.PutAsync($"/commission-contract-cutoffs/{s.Joao}", cutoff), HttpStatusCode.OK);

        var missingContract = await consultant.GetAsync("/commissions/2026-09");
        Assert.Equal(HttpStatusCode.Conflict, missingContract.StatusCode);
        Assert.Equal("commission.contract_date_missing", await ApiClient.CodeAsync(missingContract));

        await api.SqlAsync(
            """
            insert into boleto_veiculos (tenant_id, boleto_id, ordem, data_inicio_contrato)
            select tenant_id, id, 0, date '2026-09-30'
              from boletos where tenant_id = @tenant and participante_id = @maria
            """, new { tenant = s.TenantId, maria = s.Maria });

        var beforeCutoff = await consultant.GetJsonAsync<CommissionsDto>("/commissions/2026-09");
        Assert.Equal(1400m, Assert.Single(beforeCutoff.Beneficiaries).Total);
        Assert.DoesNotContain(Assert.Single(beforeCutoff.Beneficiaries).ByRule,
            r => r.RuleType == "upline");

        await api.SqlAsync(
            """
            update boleto_veiculos set data_inicio_contrato = date '2026-10-01'
             where tenant_id = @tenant and boleto_id in
               (select id from boletos where tenant_id = @tenant and participante_id = @maria)
            """, new { tenant = s.TenantId, maria = s.Maria });
        var current = await consultant.GetJsonAsync<CommissionsDto>("/commissions/2026-09");
        Assert.Equal(1600m, Assert.Single(current.Beneficiaries).Total);
        Assert.Contains(Assert.Single(current.Beneficiaries).ByRule,
            r => r.RuleType == "upline" && r.Rate == .02m && r.Amount == 200m);

        var otherTenant = await api.SeedAsync();
        var otherConsultant = await LoginAsync(otherTenant, "joao");
        var unaffected = await otherConsultant.GetJsonAsync<CommissionsDto>("/commissions/2026-09");
        Assert.Equal(1600m, Assert.Single(unaffected.Beneficiaries).Total);
        var forbidden = await consultant.PutAsync($"/commission-contract-cutoffs/{s.Joao}",
            new { cutoffDate = "2026-08-31" });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        var crossTenant = await admin.GetAsync($"/commission-contract-cutoffs/{otherTenant.Joao}");
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);
    }

    [Fact]
    public async Task Confirmed_closing_keeps_only_post_cutoff_upline_of_a_mixed_date_boleto()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        await ApiClient.ExpectAsync(await admin.PutAsync($"/commission-contract-cutoffs/{s.Joao}",
            new { cutoffDate = "2026-09-30" }), HttpStatusCode.OK);
        await api.SqlAsync(
            """
            insert into boleto_veiculos (tenant_id, boleto_id, ordem, data_inicio_contrato)
            select tenant_id, id, 0, date '2026-10-01'
              from boletos where tenant_id = @tenant and participante_id = @maria
            """, new { tenant = s.TenantId, maria = s.Maria });
        var mixedBoleto = Guid.NewGuid();
        await api.SqlAsync(
            """
            insert into boletos (id, tenant_id, participante_id, associado_ref, associado_nome, valor, status, vencimento, pago_em)
            values (@mixedBoleto, @tenant, @maria, 'mixed-date', 'Associado misto', 100, 'recebido',
                    date '2026-09-10', date '2026-09-12');
            insert into boleto_veiculos (tenant_id, boleto_id, ordem, data_inicio_contrato)
            values (@tenant, @mixedBoleto, 0, date '2026-09-30'),
                   (@tenant, @mixedBoleto, 1, date '2026-10-01');
            """, new { tenant = s.TenantId, maria = s.Maria, mixedBoleto });

        await ApiClient.ExpectAsync(await admin.PostAsync("/fechamentos/2026-09/confirm"), HttpStatusCode.OK);
        var segments = await api.SqlScalarAsync<int>(
            """
            select count(*)::int from fechamento_detalhes
             where tenant_id = @tenant and boleto_id = @mixedBoleto and beneficiario_id = @joao
               and contract_segment in (0, 1)
            """, new { tenant = s.TenantId, mixedBoleto, joao = s.Joao });
        Assert.Equal(1, segments);
        var snapshot = await (await LoginAsync(s, "joao")).GetJsonAsync<CommissionsDto>("/commissions/2026-09");
        Assert.Equal("snapshot", snapshot.Source);
        Assert.Equal(1601m, Assert.Single(snapshot.Beneficiaries).Total);
    }

    [Fact]
    public async Task Coordenador_sees_the_whole_pdf_example()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "coordenacao")).GetJsonAsync<CommissionsDto>("/commissions/2026-09");

        Assert.Equal(3100.00m, result.Total);
        var totals = result.Beneficiaries.ToDictionary(b => b.UserId, b => b.Total);
        Assert.Equal(1600.00m, totals[s.Joao]);
        Assert.Equal(800.00m, totals[s.Maria]);
        Assert.Equal(350.00m, totals[s.Pedro]);
        Assert.Equal(350.00m, totals[s.Coord]);
        Assert.Equal("Coordenação", result.Beneficiaries.Single(b => b.UserId == s.Coord).ByRule.Single().GroupName);
    }

    [Fact]
    public async Task August_matches_the_history()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CommissionsDto>("/commissions/2026-08");
        Assert.Equal(1440.00m, result.Total);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("setembro")]
    public async Task Invalid_competencia_is_rejected(string competencia)
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "joao")).GetAsync($"/commissions/{competencia}");
        Assert.Equal("commission.invalid_competencia", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Consultor_cannot_read_someone_else_entries()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "joao")).GetAsync($"/commissions/2026-09/entries?beneficiaryId={s.Maria}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Lost_dashboard_requires_due_import_and_sums_own_and_team_unpaid_commissions()
    {
        var s = await api.SeedAsync();
        var joao = await LoginAsync(s, "joao");
        var before = await joao.GetJsonAsync<LostDto>("/commissions/2026-09/lost");
        Assert.False(before.Available);

        await api.SqlAsync(
            "delete from boletos where tenant_id = @tenant and status in ('atraso', 'cancelado', 'a_vencer')",
            new { tenant = s.TenantId });
        await api.SqlAsync(
            """
            insert into boleto_vencimento_importacoes (tenant_id, competencia, total_retornado, total_vinculado)
            values (@tenant, date '2026-09-01', 2, 2)
            """, new { tenant = s.TenantId });
        await api.SqlAsync(
            """
            insert into boletos (tenant_id, participante_id, associado_ref, associado_nome, valor, status, vencimento)
            values (@tenant, @joao, 'perda-joao', 'Associado em atraso', 100, 'atraso', date '2026-09-05'),
                   (@tenant, @maria, 'perda-maria', 'Associado cancelado', 200, 'cancelado', date '2026-09-10')
            """, new { tenant = s.TenantId, joao = s.Joao, maria = s.Maria });

        var lost = await joao.GetJsonAsync<LostDto>("/commissions/2026-09/lost");
        Assert.Equal((true, 11m, 7m, 4m, 2),
            (lost.Available, lost.Total, lost.EmAtraso, lost.Cancelados, lost.Boletos));
        var forbidden = await joao.GetAsync($"/commissions/2026-09/lost?beneficiaryId={s.Maria}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Downline_includes_every_payable_level_with_zero_when_no_wallet_paid()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var created = await admin.PostAsync("/commission-plans", new
        {
            name = "Árvore com dois níveis de supervisão",
            effectiveFrom = "2026-10-01",
            rules = new object[]
            {
                new { type = "own", rate = 0.07m },
                new { type = "upline", rate = 0.02m, level = 1 },
                new { type = "upline", rate = 0.01m, level = 2 },
            },
        });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var plan = (await created.Content.ReadFromJsonAsync<CreatedPlanDto>(ApiClient.Json))!;
        await ApiClient.ExpectAsync(await admin.PostAsync($"/commission-plans/{plan.Id}/activate"), HttpStatusCode.OK);

        var consultant = await LoginAsync(s, "joao");
        var paid = await consultant.GetJsonAsync<List<DownlineDto>>(
            $"/commissions/2026-09/downline?beneficiaryId={s.Joao}");
        var mariaPaid = Assert.Single(paid);
        Assert.Equal((s.Maria, 1, 0.02m, 10000m, 200m, 50),
            (mariaPaid.UserId, mariaPaid.Level, mariaPaid.Rate,
                mariaPaid.Base, mariaPaid.Amount, mariaPaid.Pagamentos));

        var downline = await consultant.GetJsonAsync<List<DownlineDto>>(
            $"/commissions/2026-10/downline?beneficiaryId={s.Joao}");
        Assert.Equal(2, downline.Count);
        Assert.Equal((s.Maria, "Maria Oliveira", 1, 0.02m, 0m, 0m, 0),
            (downline[0].UserId, downline[0].Name, downline[0].Level,
                downline[0].Rate, downline[0].Base, downline[0].Amount, downline[0].Pagamentos));
        Assert.Equal((s.Pedro, "Pedro Santos", 2, 0.01m, 0m, 0m, 0),
            (downline[1].UserId, downline[1].Name, downline[1].Level,
                downline[1].Rate, downline[1].Base, downline[1].Amount, downline[1].Pagamentos));

        var forbidden = await consultant.GetAsync($"/commissions/2026-10/downline?beneficiaryId={s.Maria}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task Entries_hide_associate_data_outside_the_carteira_scope()
    {
        var s = await api.SeedAsync();
        var entries = await (await LoginAsync(s, "joao"))
            .GetJsonAsync<List<EntryDto>>($"/commissions/2026-09/entries?beneficiaryId={s.Joao}");

        Assert.Equal(140, entries.Count);
        Assert.All(entries.Where(e => e.RuleType == "own"), e => Assert.NotNull(e.AssociadoNome));
        var upline = entries.Where(e => e.RuleType == "upline").ToList();
        Assert.Equal(50, upline.Count);
        Assert.All(upline, e =>
        {
            Assert.Equal("Maria Oliveira", e.OriginName);
            Assert.Null(e.AssociadoNome);
            Assert.Null(e.Placa);
        });
    }

    [Fact]
    public async Task Frozen_competencia_summary_isolates_beneficiaries_for_consultor()
    {
        var s = await api.SeedAsync();
        await SeedFrozenFechamentoAsync(s, new DateOnly(2026, 9, 1), (s.Joao, s.Joao), (s.Maria, s.Maria));

        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CommissionsDto>("/commissions/2026-09");

        Assert.Equal(("confirmado", "snapshot", 70.00m), (result.Status, result.Source, result.Total));
        var joao = Assert.Single(result.Beneficiaries);
        Assert.Equal(s.Joao, joao.UserId);
    }

    [Fact]
    public async Task Frozen_competencia_entries_filter_to_the_requested_beneficiary_for_coordenador()
    {
        var s = await api.SeedAsync();
        await SeedFrozenFechamentoAsync(s, new DateOnly(2026, 9, 1), (s.Joao, s.Joao), (s.Maria, s.Maria));

        var entries = await (await LoginAsync(s, "coordenacao"))
            .GetJsonAsync<List<EntryDto>>($"/commissions/2026-09/entries?beneficiaryId={s.Joao}");

        var entry = Assert.Single(entries);
        Assert.Equal(s.Joao, entry.OriginUserId);
    }

    [Fact]
    public async Task Frozen_competencia_still_rejects_a_beneficiary_outside_the_callers_scope()
    {
        var s = await api.SeedAsync();
        await SeedFrozenFechamentoAsync(s, new DateOnly(2026, 9, 1), (s.Joao, s.Joao), (s.Maria, s.Maria));

        var response = await (await LoginAsync(s, "joao")).GetAsync($"/commissions/2026-09/entries?beneficiaryId={s.Maria}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
