using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Recorrencia.Api.Security;

public static class Tokens
{
    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
