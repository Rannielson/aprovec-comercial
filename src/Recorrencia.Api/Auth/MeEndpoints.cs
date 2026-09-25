using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Auth;

public static class MeEndpoints
{
    public sealed record TenantSummary(Guid Id, string Slug, string Name);

    public sealed record MeResponse(Guid Id, string Name, string Email, TenantSummary Tenant,
        IReadOnlyList<PermissionGrant> Permissions, IReadOnlyList<string> Modules, IReadOnlyList<string> RoleTemplates);

    public sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public static void MapMeEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/me", async (RequestContext request, Database db, CurrentPermissions permissions, CancellationToken ct) =>
        {
            var tenant = request.RequireTenant();
            var userId = request.RequireUser();
            var granted = await permissions.GetAsync(ct);
            var (user, modules, roleTemplates) = await db.InTenantAsync(tenant, userId, async tx =>
            (
                await tx.QuerySingleAsync<UserRow>("select id, name, email from users where id = @userId", new { userId }),
                (await tx.QueryAsync<string>("select module_key from tenant_modules where enabled order by module_key")).ToList(),
                // Distinct template keys behind this user's roles -- lets the frontend tell "holds every
                // permission because they're Administrador" apart from "holds them via a custom role",
                // which a plain permission set can't express. Custom (non-template) roles contribute nothing.
                (await tx.QueryAsync<string>(
                    """
                    select distinct r.source_template_key
                      from user_roles ur
                      join roles r on r.id = ur.role_id
                     where ur.user_id = @userId and r.source_template_key is not null
                    """, new { userId })).ToList()
            ), ct);
            return Results.Ok(new MeResponse(user.Id, user.Name, user.Email,
                new TenantSummary(tenant, request.TenantSlug!, request.TenantName!), granted.All, modules, roleTemplates));
        }).RequireUser();
}
