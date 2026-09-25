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
