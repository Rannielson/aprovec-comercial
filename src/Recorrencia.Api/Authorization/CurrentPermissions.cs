using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Authorization;

public sealed class CurrentPermissions(RequestContext request, Database db)
{
    private PermissionSet? _cached;

    public async Task<PermissionSet> GetAsync(CancellationToken ct)
    {
        if (_cached is not null)
            return _cached;
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var rows = await db.InTenantAsync(tenant, user,
            tx => tx.QueryAsync<PermissionRow>("select permission_key, scope from app.effective_permissions()"), ct);
        return _cached = new PermissionSet(rows.ToDictionary(r => r.PermissionKey, r => r.Scope));
    }

    public sealed class PermissionRow
    {
        public string PermissionKey { get; set; } = "";
        public string? Scope { get; set; }
    }
}
