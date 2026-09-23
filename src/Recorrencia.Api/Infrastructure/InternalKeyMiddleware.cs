using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Infrastructure;

public sealed class InternalKeyMiddleware(RequestDelegate next, IOptions<InternalOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
            return next(context);

        var provided = context.Request.Headers["X-Internal-Key"].ToString();
        if (!FixedTimeEquals(provided, options.Value.Key))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        return next(context);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
