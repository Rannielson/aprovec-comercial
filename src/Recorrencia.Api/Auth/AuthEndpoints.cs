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
        var ipKey = $"ip:{request.ClientIp}";
        var emailKey = $"email:{tenant}:{email}";
        if (throttle.IsBlocked(ipKey, emailKey))
            throw new ApiProblem(StatusCodes.Status429TooManyRequests, "auth.too_many_attempts");

        var login = await db.AnonymousAsync(tx => tx.QuerySingleOrDefaultAsync<LoginRow>(
            "select user_id, password_hash, status from app.find_login(@tenant, @email)", new { tenant, email }), ct);

        var valid = login is { Status: "ativo", PasswordHash: not null }
            ? hasher.Verify(password, login.PasswordHash)
            : hasher.VerifyAgainstDummy(password);
        if (!valid)
        {
            throttle.RecordFailure(ipKey, emailKey);
            throw new ApiProblem(StatusCodes.Status401Unauthorized, "auth.invalid_credentials");
        }

        throttle.Reset(emailKey);
        var session = await Sessions.CreateForUserAsync(db, request, tenant, login!.UserId, options.Value, time, ct);

        if (hasher.NeedsRehash(login.PasswordHash!))
        {
            var newHash = hasher.Hash(password);
            await db.InTenantAsync(tenant, login.UserId,
                tx => tx.ExecuteAsync("select app.rehash_own_password(@newHash)", new { newHash }), ct);
        }

        return Results.Ok(session);
    }

    private static async Task<IResult> LogoutAsync(RequestContext request, Database db, CancellationToken ct)
    {
        if (request.SessionTokenHash is { } hash)
            await db.AnonymousAsync(tx => tx.ExecuteAsync("select app.revoke_session(@hash)", new { hash }), ct);
        return Results.NoContent();
    }
}
