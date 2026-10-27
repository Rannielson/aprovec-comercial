using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Recorrencia.Api.Integracoes;

public sealed class HinovaTokenCache
{
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string? Token { get; set; }
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
            if (invalidToken is not null && entry.Token == invalidToken) entry.Token = null;
            return entry.Token ??= await authenticate();
        }
        finally { entry.Gate.Release(); }
    }
}
