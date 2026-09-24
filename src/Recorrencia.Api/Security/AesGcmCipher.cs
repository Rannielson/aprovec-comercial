using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Security;

public sealed class AesGcmCipher
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _key;

    public AesGcmCipher(IOptions<HinovaOptions> options) => _key = Convert.FromBase64String(options.Value.EncryptionKey);

    public byte[] Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        return [.. nonce, .. cipherBytes, .. tag];
    }

    public string Decrypt(byte[] combined)
    {
        var nonce = combined[..NonceBytes];
        var tag = combined[^TagBytes..];
        var cipherBytes = combined[NonceBytes..^TagBytes];
        var plainBytes = new byte[cipherBytes.Length];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
