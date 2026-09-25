using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Integracoes;

/// <summary>
/// Decrypt-then-authenticate against the real Hinova API, shared by every endpoint that needs a
/// tokenUsuario. Re-authenticates every call (no caching) -- same decision as the original
/// ListarVoluntariosAsync call site this was extracted from.
/// </summary>
public static class HinovaAuth
{
    public static async Task<string> GetTokenUsuarioAsync(Database db, Guid tenant, Guid user, AesGcmCipher cipher, IHinovaClient hinova, CancellationToken ct)
    {
        var credenciais = await db.InTenantAsync(tenant, user, tx => tx.QuerySingleOrDefaultAsync<CredenciaisRow>(
            "select usuario_enc, senha_enc, token_sga_enc from hinova_credenciais where tenant_id = @tenant",
            new { tenant }), ct);
        if (credenciais is null)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.nao_configurado");

        var usuario = cipher.Decrypt(credenciais.UsuarioEnc);
        var senha = cipher.Decrypt(credenciais.SenhaEnc);
        var tokenSga = cipher.Decrypt(credenciais.TokenSgaEnc);

        try
        {
            return await hinova.AutenticarAsync(usuario, senha, tokenSga, ct);
        }
        catch (HinovaAuthException)
        {
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.credenciais_invalidas");
        }
    }

    private sealed class CredenciaisRow
    {
        public byte[] UsuarioEnc { get; set; } = [];
        public byte[] SenhaEnc { get; set; } = [];
        public byte[] TokenSgaEnc { get; set; } = [];
    }
}
