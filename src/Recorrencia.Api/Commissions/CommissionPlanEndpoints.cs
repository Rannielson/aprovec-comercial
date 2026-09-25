using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Commissions;

public static class CommissionPlanEndpoints
{
    public sealed record RuleInput(string? Type, decimal Rate, int? Level, Guid? GroupId);
    public sealed record PlanRequest(string? Name, DateOnly? EffectiveFrom, RuleInput[]? Rules);
    public sealed record GroupRequest(string? Name);
    public sealed record MembersRequest(Guid[]? UserIds);
    public sealed record RuleResponse(Guid Id, string Type, decimal Rate, int? Level, Guid? GroupId);
    public sealed record PlanResponse(Guid Id, string Name, DateOnly EffectiveFrom, string Status, IReadOnlyList<RuleResponse> Rules);
    public sealed record GroupResponse(Guid Id, string Name, IReadOnlyList<Guid> Members);

    public sealed class PlanRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public DateOnly EffectiveFrom { get; set; }
        public string Status { get; set; } = "";
        public Guid? RuleId { get; set; }
        public string? Type { get; set; }
        public decimal? Rate { get; set; }
        public int? Level { get; set; }
        public Guid? GroupId { get; set; }
    }

    public sealed class GroupRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public Guid? UserId { get; set; }
    }

    private static readonly string[] RuleTypes = ["own", "upline", "global"];

    public static void MapCommissionPlanEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/commission-plans", ListPlansAsync).RequirePermission("regras_comissao.visualizar");
        app.MapPost("/commission-plans", CreatePlanAsync).RequirePermission("regras_comissao.editar");
        app.MapPost("/commission-plans/{id:guid}/activate", ActivatePlanAsync).RequirePermission("regras_comissao.editar");
        app.MapDelete("/commission-plans/{id:guid}", DeletePlanAsync).RequirePermission("regras_comissao.editar");
        app.MapGet("/commission-groups", ListGroupsAsync).RequirePermission("regras_comissao.visualizar");
        app.MapPost("/commission-groups", CreateGroupAsync).RequirePermission("regras_comissao.editar");
        app.MapPut("/commission-groups/{id:guid}/members", SetMembersAsync).RequirePermission("regras_comissao.editar");
    }

    private const string PlansSql = """
        select p.id, p.name, p.effective_from, p.status,
               r.id as rule_id, r.type, r.rate, r.level, r.group_id
          from commission_plans p
          left join commission_rules r on r.plan_id = p.id
         where (@id::uuid is null or p.id = @id)
         order by p.effective_from desc, p.created_at desc, r.type, r.level
        """;

    private static async Task<IReadOnlyList<PlanResponse>> LoadPlansAsync(Tx tx, Guid? id) =>
        (await tx.QueryAsync<PlanRow>(PlansSql, new { id }))
            .GroupBy(r => (r.Id, r.Name, r.EffectiveFrom, r.Status))
            .Select(g => new PlanResponse(g.Key.Id, g.Key.Name, g.Key.EffectiveFrom, g.Key.Status,
                g.Where(r => r.RuleId is not null)
                 .Select(r => new RuleResponse(r.RuleId!.Value, r.Type!, r.Rate!.Value, r.Level, r.GroupId))
                 .ToList()))
            .ToList();

    private static async Task<IResult> ListPlansAsync(RequestContext request, Database db, CancellationToken ct) =>
        Results.Ok(await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), tx => LoadPlansAsync(tx, null), ct));

    private static async Task<IResult> CreatePlanAsync(PlanRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.name_required");
        // EffectiveFrom is nullable precisely so an omitted field is rejected here, rather
        // than silently defaulting to DateOnly's default (0001-01-01).
        if (body.EffectiveFrom is not { Day: 1 } effectiveFrom)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.invalid_effective_from");
        var rules = body.Rules ?? [];
        if (rules.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.empty");
        if (rules.Any(r => !IsValidRule(r)))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.invalid_rule");

        var id = Guid.CreateVersion7();
        var plan = await db.InTenantAsync(tenant, actor, async tx =>
        {
            await tx.ExecuteAsync(
                "insert into commission_plans (id, tenant_id, name, effective_from) values (@id, @tenant, @name, @effectiveFrom)",
                new { id, tenant, name, effectiveFrom });
            try
            {
                foreach (var rule in rules)
                {
                    await tx.ExecuteAsync(
                        """
                        insert into commission_rules (id, tenant_id, plan_id, type, rate, level, group_id)
                        values (@ruleId, @tenant, @id, @type, @rate, @level, @groupId)
                        """,
                        new { ruleId = Guid.CreateVersion7(), tenant, id, type = rule.Type, rate = rule.Rate, level = rule.Level, groupId = rule.GroupId });
                }
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.duplicate_rule");
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.invalid_group");
            }
            await WriteAsync(tx, tenant, actor, "commission_plans.create", "commission_plans", id, null, body);
            return (await LoadPlansAsync(tx, id)).Single();
        }, ct);
        return Results.Created($"/commission-plans/{id}", plan);
    }

    private static bool IsValidRule(RuleInput rule) =>
        rule.Type is not null
        && RuleTypes.Contains(rule.Type)
        && rule.Rate > 0 && rule.Rate <= 1 && rule.Rate == Math.Round(rule.Rate, 4)
        && rule.Type switch
        {
            "own" => rule.Level is null && rule.GroupId is null,
            "upline" => rule.Level is >= 1 && rule.GroupId is null,
            "global" => rule.Level is null && rule.GroupId is not null,
            _ => false,
        };

    private static async Task<IResult> ActivatePlanAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var plan = await db.InTenantAsync(tenant, actor, async tx =>
        {
            int affected;
            try
            {
                affected = await tx.ExecuteAsync(
                    "update commission_plans set status = 'ativo', activated_at = now() where id = @id", new { id });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "plan.effective_conflict");
            }
            if (affected == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "plan.not_found");
            await WriteAsync(tx, tenant, actor, "commission_plans.activate", "commission_plans", id, null, null);
            return (await LoadPlansAsync(tx, id)).Single();
        }, ct);
        return Results.Ok(plan);
    }

    private static async Task<IResult> DeletePlanAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var affected = await tx.ExecuteAsync("delete from commission_plans where id = @id", new { id });
            if (affected == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "plan.not_found");
            await WriteAsync(tx, tenant, actor, "commission_plans.delete", "commission_plans", id, null, null);
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListGroupsAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var rows = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), tx => tx.QueryAsync<GroupRow>(
            """
            select g.id, g.name, m.user_id
              from commission_groups g
              left join commission_group_members m on m.group_id = g.id
             order by g.name, m.user_id
            """), ct);
        return Results.Ok(rows.GroupBy(r => (r.Id, r.Name))
            .Select(g => new GroupResponse(g.Key.Id, g.Key.Name, g.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).ToList()))
            .ToList());
    }

    private static async Task<IResult> CreateGroupAsync(GroupRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "group.name_required");
        var id = Guid.CreateVersion7();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            try
            {
                await tx.ExecuteAsync("insert into commission_groups (id, tenant_id, name) values (@id, @tenant, @name)", new { id, tenant, name });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "group.name_taken");
            }
            await WriteAsync(tx, tenant, actor, "commission_groups.create", "commission_groups", id, null, new { name });
            return 0;
        }, ct);
        return Results.Created($"/commission-groups/{id}", new { id });
    }

    private static async Task<IResult> SetMembersAsync(Guid id, MembersRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var userIds = (body.UserIds ?? []).Distinct().ToArray();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            _ = await tx.QuerySingleOrDefaultAsync<Guid?>("select id from commission_groups where id = @id", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "group.not_found");
            await tx.ExecuteAsync("delete from commission_group_members where group_id = @id", new { id });
            try
            {
                foreach (var userId in userIds)
                {
                    await tx.ExecuteAsync(
                        "insert into commission_group_members (tenant_id, group_id, user_id) values (@tenant, @id, @userId)",
                        new { tenant, id, userId });
                }
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new ApiProblem(StatusCodes.Status400BadRequest, "group.invalid_member");
            }
            await WriteAsync(tx, tenant, actor, "commission_groups.set_members", "commission_groups", id, null, new { userIds });
            return 0;
        }, ct);
        return Results.NoContent();
    }
}
