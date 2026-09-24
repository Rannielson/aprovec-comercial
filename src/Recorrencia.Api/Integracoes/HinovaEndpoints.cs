using Npgsql;
using static Recorrencia.Api.Audit.Audit;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Integracoes;

public static class HinovaEndpoints
{
    public sealed record CredenciaisStatusResponse(bool Configurado, DateTimeOffset? AtualizadoEm, string? AtualizadoPor);
    public sealed record SalvarCredenciaisRequest(string Usuario, string Senha, string TokenSga);

    private sealed class StatusRow
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public string UpdatedByName { get; set; } = "";
    }

    private sealed class CredenciaisRow
    {
        public byte[] UsuarioEnc { get; set; } = [];
        public byte[] SenhaEnc { get; set; } = [];
        public byte[] TokenSgaEnc { get; set; } = [];
    }

    public static void MapHinovaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/integracoes/hinova/credenciais", ObterCredenciaisStatusAsync).RequirePermission("integracoes.gerenciar");
        app.MapPut("/integracoes/hinova/credenciais", SalvarCredenciaisAsync).RequirePermission("integracoes.gerenciar");
    }

    private static async Task<IResult> ObterCredenciaisStatusAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var row = await db.InTenantAsync(tenant, user, tx => tx.QuerySingleOrDefaultAsync<StatusRow>(
            """
            select hc.updated_at, u.name as updated_by_name
              from hinova_credenciais hc join users u on u.id = hc.updated_by
             where hc.tenant_id = @tenant
            """,
            new { tenant }), ct);

        return Results.Ok(row is null
            ? new CredenciaisStatusResponse(false, null, null)
            : new CredenciaisStatusResponse(true, row.UpdatedAt, row.UpdatedByName));
    }

    private static async Task<IResult> SalvarCredenciaisAsync(SalvarCredenciaisRequest body, RequestContext request, Database db,
        IHinovaClient hinova, AesGcmCipher cipher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        if (string.IsNullOrWhiteSpace(body.Usuario) || string.IsNullOrWhiteSpace(body.Senha) || string.IsNullOrWhiteSpace(body.TokenSga))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "request.invalid");

        try
        {
            await hinova.AutenticarAsync(body.Usuario, body.Senha, body.TokenSga, ct);
        }
        catch (HinovaAuthException)
        {
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.credenciais_invalidas");
        }

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            await tx.ExecuteAsync(
                """
                insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
                values (@tenant, @usuario, @senha, @tokenSga, @actor)
                on conflict (tenant_id) do update set
                  usuario_enc = excluded.usuario_enc, senha_enc = excluded.senha_enc,
                  token_sga_enc = excluded.token_sga_enc, updated_at = now(), updated_by = excluded.updated_by
                """,
                new
                {
                    tenant, actor,
                    usuario = cipher.Encrypt(body.Usuario),
                    senha = cipher.Encrypt(body.Senha),
                    tokenSga = cipher.Encrypt(body.TokenSga),
                });
            await WriteAsync(tx, tenant, actor, "hinova.credenciais.salvar", "hinova_credenciais", null, null, new { body.Usuario });
            return 0;
        }, ct);

        return Results.NoContent();
    }
}
