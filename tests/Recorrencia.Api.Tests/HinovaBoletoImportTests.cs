using Recorrencia.Api.Cli;
using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class HinovaBoletoImportTests(ApiFixture api)
{
    public sealed record ImportarBoletosResponseDto(int TotalBoletos, int Importados, int SemVinculo, int ConflitoMultiploVendedor, int NaoPago);
    public sealed record CommissionEntryDto(Guid BoletoId);
    public sealed record SyncJobDto(Guid Id, DateOnly Competencia, string Status, string Fase,
        int? TotalBoletos, int PagamentosConsultados, int PagamentosProcessados, int? Importados,
        int? VencimentoRetornados, int VencimentosConsultados, int VencimentosProcessados, string? ErrorCode);

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
        veiculos = veiculos.Select(v => new { codigo_veiculo = "1", codigo_voluntario = v.codigoVoluntario, placa = v.placa,
            data_inicio_contrato_veiculo = "2026-09-30" }).ToList(),
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

    [Fact]
    public async Task Admin_can_sync_selected_payment_month_in_background_and_repeat_without_duplicates()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(await admin.PostAsync("/integracoes/hinova/mapeamentos",
            new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Consultor", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        SetPage(api, Boleto("sync-2026-10-001", 220m, "10/10/2026", ("101", "ABC1D23")));

        async Task<SyncJobDto> CompletedAsync()
        {
            var startResponse = await admin.PostAsync("/integracoes/hinova/boletos/syncs",
                new { competencia = "2026-10" });
            await ApiClient.ExpectAsync(startResponse, HttpStatusCode.Accepted);
            var started = (await startResponse.Content.ReadFromJsonAsync<SyncJobDto>(ApiClient.Json))!;
            for (var attempt = 0; attempt < 80; attempt++)
            {
                var job = await admin.GetJsonAsync<SyncJobDto>($"/integracoes/hinova/boletos/syncs/{started.Id}");
                if (job.Status == "completed") return job;
                Assert.NotEqual("failed", job.Status);
                await Task.Delay(100);
            }
            throw new TimeoutException("A sincronização não terminou no prazo do teste.");
        }

        var first = await CompletedAsync();
        Assert.Equal(new DateOnly(2026, 10, 1), first.Competencia);
        Assert.Equal("concluido", first.Fase);
        Assert.Equal((1, 1, 1), (first.TotalBoletos, first.PagamentosConsultados, first.PagamentosProcessados));
        Assert.Equal((1, 1, 1), (first.VencimentoRetornados, first.VencimentosConsultados, first.VencimentosProcessados));
        Assert.True(api.Hinova.UltimoFiltroBoletos?.FiltrarPorVencimento);
        Assert.Equal(1, first.Importados);
        var again = await CompletedAsync();
        Assert.NotEqual(first.Id, again.Id);
        Assert.Equal(1, again.Importados);
        Assert.Equal(1, await api.SqlScalarAsync<int>(
            "select count(*)::int from boletos where tenant_id = @tenant and hinova_nosso_numero = 'sync-2026-10-001'",
            new { tenant = s.TenantId }));
    }

    [Fact]
    public async Task Open_wallet_sync_skips_payments_and_retries_a_transient_Hinova_failure()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(await admin.PostAsync("/integracoes/hinova/mapeamentos",
            new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Consultor", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        var open = ParseViaClient(Boleto("open-only", 190m, null, ("101", "ABC1D25")))
            with { Vencimento = new DateOnly(2026, 10, 10) };
        api.Hinova.BoletosPaginas.Clear();
        api.Hinova.BoletosPaginas.Add(new HinovaBoletoPagina(1, 1, 0, [open]));
        var calls = 0;
        api.Hinova.BeforeListBoletos = (filter, _) =>
        {
            Assert.True(filter.FiltrarPorVencimento);
            if (++calls == 1) throw new HttpRequestException("transient", null, HttpStatusCode.ServiceUnavailable);
            return Task.CompletedTask;
        };
        try
        {
            var response = await admin.PostAsync("/integracoes/hinova/boletos/syncs",
                new { competencia = "2026-10", somenteVencimentos = true });
            await ApiClient.ExpectAsync(response, HttpStatusCode.Accepted);
            var started = (await response.Content.ReadFromJsonAsync<SyncJobDto>(ApiClient.Json))!;
            SyncJobDto? job = null;
            for (var attempt = 0; attempt < 120; attempt++)
            {
                job = await admin.GetJsonAsync<SyncJobDto>($"/integracoes/hinova/boletos/syncs/{started.Id}");
                if (job.Status is "completed" or "failed") break;
                await Task.Delay(100);
            }
            Assert.Equal("completed", job?.Status);
            Assert.Equal(2, calls);
            Assert.Equal(0, job?.Importados);
            Assert.Equal(1, job?.VencimentoRetornados);
            Assert.Equal(1, await api.SqlScalarAsync<int>(
                "select count(*)::int from boletos where tenant_id=@tenant and hinova_nosso_numero='open-only' and pago_em is null",
                new { tenant = s.TenantId }));
        }
        finally { api.Hinova.BeforeListBoletos = null; }
    }

    [Fact]
    public async Task Daily_sync_imports_payments_before_open_wallets_and_revisits_previous_open_months()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(await admin.PostAsync("/integracoes/hinova/mapeamentos",
            new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Consultor", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        var old = ParseViaClient(Boleto("daily-old", 180m, null, ("101", "ABC1D23")));
        api.Hinova.BoletosPaginas.Clear();
        api.Hinova.BoletosPaginas.Add(new HinovaBoletoPagina(1, 1, 0, [old]));
        await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });
        var paid = ParseViaClient(Boleto("daily-paid", 220m, "20/10/2026", ("101", "ABC1D24")))
            with { Vencimento = new DateOnly(2026, 10, 10) };
        var open = ParseViaClient(Boleto("daily-open", 190m, null, ("101", "ABC1D25")))
            with { Vencimento = new DateOnly(2026, 10, 10) };
        var months = new HashSet<DateOnly>();
        api.Hinova.BeforeListBoletos = async (filter, _) =>
        {
            IReadOnlyList<HinovaBoleto> rows;
            if (!filter.FiltrarPorVencimento) rows = [paid];
            else
            {
                Assert.Equal(1, await api.SqlScalarAsync<int>(
                    "select count(*)::int from boletos where tenant_id=@tenant and hinova_nosso_numero='daily-paid' and status='recebido'",
                    new { tenant = s.TenantId }));
                months.Add(filter.DataPagamentoInicial);
                rows = filter.DataPagamentoInicial.Month == 9
                    ? [old with { DataPagamento = new DateOnly(2026, 10, 1) }]
                    : [paid with { DataPagamento = null }, open];
            }
            api.Hinova.BoletosPaginas.Clear();
            api.Hinova.BoletosPaginas.Add(new HinovaBoletoPagina(1, rows.Count, 0, rows));
        };
        var id = Guid.NewGuid();
        try
        {
            await api.SqlAsync(
                """
                insert into hinova_boleto_sync_jobs
                    (id,tenant_id,requested_by,competencia,pagamento_inicio,pagamento_fim,agendado_para)
                values (@id,@tenant,@actor,'2026-10-01','2026-10-18','2026-10-23','2026-10-23')
                """, new { id, tenant = s.TenantId, actor = s.Admin });
            SyncJobDto? job = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                job = await admin.GetJsonAsync<SyncJobDto>($"/integracoes/hinova/boletos/syncs/{id}");
                if (job.Status is "completed" or "failed") break;
                await Task.Delay(100);
            }
            Assert.Equal("completed", job?.Status);
            Assert.Equal(3, job?.VencimentoRetornados);
            Assert.True(months.SetEquals([new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)]));
            Assert.Equal(2, await api.SqlScalarAsync<int>(
                "select count(*)::int from boletos where tenant_id=@tenant and status='recebido' and hinova_nosso_numero in ('daily-paid','daily-old')",
                new { tenant = s.TenantId }));
            Assert.Equal(1, await api.SqlScalarAsync<int>(
                "select count(*)::int from boletos where tenant_id=@tenant and pago_em is null and hinova_nosso_numero='daily-open'",
                new { tenant = s.TenantId }));
            Assert.Equal(3, await api.SqlScalarAsync<int>(
                "select count(*)::int from boletos where tenant_id=@tenant and hinova_nosso_numero like 'daily-%'",
                new { tenant = s.TenantId }));
        }
        finally { api.Hinova.BeforeListBoletos = null; }
    }

    [Fact]
    public async Task Sync_exposes_partial_progress_before_the_next_Hinova_stage_finishes()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(await admin.PostAsync("/integracoes/hinova/mapeamentos",
            new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Consultor", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        api.Hinova.BoletosPaginas.Clear();
        api.Hinova.BoletosPaginas.Add(new HinovaBoletoPagina(2, 2, 0,
            [ParseViaClient(Boleto("sync-progress-001", 180m, "10/10/2026", ("101", "ABC1D23")))]));
        api.Hinova.BoletosPaginas.Add(new HinovaBoletoPagina(2, 2, 1,
            [ParseViaClient(Boleto("sync-progress-002", 180m, "11/10/2026", ("101", "ABC1D24")))]));
        var releaseSecondPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Hinova.BeforeListBoletos = (filter, token) => filter.InicioPaginacao == 1
            ? releaseSecondPage.Task.WaitAsync(token)
            : Task.CompletedTask;
        Guid? jobId = null;

        try
        {
            var response = await admin.PostAsync("/integracoes/hinova/boletos/syncs", new { competencia = "2026-10" });
            await ApiClient.ExpectAsync(response, HttpStatusCode.Accepted);
            var started = (await response.Content.ReadFromJsonAsync<SyncJobDto>(ApiClient.Json))!;
            jobId = started.Id;
            SyncJobDto? partial = null;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var job = await admin.GetJsonAsync<SyncJobDto>($"/integracoes/hinova/boletos/syncs/{started.Id}");
                if (job.Fase == "buscando_pagamentos" && job.PagamentosConsultados == 1)
                {
                    partial = job;
                    break;
                }
                Assert.NotEqual("failed", job.Status);
                await Task.Delay(100);
            }
            Assert.NotNull(partial);
            Assert.Equal("running", partial.Status);
            Assert.Equal(0, partial.PagamentosProcessados);
            Assert.Equal(2, partial.TotalBoletos);
        }
        finally
        {
            releaseSecondPage.TrySetResult();
            if (jobId is { } id)
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    var job = await admin.GetJsonAsync<SyncJobDto>($"/integracoes/hinova/boletos/syncs/{id}");
                    if (job.Status is "completed" or "failed") break;
                    await Task.Delay(100);
                }
            }
            api.Hinova.BeforeListBoletos = null;
        }
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
        var contrato = await api.SqlScalarAsync<DateOnly>(
            """
            select v.data_inicio_contrato from boleto_veiculos v
              join boletos b on b.id = v.boleto_id
             where b.tenant_id = @tenant and b.hinova_nosso_numero = '777001'
            """, new { tenant = s.TenantId });
        Assert.Equal(new DateOnly(2026, 9, 30), contrato);

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
    public async Task Rerun_holds_an_existing_payment_when_a_new_mapping_makes_its_vendor_ambiguous()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        SetPage(api, Boleto("777003-rerun", 300.00m, "10/09/2026", ("101", "AAA1A11"), ("102", "BBB2B22")));

        var first = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });
        Assert.Equal(1, first.Importados);
        var boletoId = await api.SqlScalarAsync<Guid>(
            "select id from boletos where tenant_id = @tenant and hinova_nosso_numero = '777003-rerun'", new { tenant = s.TenantId });

        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Maria, codigoVoluntario = "102", nomeHinova = "Bruno Costa Lima", cpfHinova = "22233344455" }),
            HttpStatusCode.Created);
        var conflict = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });
        Assert.Equal((0, 1), (conflict.Importados, conflict.ConflitoMultiploVendedor));
        Assert.Equal("multiple_mapped_volunteers", await api.SqlScalarAsync<string>(
            "select commission_hold_reason from boletos where id = @boletoId", new { boletoId }));
        var heldEntries = await admin.GetJsonAsync<List<CommissionEntryDto>>($"/commissions/2026-09/entries?beneficiaryId={s.Joao}");
        Assert.DoesNotContain(heldEntries, e => e.BoletoId == boletoId);

        SetPage(api, Boleto("777003-rerun", 300.00m, "10/09/2026", ("101", "AAA1A11")));
        var resolved = await admin.PostJsonAsync<ImportarBoletosResponseDto>("/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });
        Assert.Equal(1, resolved.Importados);
        Assert.Null(await api.SqlScalarAsync<string?>(
            "select commission_hold_reason from boletos where id = @boletoId", new { boletoId }));
        var activeEntries = await admin.GetJsonAsync<List<CommissionEntryDto>>($"/commissions/2026-09/entries?beneficiaryId={s.Joao}");
        Assert.Contains(activeEntries, e => e.BoletoId == boletoId);
    }

    [Fact]
    public async Task Rerun_preserves_a_historical_payment_when_its_volunteer_has_no_current_mapping()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        SetPage(api, Boleto("777004-rerun", 300.00m, "10/09/2026", ("101", "AAA1A11")));
        Assert.Equal(1, (await admin.PostJsonAsync<ImportarBoletosResponseDto>(
            "/integracoes/hinova/boletos/importar", new { competencia = "2026-09" })).Importados);
        var boletoId = await api.SqlScalarAsync<Guid>(
            "select id from boletos where tenant_id = @tenant and hinova_nosso_numero = '777004-rerun'", new { tenant = s.TenantId });

        await ApiClient.ExpectAsync(await admin.DeleteAsync($"/integracoes/hinova/mapeamentos/{s.Joao}"), HttpStatusCode.NoContent);
        var rerun = await admin.PostJsonAsync<ImportarBoletosResponseDto>(
            "/integracoes/hinova/boletos/importar", new { competencia = "2026-09" });
        Assert.Equal(1, rerun.SemVinculo);
        Assert.Null(await api.SqlScalarAsync<string?>(
            "select commission_hold_reason from boletos where id = @boletoId", new { boletoId }));
        var entries = await admin.GetJsonAsync<List<CommissionEntryDto>>($"/commissions/2026-09/entries?beneficiaryId={s.Joao}");
        Assert.Contains(entries, e => e.BoletoId == boletoId);
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
    public async Task A_boleto_with_no_data_pagamento_is_imported_as_unpaid_even_when_valor_pagamento_mirrors_valor_boleto()
    {
        // Confirmed against the real API: an unpaid boleto's valor_pagamento often just echoes
        // valor_boleto rather than coming back as 0.00 -- data_pagamento is the only reliable
        // "was this actually paid" signal, so a boleto with a real-looking valor_pagamento but no
        // data_pagamento must not generate paid commission, but is retained for the loss dashboard.
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
        Assert.Equal(1, count);
        var status = await api.SqlScalarAsync<string>(
            "select status from boletos where tenant_id = @tenant and hinova_nosso_numero = '777007'", new { tenant = s.TenantId });
        Assert.Equal("atraso", status);
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
        Assert.Equal(500, api.Hinova.UltimoFiltroBoletos!.QuantidadePorPagina);
    }
}
