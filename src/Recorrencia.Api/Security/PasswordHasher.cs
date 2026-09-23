using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Security;

public sealed class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly Argon2Options _options;
    private readonly Lazy<string> _dummy;

    public PasswordHasher(IOptions<Argon2Options> options)
    {
        _options = options.Value;
        _dummy = new Lazy<string>(() => Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, _options.MemoryKb, _options.Iterations, _options.Parallelism, HashBytes);
        return $"$argon2id$v=19$m={_options.MemoryKb},t={_options.Iterations},p={_options.Parallelism}${Encode(salt)}${Encode(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        if (!TryParse(encoded, out var parsed))
            return false;
        var actual = Compute(password, parsed.Salt, parsed.MemoryKb, parsed.Iterations, parsed.Parallelism, parsed.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, parsed.Hash);
    }

    public bool VerifyAgainstDummy(string password)
    {
        Verify(password, _dummy.Value);
        return false;
    }

    public bool NeedsRehash(string encoded) =>
        !TryParse(encoded, out var parsed)
        || parsed.MemoryKb != _options.MemoryKb
        || parsed.Iterations != _options.Iterations
        || parsed.Parallelism != _options.Parallelism;

    private static byte[] Compute(string password, byte[] salt, int memoryKb, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] Decode(string value) =>
        Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='));

    private sealed record Parsed(int MemoryKb, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);

    private static bool TryParse(string encoded, [NotNullWhen(true)] out Parsed? parsed)
    {
        parsed = null;
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19")
                return false;
            var settings = parts[3].Split(',')
                .Select(p => p.Split('='))
                .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
            if (!int.TryParse(settings.GetValueOrDefault("m"), out var m)
                || !int.TryParse(settings.GetValueOrDefault("t"), out var t)
                || !int.TryParse(settings.GetValueOrDefault("p"), out var p))
                return false;
            var salt = Decode(parts[4]);
            var hash = Decode(parts[5]);
            if (salt.Length == 0 || hash.Length == 0)
                return false;
            parsed = new Parsed(m, t, p, salt, hash);
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
