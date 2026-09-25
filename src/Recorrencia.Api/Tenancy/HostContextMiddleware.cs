using System.Net;
using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Tenancy;

public sealed class HostContextMiddleware(RequestDelegate next)
{
    public const string PlatformHost = "admin";

    public async Task InvokeAsync(HttpContext context, RequestContext request, TenantResolver resolver)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        request.ClientIp = IPAddress.TryParse(context.Request.Headers["X-Client-Ip"].ToString(), out var ip) ? ip.ToString() : null;
        var agent = context.Request.Headers["X-Client-User-Agent"].ToString();
        request.UserAgent = agent.Length == 0 ? null : agent[..Math.Min(agent.Length, 512)];

        var host = context.Request.Headers["X-Tenant-Host"].ToString().Trim().ToLowerInvariant();
        if (host == PlatformHost)
        {
            request.IsPlatformHost = true;
        }
        else if (host.Length > 0 && await resolver.ResolveActiveAsync(host, context.RequestAborted) is { } tenant)
        {
            request.TenantId = tenant.Id;
            request.TenantSlug = host;
            request.TenantName = tenant.Name;
        }
        else
        {
            throw new ApiProblem(StatusCodes.Status404NotFound, "tenant.not_found");
        }

        await next(context);
    }
}
