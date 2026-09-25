using System.Reflection;
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

    private LoginThrottle Throttle(int max = 3, int windowSeconds = 60) =>
        new(_time, Options.Create(new AuthOptions { MaxFailuresPerWindow = max, FailureWindowSeconds = windowSeconds }));

    // Simulates a real failed login: reserve, then complete as failed.
    private static void Fail(LoginThrottle throttle, params string[] keys)
    {
        Assert.True(throttle.TryReserve(keys));
        throttle.Complete(keys, failed: true);
    }

    // Simulates a "peek": reserve and immediately release without recording
    // a failure, leaving the throttle's recorded-failure state unchanged.
    // There is no pure read-only check left in the new API on purpose --
    // that separation (check, then much later, act) is exactly what let the
    // limit be bypassed under concurrency.
    private static bool Reserved(LoginThrottle throttle, params string[] keys)
    {
        var ok = throttle.TryReserve(keys);
        if (ok)
            throttle.Complete(keys, failed: false);
        return ok;
    }

    [Fact]
    public void Blocks_after_the_limit_inside_the_window()
    {
        var throttle = Throttle();
        Fail(throttle, "ip:1", "email:a");
        Fail(throttle, "ip:1", "email:a");
        Assert.True(Reserved(throttle, "ip:1"));

        Fail(throttle, "ip:1", "email:b");
        Assert.False(Reserved(throttle, "ip:1"));
        Assert.False(Reserved(throttle, "ip:2", "ip:1"));
        Assert.True(Reserved(throttle, "email:a"));
    }

    [Fact]
    public void Releases_after_the_window()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++)
            Fail(throttle, "ip:1");

        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.True(Reserved(throttle, "ip:1"));
    }

    [Fact]
    public void Reset_clears_a_key()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++)
            Fail(throttle, "email:a");

        throttle.Reset("email:a");

        Assert.True(Reserved(throttle, "email:a"));
    }

    [Fact]
    public void A_blocked_reservation_has_no_side_effect_on_other_keys()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++)
            Fail(throttle, "ip:1");

        // "ip:1" is at the limit, so reserving {ip:1, email:z} together must
        // reserve NEITHER key -- email:z must remain untouched.
        Assert.False(Reserved(throttle, "ip:1", "email:z"));
        Assert.True(Reserved(throttle, "email:z"));
    }

    // Critical-fix regression: the previous check-then-act design let any
    // number of concurrent requests pass IsBlocked() before any of them
    // called RecordFailure(), because the check and the write were two
    // separate, unsynchronized steps. This spins up real concurrent
    // reservations against the very same keys and proves that at most
    // MaxFailuresPerWindow of them can ever succeed -- not that all of them
    // do, which is what the buggy check-then-act version allowed.
    [Fact]
    public async Task Concurrent_reservations_never_exceed_the_limit()
    {
        var throttle = new LoginThrottle(TimeProvider.System, Options.Create(new AuthOptions { MaxFailuresPerWindow = 5, FailureWindowSeconds = 900 }));
        const int attempts = 40;

        var results = await Task.WhenAll(Enumerable.Range(0, attempts)
            .Select(_ => Task.Run(() => throttle.TryReserve("ip:1", "email:a"))));

        var succeeded = results.Count(ok => ok);
        // Exactly 5, not merely "at most 5": none of the 5 successful
        // reservations are ever completed/released in this test, so once
        // the 5th lands, every later concurrent call must see the limit
        // reached and be blocked. `<=` would also pass a fix that was too
        // aggressive and blocked everything; the property under test is
        // "exactly N get through".
        Assert.Equal(5, succeeded);
    }

    // Important-fix regression (unbounded growth): stresses the exact
    // mechanism that makes eviction safe -- many real threads concurrently
    // reserving/completing the SAME key, half of them "succeeding" (which
    // can retire and evict the entry once it is idle) while others are
    // concurrently racing to reserve it again. If the retire-check-on-lock
    // safeguard in LockEntry were missing (i.e. if eviction just did a bare
    // dictionary removal), a concurrently racing writer could land on an
    // orphaned entry and its failure would be silently lost. With a very
    // high limit every reservation succeeds, so the final recorded-failure
    // count must equal exactly the number of calls that completed as
    // "failed" -- proving none were lost to a race with a concurrent
    // eviction.
    [Fact]
    public async Task Concurrent_churn_across_evictions_does_not_lose_recorded_failures()
    {
        const int attempts = 400;
        var throttle = new LoginThrottle(TimeProvider.System,
            Options.Create(new AuthOptions { MaxFailuresPerWindow = attempts * 2, FailureWindowSeconds = 900 }));

        await Task.WhenAll(Enumerable.Range(0, attempts).Select(i => Task.Run(() =>
        {
            Assert.True(throttle.TryReserve("k"));
            throttle.Complete(["k"], failed: i % 2 == 0);
        })));

        var entries = (System.Collections.IDictionary)
            typeof(LoginThrottle).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(throttle)!;
        Assert.True(entries.Contains("k"), "the entry should still exist -- it holds real recorded failures, so it must not have been evicted");
        var failures = (System.Collections.ICollection)entries["k"]!.GetType()
            .GetField("Failures", BindingFlags.Public | BindingFlags.Instance)!.GetValue(entries["k"])!;
        Assert.Equal(attempts / 2, failures.Count);
    }

    // Important-fix regression (unbounded growth), straightforward
    // sequential check as permitted: once a key's recorded failures expire
    // and it has no in-flight reservations, its entry must actually be
    // freed from the dictionary -- not left sitting there forever, which is
    // what the previous design deliberately did to avoid Task 2's
    // lost-update race.
    [Fact]
    public void Idle_entries_are_evicted_once_their_failures_expire()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++)
            Fail(throttle, "k");
        Assert.False(Reserved(throttle, "k"));

        _time.Advance(TimeSpan.FromSeconds(61));

        // The failures have expired: this reservation succeeds, and
        // completing it as a success (no failure recorded) leaves the entry
        // idle and empty, which must retire and evict it.
        Assert.True(Reserved(throttle, "k"));

        var entries = (System.Collections.IDictionary)
            typeof(LoginThrottle).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(throttle)!;
        Assert.False(entries.Contains("k"));
    }

    // Important-fix regression (unbounded growth), the case the eviction
    // test above does NOT cover: a key that fails once and is then NEVER
    // touched again (the common case -- most one-off failed logins don't
    // come back). ReleaseEntry alone can't evict that, because nothing
    // ever calls it for that key again. The opportunistic sweep is what
    // reaches it: triggered here from an unrelated TryReserve call, well
    // after the failure window has passed.
    [Fact]
    public void Sweep_evicts_keys_whose_last_event_was_a_failure_nobody_revisits()
    {
        var throttle = Throttle();
        for (var i = 0; i < 8; i++)
            Fail(throttle, $"one-off:{i}");

        _time.Advance(TimeSpan.FromSeconds(61));

        // A single, otherwise unrelated reservation is enough to trigger
        // the opportunistic sweep (gated so it runs at most once per
        // FailureWindowSeconds) -- none of the 8 one-off keys above are
        // touched directly.
        Assert.True(Reserved(throttle, "unrelated"));

        var entries = (System.Collections.IDictionary)
            typeof(LoginThrottle).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(throttle)!;
        Assert.Empty(entries);
    }

    // Important-fix regression (unrelated exceptions must not count as
    // failures): mirrors the AuthEndpoints.LoginAsync contract directly at
    // the throttle level. A reservation completed with failed=false must
    // never be recorded as a failure -- whether that's because the login
    // actually succeeded, or because something aborted the attempt before
    // a verdict was ever reached (a DB error, a cancelled request, etc).
    // From the throttle's point of view there is no third option, and
    // there must not be one: the endpoint's "failed starts false, flips to
    // true only in the !valid branch" fix is only safe because Complete
    // treats every non-failed completion identically.
    [Fact]
    public void Completing_an_inconclusive_reservation_does_not_count_as_a_failure()
    {
        var throttle = Throttle(); // Max = 3

        // Three "inconclusive" attempts, as if a DB outage aborted each one
        // before the password was ever checked.
        for (var i = 0; i < 3; i++)
        {
            Assert.True(throttle.TryReserve("email:a"));
            throttle.Complete(["email:a"], failed: false);
        }

        // None of those counted: it still takes exactly 3 REAL failures
        // (not 0, and not fewer) to reach the limit of 3 -- proving the
        // three inconclusive completions above left the recorded-failure
        // count untouched.
        Fail(throttle, "email:a");
        Assert.True(Reserved(throttle, "email:a"));
        Fail(throttle, "email:a");
        Assert.True(Reserved(throttle, "email:a"));
        Fail(throttle, "email:a");
        Assert.False(Reserved(throttle, "email:a"));
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
