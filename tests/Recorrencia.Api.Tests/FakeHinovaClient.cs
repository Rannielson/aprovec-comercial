using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

public sealed class FakeHinovaClient : IHinovaClient
{
    public bool RejectAuth { get; set; }
    public List<HinovaVoluntario> Voluntarios { get; } =
    [
        new HinovaVoluntario("101", "Ana Paula Ferreira", "11122233344", "(31)99111-2233", ["Cooperativa Central"]),
        new HinovaVoluntario("102", "Bruno Costa Lima", "22233344455", "(31)99222-3344", ["Cooperativa Central"]),
    ];

    public Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct) =>
        RejectAuth ? throw new HinovaAuthException() : Task.FromResult("token-usuario-fake");

    public Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HinovaVoluntario>>(Voluntarios);
}
