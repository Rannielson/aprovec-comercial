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

        await EnsureCanGrantRolePermissionsAsync(tx, mine, distinct);
    }

    /// <summary>
    /// Same grant-ceiling check as <see cref="EnsureCanGrantRolesAsync"/>, but for roles
    /// already known to exist (e.g. a role currently assigned to a user that is about to be
    /// removed). Used both for additions and, symmetrically, for removals: whether a role's
    /// permissions are being granted or taken away, the actor must personally hold every one
    /// of them at an equal-or-greater scope.
    /// </summary>
    public static async Task EnsureCanGrantRolePermissionsAsync(Tx tx, PermissionSet mine, IReadOnlyCollection<Guid> roleIds)
    {
        if (roleIds.Count == 0)
            return;
        var distinct = roleIds.Distinct().ToArray();
        var grants = await tx.QueryAsync<GrantRow>(
            "select permission_key, scope from role_permissions where role_id = any(@distinct)", new { distinct });
        EnsureCanGrant(mine, grants);
    }

    /// <summary>
    /// Grant ceiling for acting on an existing user's identity or credentials (editing their
    /// email, resetting their password): the actor must personally hold every permission the
    /// target's current roles grant, at an equal-or-greater scope -- otherwise a narrow
    /// onboarding role could take over the Administrador account. The target's role ids come
    /// from app.user_role_ids, NOT a plain select on user_roles: user_roles RLS hides another
    /// user's role rows from anyone without usuarios.gerenciar_perfis, so a plain select would
    /// return nothing for exactly the actors this guard exists to stop, and silently pass.
    /// </summary>
    public static async Task EnsureCanManageUserAsync(Tx tx, PermissionSet mine, Guid userId)
    {
        var roleIds = (await tx.QueryAsync<Guid>("select * from app.user_role_ids(@userId)", new { userId })).ToArray();
        await EnsureCanGrantRolePermissionsAsync(tx, mine, roleIds);
    }

    /// <summary>
    /// The core grant-ceiling check, factored out so it can be applied symmetrically to both
    /// additions and removals: an actor may only add or remove a (permission, scope) grant
    /// they personally hold at an equal-or-greater scope. Without this applied to removals
    /// too, a role-manager holding nothing but `usuarios.gerenciar_perfis` could strip a role
    /// or user of permissions far beyond their own reach (e.g. deleting the Administrador
    /// role entirely), which is exactly as dangerous as granting those permissions would be.
    /// </summary>
    public static void EnsureCanGrant(PermissionSet mine, IEnumerable<GrantRow> grants)
    {
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
