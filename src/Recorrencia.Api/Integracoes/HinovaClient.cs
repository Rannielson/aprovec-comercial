using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recorrencia.Api.Integracoes;

public sealed class HinovaClient(HttpClient http, HinovaTokenCache? tokenCache = null) : IHinovaClient
{
    private (string Usuario, string Senha, string TokenSga)? _credentials;

    public Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct)
    {
        _credentials = (usuario, senha, tokenSga);
        return tokenCache is null ? AuthenticateRawAsync(usuario, senha, tokenSga, ct)
            : tokenCache.GetAsync(HinovaTokenCache.Key(usuario, senha, tokenSga),
                () => AuthenticateRawAsync(usuario, senha, tokenSga, ct), ct);
    }

    private async Task<string> AuthenticateRawAsync(string usuario, string senha, string tokenSga, CancellationToken ct)
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

        using var response = await SendReadAsync(request, ct);
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

        using var response = await SendReadAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        // The live SGA endpoint returns an array even when exactly one volunteer matches.
        // Keep accepting a single object for compatibility, but never treat an unrelated
        // result in a search array as the volunteer being requested.
        var root = doc.RootElement;
        JsonElement match;
        if (root.ValueKind == JsonValueKind.Array)
        {
            match = root.EnumerateArray().FirstOrDefault(item => item.ValueKind == JsonValueKind.Object &&
                (string.Equals(GetFlexString(item, "codigo_voluntario"), cpfOuCodigo, StringComparison.Ordinal) ||
                 (cpfOuCodigo.Length == 11 && string.Equals(
                     new string((GetFlexString(item, "cpf") ?? "").Where(char.IsDigit).ToArray()),
                     cpfOuCodigo, StringComparison.Ordinal))));
            if (match.ValueKind == JsonValueKind.Undefined) return null;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            match = root;
        }
        else
        {
            throw new JsonException("Formato inesperado na busca de voluntário da Hinova.");
        }

        var code = GetFlexString(match, "codigo_voluntario");
        if (string.IsNullOrWhiteSpace(code))
            throw new JsonException("Voluntário da Hinova sem código.");
        var cooperativas = match.TryGetProperty("cooperativas", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(c => GetFlexString(c, "codigo_cooperativa"))
                .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).ToList()
            : [];
        return new HinovaVoluntarioDetalhe(code, GetFlexString(match, "nome") ?? "",
            GetFlexString(match, "cpf") ?? "", cooperativas, GetFlexString(match, "email"));
    }

    public async Task<string?> BuscarContatoAssociadoAsync(string tokenUsuario, string codigoAssociado, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"listar/associado/contatos/{Uri.EscapeDataString(codigoAssociado)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsuario);

        using var response = await SendReadAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var contacts = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToArray()
            : doc.RootElement.ValueKind == JsonValueKind.Object ? [doc.RootElement] : [];
        foreach (var contact in contacts)
        {
            foreach (var field in new[] { "telefone_celular", "telefone_celular_aux", "celular", "telefone", "telefone_comercial" })
            {
                var normalized = NormalizeWhatsappNumber(GetFlexString(contact, field));
                if (normalized is not null) return normalized;
            }
        }
        return null;
    }

    private static string? NormalizeWhatsappNumber(string? value)
    {
        if (value is null) return null;
        var digits = new string(value.Where(c => c is >= '0' and <= '9').ToArray());
        if (digits.StartsWith("55", StringComparison.Ordinal) && digits.Length is 12 or 13)
            return digits;
        return digits.Length is 10 or 11 ? "55" + digits : null;
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
        if (tokenCache is not null && _credentials is { } credentials)
            tokenUsuario = await tokenCache.GetAsync(HinovaTokenCache.Key(credentials.Usuario, credentials.Senha, credentials.TokenSga),
                () => AuthenticateRawAsync(credentials.Usuario, credentials.Senha, credentials.TokenSga, ct), ct);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsuario);

        // A creation is sent once; only read operations are replayed after token refresh.
        using var response = await http.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CadastrarResponseDto>(cancellationToken: ct);
        return result?.CodigoVoluntario ?? throw new InvalidOperationException("Hinova não retornou codigo_voluntario ao cadastrar.");
    }

    // Manual JsonElement parsing (not typed DTOs) because the real API is inconsistent about
    // which fields come back as a JSON number vs a JSON string (confirmed against the docs' own
    // example: nosso_numero/codigo_associado are numbers, valor_boleto/valor_pagamento/
    // total_registros are strings, codigo_veiculo is a number but codigo_voluntario is a string)
    // -- a strict DTO throws the moment reality doesn't match one example payload.
    public async Task<HinovaBoletoPagina> ListarBoletosPeriodoAsync(string tokenUsuario, HinovaBoletoPeriodoFiltro filtro, CancellationToken ct)
    {
        // Not `using` -- disposing an HttpRequestMessage disposes its Content too, which would
        // tear down the JsonContent's buffer before a caller/test can inspect what was sent.
        var startField = filtro.FiltrarPorVencimento ? "data_vencimento_inicial" : "data_pagamento_inicial";
        var endField = filtro.FiltrarPorVencimento ? "data_vencimento_final" : "data_pagamento_final";
        var body = new Dictionary<string, object>
        {
            [startField] = filtro.DataPagamentoInicial.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            [endField] = filtro.DataPagamentoFinal.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
            ["quantidade_por_pagina"] = filtro.QuantidadePorPagina,
            ["inicio_paginacao"] = filtro.InicioPaginacao,
        };
        if (filtro.CodigoVoluntario is not null)
            body["codigo_voluntario"] = new[] { filtro.CodigoVoluntario };
        var request = new HttpRequestMessage(HttpMethod.Post, "listar/boleto-associado/periodo")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsuario);

        using var response = await SendReadAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotAcceptable)
            return new HinovaBoletoPagina(0, 0, filtro.InicioPaginacao, []);
        response.EnsureSuccessStatusCode();

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            // Hinova sometimes sends a plain-text error with HTTP 200. Treat it as a
            // transient upstream failure, never as an empty/successful reconciliation.
            throw new HttpRequestException("Resposta inválida da Hinova ao consultar boletos.", ex, HttpStatusCode.BadGateway);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("boletos", out var boletosEl)
                || boletosEl.ValueKind != JsonValueKind.Array)
                throw new HttpRequestException("Resposta da Hinova sem a lista de boletos.", null, HttpStatusCode.BadGateway);
            var boletos = boletosEl.EnumerateArray().Select(ParseBoleto).ToList();
            var paginas = GetFlexInt(root, "numero_paginas");
            if (boletos.Count > 0 && paginas < 1)
                throw new HttpRequestException("Resposta da Hinova sem paginação válida.", null, HttpStatusCode.BadGateway);
            return new HinovaBoletoPagina(paginas, GetFlexInt(root, "total_registros"), GetFlexInt(root, "pagina_corrente"), boletos);
        }
    }

    public async Task<HinovaBoleto?> BuscarBoletoAsync(string tokenUsuario, string nossoNumero, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "buscar/boleto/" + Uri.EscapeDataString(nossoNumero));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsuario);
        using var response = await SendReadAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotAcceptable) return null;
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return ParseBoleto(doc.RootElement);
    }

    private async Task<HttpResponseMessage> SendReadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (tokenCache is null || _credentials is not { } c) return await http.SendAsync(request, ct);
        var key = HinovaTokenCache.Key(c.Usuario, c.Senha, c.TokenSga);
        Task<string> Authenticate() => AuthenticateRawAsync(c.Usuario, c.Senha, c.TokenSga, ct);
        var token = await tokenCache.GetAsync(key, Authenticate, ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        var refreshed = await tokenCache.GetAsync(key, Authenticate, ct, invalidToken: token);
        using var retry = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            retry.Headers.TryAddWithoutValidation(header.Key, header.Value);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshed);
        if (request.Content is not null)
        {
            retry.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(ct));
            foreach (var header in request.Content.Headers)
                retry.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return await http.SendAsync(retry, ct);
    }

    private static HinovaBoleto ParseBoleto(JsonElement el)
    {
        var veiculos = el.TryGetProperty("veiculos", out var veiculosEl) && veiculosEl.ValueKind == JsonValueKind.Array
            ? veiculosEl.EnumerateArray().Select(v => new HinovaVeiculoBoleto(
                GetFlexString(v, "codigo_veiculo") ?? "", GetFlexString(v, "codigo_voluntario"), GetFlexString(v, "placa"),
                ParseBrDate(GetFlexString(v, "data_inicio_contrato_veiculo")))).ToList()
            : [];
        return new HinovaBoleto(
            GetFlexString(el, "nosso_numero") ?? "",
            GetFlexDecimal(el, "valor_boleto"),
            GetFlexDecimal(el, "valor_pagamento"),
            GetFlexString(el, "codigo_associado") ?? "",
            GetFlexString(el, "nome_associado") ?? "",
            ParseBrDate(GetFlexString(el, "data_vencimento")),
            ParseBrDate(GetFlexString(el, "data_pagamento")),
            veiculos,
            GetFlexString(el, "codigo_tipo_boleto"),
            GetFlexString(el, "tipo_boleto") ?? GetFlexString(el, "descricao_tipo_boleto"),
            GetFlexString(el, "situacao_boleto") ?? GetFlexString(el, "descricao_situacao_boleto"));
    }

    // The real API returns data_pagamento/data_vencimento as ISO (yyyy-MM-dd), not the
    // dd/MM/yyyy shown in the Hinova docs' own example -- confirmed against a live response,
    // where every data_pagamento came back null under the old dd/MM/yyyy-only parser despite
    // the boletos being genuinely paid. dd/MM/yyyy is kept as a fallback since this API has
    // already shown it mixes formats/types across fields and endpoints.
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy"];

    private static DateOnly? ParseBrDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        // Some Hinova responses include a time/offset on an otherwise date-only field.
        // Keep the calendar date as sent instead of shifting it across time zones.
        if (text.Length > 10 && (text[10] == 'T' || text[10] == ' '))
            text = text[..10];
        return DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;
    }

    private static string? GetFlexString(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString();
    }

    private static int GetFlexInt(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var value) || value.ValueKind == JsonValueKind.Null) return 0;
        return value.ValueKind == JsonValueKind.Number ? value.GetInt32() : int.Parse(value.GetString() ?? "0", CultureInfo.InvariantCulture);
    }

    private static decimal GetFlexDecimal(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var value) || value.ValueKind == JsonValueKind.Null) return 0m;
        return value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : decimal.Parse(value.GetString() ?? "0", CultureInfo.InvariantCulture);
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

    private sealed record CadastrarResponseDto([property: JsonPropertyName("codigo_voluntario")] string CodigoVoluntario);
}
