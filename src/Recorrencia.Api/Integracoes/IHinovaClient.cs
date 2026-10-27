using System.Net;

namespace Recorrencia.Api.Integracoes;

public interface IHinovaClient
{
    Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct);
    Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct);
    Task<HinovaVoluntarioDetalhe?> BuscarVoluntarioAsync(string tokenUsuario, string cpfOuCodigo, CancellationToken ct);
    Task<string?> BuscarContatoAssociadoAsync(string tokenUsuario, string codigoAssociado, CancellationToken ct);
    Task<string> CadastrarVoluntarioAsync(string tokenUsuario, CadastrarVoluntarioRequest request, CancellationToken ct);
    Task<HinovaBoleto?> BuscarBoletoAsync(string tokenUsuario, string nossoNumero, CancellationToken ct);
    Task<HinovaBoletoPagina> ListarBoletosPeriodoAsync(string tokenUsuario, HinovaBoletoPeriodoFiltro filtro, CancellationToken ct);
}

/// <summary>The full import leaves CodigoVoluntario null. Targeted bonus previews may request
/// a vendor filter to limit pagination, but the response must still be checked per vehicle:
/// Hinova sometimes returns boletos whose vehicles belong to other vendors.</summary>
public sealed record HinovaBoletoPeriodoFiltro(DateOnly DataPagamentoInicial, DateOnly DataPagamentoFinal, int QuantidadePorPagina, int InicioPaginacao,
    string? CodigoVoluntario = null, bool FiltrarPorVencimento = false);

public sealed record HinovaVeiculoBoleto(string CodigoVeiculo, string? CodigoVoluntario, string? Placa,
    DateOnly? DataInicioContratoVeiculo = null);

public sealed record HinovaBoleto(
    string NossoNumero, decimal ValorBoleto, decimal ValorPagamento, string CodigoAssociado, string NomeAssociado,
    DateOnly? Vencimento, DateOnly? DataPagamento, IReadOnlyList<HinovaVeiculoBoleto> Veiculos,
    string? CodigoTipoBoleto = null, string? TipoBoleto = null, string? SituacaoBoleto = null);

/// <summary>One page of "Boleto - Listar por período". <see cref="PaginaCorrente"/> is 0-based
/// (matches the request's own inicio_paginacao) -- the last page is reached once it's >= NumeroPaginas - 1.</summary>
public sealed record HinovaBoletoPagina(int NumeroPaginas, int TotalRegistros, int PaginaCorrente, IReadOnlyList<HinovaBoleto> Boletos);

public sealed record HinovaVoluntario(string Codigo, string Nome, string Cpf, string? Telefone, IReadOnlyList<string> Cooperativas);

/// <summary>Result of "Buscar Voluntário" -- unlike <see cref="HinovaVoluntario"/>, carries cooperativa
/// CODES (needed by "Cadastrar Voluntário"), not cooperativa names.</summary>
public sealed record HinovaVoluntarioDetalhe(string Codigo, string Nome, string Cpf, IReadOnlyList<string> CooperativaCodigos, string? Email = null);

public sealed record CadastrarVoluntarioRequest(
    string Nome, string Cpf, string? Celular, string? Email,
    string Logradouro, string Numero, string? Complemento, string Bairro, string Cidade, string Estado, string Cep,
    IReadOnlyList<string> CooperativaCodigos, string? CodigoVoluntarioVinculado, string? Obs);

public sealed class HinovaAuthException(HttpStatusCode statusCode = HttpStatusCode.Unauthorized,
    bool extractionLimit = false, TimeSpan? retryAfter = null)
    : Exception($"Hinova recusou a requisição (HTTP {(int)statusCode}).")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public bool IsExtractionLimit { get; } = extractionLimit || statusCode == HttpStatusCode.TooManyRequests;
    public TimeSpan? RetryAfter { get; } = extractionLimit || statusCode == HttpStatusCode.TooManyRequests
        ? retryAfter is { Ticks: > 0 } ? retryAfter : TimeSpan.FromMinutes(10) : null;
    public string Code => IsExtractionLimit ? "hinova.limite_extracao" : StatusCode switch
    {
        HttpStatusCode.Unauthorized => "hinova.credenciais_invalidas",
        HttpStatusCode.Forbidden => "hinova.acesso_recusado",
        _ => "hinova.autenticacao_indisponivel"
    };

    public static HinovaAuthException FromResponse(HttpStatusCode status, string body, TimeSpan? retryAfter = null)
    {
        // Keep the vendor response out of exceptions/logs: it may contain sensitive fields.
        var limit = body.Contains("excesso", StringComparison.OrdinalIgnoreCase)
            && (body.Contains("extra", StringComparison.OrdinalIgnoreCase)
                || body.Contains("requisi", StringComparison.OrdinalIgnoreCase));
        return new HinovaAuthException(status, limit, retryAfter);
    }
}
