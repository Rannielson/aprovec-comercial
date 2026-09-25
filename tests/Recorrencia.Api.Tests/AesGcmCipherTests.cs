using Microsoft.Extensions.Options;
using Recorrencia.Api;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Tests;

public class AesGcmCipherTests
{
    private static AesGcmCipher NewCipher() =>
        new(Options.Create(new HinovaOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));

    [Fact]
    public void Round_trips_a_plaintext_value()
    {
        var cipher = NewCipher();
        var encrypted = cipher.Encrypt("usuario-secreto");
        Assert.Equal("usuario-secreto", cipher.Decrypt(encrypted));
    }

    [Fact]
    public void Two_encryptions_of_the_same_value_produce_different_bytes()
    {
        var cipher = NewCipher();
        var a = cipher.Encrypt("mesmo-valor");
        var b = cipher.Encrypt("mesmo-valor");
        Assert.False(a.SequenceEqual(b)); // nonce aleatório por chamada
    }
}
