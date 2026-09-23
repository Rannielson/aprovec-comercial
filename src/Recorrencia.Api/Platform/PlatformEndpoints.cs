using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using Recorrencia.Api.Auth;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Email;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Platform;

public static partial class PlatformEndpoints
{
    public sealed record CreateTenantRequest(string? Slug, string? Name, string? AdminName, string? AdminEmail, string? PlanTemplate, DateOnly? PlanEffectiveFrom);
    public sealed record StatusRequest(string? Status);

    public sealed class PlatformLoginRow
    {
        public Guid AdminId { get; set; }
        public string PasswordHash { get; set; } = "";
    }

    public sealed class TenantRow
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public sealed class AdminRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
    }

    private static readonly string[] ReservedSlugs = ["admin", "www", "api"];

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex SlugPattern();

    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/platform/auth/login", LoginAsync);
        app.MapGet("/platform/me", MeAsync).RequirePlatformAdmin();
        app.MapGet("/platform/tenants", ListTenantsAsync).RequirePlatformAdmin();
        app.MapPost("/platform/tenants", CreateTenantAsync).RequirePlatformAdmin();
        app.MapPost("/platform/tenants/{id:guid}/status", SetStatusAsync).RequirePlatformAdmin();
    }

    // Platform login authenticates through the same app_user-role connection
    // (db.AnonymousAsync) as tenant logins -- the API's single connection
    // pool handles authentication for both hosts. Only already-authenticated
    // platform actions below (me/list/create/status) switch to the
    // superadmin connection via db.AsPlatformAsync.
    private static async Task<IResult> LoginAsync(AuthEndpoints.LoginRequest body, RequestContext request, Database db, PasswordHasher hasher,
        LoginThrottle throttle, IOptions<AuthOptions> options, TimeProvider time, CancellationToken ct)
    {
        if (!request.IsPlatformHost)
            throw new ApiProblem(StatusCodes.Status404NotFound, "tenant.not_found");

        var email = AuthEndpoints.NormalizeEmail(body.Email);
        var password = body.Password ?? "";
        var ipKey = $"ip:{request.ClientIp}";
        var emailKey = $"platform-email:{email}";
        var keys = new[] { ipKey, emailKey };

        // Same atomic reserve-then-complete throttle pattern as the tenant
        // login endpoint (Task 4/AuthEndpoints.LoginAsync).
        if (!throttle.TryReserve(keys))
            throw new ApiProblem(StatusCodes.Status429TooManyRequests, "auth.too_many_attempts");

        var failed = false;
        var succeeded = false;
        try
        {
            var login = await db.AnonymousAsync(tx => tx.QuerySingleOrDefaultAsync<PlatformLoginRow>(
                "select admin_id, password_hash from app.find_platform_login(@email)", new { email }), ct);
            var valid = login is not null ? hasher.Verify(password, login.PasswordHash) : hasher.VerifyAgainstDummy(password);
            if (!valid)
            {
                failed = true;
                throw new ApiProblem(StatusCodes.Status401Unauthorized, "auth.invalid_credentials");
            }

            succeeded = true;
            return Results.Ok(await Sessions.CreateForPlatformAdminAsync(db, request, login!.AdminId, options.Value, time, ct));
        }
        finally
        {
            throttle.Complete(keys, failed);
            if (succeeded)
                throttle.Reset(emailKey);
        }
    }

    private static async Task<IResult> MeAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var id = request.RequirePlatformAdmin();
        var admin = await db.AsPlatformAsync(null, tx => tx.QuerySingleAsync<AdminRow>(
            "select id, email from platform_admins where id = @id", new { id }), ct);
        return Results.Ok(new { admin.Id, admin.Email });
    }

    private static async Task<IResult> ListTenantsAsync(Database db, CancellationToken ct) =>
        Results.Ok(await db.AsPlatformAsync(null, tx => tx.QueryAsync<TenantRow>(
            "select id, slug, name, status, created_at from tenants order by name"), ct));

    private static async Task<IResult> CreateTenantAsync(CreateTenantRequest body, Database db, TenantResolver resolver,
        IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var slug = body.Slug?.Trim() ?? "";
        if (!SlugPattern().IsMatch(slug) || ReservedSlugs.Contains(slug))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "tenant.invalid_slug");
        var name = body.Name?.Trim() ?? "";
        var adminName = body.AdminName?.Trim() ?? "";
        if (name.Length == 0 || adminName.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "tenant.invalid_request");
        var adminEmail = AuthEndpoints.NormalizeEmail(body.AdminEmail);
        if (!MailAddress.TryCreate(adminEmail, out _))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_email");
        if (body.PlanTemplate is not null && body.PlanEffectiveFrom is not { Day: 1 })
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.invalid_effective_from");

        var token = Tokens.New();
        // Superadmin-only: app.provision_tenant is GRANTed to app_superadmin
        // alone, so tenant creation must go through db.AsPlatformAsync.
        var created = await db.AsPlatformAsync(null, async tx =>
        {
            (Guid TenantId, Guid AdminUserId) ids;
            try
            {
                ids = await tx.QuerySingleAsync<(Guid, Guid)>(
                    """
                    select tenant_id, admin_user_id
                      from app.provision_tenant(@slug, @name, @adminName, @adminEmail, @planTemplate, @effectiveFrom::date)
                    """,
                    new { slug, name, adminName, adminEmail, planTemplate = body.PlanTemplate, effectiveFrom = body.PlanEffectiveFrom?.ToString("yyyy-MM-dd") });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "tenant.slug_taken");
            }
            await tx.ExecuteAsync("select set_config('app.tenant_id', @tenant, true)", new { tenant = ids.TenantId.ToString() });
            await tx.ExecuteAsync("select app.create_invite(@admin, @hash, 'convite', @ttl)",
                new { admin = ids.AdminUserId, hash = Tokens.Hash(token), ttl = options.Value.InviteTtlSeconds });
            return ids;
        }, ct);

        // Guard against the slug having been cached as "not found" by a
        // probe before creation, and so the new tenant is resolvable right
        // away rather than possibly waiting out a stale negative cache entry.
        resolver.Invalidate(slug);
        await email.SendAsync(adminEmail, "Sua empresa foi criada",
            $"""
            A empresa {name} foi criada na plataforma.

            Defina sua senha de administrador em: {links.SetPassword(slug, token)}

            O link vale por 72 horas.
            """, ct);
        return Results.Created($"/platform/tenants/{created.TenantId}", new { id = created.TenantId, slug });
    }

    private static async Task<IResult> SetStatusAsync(Guid id, StatusRequest body, Database db, TenantResolver resolver, CancellationToken ct)
    {
        if (body.Status is not ("ativo" or "suspenso"))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "tenant.invalid_request");
        var slug = await db.AsPlatformAsync(null, tx => tx.QuerySingleOrDefaultAsync<string>(
            "update tenants set status = @status where id = @id returning slug", new { status = body.Status, id }), ct)
            ?? throw new ApiProblem(StatusCodes.Status404NotFound, "tenant.not_found");
        // Suspending/reactivating must take effect immediately rather than
        // after up to 5 minutes of TenantResolver's cache TTL.
        resolver.Invalidate(slug);
        return Results.NoContent();
    }
}
