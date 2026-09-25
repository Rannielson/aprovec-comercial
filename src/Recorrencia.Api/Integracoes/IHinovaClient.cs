namespace Recorrencia.Api.Integracoes;

public interface IHinovaClient
{
    Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct);
    Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct);
    Task<HinovaVoluntarioDetalhe?> BuscarVoluntarioAsync(string tokenUsuario, string cpfOuCodigo, CancellationToken ct);
    Task<string> CadastrarVoluntarioAsync(string tokenUsuario, CadastrarVoluntarioRequest request, CancellationToken ct);
}

public sealed record HinovaVoluntario(string Codigo, string Nome, string Cpf, string? Telefone, IReadOnlyList<string> Cooperativas);

/// <summary>Result of "Buscar Voluntário" -- unlike <see cref="HinovaVoluntario"/>, carries cooperativa
/// CODES (needed by "Cadastrar Voluntário"), not cooperativa names.</summary>
public sealed record HinovaVoluntarioDetalhe(string Codigo, string Nome, string Cpf, IReadOnlyList<string> CooperativaCodigos);

public sealed record CadastrarVoluntarioRequest(
    string Nome, string Cpf, string? Celular, string? Email,
    string Logradouro, string Numero, string? Complemento, string Bairro, string Cidade, string Estado, string Cep,
    IReadOnlyList<string> CooperativaCodigos, string? CodigoVoluntarioVinculado, string? Obs);

public sealed class HinovaAuthException() : Exception("Falha ao autenticar na Hinova.");
