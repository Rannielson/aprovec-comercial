using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Integracoes;

/// <summary>
/// Decrypt-then-authenticate against the real Hinova API, shared by every endpoint that needs a
/// tokenUsuario. The production client shares authentication across requests and refreshes
/// rejected tokens without allowing concurrent authentications to invalidate an active sync.
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
        catch (HinovaAuthException ex)
        {
            throw ToProblem(ex);
        }
    }

    public static ApiProblem ToProblem(HinovaAuthException exception, int invalidCredentialsStatus = StatusCodes.Status400BadRequest) =>
        new(exception.Code == "hinova.credenciais_invalidas" ? invalidCredentialsStatus : StatusCodes.Status503ServiceUnavailable,
            exception.Code, exception.RetryAfter);

    private sealed class CredenciaisRow
    {
        public byte[] UsuarioEnc { get; set; } = [];
        public byte[] SenhaEnc { get; set; } = [];
        public byte[] TokenSgaEnc { get; set; } = [];
    }
}
