using Recorrencia.Api.Authorization;
using Recorrencia.Api.Email;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Convites;

public static class ConviteLinkEndpoints
{
    public sealed record ConviteLinkResponse(string Url);
    public sealed record ConviteLinkPublicoResponse(string IndicadorNome, string TenantNome);

    private sealed class LinkRow
    {
        public string Token { get; set; } = "";
    }

    private sealed class ResolvedLinkRow
    {
        public Guid UserId { get; set; }
        public string Nome { get; set; } = "";
    }

    public static void MapConviteLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/convite-links/me", MeAsync).RequireUser();
        app.MapGet("/convite-links/{token}", PublicoAsync);
    }

    private static async Task<IResult> MeAsync(RequestContext request, Database db, LinkBuilder links, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();

        var token = await db.InTenantAsync(tenant, user, async tx =>
        {
            var existing = await tx.QuerySingleOrDefaultAsync<LinkRow>(
                "select token from convite_links where tenant_id = @tenant and user_id = @user", new { tenant, user });
            if (existing is not null)
                return existing.Token;

            var hasHinova = await tx.ExecuteScalarAsync<bool>(
                "select exists(select 1 from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @user)",
                new { tenant, user });
            if (!hasHinova)
                throw new ApiProblem(StatusCodes.Status409Conflict, "convite.indicador_sem_hinova");

            var novo = Tokens.New();
            await tx.ExecuteAsync("insert into convite_links (tenant_id, user_id, token) values (@tenant, @user, @novo)",
                new { tenant, user, novo });
            return novo;
        }, ct);

        return Results.Ok(new ConviteLinkResponse(links.Indicar(request.TenantSlug!, token)));
    }

    private static async Task<IResult> PublicoAsync(string token, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var resolved = await db.InTenantAsync(tenant, null, tx => tx.QuerySingleOrDefaultAsync<ResolvedLinkRow>(
            "select * from app.resolve_convite_link(@tenant, @token)", new { tenant, token }), ct);
        if (resolved is null)
            throw new ApiProblem(StatusCodes.Status404NotFound, "convite.link_invalido");

        return Results.Ok(new ConviteLinkPublicoResponse(resolved.Nome, request.TenantName!));
    }
}
