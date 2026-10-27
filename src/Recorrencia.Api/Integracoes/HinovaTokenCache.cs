using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Recorrencia.Api.Integracoes;

public sealed class HinovaTokenCache(TimeProvider? clock = null)
{
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Token { get; set; }
        public HinovaAuthException? Blocked { get; set; }
        public DateTimeOffset BlockedUntil { get; set; }
    }
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public static string Key(string usuario, string senha, string tokenSga) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { usuario, senha, tokenSga }))));

    public async Task<string> GetAsync(string key, Func<Task<string>> authenticate, CancellationToken ct, string? invalidToken = null)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        await entry.Gate.WaitAsync(ct);
        try
        {
            if (entry.Blocked is { } blocked && entry.BlockedUntil > (clock?.GetUtcNow() ?? DateTimeOffset.UtcNow))
                throw blocked;
            entry.Blocked = null;
            if (invalidToken is not null && entry.Token == invalidToken) entry.Token = null;
            try { return entry.Token ??= await authenticate(); }
            catch (HinovaAuthException ex) when (ex.IsExtractionLimit)
            {
                entry.Token = null;
                entry.Blocked = ex;
                entry.BlockedUntil = (clock?.GetUtcNow() ?? DateTimeOffset.UtcNow) + ex.RetryAfter!.Value;
                throw;
            }
        }
        finally { entry.Gate.Release(); }
    }
    public async Task BlockAsync(string key, HinovaAuthException exception, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        await entry.Gate.WaitAsync(ct);
        try
        {
            entry.Token = null;
            entry.Blocked = exception;
            entry.BlockedUntil = (clock?.GetUtcNow() ?? DateTimeOffset.UtcNow) + exception.RetryAfter!.Value;
        }
        finally { entry.Gate.Release(); }
    }
}
