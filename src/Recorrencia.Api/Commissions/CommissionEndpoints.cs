using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;
using Recorrencia.Domain.Commissions;

namespace Recorrencia.Api.Commissions;

public static class CommissionEndpoints
{
    public sealed record RuleTotalResponse(RuleType RuleType, int? Level, Guid? GroupId, string? GroupName, decimal Rate, decimal Base, decimal Amount);
    public sealed record BeneficiaryResponse(Guid UserId, string? Name, decimal Total, IReadOnlyList<RuleTotalResponse> ByRule);
    public sealed record CommissionsResponse(string Competencia, string Status, string Source, decimal Total, IReadOnlyList<BeneficiaryResponse> Beneficiaries);
    public sealed record EntryResponse(Guid BoletoId, Guid OriginUserId, string? OriginName, RuleType RuleType, int? Level,
        decimal Rate, decimal Base, decimal Amount, string? AssociadoNome, string? Placa, DateOnly? PagoEm);

    public sealed class NameRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class VisibleBoletoRow
    {
        public Guid Id { get; set; }
        public string AssociadoNome { get; set; } = "";
        public string? Placa { get; set; }
        public DateOnly? PagoEm { get; set; }
    }

    public static void MapCommissionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/commissions/{competencia}", SummaryAsync).RequirePermission("comissoes.visualizar");
        app.MapGet("/commissions/{competencia}/entries", EntriesAsync).RequirePermission("comissoes.visualizar");
    }

    private static async Task<IResult> SummaryAsync(string competencia, RequestContext request, Database db, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var response = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            var beneficiaries = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            var loaded = await CommissionService.LoadAsync(tx, month, beneficiaries);
            var summary = CommissionSummary.ByBeneficiary(loaded.Entries);
            var names = await NamesAsync(tx, summary.Select(s => s.BeneficiaryId));
            var groups = (await tx.QueryAsync<NameRow>("select id, name from commission_groups")).ToDictionary(g => g.Id, g => g.Name);

            return new CommissionsResponse(
                month.ToString("yyyy-MM"),
                loaded.Status,
                loaded.Source,
                summary.Sum(s => s.Total),
                summary.Select(s => new BeneficiaryResponse(
                    s.BeneficiaryId,
                    names.GetValueOrDefault(s.BeneficiaryId),
                    s.Total,
                    s.ByRule.Select(r => new RuleTotalResponse(
                        r.RuleType, r.Level, r.GroupId,
                        r.GroupId is { } groupId ? groups.GetValueOrDefault(groupId) : null,
                        r.Rate, r.Base, r.Amount)).ToList())).ToList());
        }, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> EntriesAsync(string competencia, Guid beneficiaryId, RequestContext request, Database db, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var response = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            var allowed = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            if (!allowed.Contains(beneficiaryId))
                throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");

            var loaded = await CommissionService.LoadAsync(tx, month, [beneficiaryId]);
            var boletoIds = loaded.Entries.Select(e => e.BoletoId).Distinct().ToArray();
            var visible = (await tx.QueryAsync<VisibleBoletoRow>(
                    "select id, associado_nome, placa, pago_em from boletos where id = any(@boletoIds)", new { boletoIds }))
                .ToDictionary(b => b.Id);
            var names = await NamesAsync(tx, loaded.Entries.Select(e => e.OwnerId));

            return loaded.Entries
                .Select(e =>
                {
                    var boleto = visible.GetValueOrDefault(e.BoletoId);
                    return new EntryResponse(e.BoletoId, e.OwnerId, names.GetValueOrDefault(e.OwnerId), e.RuleType, e.Level,
                        e.Rate, e.Base, e.Amount, boleto?.AssociadoNome, boleto?.Placa, boleto?.PagoEm);
                })
                .OrderBy(e => e.RuleType)
                .ThenBy(e => e.OriginName)
                .ThenBy(e => e.BoletoId)
                .ToList();
        }, ct);
        return Results.Ok(response);
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(Tx tx, IEnumerable<Guid> ids)
    {
        var distinct = ids.Distinct().ToArray();
        return (await tx.QueryAsync<NameRow>("select id, name from users where id = any(@distinct)", new { distinct }))
            .ToDictionary(r => r.Id, r => r.Name);
    }
}
