using Recorrencia.Api.Cli;
using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class HinovaBoletoImportTests(ApiFixture api)
{
    public sealed record ImportarBoletosResponseDto(int TotalBoletos, int Importados, int SemVinculo, int ConflitoMultiploVendedor, int NaoPago);

    private async Task<ApiClient> LoginAsAdminAsync(SeededTenant s)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    private static async Task ConfigureCredentialsAsync(ApiClient admin) =>
        await ApiClient.ExpectAsync(
            await admin.PutAsync("/integracoes/hinova/credenciais", new { usuario = "usuario", senha = "senha", tokenSga = "token" }),
            HttpStatusCode.NoContent);

    private static object Boleto(string nossoNumero, decimal valorPagamento, string? dataPagamento, params (string codigoVoluntario, string? placa)[] veiculos) => new
    {
        nosso_numero = nossoNumero,
        valor_boleto = valorPagamento,
        valor_pagamento = valorPagamento,
        codigo_associado = "999",
        nome_associado = "Associado Teste",
        data_vencimento = "05/09/2026",
        data_pagamento = dataPagamento,
        veiculos = veiculos.Select(v => new { codigo_veiculo = "1", codigo_voluntario = v.codigoVoluntario, placa = v.placa }).ToList(),
    };

    // FakeHinovaClient.BoletosPaginas is a singleton shared by every test in this collection --
    // always start from a clean slate rather than appending onto whatever a previous test left.
    private static void SetPage(ApiFixture api, params object[] boletos)
    {
        api.Hinova.BoletosPaginas.Clear();
        api.Hinova.BoletosPaginas.Add(new HinovaBoletoPagina(1, boletos.Length, 0,
            boletos.Select(b => ParseViaClient(b)).ToList()));
    }

    // Reuses HinovaClient's own JSON parsing (rather than hand-building HinovaBoleto records) so
    // the test data goes through the exact same flexible-typing path production traffic does.
    private static HinovaBoleto ParseViaClient(object boletoJson)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { numero_paginas = 1, total_registros = "1", pagina_corrente = 0, boletos = new[] { boletoJson } });
        var handler = new CapturingHandler(json);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.hinova.com.br/api/sga/v2/") };
        var client = new HinovaClient(http);
        var page = client.ListarBoletosPeriodoAsync("t", new HinovaBoletoPeriodoFiltro(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 100, 0), CancellationToken.None).GetAwaiter().GetResult();
        return page.Boletos[0];
    }

    private sealed class CapturingHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task Importing_without_credentials_configured_is_a_bad_request()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var response = await admin.PostAsync("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("hinova.nao_configurado", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Missing_competencia_is_a_bad_request()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);

        var response = await admin.PostAsync("/integracoes/hinova/boletos/importar", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("commission.invalid_competencia", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Without_integracoes_gerenciar_is_forbidden()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var response = await joao.PostAsync("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task No_mapped_voluntarios_short_circuits_without_calling_hinova()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);

        var result = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });

        Assert.Equal((0, 0, 0, 0, 0), (result.TotalBoletos, result.Importados, result.SemVinculo, result.ConflitoMultiploVendedor, result.NaoPago));
    }

    [Fact]
    public async Task Imports_a_single_vendor_boleto_and_is_idempotent_on_rerun()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        SetPage(api, Boleto("777001", 200.00m, "10/09/2026", ("101", "ABC1D23")));

        var result = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });
        Assert.Equal((1, 1, 0, 0, 0), (result.TotalBoletos, result.Importados, result.SemVinculo, result.ConflitoMultiploVendedor, result.NaoPago));

        var valor = await api.SqlScalarAsync<decimal>(
            "select valor from boletos where tenant_id = @tenant and hinova_nosso_numero = '777001'", new { tenant = s.TenantId });
        Assert.Equal(200.00m, valor);

        // Re-run with an updated payment amount for the same nosso_numero -- must update the
        // existing row (still exactly one), never insert a second one.
        api.Hinova.BoletosPaginas.Clear();
        SetPage(api, Boleto("777001", 250.00m, "10/09/2026", ("101", "ABC1D23")));
        var second = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });
        Assert.Equal(1, second.Importados);

        var count = await api.SqlScalarAsync<int>(
            "select count(*)::int from boletos where tenant_id = @tenant and hinova_nosso_numero = '777001'", new { tenant = s.TenantId });
        Assert.Equal(1, count);
        var updatedValor = await api.SqlScalarAsync<decimal>(
            "select valor from boletos where tenant_id = @tenant and hinova_nosso_numero = '777001'", new { tenant = s.TenantId });
        Assert.Equal(250.00m, updatedValor);
    }

    [Fact]
    public async Task A_veiculo_whose_codigo_voluntario_is_not_mapped_is_counted_as_semVinculo()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        // "102" was never mapped to anyone in this tenant.
        SetPage(api, Boleto("777002", 200.00m, "10/09/2026", ("102", "XYZ9Z99")));

        var result = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });

        Assert.Equal((1, 0, 1, 0, 0), (result.TotalBoletos, result.Importados, result.SemVinculo, result.ConflitoMultiploVendedor, result.NaoPago));
    }

    [Fact]
    public async Task A_boleto_whose_veiculos_resolve_to_two_different_mapped_vendors_is_a_conflict_not_a_guess()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Maria, codigoVoluntario = "102", nomeHinova = "Bruno Costa Lima", cpfHinova = "22233344455" }),
            HttpStatusCode.Created);
        SetPage(api, Boleto("777003", 300.00m, "10/09/2026", ("101", "AAA1A11"), ("102", "BBB2B22")));

        var result = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });

        Assert.Equal((1, 0, 0, 1, 0), (result.TotalBoletos, result.Importados, result.SemVinculo, result.ConflitoMultiploVendedor, result.NaoPago));
        var count = await api.SqlScalarAsync<int>(
            "select count(*)::int from boletos where tenant_id = @tenant and hinova_nosso_numero = '777003'", new { tenant = s.TenantId });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Multiple_veiculos_for_the_same_mapped_vendor_import_the_payment_once_not_per_vehicle()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        SetPage(api, Boleto("777004", 400.00m, "10/09/2026", ("101", "AAA1A11"), ("101", "CCC3C33")));

        var result = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });

        Assert.Equal((1, 1, 0, 0, 0), (result.TotalBoletos, result.Importados, result.SemVinculo, result.ConflitoMultiploVendedor, result.NaoPago));
        var valor = await api.SqlScalarAsync<decimal>(
            "select valor from boletos where tenant_id = @tenant and hinova_nosso_numero = '777004'", new { tenant = s.TenantId });
        Assert.Equal(400.00m, valor); // not 800 -- one payment, not one per vehicle.
    }

    [Fact]
    public async Task A_boleto_with_no_data_pagamento_is_counted_as_naoPago_even_when_valor_pagamento_mirrors_valor_boleto()
    {
        // Confirmed against the real API: an unpaid boleto's valor_pagamento often just echoes
        // valor_boleto rather than coming back as 0.00 -- data_pagamento is the only reliable
        // "was this actually paid" signal, so a boleto with a real-looking valor_pagamento but no
        // data_pagamento must still be excluded from commission-relevant import.
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        SetPage(api, Boleto("777007", 197.00m, dataPagamento: null, ("101", "AAA1A11")));

        var result = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });

        Assert.Equal((1, 0, 0, 0, 1), (result.TotalBoletos, result.Importados, result.SemVinculo, result.ConflitoMultiploVendedor, result.NaoPago));
        var count = await api.SqlScalarAsync<int>(
            "select count(*)::int from boletos where tenant_id = @tenant and hinova_nosso_numero = '777007'", new { tenant = s.TenantId });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Walks_every_page_the_fake_client_reports()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        api.Hinova.BoletosPaginas.Clear();
        api.Hinova.BoletosPaginas.Add(new HinovaBoletoPagina(2, 2, 0, [ParseViaClient(Boleto("777005", 100.00m, "10/09/2026", ("101", "AAA1A11")))]));
        api.Hinova.BoletosPaginas.Add(new HinovaBoletoPagina(2, 2, 1, [ParseViaClient(Boleto("777006", 100.00m, "10/09/2026", ("101", "BBB2B22")))]));

        var result = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });

        Assert.Equal((2, 2, 0, 0, 0), (result.TotalBoletos, result.Importados, result.SemVinculo, result.ConflitoMultiploVendedor, result.NaoPago));
        Assert.Equal(100, api.Hinova.UltimoFiltroBoletos!.QuantidadePorPagina);
    }
}
