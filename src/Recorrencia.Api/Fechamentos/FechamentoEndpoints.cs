using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Commissions;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Fechamentos;

public static class FechamentoEndpoints
{
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
        app.MapGet("/fechamentos", ListAsync).RequirePermission("fechamento.visualizar");
        app.MapPost("/fechamentos/{competencia}/confirm", ConfirmAsync)
            .RequirePermission("fechamento.confirmar")
            .RequirePermission("fechamento.visualizar");
        app.MapPost("/fechamentos/{competencia}/provision", ProvisionAsync)
            .RequirePermission("fechamento.provisionar")
            .RequirePermission("fechamento.visualizar");
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
        CurrentPermissions permissions, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        if ((await permissions.GetAsync(ct)).ScopeOf("comissoes.visualizar") != "tenant")
            throw new ApiProblem(StatusCodes.Status403Forbidden, "fechamento.requires_full_visibility");

        var result = await db.InTenantAsync(tenant, user, async tx =>
        {
            await tx.ExecuteAsync(
                "insert into fechamentos (tenant_id, competencia) values (@tenant, @month) on conflict (tenant_id, competencia) do nothing",
                new { tenant, month });
            var fechamento = await tx.QuerySingleAsync<FechamentoRow>(SelectFechamento, new { tenant, month });
            if (fechamento.Status is "confirmado" or "provisionado")
                throw new ApiProblem(StatusCodes.Status409Conflict, "fechamento.already_confirmed");

            var beneficiaries = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            var entries = await CommissionService.CalculateLiveAsync(tx, month, beneficiaries);

            await tx.ExecuteAsync(
                """
                insert into fechamento_detalhes
                  (tenant_id, fechamento_id, beneficiario_id, origem_participante_id, boleto_id, rule_id, rule_type, level, group_id, rate, base, valor)
                select @tenant, @fechamentoId, d.beneficiario, d.origem, d.boleto, d.rule_id, d.rule_type, d.level, d.group_id, d.rate, d.base, d.valor
                  from unnest(@beneficiarios, @origens, @boletos, @ruleIds, @ruleTypes, @levels, @groups, @rates, @bases, @valores)
                    as d(beneficiario, origem, boleto, rule_id, rule_type, level, group_id, rate, base, valor)
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
                });

            await tx.ExecuteAsync(
                "update fechamentos set status = 'confirmado', confirmado_em = now(), confirmado_por = @user where id = @id",
                new { user, id = fechamento.Id });

            var total = entries.Sum(e => e.Amount);
            await WriteAsync(tx, tenant, user, "fechamentos.confirm", "fechamentos", fechamento.Id, null,
                new { competencia = month, entries = entries.Count, total });
            return new ConfirmResponse(month.ToString("yyyy-MM"), "confirmado", entries.Count, total);
        }, ct);
        return Results.Ok(result);
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
