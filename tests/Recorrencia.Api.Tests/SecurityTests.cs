using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Tests;

public class PasswordHasherTests
{
    private static PasswordHasher Hasher(int memoryKb = 1024, int iterations = 1) =>
        new(Options.Create(new Argon2Options { MemoryKb = memoryKb, Iterations = iterations, Parallelism = 1 }));

    [Fact]
    public void Hash_uses_the_phc_format_and_verifies()
    {
        var hasher = Hasher();
        var encoded = hasher.Hash("senha-correta-1");

        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", encoded);
        Assert.True(hasher.Verify("senha-correta-1", encoded));
        Assert.False(hasher.Verify("senha-errada-01", encoded));
    }

    [Fact]
    public void Same_password_gets_a_different_salt()
    {
        var hasher = Hasher();
        Assert.NotEqual(hasher.Hash("mesma-senha-1"), hasher.Hash("mesma-senha-1"));
    }

    [Fact]
    public void Hashes_made_with_other_parameters_still_verify_and_need_rehash()
    {
        var old = Hasher(memoryKb: 2048, iterations: 2).Hash("senha-antiga-1");

        Assert.True(Hasher().Verify("senha-antiga-1", old));
        Assert.True(Hasher().NeedsRehash(old));
        Assert.False(Hasher(memoryKb: 2048, iterations: 2).NeedsRehash(old));
    }

    [Theory]
    [InlineData("")]
    [InlineData("texto-qualquer")]
    [InlineData("$argon2id$v=19$m=x,t=1,p=1$aaaa$bbbb")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$***$bbbb")]
    public void Malformed_hashes_never_verify(string encoded)
    {
        Assert.False(Hasher().Verify("qualquer-senha", encoded));
        Assert.True(Hasher().NeedsRehash(encoded));
    }

    [Fact]
    public void Dummy_verification_always_fails()
    {
        Assert.False(Hasher().VerifyAgainstDummy("qualquer-senha"));
    }
}

public class TokensTests
{
    [Fact]
    public void Tokens_are_url_safe_random_and_hash_to_32_bytes()
    {
        var a = Tokens.New();
        var b = Tokens.New();

        Assert.Equal(43, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.NotEqual(a, b);
        Assert.Equal(32, Tokens.Hash(a).Length);
        Assert.Equal(Tokens.Hash(a), Tokens.Hash(a));
    }
}

public class LoginThrottleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

    private LoginThrottle Throttle() =>
        new(_time, Options.Create(new AuthOptions { MaxFailuresPerWindow = 3, FailureWindowSeconds = 60 }));

    [Fact]
    public void Blocks_after_the_limit_inside_the_window()
    {
        var throttle = Throttle();
        throttle.RecordFailure("ip:1", "email:a");
        throttle.RecordFailure("ip:1", "email:a");
        Assert.False(throttle.IsBlocked("ip:1"));

        throttle.RecordFailure("ip:1", "email:b");
        Assert.True(throttle.IsBlocked("ip:1"));
        Assert.True(throttle.IsBlocked("ip:2", "ip:1"));
        Assert.False(throttle.IsBlocked("email:a"));
    }

    [Fact]
    public void Releases_after_the_window()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++)
            throttle.RecordFailure("ip:1");

        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.False(throttle.IsBlocked("ip:1"));
    }

    [Fact]
    public void Reset_clears_a_key()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++)
            throttle.RecordFailure("email:a");

        throttle.Reset("email:a");

        Assert.False(throttle.IsBlocked("email:a"));
    }
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123456789")]
    public void Short_passwords_are_rejected(string? password)
    {
        var ex = Assert.Throws<ApiProblem>(() => PasswordPolicy.Validate(password));
        Assert.Equal(("auth.weak_password", 400), (ex.Code, ex.Status));
    }

    [Fact]
    public void Limits_are_inclusive()
    {
        PasswordPolicy.Validate(new string('a', 10));
        PasswordPolicy.Validate(new string('a', 128));
        Assert.Throws<ApiProblem>(() => PasswordPolicy.Validate(new string('a', 129)));
    }
}
