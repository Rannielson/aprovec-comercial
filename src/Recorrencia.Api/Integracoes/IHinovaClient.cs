namespace Recorrencia.Api.Integracoes;

public interface IHinovaClient
{
    Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct);
    Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct);
}

public sealed record HinovaVoluntario(string Codigo, string Nome, string Cpf, string? Telefone, IReadOnlyList<string> Cooperativas);

public sealed class HinovaAuthException() : Exception("Falha ao autenticar na Hinova.");
