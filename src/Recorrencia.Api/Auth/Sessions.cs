using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Auth;

public sealed record SessionResponse(string Token, DateTimeOffset AbsoluteExpiresAt);

public static class Sessions
{
    public static async Task<SessionResponse> CreateForUserAsync(Database db, RequestContext request, Guid tenantId, Guid userId,
        AuthOptions options, TimeProvider time, CancellationToken ct)
    {
        var token = Tokens.New();
        await db.AnonymousAsync(tx => tx.ExecuteScalarAsync<Guid>(
            "select app.create_session(@hash, @tenantId, @userId, @idle, @absolute, @ip, @agent)",
            new
            {
                hash = Tokens.Hash(token),
                tenantId,
                userId,
                idle = options.IdleTimeoutSeconds,
                absolute = options.AbsoluteTimeoutSeconds,
                ip = request.ClientIp,
                agent = request.UserAgent,
            }), ct);
        return new SessionResponse(token, time.GetUtcNow().AddSeconds(options.AbsoluteTimeoutSeconds));
    }

    public static async Task<SessionResponse> CreateForPlatformAdminAsync(Database db, RequestContext request, Guid adminId,
        AuthOptions options, TimeProvider time, CancellationToken ct)
    {
        var token = Tokens.New();
        await db.AnonymousAsync(tx => tx.ExecuteScalarAsync<Guid>(
            "select app.create_platform_session(@hash, @adminId, @idle, @absolute, @ip, @agent)",
            new
            {
                hash = Tokens.Hash(token),
                adminId,
                idle = options.IdleTimeoutSeconds,
                absolute = options.AbsoluteTimeoutSeconds,
                ip = request.ClientIp,
                agent = request.UserAgent,
            }), ct);
        return new SessionResponse(token, time.GetUtcNow().AddSeconds(options.AbsoluteTimeoutSeconds));
    }
}
