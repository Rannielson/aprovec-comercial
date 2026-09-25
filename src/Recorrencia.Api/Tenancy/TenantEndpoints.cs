namespace Recorrencia.Api.Tenancy;

public static class TenantEndpoints
{
    public sealed record TenantResponse(string Slug, string Name, bool Platform);

    public static void MapTenantEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/tenant", (RequestContext request) => request.IsPlatformHost
            ? Results.Ok(new TenantResponse(HostContextMiddleware.PlatformHost, "Administração da plataforma", true))
            : Results.Ok(new TenantResponse(request.TenantSlug!, request.TenantName!, false)));
}
