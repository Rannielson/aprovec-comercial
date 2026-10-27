using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

public sealed class FakeHinovaClient : IHinovaClient
{
    public Func<DateOnly, int, int, HinovaTarifaPagina>? TarifaResponse { get; set; }
    public List<HinovaTarifaAlteracao> Tarifas { get; } = [];
    public Task<HinovaTarifaPagina> ListarAlteracoesTarifaAsync(string tokenUsuario, DateOnly dia, int pagina, int tamanho, CancellationToken ct)
    {
        if (TarifaResponse is not null) return Task.FromResult(TarifaResponse(dia, pagina, tamanho));
        var rows = Tarifas.Where(t => DateOnly.FromDateTime(t.DataAlteracao.DateTime) == dia).ToList();
        return Task.FromResult(new HinovaTarifaPagina(rows.Count, rows.Skip(pagina*tamanho).Take(tamanho).ToList()));
    }
    public Dictionary<string, HinovaBoleto> BoletosPorNumero { get; } = new();
    public Task<HinovaBoleto?> BuscarBoletoAsync(string tokenUsuario, string nossoNumero, CancellationToken ct) => Task.FromResult(BoletosPorNumero.GetValueOrDefault(nossoNumero));

    public bool RejectAuth { get; set; }
    public HinovaAuthException? AuthFailure { get; set; }
    public List<HinovaVoluntario> Voluntarios { get; } =
    [
        new HinovaVoluntario("101", "Ana Paula Ferreira", "11122233344", "(31)99111-2233", ["Cooperativa Central"]),
        new HinovaVoluntario("102", "Bruno Costa Lima", "22233344455", "(31)99222-3344", ["Cooperativa Central"]),
    ];

    /// <summary>Set up before a test to make BuscarVoluntarioAsync report an existing match for that
    /// key (CPF or código) -- simulates the CPF-already-in-Hinova collision. Leave empty for the
    /// happy path (not found = null).</summary>
    public Dictionary<string, HinovaVoluntarioDetalhe> BuscarPorChave { get; } = new();
    public Dictionary<string, string> ContatosAssociados { get; } = new();

    public string ProximoCodigoCadastrado { get; set; } = "999";
    public CadastrarVoluntarioRequest? UltimoCadastro { get; private set; }

    /// <summary>One entry per page, consumed in InicioPaginacao order -- set up before a test so
    /// ImportarBoletosAsync's paging loop has something to walk. Empty by default (no boletos).</summary>
    public List<HinovaBoletoPagina> BoletosPaginas { get; } = [];
    public HinovaBoletoPeriodoFiltro? UltimoFiltroBoletos { get; private set; }
    public Func<HinovaBoletoPeriodoFiltro, CancellationToken, Task>? BeforeListBoletos { get; set; }

    public Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct) =>
        AuthFailure is { } error ? throw error : RejectAuth ? throw new HinovaAuthException() : Task.FromResult("token-usuario-fake");

    public Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HinovaVoluntario>>(Voluntarios);

    public Task<HinovaVoluntarioDetalhe?> BuscarVoluntarioAsync(string tokenUsuario, string cpfOuCodigo, CancellationToken ct) =>
        Task.FromResult(BuscarPorChave.GetValueOrDefault(cpfOuCodigo));

    public Task<string?> BuscarContatoAssociadoAsync(string tokenUsuario, string codigoAssociado, CancellationToken ct) =>
        Task.FromResult(ContatosAssociados.GetValueOrDefault(codigoAssociado));

    public Task<string> CadastrarVoluntarioAsync(string tokenUsuario, CadastrarVoluntarioRequest request, CancellationToken ct)
    {
        UltimoCadastro = request;
        return Task.FromResult(ProximoCodigoCadastrado);
    }

    public async Task<HinovaBoletoPagina> ListarBoletosPeriodoAsync(string tokenUsuario, HinovaBoletoPeriodoFiltro filtro, CancellationToken ct)
    {
        if (BeforeListBoletos is not null)
            await BeforeListBoletos(filtro, ct);
        UltimoFiltroBoletos = filtro;
        var pagina = filtro.InicioPaginacao < BoletosPaginas.Count
            ? BoletosPaginas[filtro.InicioPaginacao]
            : new HinovaBoletoPagina(BoletosPaginas.Count, 0, filtro.InicioPaginacao, []);
        return pagina;
    }
}
