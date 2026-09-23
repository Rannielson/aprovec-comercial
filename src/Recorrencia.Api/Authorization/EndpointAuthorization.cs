using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Authorization;

public static class EndpointAuthorization
{
    public static RouteHandlerBuilder RequireUser(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.RequestServices.GetRequiredService<RequestContext>().RequireUser();
            return await next(context);
        });

    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var permissions = await context.HttpContext.RequestServices
                .GetRequiredService<CurrentPermissions>()
                .GetAsync(context.HttpContext.RequestAborted);
            if (!permissions.Has(permission))
                throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");
            return await next(context);
        });

    public static RouteHandlerBuilder RequirePlatformAdmin(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.RequestServices.GetRequiredService<RequestContext>().RequirePlatformAdmin();
            return await next(context);
        });
}
