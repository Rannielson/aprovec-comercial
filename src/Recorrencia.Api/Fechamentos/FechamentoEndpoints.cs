using Npgsql;
using System.Text.Json;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Commissions;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Integracoes;
using Recorrencia.Api.PlanosCarreira;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Fechamentos;

public static class FechamentoEndpoints
{
    public sealed record ReopenRequest(string? Motivo);
    public sealed record ConfirmResponse(string Competencia, string Status, int Entries, decimal Total);
    public sealed record FechamentoResponse(string Competencia, string Status, DateTime? ConfirmadoEm, DateTime? ProvisionadoEm);

    public sealed class FechamentoRow
    {
        public Guid Id { get; set; }
        public DateOnly Competencia { get; set; }
        public string Status { get; set; } = "";
        public DateTime? ConfirmadoEm { get; set; }
        public DateTime? ProvisionadoEm { get; set; }

        public FechamentoResponse ToResponse() => new(Competencia.ToString("yyyy-MM"), Status, ConfirmadoEm, ProvisionadoEm);
    }

    public static void MapFechamentoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/fechamentos/{competencia}/reopen", ReopenAsync).RequirePermission("fechamento.confirmar").RequirePermission("fechamento.visualizar");
        app.MapGet("/fechamentos", ListAsync).RequirePermission("fechamento.visualizar");
        app.MapPost("/fechamentos/{competencia}/confirm", ConfirmAsync)
            .RequirePermission("fechamento.confirmar")
            .RequirePermission("fechamento.visualizar");
        app.MapPut("/fechamentos/{competencia}/bonus-extra/{beneficiarioId:guid}", AprovarBonusExtraAsync)
            .RequirePermission("fechamento.confirmar");
        app.MapPost("/fechamentos/{competencia}/provision", ProvisionAsync)
            .RequirePermission("fechamento.provisionar")
            .RequirePermission("fechamento.visualizar");
    }

    private static async Task<IResult> ReopenAsync(string competencia, ReopenRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var month=CommissionService.ParseCompetencia(competencia);
        if ((body.Motivo??"").Trim().Length<10) throw new ApiProblem(400,"fechamento.reason_required");
        var revision=await db.InTenantAsync(request.RequireTenant(),request.RequireUser(),async tx=>{
            if (await tx.ExecuteScalarAsync<string?>("select app.scope_for('comissoes.visualizar')")!="tenant")
                throw new ApiProblem(403,"fechamento.requires_full_visibility");
            var row=await tx.QuerySingleOrDefaultAsync<FechamentoRow>(SelectFechamento,new{tenant=request.RequireTenant(),month})
                ?? throw new ApiProblem(404,"fechamento.not_found");
            if(row.Status!="confirmado")throw new ApiProblem(409,"fechamento.invalid_transition");
            var id=await tx.ExecuteScalarAsync<Guid>("select app.reopen_fechamento(@month,@motivo)",new{month,motivo=body.Motivo!.Trim()});
            await WriteAsync(tx,request.RequireTenant(),request.RequireUser(),"fechamento.reopen","fechamentos",row.Id,
                new{status="confirmado"},new{status="apuracao",revisionId=id,motivo=body.Motivo.Trim()});
            return id;
        },ct);
        return Results.Ok(new{revisionId=revision,status="apuracao"});
    }

    private const string SelectFechamento = """
        select id, competencia, status, confirmado_em, provisionado_em
          from fechamentos
         where tenant_id = @tenant and competencia = @month
         for update
        """;

    private static async Task<IResult> ListAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var rows = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), tx => tx.QueryAsync<FechamentoRow>(
            "select id, competencia, status, confirmado_em, provisionado_em from fechamentos order by competencia desc"), ct);
        return Results.Ok(rows.Select(r => r.ToResponse()).ToList());
    }

    private static async Task<IResult> ConfirmAsync(string competencia, RequestContext request, Database db,
        IHinovaClient hinova, AesGcmCipher cipher, TimeProvider time, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var tenant = request.RequireTenant();
        var user = request.RequireUser();

        var currentMonth = time.GetUtcNow().UtcDateTime;
        var currentMonthStart = new DateOnly(currentMonth.Year, currentMonth.Month, 1);
        if (month >= currentMonthStart)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "fechamento.competencia_not_closed");

        var scope = await db.InTenantAsync(tenant, user,
            tx => tx.ExecuteScalarAsync<string?>("select app.scope_for('comissoes.visualizar')"), ct);
        if (scope != "tenant")
            throw new ApiProblem(StatusCodes.Status403Forbidden, "fechamento.requires_full_visibility");

        var bonusPlacas = await BonusPayoutService.CalculateAsync(month, tenant, user, db, hinova, cipher, time, ct);

        var result = await db.InTenantAsync(tenant, user, async tx =>
        {
            // Re-check full-tenant visibility inside the transaction (not just via the caller's
            // permissions before it started): if the caller's role was narrowed in the window
            // between the permission filter and this point, this is the check that actually
            // prevents a partial-tenant snapshot from being frozen without any record of the gap.
            var scope = await tx.ExecuteScalarAsync<string?>("select app.scope_for('comissoes.visualizar')");
            if (scope != "tenant")
                throw new ApiProblem(StatusCodes.Status403Forbidden, "fechamento.requires_full_visibility");

            await tx.ExecuteAsync(
                "insert into fechamentos (tenant_id, competencia) values (@tenant, @month) on conflict (tenant_id, competencia) do nothing",
                new { tenant, month });
            var fechamento = await tx.QuerySingleAsync<FechamentoRow>(SelectFechamento, new { tenant, month });
            if (fechamento.Status is "confirmado" or "provisionado")
                throw new ApiProblem(StatusCodes.Status409Conflict, "fechamento.already_confirmed");

            var beneficiaries = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            // Hold source rows until the snapshot is committed so a concurrent sync cannot
            // change a fee between the completeness check and the commission calculation.
            await tx.QueryAsync<Guid>("select id from boletos where tenant_id=@tenant and status='recebido' and pago_em>=@month and pago_em<@fim for share",
                new { tenant, month, fim = month.AddMonths(1) });
            if (await tx.ExecuteScalarAsync<int>("select count(*)::int from app.commission_pending_fees(@month,@beneficiaries)", new { month, beneficiaries }) > 0)
                throw new ApiProblem(StatusCodes.Status409Conflict, "fechamento.tarifas_pendentes");
            var entries = await CommissionService.CalculateLiveAsync(tx, month, beneficiaries);

            await tx.ExecuteAsync(
                """
                insert into fechamento_detalhes
                  (tenant_id, fechamento_id, beneficiario_id, origem_participante_id, boleto_id, rule_id, rule_type, level, group_id, rate, base, valor, contract_segment, gross_base, bank_fee)
                select @tenant, @fechamentoId, d.beneficiario, d.origem, d.boleto, d.rule_id, d.rule_type, d.level, d.group_id, d.rate, d.base, d.valor, d.contract_segment, d.gross_base, d.bank_fee
                  from unnest(@beneficiarios, @origens, @boletos, @ruleIds, @ruleTypes, @levels, @groups, @rates, @bases, @valores, @contractSegments, @grossBases, @bankFees)
                    as d(beneficiario, origem, boleto, rule_id, rule_type, level, group_id, rate, base, valor, contract_segment, gross_base, bank_fee)
                """,
                new
                {
                    tenant,
                    fechamentoId = fechamento.Id,
                    beneficiarios = entries.Select(e => e.BeneficiaryId).ToArray(),
                    origens = entries.Select(e => e.OwnerId).ToArray(),
                    boletos = entries.Select(e => e.BoletoId).ToArray(),
                    ruleIds = entries.Select(e => e.RuleId).ToArray(),
                    ruleTypes = entries.Select(e => CommissionService.ToDb(e.RuleType)).ToArray(),
                    levels = entries.Select(e => e.Level).ToArray(),
                    groups = entries.Select(e => e.GroupId).ToArray(),
                    rates = entries.Select(e => e.Rate).ToArray(),
                    bases = entries.Select(e => e.Base).ToArray(),
                    valores = entries.Select(e => e.Amount).ToArray(),
                    grossBases = entries.Select(e => e.GrossBase ?? e.Base).ToArray(),
                    bankFees = entries.Select(e => e.BankFee ?? 0).ToArray(),
                    contractSegments = entries.Select(e => e.ContractSegment).ToArray(),
                });

            foreach (var bonus in bonusPlacas)
            {
                await tx.ExecuteAsync(
                    """
                    insert into fechamento_remuneracao
                      (tenant_id, fechamento_id, beneficiario_id, tipo, plano_carreira_id, plano_nome,
                       competencia_producao, data_limite_pagamento, quantidade, valor_unitario, valor, detalhes)
                    values (@tenant, @fechamentoId, @beneficiarioId, 'placas', @planoId, @planoNome,
                            @producao, @limite, @quantidade, @valorUnitario, @valor, @detalhes::jsonb)
                    """,
                    new { tenant, fechamentoId = fechamento.Id, beneficiarioId = bonus.BeneficiarioId,
                        planoId = bonus.PlanoId, planoNome = bonus.PlanoNome, producao = bonus.CompetenciaProducao,
                        limite = bonus.DataLimitePagamento, quantidade = bonus.Quantidade,
                        valorUnitario = bonus.ValorUnitario, valor = bonus.Valor,
                        detalhes = JsonSerializer.Serialize(bonus.Placas, new JsonSerializerOptions(JsonSerializerDefaults.Web)) });
            }

            var bonusExtra = await tx.ExecuteScalarAsync<decimal>(
                """
                select coalesce(sum(r.valor), 0)
                  from fechamento_remuneracao r
                  join users u on u.id = r.beneficiario_id
                  join planos_carreira p on p.id = u.plano_carreira_id
                 where r.fechamento_id = @id and r.tipo = 'extra'
                   and u.status = 'ativo' and p.status = 'ativo'
                   and r.plano_carreira_id = p.id and r.valor = p.bonus_extra_valor
                """, new { id = fechamento.Id });
            var aprovacoes = await tx.ExecuteScalarAsync<int>(
                "select count(*)::int from fechamento_remuneracao where fechamento_id = @id and tipo = 'extra'",
                new { id = fechamento.Id });
            var aprovacoesValidas = await tx.ExecuteScalarAsync<int>(
                """
                select count(*)::int from fechamento_remuneracao r
                  join users u on u.id = r.beneficiario_id
                  join planos_carreira p on p.id = u.plano_carreira_id
                 where r.fechamento_id = @id and r.tipo = 'extra' and u.status = 'ativo'
                   and p.status = 'ativo' and r.plano_carreira_id = p.id
                   and r.valor = p.bonus_extra_valor
                """, new { id = fechamento.Id });
            if (aprovacoes != aprovacoesValidas)
                throw new ApiProblem(StatusCodes.Status409Conflict, "bonus_extra.aprovacao_desatualizada");

            await tx.ExecuteAsync(
                "update fechamentos set status = 'confirmado', confirmado_em = now(), confirmado_por = @user where id = @id",
                new { user, id = fechamento.Id });

            var total = entries.Sum(e => e.Amount) + bonusPlacas.Sum(b => b.Valor) + bonusExtra;
            await WriteAsync(tx, tenant, user, "fechamentos.confirm", "fechamentos", fechamento.Id, null,
                new { competencia = month, entries = entries.Count + bonusPlacas.Count + aprovacoes, total });
            return new ConfirmResponse(month.ToString("yyyy-MM"), "confirmado", entries.Count + bonusPlacas.Count + aprovacoes, total);
        }, ct);
        return Results.Ok(result);
    }

    public sealed record AprovarBonusExtraRequest(bool Aprovado);

    private static async Task<IResult> AprovarBonusExtraAsync(string competencia, Guid beneficiarioId,
        AprovarBonusExtraRequest body, RequestContext request, Database db, TimeProvider time, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var producao = BonusPayoutService.CompetenciaProducao(month);
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            if (await tx.ExecuteScalarAsync<string?>("select app.scope_for('comissoes.visualizar')") != "tenant")
                throw new ApiProblem(StatusCodes.Status403Forbidden, "fechamento.requires_full_visibility");
            await tx.ExecuteAsync(
                "insert into fechamentos (tenant_id, competencia) values (@tenant, @month) on conflict (tenant_id, competencia) do nothing",
                new { tenant, month });
            var fechamento = await tx.QuerySingleAsync<FechamentoRow>(SelectFechamento, new { tenant, month });
            if (fechamento.Status is "confirmado" or "provisionado")
                throw new ApiProblem(StatusCodes.Status409Conflict, "fechamento.already_confirmed");

            var plano = await tx.QuerySingleOrDefaultAsync<BonusExtraPlanRow>(
                """
                select p.id as plano_id, p.name as plano_nome, p.bonus_extra_valor
                  from users u join planos_carreira p on p.id = u.plano_carreira_id
                 where u.id = @beneficiarioId and u.status = 'ativo' and p.status = 'ativo'
                """, new { beneficiarioId });
            if (plano?.BonusExtraValor is null)
                throw new ApiProblem(StatusCodes.Status409Conflict, "bonus_extra.nao_configurado");

            if (body.Aprovado)
                await tx.ExecuteAsync(
                    """
                    insert into fechamento_remuneracao
                      (tenant_id, fechamento_id, beneficiario_id, tipo, plano_carreira_id, plano_nome,
                       competencia_producao, valor, aprovado_por, aprovado_em)
                    values (@tenant, @fechamentoId, @beneficiarioId, 'extra', @planoId, @planoNome,
                            @producao, @valor, @actor, @agora)
                    on conflict (fechamento_id, beneficiario_id, tipo) do update
                      set plano_carreira_id = excluded.plano_carreira_id, plano_nome = excluded.plano_nome,
                          competencia_producao = excluded.competencia_producao, valor = excluded.valor,
                          aprovado_por = excluded.aprovado_por, aprovado_em = excluded.aprovado_em
                    """, new { tenant, fechamentoId = fechamento.Id, beneficiarioId,
                        planoId = plano.PlanoId, planoNome = plano.PlanoNome, producao,
                        valor = plano.BonusExtraValor, actor, agora = time.GetUtcNow() });
            else
                await tx.ExecuteAsync(
                    "delete from fechamento_remuneracao where fechamento_id = @fechamentoId and beneficiario_id = @beneficiarioId and tipo = 'extra'",
                    new { fechamentoId = fechamento.Id, beneficiarioId });
            await WriteAsync(tx, tenant, actor, body.Aprovado ? "bonus_extra.aprovar" : "bonus_extra.revogar",
                "fechamento_remuneracao", fechamento.Id, null,
                new { competencia = month, beneficiarioId, valor = body.Aprovado ? plano.BonusExtraValor : null });
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private sealed class BonusExtraPlanRow
    {
        public Guid PlanoId { get; set; }
        public string PlanoNome { get; set; } = "";
        public decimal? BonusExtraValor { get; set; }
    }

    private static async Task<IResult> ProvisionAsync(string competencia, RequestContext request, Database db, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var tenant = request.RequireTenant();
        var user = request.RequireUser();

        var result = await db.InTenantAsync(tenant, user, async tx =>
        {
            var fechamento = await tx.QuerySingleOrDefaultAsync<FechamentoRow>(SelectFechamento, new { tenant, month })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "fechamento.not_found");
            if (fechamento.Status != "confirmado")
                throw new ApiProblem(StatusCodes.Status409Conflict, "fechamento.invalid_transition");

            await tx.ExecuteAsync(
                "update fechamentos set status = 'provisionado', provisionado_em = now(), provisionado_por = @user where id = @id",
                new { user, id = fechamento.Id });
            await WriteAsync(tx, tenant, user, "fechamentos.provision", "fechamentos", fechamento.Id, null, new { competencia = month });
            return (await tx.QuerySingleAsync<FechamentoRow>(SelectFechamento, new { tenant, month })).ToResponse();
        }, ct);
        return Results.Ok(result);
    }
}
