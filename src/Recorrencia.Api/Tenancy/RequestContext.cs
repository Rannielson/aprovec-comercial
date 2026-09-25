using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Tenancy;

public sealed class RequestContext
{
    public bool IsPlatformHost { get; set; }
    public Guid? TenantId { get; set; }
    public string? TenantSlug { get; set; }
    public string? TenantName { get; set; }
    public Guid? UserId { get; set; }
    public Guid? PlatformAdminId { get; set; }
    public byte[]? SessionTokenHash { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }

    public Guid RequireTenant() =>
        TenantId ?? throw new ApiProblem(StatusCodes.Status404NotFound, "tenant.not_found");

    public Guid RequireUser() =>
        UserId ?? throw new ApiProblem(StatusCodes.Status401Unauthorized, "auth.unauthenticated");

    public Guid RequirePlatformAdmin() =>
        IsPlatformHost && PlatformAdminId is { } id
            ? id
            : throw new ApiProblem(StatusCodes.Status401Unauthorized, "auth.unauthenticated");
}
