using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Authorization;

public static class RoleGuards
{
    public static async Task EnsureCanGrantRolesAsync(Tx tx, PermissionSet mine, IReadOnlyCollection<Guid> roleIds)
    {
        if (roleIds.Count == 0)
            return;
        var distinct = roleIds.Distinct().ToArray();
        var found = await tx.ExecuteScalarAsync<int>("select count(*)::int from roles where id = any(@distinct)", new { distinct });
        if (found != distinct.Length)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "roles.not_found");

        var grants = await tx.QueryAsync<GrantRow>(
            "select permission_key, scope from role_permissions where role_id = any(@distinct)", new { distinct });
        if (grants.Any(g => !mine.CanGrant(g.PermissionKey, g.Scope)))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "role.grant_exceeds_own");
    }

    public static async Task EnsureProfileManagerRemainsAsync(Tx tx)
    {
        // The caller's own mutating UPDATE happens before this check runs (existing call
        // order in UserEndpoints). Without serialization, two concurrent requests that each
        // remove a DIFFERENT one of the last two role-managers can both read the count under
        // READ COMMITTED before either commits: each still sees the other's manager as active,
        // so both counts come back >= 1 and both pass, leaving zero role-managers once both
        // commit. A per-tenant advisory lock forces the second request to wait for the first
        // to commit, so by the time it runs the count it sees the full post-commit state
        // (including its own already-applied mutation) and correctly catches the double
        // removal. Same pattern as `app.hierarchy_after_insert`'s pg_advisory_xact_lock.
        await tx.ExecuteAsync("select pg_advisory_xact_lock(hashtext('role-manager:' || app.current_tenant()::text))");
        var count = await tx.ExecuteScalarAsync<int>("select app.count_active_with_permission('usuarios.gerenciar_perfis')");
        if (count == 0)
            throw new ApiProblem(StatusCodes.Status409Conflict, "role.last_admin");
    }

    public sealed class GrantRow
    {
        public string PermissionKey { get; set; } = "";
        public string? Scope { get; set; }
    }
}
