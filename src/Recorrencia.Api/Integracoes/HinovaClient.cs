using System.Net;
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

    public async Task<HinovaVoluntarioDetalhe?> BuscarVoluntarioAsync(string tokenUsuario, string cpfOuCodigo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"buscar/voluntario/{Uri.EscapeDataString(cpfOuCodigo)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsuario);

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<BuscarVoluntarioDto>(cancellationToken: ct);
        return body is null ? null : new HinovaVoluntarioDetalhe(
            body.CodigoVoluntario, body.Nome, body.Cpf,
            (body.Cooperativas ?? []).Select(c => c.CodigoCooperativa).ToList());
    }

    public async Task<string> CadastrarVoluntarioAsync(string tokenUsuario, CadastrarVoluntarioRequest request, CancellationToken ct)
    {
        // Not `using` -- disposing an HttpRequestMessage disposes its Content too, which would
        // tear down the JsonContent's buffer before callers/tests can inspect what was sent.
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "voluntario/cadastrar")
        {
            Content = JsonContent.Create(new
            {
                nome = request.Nome,
                cpf = request.Cpf,
                celular = request.Celular,
                email = request.Email,
                logradouro = request.Logradouro,
                numero = request.Numero,
                complemento = request.Complemento,
                bairro = request.Bairro,
                cidade = request.Cidade,
                estado = request.Estado,
                cep = request.Cep,
                obs = request.Obs,
                codigo_voluntario_vinculado = request.CodigoVoluntarioVinculado,
                cooperativas = request.CooperativaCodigos.Select(c => new { codigo_cooperativa = c }).ToList(),
            }),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsuario);

        using var response = await http.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CadastrarResponseDto>(cancellationToken: ct);
        return result?.CodigoVoluntario ?? throw new InvalidOperationException("Hinova não retornou codigo_voluntario ao cadastrar.");
    }

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

    private sealed record BuscarVoluntarioDto(
        [property: JsonPropertyName("codigo_voluntario")] string CodigoVoluntario,
        [property: JsonPropertyName("nome")] string Nome,
        [property: JsonPropertyName("cpf")] string Cpf,
        [property: JsonPropertyName("cooperativas")] List<CooperativaCodigoDto>? Cooperativas);

    private sealed record CooperativaCodigoDto([property: JsonPropertyName("codigo_cooperativa")] string CodigoCooperativa);

    private sealed record CadastrarResponseDto([property: JsonPropertyName("codigo_voluntario")] string CodigoVoluntario);
}
