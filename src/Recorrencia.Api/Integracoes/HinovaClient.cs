using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Recorrencia.Api.Integracoes;

public sealed class HinovaClient(HttpClient http) : IHinovaClient
{
    public async Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "usuario/autenticar")
        {
            Content = JsonContent.Create(new { usuario, senha }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenSga);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HinovaAuthException();

        var body = await response.Content.ReadFromJsonAsync<AutenticarResponse>(cancellationToken: ct);
        return body?.TokenUsuario ?? throw new HinovaAuthException();
    }

    public async Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct)
    {
        // Path is "listar/voluntario/:situacao" -- confirmed against the Hinova SGA v2 docs
        // (Voluntario - Listar), whose own example request is exactly "listar/voluntario/ativo".
        using var request = new HttpRequestMessage(HttpMethod.Get, "listar/voluntario/ativo");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsuario);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var items = await response.Content.ReadFromJsonAsync<List<VoluntarioDto>>(cancellationToken: ct) ?? [];
        return items.Select(i => new HinovaVoluntario(
            i.CodigoVoluntario,
            i.Nome,
            i.Cpf,
            // Mobile first -- landline and telefone_comercial are frequently blank on file. A blank
            // field on the real API often comes back as "()" (the format mask with no digits filled
            // in) rather than null or "", so blank-ness is judged by digit content, not just length.
            FirstWithDigits(i.Celular, i.Telefone, i.TelefoneComercial),
            (i.Cooperativas ?? []).Select(c => c.NomeCooperativa).ToList())).ToList();
    }

    private static string? FirstWithDigits(params string?[] candidates) =>
        candidates.FirstOrDefault(c => c is not null && c.Any(char.IsDigit));

    private sealed record AutenticarResponse([property: JsonPropertyName("token_usuario")] string TokenUsuario);

    private sealed record CooperativaDto([property: JsonPropertyName("nome_cooperativa")] string NomeCooperativa);

    private sealed record VoluntarioDto(
        [property: JsonPropertyName("codigo_voluntario")] string CodigoVoluntario,
        [property: JsonPropertyName("nome")] string Nome,
        [property: JsonPropertyName("cpf")] string Cpf,
        [property: JsonPropertyName("telefone")] string? Telefone,
        [property: JsonPropertyName("celular")] string? Celular,
        [property: JsonPropertyName("telefone_comercial")] string? TelefoneComercial,
        [property: JsonPropertyName("cooperativas")] List<CooperativaDto>? Cooperativas);
}
