using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Roles;

public static class RoleEndpoints
{
    public sealed record PermissionInput(string? Key, string? Scope);
    public sealed record RoleRequest(string? Name, PermissionInput[]? Permissions);
    public sealed record CatalogPermission(string Key, string Name, bool Scoped);
    public sealed record CatalogModule(string Key, string Name, bool Enabled, IReadOnlyList<CatalogPermission> Permissions);
    public sealed record RolePermission(string Key, string? Scope);
    public sealed record RoleResponse(Guid Id, string Name, string? SourceTemplateKey, IReadOnlyList<RolePermission> Permissions);

    public sealed class ModuleRow
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
    }

    public sealed class PermissionRow
    {
        public string Key { get; set; } = "";
        public string ModuleKey { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Scoped { get; set; }
    }

    public sealed class RoleRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? SourceTemplateKey { get; set; }
        public string? PermissionKey { get; set; }
        public string? Scope { get; set; }
    }

    public static void MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/permissions", CatalogAsync).RequirePermission("usuarios.gerenciar_perfis");
        app.MapGet("/roles", ListAsync).RequirePermission("usuarios.gerenciar_perfis");
        app.MapPost("/roles", CreateAsync).RequirePermission("usuarios.gerenciar_perfis");
        app.MapPut("/roles/{id:guid}", UpdateAsync).RequirePermission("usuarios.gerenciar_perfis");
        app.MapDelete("/roles/{id:guid}", DeleteAsync).RequirePermission("usuarios.gerenciar_perfis");
    }

    private static async Task<IResult> CatalogAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var catalog = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            var modules = await tx.QueryAsync<ModuleRow>(
                "select m.key, m.name, tm.enabled from modules m join tenant_modules tm on tm.module_key = m.key order by m.sort_order");
            var permissions = (await tx.QueryAsync<PermissionRow>("select key, module_key, name, scoped from permissions order by key")).ToList();
            return modules.Select(m => new CatalogModule(m.Key, m.Name, m.Enabled,
                permissions.Where(p => p.ModuleKey == m.Key).Select(p => new CatalogPermission(p.Key, p.Name, p.Scoped)).ToList())).ToList();
        }, ct);
        return Results.Ok(catalog);
    }

    private static async Task<IResult> ListAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var rows = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), tx => tx.QueryAsync<RoleRow>(
            """
            select r.id, r.name, r.source_template_key, rp.permission_key, rp.scope
              from roles r
              left join role_permissions rp on rp.role_id = r.id
             order by r.name, rp.permission_key
            """), ct);
        var roles = rows.GroupBy(r => (r.Id, r.Name, r.SourceTemplateKey))
            .Select(g => new RoleResponse(g.Key.Id, g.Key.Name, g.Key.SourceTemplateKey,
                g.Where(r => r.PermissionKey is not null).Select(r => new RolePermission(r.PermissionKey!, r.Scope)).ToList()))
            .ToList();
        return Results.Ok(roles);
    }

    private static async Task<IResult> CreateAsync(RoleRequest body, RequestContext request, Database db, CurrentPermissions permissions, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var mine = await permissions.GetAsync(ct);
        var id = Guid.CreateVersion7();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var (name, grants) = await ValidateAsync(tx, body, mine);
            try
            {
                await tx.ExecuteAsync("insert into roles (id, tenant_id, name) values (@id, @tenant, @name)", new { id, tenant, name });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "role.name_taken");
            }
            await InsertPermissionsAsync(tx, tenant, id, grants);
            await WriteAsync(tx, tenant, actor, "roles.create", "roles", id, null, new { name, permissions = grants });
            return 0;
        }, ct);
        return Results.Created($"/roles/{id}", new { id });
    }

    private static async Task<IResult> UpdateAsync(Guid id, RoleRequest body, RequestContext request, Database db, CurrentPermissions permissions, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var mine = await permissions.GetAsync(ct);
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var before = (await tx.QueryAsync<RoleRow>(
                "select r.id, r.name, rp.permission_key, rp.scope from roles r left join role_permissions rp on rp.role_id = r.id where r.id = @id",
                new { id })).ToList();
            if (before.Count == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "role.not_found");

            var (name, grants) = await ValidateAsync(tx, body, mine);
            try
            {
                await tx.ExecuteAsync("update roles set name = @name where id = @id", new { name, id });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "role.name_taken");
            }

            // Remoções por último: sem gerenciar_perfis no meio da transação, o RLS bloquearia as escritas seguintes.
            var current = before.Where(r => r.PermissionKey is not null).ToDictionary(r => r.PermissionKey!, r => r.Scope);

            // A permission is being revoked when it's dropped entirely or downgraded to a
            // narrower scope. The actor must personally hold every one of those (permission,
            // scope) combinations -- the same ceiling already enforced for additions in
            // ValidateAsync -- otherwise they could strip the role of standing they never had
            // themselves (e.g. deleting fechamento.confirmar from Administrador while holding
            // only usuarios.gerenciar_perfis).
            var newScopeByKey = grants.ToDictionary(g => g.Key, g => g.Scope);
            var revoked = current
                .Where(kv => !newScopeByKey.TryGetValue(kv.Key, out var newScope) || Scopes.Rank(newScope) < Scopes.Rank(kv.Value))
                .Select(kv => new RoleGuards.GrantRow { PermissionKey = kv.Key, Scope = kv.Value });
            RoleGuards.EnsureCanGrant(mine, revoked);

            await InsertPermissionsAsync(tx, tenant, id, grants.Where(g => !current.ContainsKey(g.Key)).ToList());
            foreach (var changed in grants.Where(g => current.TryGetValue(g.Key, out var scope) && scope != g.Scope))
            {
                await tx.ExecuteAsync(
                    "update role_permissions set scope = @scope where role_id = @id and permission_key = @key",
                    new { scope = changed.Scope, id, key = changed.Key });
            }
            var removed = current.Keys.Except(grants.Select(g => g.Key)).ToArray();
            if (removed.Length > 0)
                await tx.ExecuteAsync("delete from role_permissions where role_id = @id and permission_key = any(@removed)", new { id, removed });

            await RoleGuards.EnsureProfileManagerRemainsAsync(tx);
            await WriteAsync(tx, tenant, actor, "roles.update", "roles", id,
                new { name = before[0].Name, permissions = before.Where(r => r.PermissionKey is not null).Select(r => new { r.PermissionKey, r.Scope }) },
                new { name, permissions = grants });
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, RequestContext request, Database db, CurrentPermissions permissions, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var mine = await permissions.GetAsync(ct);
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var name = await tx.QuerySingleOrDefaultAsync<string>("select name from roles where id = @id", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "role.not_found");

            // Deleting the role removes ALL the permissions it grants at once, so the actor
            // must personally hold every one of them -- same ceiling as removing them one by
            // one via UpdateAsync would require.
            var grants = await tx.QueryAsync<RoleGuards.GrantRow>(
                "select permission_key, scope from role_permissions where role_id = @id", new { id });
            RoleGuards.EnsureCanGrant(mine, grants);

            await tx.ExecuteAsync("delete from roles where id = @id", new { id });
            await RoleGuards.EnsureProfileManagerRemainsAsync(tx);
            await WriteAsync(tx, tenant, actor, "roles.delete", "roles", id, new { name }, null);
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<(string Name, IReadOnlyList<RolePermission> Grants)> ValidateAsync(Tx tx, RoleRequest body, PermissionSet mine)
    {
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "role.name_required");

        var inputs = body.Permissions ?? [];
        var keys = inputs.Select(p => p.Key ?? "").ToArray();
        if (keys.Distinct().Count() != keys.Length)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "role.duplicate_permission");

        var catalog = (await tx.QueryAsync<PermissionRow>("select key, module_key, name, scoped from permissions where key = any(@keys)", new { keys }))
            .ToDictionary(p => p.Key);

        foreach (var input in inputs)
        {
            if (!catalog.TryGetValue(input.Key ?? "", out var permission))
                throw new ApiProblem(StatusCodes.Status400BadRequest, "role.invalid_permission");
            if (!Scopes.IsValid(input.Scope))
                throw new ApiProblem(StatusCodes.Status400BadRequest, "role.invalid_scope");
            if (permission.Scoped && input.Scope is null)
                throw new ApiProblem(StatusCodes.Status400BadRequest, "role.scope_required");
            if (!permission.Scoped && input.Scope is not null)
                throw new ApiProblem(StatusCodes.Status400BadRequest, "role.scope_not_allowed");
        }

        if (inputs.Any(p => !mine.CanGrant(p.Key!, p.Scope)))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "role.grant_exceeds_own");

        return (name, inputs.Select(p => new RolePermission(p.Key!, p.Scope)).ToList());
    }

    private static async Task InsertPermissionsAsync(Tx tx, Guid tenant, Guid roleId, IReadOnlyList<RolePermission> grants)
    {
        foreach (var grant in grants)
        {
            await tx.ExecuteAsync(
                "insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @roleId, @key, @scope)",
                new { tenant, roleId, key = grant.Key, scope = grant.Scope });
        }
    }
}
