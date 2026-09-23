using Microsoft.Extensions.Options;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Auth;

public sealed class SessionMiddleware(RequestDelegate next, IOptions<AuthOptions> options)
{
    public async Task InvokeAsync(HttpContext context, RequestContext request, Database db)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var hash = Tokens.Hash(header["Bearer ".Length..].Trim());
            var session = await db.AnonymousAsync(tx => tx.QuerySingleOrDefaultAsync<SessionRow>(
                "select session_id, kind, tenant_id, user_id, platform_admin_id from app.resolve_session(@hash, @idle)",
                new { hash, idle = options.Value.IdleTimeoutSeconds }), context.RequestAborted);

            if (session is not null)
            {
                var platformSession = session.Kind == "platform";
                if (request.IsPlatformHost != platformSession || (!platformSession && session.TenantId != request.TenantId))
                    throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.tenant_mismatch");

                request.UserId = session.UserId;
                request.PlatformAdminId = session.PlatformAdminId;
                request.SessionTokenHash = hash;
            }
        }

        await next(context);
    }

    public sealed class SessionRow
    {
        public Guid SessionId { get; set; }
        public string Kind { get; set; } = "";
        public Guid? TenantId { get; set; }
        public Guid? UserId { get; set; }
        public Guid? PlatformAdminId { get; set; }
    }
}
