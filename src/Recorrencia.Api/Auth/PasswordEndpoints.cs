using Microsoft.Extensions.Options;
using Recorrencia.Api.Email;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Auth;

public static class PasswordEndpoints
{
    public sealed record SetPasswordRequest(string? Token, string? Password);

    public sealed record ResetRequest(string? Email);

    public static void MapPasswordEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/set-password", SetPasswordAsync);
        app.MapPost("/auth/password-reset", RequestResetAsync);
    }

    private static async Task<IResult> SetPasswordAsync(SetPasswordRequest body, RequestContext request, Database db,
        PasswordHasher hasher, IOptions<AuthOptions> options, TimeProvider time, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        PasswordPolicy.Validate(body.Password);
        var hash = hasher.Hash(body.Password!);
        var userId = await db.AnonymousAsync(tx => tx.ExecuteScalarAsync<Guid>(
            "select app.consume_invite(@tenant, @tokenHash, @hash)",
            new { tenant, tokenHash = Tokens.Hash(body.Token ?? ""), hash }), ct);
        return Results.Ok(await Sessions.CreateForUserAsync(db, request, tenant, userId, options.Value, time, ct));
    }

    private static async Task<IResult> RequestResetAsync(ResetRequest body, RequestContext request, Database db,
        LoginThrottle throttle, IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        // A missing/unparseable client IP must not collapse every such request into one
        // shared "reset-ip:" bucket -- there is no email-based key on this endpoint to fall
        // back on, so simply skip reserving/throttling on the IP dimension for this request
        // rather than letting it share a bucket with unrelated tenants/clients.
        var ipKey = request.ClientIp is { } ip ? $"reset-ip:{ip}" : null;
        var keys = ipKey is not null ? new[] { ipKey } : Array.Empty<string>();

        // Same reserve-then-complete pattern as login (Task 4): every
        // request against this IP consumes a slot up front, regardless of
        // whether the e-mail turns out to exist, so the throttle itself
        // never becomes a side channel for account enumeration.
        if (!throttle.TryReserve(keys))
            throw new ApiProblem(StatusCodes.Status429TooManyRequests, "auth.too_many_attempts");

        try
        {
            var address = AuthEndpoints.NormalizeEmail(body.Email);
            var login = await db.AnonymousAsync(tx => tx.QuerySingleOrDefaultAsync<AuthEndpoints.LoginRow>(
                "select user_id, password_hash, status from app.find_login(@tenant, @address)", new { tenant, address }), ct);

            if (login is { Status: "ativo" })
            {
                var token = Tokens.New();
                await db.InTenantAsync(tenant, null, tx => tx.ExecuteAsync(
                    "select app.create_invite(@userId, @hash, 'redefinicao', @ttl)",
                    new { userId = login.UserId, hash = Tokens.Hash(token), ttl = options.Value.ResetTtlSeconds }), ct);
                await email.SendAsync(address, "Redefinição de senha",
                    $"""
                    Recebemos um pedido para redefinir sua senha.

                    Defina uma nova senha em: {links.SetPassword(request.TenantSlug!, token)}

                    O link vale por 1 hora. Se você não fez esse pedido, ignore este e-mail.
                    """, ct);
            }
        }
        finally
        {
            // Always counted as a "failure" for throttling purposes: this
            // endpoint has no real success/failure distinction to make
            // (unlike login), and every call — hit or miss on the e-mail —
            // should count equally against the per-IP budget.
            throttle.Complete(keys, true);
        }

        return Results.Accepted();
    }
}
