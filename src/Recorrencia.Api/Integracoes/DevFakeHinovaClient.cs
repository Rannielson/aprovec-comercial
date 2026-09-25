namespace Recorrencia.Api.Integracoes;

/// <summary>
/// Deterministic stand-in for the real Hinova API, used when `Hinova:UseFake` is true
/// (local docker compose and E2E) so nothing outside this process ever calls the real
/// third-party service. Mirrors DevSeed's role for boletos.
/// </summary>
public sealed class DevFakeHinovaClient : IHinovaClient
{
    public static readonly IReadOnlyList<HinovaVoluntario> Voluntarios =
    [
        new HinovaVoluntario("101", "Ana Paula Ferreira", "11122233344", "(31)99111-2233", ["Cooperativa Central"]),
        new HinovaVoluntario("102", "Bruno Costa Lima", "22233344455", "(31)99222-3344", ["Cooperativa Central"]),
        new HinovaVoluntario("103", "Carla Souza Mendes", "33344455566", "(31)99333-4455", ["Cooperativa Norte"]),
    ];

    public Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(senha) || string.IsNullOrWhiteSpace(tokenSga)
            ? throw new HinovaAuthException()
            : Task.FromResult("token-usuario-dev-fake");

    public Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct) =>
        Task.FromResult(Voluntarios);
}
