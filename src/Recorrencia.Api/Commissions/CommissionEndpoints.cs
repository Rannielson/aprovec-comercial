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
    public sealed record DownlineResponse(Guid UserId, string Name, int Level, decimal Rate,
        decimal Base, decimal Amount, int Pagamentos);
    public sealed record LostResponse(bool Available, decimal Total, decimal EmAtraso, decimal Cancelados, int Boletos);

    private sealed class UnpaidBoletoRow
    {
        public Guid BoletoId { get; set; }
        public Guid ParticipanteId { get; set; }
        public decimal Valor { get; set; }
        public string Status { get; set; } = "";
    }

    private sealed class DownlinePersonRow
    {
        public Guid UserId { get; set; }
        public string Name { get; set; } = "";
        public int Level { get; set; }
    }

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
        app.MapGet("/commissions/{competencia}/pending-fees", PendingFeesAsync).RequirePermission("comissoes.visualizar").RequirePermission("integracoes.gerenciar");
        app.MapGet("/commissions/{competencia}", SummaryAsync).RequirePermission("comissoes.visualizar");
        app.MapGet("/commissions/{competencia}/entries", EntriesAsync).RequirePermission("comissoes.visualizar");
        app.MapGet("/commissions/{competencia}/downline", DownlineAsync).RequirePermission("comissoes.visualizar");
        app.MapGet("/commissions/{competencia}/lost", LostAsync).RequirePermission("comissoes.visualizar");
    }

    private static async Task<IResult> PendingFeesAsync(string competencia, RequestContext request, Database db, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var count = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx => {
            var beneficiaries = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            return await tx.ExecuteScalarAsync<int>("select count(*)::int from app.commission_pending_fees(@month,@beneficiaries)", new { month, beneficiaries });
        }, ct);
        return Results.Ok(new { count });
    }

    private static async Task<IResult> LostAsync(string competencia, Guid? beneficiaryId,
        RequestContext request, Database db, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var beneficiary = beneficiaryId ?? request.RequireUser();
        var response = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            var allowed = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            if (!allowed.Contains(beneficiary))
                throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");

            var available = await tx.ExecuteScalarAsync<bool>(
                "select exists(select 1 from boleto_vencimento_importacoes where tenant_id = @tenant and competencia = @month)",
                new { tenant = request.RequireTenant(), month });
            if (!available) return new LostResponse(false, 0, 0, 0, 0);

            var planId = await tx.ExecuteScalarAsync<Guid?>("select app.commission_plan_for(@month)", new { month });
            if (planId is null) return new LostResponse(true, 0, 0, 0, 0);

            var rules = await tx.QueryAsync<CommissionService.RuleRow>(
                "select id, type, rate, level, group_id from commission_rules where plan_id = @planId", new { planId });
            var boletos = (await tx.QueryAsync<UnpaidBoletoRow>(
                "select boleto_id, participante_id, valor, status from app.commission_source_unpaid_boletos(@month, @beneficiaries)",
                new { month, beneficiaries = new[] { beneficiary } })).ToList();
            if (boletos.Count == 0) return new LostResponse(true, 0, 0, 0, 0);

            var paths = await tx.QueryAsync<CommissionService.PathRow>(
                "select ancestor_id, descendant_id, depth from app.commission_source_paths(@month, @beneficiaries)",
                new { month, beneficiaries = new[] { beneficiary } });
            var members = await tx.QueryAsync<CommissionService.MemberRow>(
                "select group_id, user_id from commission_group_members");
            var plan = new CommissionPlan(planId.Value, month,
                rules.Select(r => new CommissionRule(r.Id, CommissionService.ParseRuleType(r.Type), r.Rate, r.Level, r.GroupId)).ToList());
            var baseEntries = CommissionEngine.Calculate(plan,
                    boletos.Select(b => new PaidBoleto(b.BoletoId, b.ParticipanteId, b.Valor)),
                    paths.Select(p => new HierarchyPath(p.AncestorId, p.DescendantId, p.Depth)),
                    members.Select(m => new GroupMembership(m.GroupId, m.UserId)))
                .Where(e => e.BeneficiaryId == beneficiary).ToList();
            var entries = await CommissionService.ApplyCutoffsAsync(tx, month, [beneficiary], true, baseEntries);
            var statusByBoleto = boletos.ToDictionary(b => b.BoletoId, b => b.Status);
            return new LostResponse(true, entries.Sum(e => e.Amount),
                entries.Where(e => statusByBoleto[e.BoletoId] == "atraso").Sum(e => e.Amount),
                entries.Where(e => statusByBoleto[e.BoletoId] == "cancelado").Sum(e => e.Amount),
                entries.Select(e => e.BoletoId).Distinct().Count());
        }, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> SummaryAsync(string competencia, bool? recalculate, RequestContext request, Database db, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var response = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            if (recalculate is true && !await tx.ExecuteScalarAsync<bool>(
                "select app.has_permission('fechamento.confirmar') and app.scope_for('comissoes.visualizar') = 'tenant'"))
                throw new ApiProblem(StatusCodes.Status403Forbidden, "fechamento.requires_full_visibility");
            var beneficiaries = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            var loaded = await CommissionService.LoadAsync(tx, month, beneficiaries);
            if (recalculate is true)
                loaded = new CommissionService.LoadedCommissions(loaded.Status, "live_review",
                    await CommissionService.CalculateLiveAsync(tx, month, beneficiaries));

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

    private static async Task<IResult> DownlineAsync(string competencia, Guid beneficiaryId,
        RequestContext request, Database db, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var response = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            var allowed = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            if (!allowed.Contains(beneficiaryId))
                throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");

            var people = (await tx.QueryAsync<DownlinePersonRow>(
                "select user_id, name, level from app.commission_source_people(@month, @beneficiaryId)",
                new { month, beneficiaryId })).ToList();
            if (people.Count == 0) return new List<DownlineResponse>();

            var planId = await tx.ExecuteScalarAsync<Guid?>(
                "select app.commission_plan_for(@month)", new { month });
            var rates = (await tx.QueryAsync<CommissionService.RuleRow>(
                "select level, rate from commission_rules where plan_id = @planId and type = 'upline'",
                new { planId })).Where(r => r.Level is not null)
                .ToDictionary(r => r.Level!.Value, r => r.Rate);
            var entries = (await CommissionService.LoadAsync(tx, month, [beneficiaryId])).Entries;

            return people.Where(person => rates.ContainsKey(person.Level)).Select(person =>
            {
                var fromPerson = entries.Where(e => e.RuleType == RuleType.Upline
                    && e.OwnerId == person.UserId && e.Level == person.Level).ToList();
                return new DownlineResponse(person.UserId, person.Name, person.Level,
                    rates.GetValueOrDefault(person.Level), fromPerson.Sum(e => e.Base),
                    fromPerson.Sum(e => e.Amount), fromPerson.Select(e => e.BoletoId).Distinct().Count());
            }).ToList();
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
