namespace Recorrencia.Api.Authorization;

public static class Scopes
{
    public static readonly string[] Ordered = ["own", "direct", "subtree", "tenant"];

    public static bool IsValid(string? scope) => scope is null || Ordered.Contains(scope);

    public static int Rank(string? scope) => scope is null ? -1 : Array.IndexOf(Ordered, scope);
}

public sealed record PermissionGrant(string Key, string? Scope);

public sealed class PermissionSet(IReadOnlyDictionary<string, string?> grants)
{
    public bool Has(string key) => grants.ContainsKey(key);

    public string? ScopeOf(string key) => grants.GetValueOrDefault(key);

    public bool CanGrant(string key, string? scope) =>
        grants.TryGetValue(key, out var own) && Scopes.Rank(own) >= Scopes.Rank(scope);

    public IReadOnlyList<PermissionGrant> All =>
        grants.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new PermissionGrant(g.Key, g.Value)).ToList();
}
