using Microsoft.Extensions.Options;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Auth;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string? Email, string? Password);

    public sealed class LoginRow
    {
        public Guid UserId { get; set; }
        public string? PasswordHash { get; set; }
        public string Status { get; set; } = "";
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", LoginAsync);
        app.MapPost("/auth/logout", LogoutAsync);
    }

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static async Task<IResult> LoginAsync(LoginRequest body, RequestContext request, Database db, PasswordHasher hasher,
        LoginThrottle throttle, IOptions<AuthOptions> options, TimeProvider time, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var email = NormalizeEmail(body.Email);
        var password = body.Password ?? "";
        // A missing/unparseable client IP must NOT collapse into a single shared "ip:"
        // bucket -- that would let failed attempts from one tenant/client throttle a
        // completely unrelated login elsewhere. Only include the IP key when there
        // actually is one; the email-based key alone still throttles this attempt.
        var ipKey = request.ClientIp is { } ip ? $"ip:{ip}" : null;
        var emailKey = $"email:{tenant}:{email}";
        var keys = ipKey is not null ? new[] { ipKey, emailKey } : new[] { emailKey };

        // Atomic reserve-then-complete: TryReserve checks AND reserves a
        // slot on every key in one step, so no number of concurrent requests
        // can all pass the check before any of them is accounted for. If the
        // limit is already reached, nothing is reserved and we reject
        // immediately, same as before.
        if (!throttle.TryReserve(keys))
            throw new ApiProblem(StatusCodes.Status429TooManyRequests, "auth.too_many_attempts");

        // `failed` starts false and is flipped to true ONLY in the actual
        // wrong-password/unknown-user branch below. An unrelated exception
        // before credentials are even checked (a DB connectivity error, a
        // cancelled request, anything) must NOT count as a login failure --
        // no password was checked, so an attacker forcing exceptions gains
        // nothing, while counting it would let a transient outage lock out
        // a shared office IP for up to the full window after the database
        // recovers. `succeeded` is separate from `!failed`, so an
        // inconclusive attempt (an exception, `failed` still false) does
        // not get treated as a real success either.
        var failed = false;
        var succeeded = false;
        try
        {
            var login = await db.AnonymousAsync(tx => tx.QuerySingleOrDefaultAsync<LoginRow>(
                "select user_id, password_hash, status from app.find_login(@tenant, @email)", new { tenant, email }), ct);

            var valid = login is { Status: "ativo", PasswordHash: not null }
                ? hasher.Verify(password, login.PasswordHash)
                : hasher.VerifyAgainstDummy(password);
            if (!valid)
            {
                failed = true;
                throw new ApiProblem(StatusCodes.Status401Unauthorized, "auth.invalid_credentials");
            }

            succeeded = true;
            var session = await Sessions.CreateForUserAsync(db, request, tenant, login!.UserId, options.Value, time, ct);

            if (hasher.NeedsRehash(login.PasswordHash!))
            {
                var newHash = hasher.Hash(password);
                await db.InTenantAsync(tenant, login.UserId,
                    tx => tx.ExecuteAsync("select app.rehash_own_password(@newHash)", new { newHash }), ct);
            }

            return Results.Ok(session);
        }
        finally
        {
            // Always releases the reservation made above, on every exit path
            // (success, invalid credentials, or an unexpected exception).
            throttle.Complete(keys, failed);
            if (succeeded)
                throttle.Reset(emailKey);
        }
    }

    private static async Task<IResult> LogoutAsync(RequestContext request, Database db, CancellationToken ct)
    {
        if (request.SessionTokenHash is { } hash)
            await db.AnonymousAsync(tx => tx.ExecuteAsync("select app.revoke_session(@hash)", new { hash }), ct);
        return Results.NoContent();
    }
}
