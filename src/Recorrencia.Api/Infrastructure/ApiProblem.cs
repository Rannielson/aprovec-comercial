using System.Diagnostics;
using System.Text.Json;
using Npgsql;

namespace Recorrencia.Api.Infrastructure;

public sealed class ApiProblem(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    private static readonly Dictionary<string, int> DatabaseCodes = new()
    {
        ["hierarchy.cycle"] = StatusCodes.Status409Conflict,
        ["users.tenant_immutable"] = StatusCodes.Status409Conflict,
        ["plan.active_immutable"] = StatusCodes.Status409Conflict,
        ["plan.empty"] = StatusCodes.Status400BadRequest,
        ["plan.template_not_found"] = StatusCodes.Status400BadRequest,
        ["fechamento.invalid_transition"] = StatusCodes.Status409Conflict,
        ["fechamento.immutable"] = StatusCodes.Status409Conflict,
        ["invite.invalid"] = StatusCodes.Status400BadRequest,
        ["invite.invalid_status"] = StatusCodes.Status409Conflict,
        ["invite.user_not_found"] = StatusCodes.Status404NotFound,
        ["role.scope_required"] = StatusCodes.Status400BadRequest,
        ["role.scope_not_allowed"] = StatusCodes.Status400BadRequest,
        ["commission.invalid_competencia"] = StatusCodes.Status400BadRequest,
        ["auth.invalid_user"] = StatusCodes.Status401Unauthorized,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiProblem problem)
        {
            await WriteAsync(context, problem.Status, problem.Code);
        }
        catch (BadHttpRequestException)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, "request.invalid");
        }
        catch (PostgresException pg) when (DatabaseCodes.TryGetValue(pg.MessageText, out var status))
        {
            await WriteAsync(context, status, pg.MessageText);
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            await WriteAsync(context, StatusCodes.Status403Forbidden, "auth.forbidden");
        }
        catch (Exception ex)
        {
            var correlationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            logger.LogError(ex, "Erro inesperado. Correlação {CorrelationId}", correlationId);
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "internal.error", correlationId);
        }
    }

    private static Task WriteAsync(HttpContext context, int status, string code, string? correlationId = null)
    {
        if (context.Response.HasStarted)
            return Task.CompletedTask;
        context.Response.Clear();
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            new ProblemBody("about:blank", code, status, code, correlationId),
            (JsonSerializerOptions?)null,
            "application/problem+json");
    }

    private sealed record ProblemBody(string Type, string Title, int Status, string Code, string? CorrelationId);
}
