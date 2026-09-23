using Microsoft.Extensions.Caching.Memory;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Platform;

namespace Recorrencia.Api.Tenancy;

public sealed record TenantInfo(Guid Id, string Name);

public sealed class TenantResolver(Database db, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<TenantInfo?> ResolveActiveAsync(string slug, CancellationToken ct)
    {
        // Reject an obviously-invalid subdomain shape (uppercase, underscores, empty, etc.)
        // before it costs a DB round-trip or an unbounded cache entry -- any distinct string
        // that reaches this API otherwise gets cached (even as a negative result) forever.
        if (!PlatformEndpoints.SlugPattern().IsMatch(slug))
            return null;

        var key = CacheKey(slug);
        if (cache.TryGetValue(key, out TenantInfo? cached))
            return cached;

        var row = await db.AnonymousAsync(
            tx => tx.QuerySingleOrDefaultAsync<TenantRow>("select id, name, status from app.resolve_tenant(@slug)", new { slug }),
            ct);
        var info = row is { Status: "ativo" } ? new TenantInfo(row.Id, row.Name) : null;
        cache.Set(key, info, Ttl);
        return info;
    }

    public void Invalidate(string slug) => cache.Remove(CacheKey(slug));

    private static string CacheKey(string slug) => "tenant:" + slug.ToLowerInvariant();

    public sealed class TenantRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
