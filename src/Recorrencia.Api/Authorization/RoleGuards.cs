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
