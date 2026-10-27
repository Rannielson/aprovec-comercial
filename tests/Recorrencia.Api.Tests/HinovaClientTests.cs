using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

public class HinovaClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static (HinovaClient Client, StubHandler Handler) NewClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.hinova.com.br/api/sga/v2/") };
        return (new HinovaClient(http), handler);
    }

    [Fact]
    public async Task Shared_cache_authenticates_once_and_refreshes_a_rejected_token_once()
    {
        var authentications=0;
        var seen=new List<string?>();
        var handler=new StubHandler(request=>
        {
            if(request.RequestUri!.AbsolutePath.EndsWith("usuario/autenticar"))
                return new HttpResponseMessage(HttpStatusCode.OK) {Content=JsonContent.Create(new {token_usuario=++authentications==1?"old":"fresh"})};
            seen.Add(request.Headers.Authorization?.Parameter);
            return request.Headers.Authorization?.Parameter=="old" ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : new HttpResponseMessage(HttpStatusCode.OK) {Content=JsonContent.Create(new {numero_paginas=0,total_registros=0,pagina_corrente=0,boletos=Array.Empty<object>()})};
        });
        var cache=new HinovaTokenCache();
        HinovaClient Create()=>new(new HttpClient(handler){BaseAddress=new Uri("https://api.hinova.com.br/api/sga/v2/")},cache);
        var first=Create();var second=Create();
        var tokens=await Task.WhenAll(Enumerable.Range(0,20).Select(_=>first.AutenticarAsync("user","pass","static",CancellationToken.None)));
        Assert.All(tokens,t=>Assert.Equal("old",t));
        Assert.Equal(1,authentications);
        var other=await second.AutenticarAsync("user","pass","static",CancellationToken.None);
        Assert.Equal("old",other);
        var filter=new HinovaBoletoPeriodoFiltro(new DateOnly(2026,10,1),new DateOnly(2026,10,31),500,0);
        await first.ListarBoletosPeriodoAsync("old",filter,CancellationToken.None);
        await second.ListarBoletosPeriodoAsync(other,filter,CancellationToken.None);
        Assert.Equal(2,authentications);
        Assert.Equal(new[]{"old","fresh","fresh"},seen);
    }

    [Theory]
    [InlineData("Ocorreu um erro no servidor")]
    [InlineData("{\"mensagem\":\"Erro temporário\"}")]
    [InlineData("[]")]
    public async Task ListarBoletos_rejects_upstream_errors_with_HTTP_200_as_retryable_bad_gateway(string body)
    {
        var (client, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body,System.Text.Encoding.UTF8,"application/json") });
        var error=await Assert.ThrowsAsync<HttpRequestException>(() => client.ListarBoletosPeriodoAsync("token",
            new HinovaBoletoPeriodoFiltro(new DateOnly(2026,10,1),new DateOnly(2026,10,31),500,0),CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadGateway,error.StatusCode);
    }

    [Fact]
    public async Task BuscarBoleto_parses_detail_field_names_and_iso_payment()
    {
        var (client, handler) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { nosso_numero = "123", valor_boleto = "100.00", valor_pagamento = "100.00",
                codigo_associado = "99", descricao_situacao_boleto = "BAIXADO", data_pagamento = "2026-10-27T10:20:00-03:00" })
        });
        var boleto = await client.BuscarBoletoAsync("token", "123", CancellationToken.None);
        Assert.NotNull(boleto);
        Assert.True(HinovaBoletoStatus.IsPaid(boleto));
        Assert.Equal("BAIXADO", boleto.SituacaoBoleto);
        Assert.Equal("/api/sga/v2/buscar/boleto/123", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Autenticar_sends_the_static_token_and_returns_token_usuario()
    {
        var (client, handler) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { mensagem = "OK", token_usuario = "abc123" }),
        });

        var token = await client.AutenticarAsync("usuario", "senha", "token-estatico", CancellationToken.None);

        Assert.Equal("abc123", token);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("token-estatico", handler.LastRequest.Headers.Authorization!.Parameter);
        Assert.Equal("https://api.hinova.com.br/api/sga/v2/usuario/autenticar", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task Autenticar_throws_HinovaAuthException_on_a_non_success_response()
    {
        var (client, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<HinovaAuthException>(() =>
            client.AutenticarAsync("usuario", "senha", "token-estatico", CancellationToken.None));
    }

    [Fact]
    public async Task BuscarContatoAssociado_prefers_a_valid_mobile_and_builds_brazilian_whatsapp_number()
    {
        var (client, handler) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                [{"telefone_celular":"()","telefone_celular_aux":"(31) 99876-5432","telefone":"(31) 3333-4444"}]
                """, System.Text.Encoding.UTF8, "application/json"),
        });

        var phone = await client.BuscarContatoAssociadoAsync("token-usuario", "123", CancellationToken.None);

        Assert.Equal("5531998765432", phone);
        Assert.Equal("https://api.hinova.com.br/api/sga/v2/listar/associado/contatos/123", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("token-usuario", handler.LastRequest.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task ListarVoluntarios_sends_the_user_token_and_parses_the_array()
    {
        var (client, handler) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[]
            {
                new { codigo_voluntario = "101", nome = "Ana Paula", cpf = "11122233344" },
                new { codigo_voluntario = "102", nome = "Bruno Costa", cpf = "22233344455" },
            }),
        });

        var voluntarios = await client.ListarVoluntariosAsync("token-usuario", CancellationToken.None);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("token-usuario", handler.LastRequest.Headers.Authorization!.Parameter);
        // The path is "listar/voluntario/:situacao" per the Hinova SGA v2 docs -- a previous
        // version of this call appended a stray "/0" that would 404 against the real API.
        Assert.Equal("https://api.hinova.com.br/api/sga/v2/listar/voluntario/ativo", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(2, voluntarios.Count);
        var ana = voluntarios[0];
        Assert.Equal(("101", "Ana Paula", "11122233344", (string?)null), (ana.Codigo, ana.Nome, ana.Cpf, ana.Telefone));
        Assert.Empty(ana.Cooperativas);
    }

    [Fact]
    public async Task ListarVoluntarios_prefers_celular_and_maps_cooperativa_names()
    {
        const string json = """
            [
              {
                "codigo_voluntario": "101",
                "nome": "Ana Paula",
                "cpf": "11122233344",
                "telefone": "(31)3333-4444",
                "celular": "(31)99111-2233",
                "telefone_comercial": "(31)3333-5555",
                "cooperativas": [{ "codigo_cooperativa": "1", "nome_cooperativa": "Cooperativa Central" }]
              },
              {
                "codigo_voluntario": "102",
                "nome": "Bruno Costa",
                "cpf": "22233344455",
                "telefone": "(31)3333-4444",
                "celular": null,
                "telefone_comercial": null,
                "cooperativas": []
              },
              {
                "codigo_voluntario": "103",
                "nome": "Carla Souza",
                "cpf": "33344455566",
                "telefone": "()",
                "celular": "()",
                "telefone_comercial": null,
                "cooperativas": []
              }
            ]
            """;
        var (client, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        var voluntarios = await client.ListarVoluntariosAsync("token-usuario", CancellationToken.None);

        var ana = voluntarios[0];
        Assert.Equal("(31)99111-2233", ana.Telefone);
        Assert.Equal(["Cooperativa Central"], ana.Cooperativas);
        // No celular on file -- falls back to the landline.
        var bruno = voluntarios[1];
        Assert.Equal("(31)3333-4444", bruno.Telefone);
        // Both phone fields are just the empty format mask "()" with no digits -- treated as blank.
        var carla = voluntarios[2];
        Assert.Null(carla.Telefone);
        Assert.Empty(bruno.Cooperativas);
    }

    [Fact]
    public async Task BuscarVoluntario_returns_null_on_a_404()
    {
        var (client, handler) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var found = await client.BuscarVoluntarioAsync("token-usuario", "99999999999", CancellationToken.None);

        Assert.Null(found);
        Assert.Equal("https://api.hinova.com.br/api/sga/v2/buscar/voluntario/99999999999", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task BuscarVoluntario_parses_codigo_and_cooperativa_codes_on_success()
    {
        const string json = """
            {
                "codigo_voluntario": "99",
                "nome": "HINOVA SOLUÇÕES DIGITAIS",
                "cpf": "99999999999",
                "cooperativas": [{ "codigo_cooperativa": "9", "nome_cooperativa": "Central" }]
            }
            """;
        var (client, handler) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        var found = await client.BuscarVoluntarioAsync("token-usuario", "99", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(("99", "HINOVA SOLUÇÕES DIGITAIS", "99999999999"), (found!.Codigo, found.Nome, found.Cpf));
        Assert.Equal(["9"], found.CooperativaCodigos);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("token-usuario", handler.LastRequest.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task BuscarVoluntario_parses_live_array_response_and_matches_the_requested_cpf()
    {
        const string json = """
            [
              { "codigo_voluntario": "88", "nome": "Outra pessoa", "cpf": "11122233344", "cooperativas": [] },
              { "codigo_voluntario": "99", "nome": "Pessoa procurada", "cpf": "222.333.444-55",
                "cooperativas": [{ "codigo_cooperativa": "9" }] }
            ]
            """;
        var (client, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        var found = await client.BuscarVoluntarioAsync("token-usuario", "22233344455", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(("99", "Pessoa procurada"), (found!.Codigo, found.Nome));
        Assert.Equal(["9"], found.CooperativaCodigos);
    }

    [Fact]
    public async Task BuscarVoluntario_returns_null_on_an_empty_success_array()
    {
        var (client, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
        });

        Assert.Null(await client.BuscarVoluntarioAsync("token-usuario", "99999999999", CancellationToken.None));
    }

    [Fact]
    public async Task BuscarVoluntario_matches_the_requested_code_in_an_array()
    {
        const string json = """
            [{ "codigo_voluntario": "214", "nome": "Indicador", "cpf": "12345678901",
               "cooperativas": [{ "codigo_cooperativa": 5 }] }]
            """;
        var (client, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        var found = await client.BuscarVoluntarioAsync("token-usuario", "214", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("214", found!.Codigo);
        Assert.Equal(["5"], found.CooperativaCodigos);
    }

    [Fact]
    public async Task CadastrarVoluntario_sends_every_field_and_returns_codigo_voluntario()
    {
        HttpRequestMessage? captured = null;
        var (client, handler) = NewClient(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { mensagem = "OK", codigo_voluntario = "131" }),
            };
        });

        var request = new CadastrarVoluntarioRequest(
            "Fulano de Tal", "11122233344", "11999998888", "fulano@teste.local",
            "Rua X", "10", "Ap 1", "Bairro", "Cidade", "UF", "30000-000",
            ["9", "10"], "101", "Cadastrado via indicação de João Silva em 25/09/2026.");

        var codigo = await client.CadastrarVoluntarioAsync("token-usuario", request, CancellationToken.None);

        Assert.Equal("131", codigo);
        Assert.Equal("https://api.hinova.com.br/api/sga/v2/voluntario/cadastrar", handler.LastRequest!.RequestUri!.ToString());
        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains("\"codigo_voluntario_vinculado\":\"101\"", body);
        Assert.Contains("\"codigo_cooperativa\":\"9\"", body);
    }

    [Fact]
    public async Task ListarBoletosPeriodo_sends_dates_as_ddMMyyyy_and_the_paging_params()
    {
        HttpRequestMessage? captured = null;
        var (client, handler) = NewClient(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { mostrando = 0, numero_paginas = 0, total_registros = "0", pagina_corrente = 0, boletos = Array.Empty<object>() }),
            };
        });

        var filtro = new HinovaBoletoPeriodoFiltro(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 100, 0);
        await client.ListarBoletosPeriodoAsync("token-usuario", filtro, CancellationToken.None);

        Assert.Equal("https://api.hinova.com.br/api/sga/v2/listar/boleto-associado/periodo", handler.LastRequest!.RequestUri!.ToString());
        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains("\"data_pagamento_inicial\":\"01/09/2026\"", body);
        Assert.Contains("\"data_pagamento_final\":\"30/09/2026\"", body);
        Assert.DoesNotContain("codigo_voluntario", body);
        Assert.Contains("\"quantidade_por_pagina\":100", body);
        Assert.Contains("\"inicio_paginacao\":0", body);
    }

    [Fact]
    public async Task ListarBoletosPeriodo_can_filter_by_due_date_instead_of_payment_date()
    {
        HttpRequestMessage? captured = null;
        var (client, _) = NewClient(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { numero_paginas = 0, total_registros = "0", pagina_corrente = 0, boletos = Array.Empty<object>() }),
            };
        });

        await client.ListarBoletosPeriodoAsync("token-usuario",
            new HinovaBoletoPeriodoFiltro(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 100, 0,
                FiltrarPorVencimento: true), CancellationToken.None);

        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains("\"data_vencimento_inicial\":\"01/09/2026\"", body);
        Assert.Contains("\"data_vencimento_final\":\"30/09/2026\"", body);
        Assert.DoesNotContain("data_pagamento_inicial", body);
    }

    [Fact]
    public async Task ListarBoletosPeriodo_parses_the_real_apis_mixed_number_and_string_fields()
    {
        // Shape confirmed against a real, live response: nosso_numero/codigo_associado are JSON
        // numbers, valor_boleto/valor_pagamento/total_registros are JSON strings, dates are ISO
        // (yyyy-MM-dd, not the dd/MM/yyyy the docs' own example shows), and within veiculos,
        // codigo_veiculo is a number but codigo_voluntario is a string. A strict DTO throws on
        // this mix; the client has to tolerate it either way.
        const string json = """
            {
              "mostrando": 1,
              "numero_paginas": 1,
              "total_registros": "1",
              "pagina_corrente": 0,
              "boletos": [
                {
                  "nosso_numero": 555111,
                  "valor_boleto": "150.00",
                  "valor_pagamento": "150.00",
                  "codigo_associado": 4242,
                  "nome_associado": "FULANO DA SILVA",
                  "data_vencimento": "2026-09-10",
                  "data_pagamento": "2026-09-09T00:00:00.000Z",
                  "codigo_tipo_boleto": "5",
                  "tipo_boleto": "FECHAMENTO",
                  "situacao_boleto": "BAIXADO",
                  "veiculos": [
                    {
                      "codigo_veiculo": 9001,
                      "codigo_voluntario": "26",
                      "placa": "ABC1D23",
                      "data_inicio_contrato_veiculo": "2026-08-31T00:00:00-03:00"
                    }
                  ]
                }
              ]
            }
            """;
        var (client, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        var page = await client.ListarBoletosPeriodoAsync(
            "token-usuario", new HinovaBoletoPeriodoFiltro(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 100, 0), CancellationToken.None);

        Assert.Equal((1, 1, 0), (page.NumeroPaginas, page.TotalRegistros, page.PaginaCorrente));
        var boleto = Assert.Single(page.Boletos);
        Assert.Equal(("555111", 150.00m, 150.00m, "4242", "FULANO DA SILVA"), (boleto.NossoNumero, boleto.ValorBoleto, boleto.ValorPagamento, boleto.CodigoAssociado, boleto.NomeAssociado));
        Assert.Equal(new DateOnly(2026, 9, 10), boleto.Vencimento);
        Assert.Equal(new DateOnly(2026, 9, 9), boleto.DataPagamento);
        Assert.Equal(("5", "FECHAMENTO"), (boleto.CodigoTipoBoleto, boleto.TipoBoleto));
        Assert.Equal("BAIXADO", boleto.SituacaoBoleto);
        var veiculo = Assert.Single(boleto.Veiculos);
        Assert.Equal(("9001", "26", "ABC1D23"), (veiculo.CodigoVeiculo, veiculo.CodigoVoluntario, veiculo.Placa));
        Assert.Equal(new DateOnly(2026, 8, 31), veiculo.DataInicioContratoVeiculo);
    }

    [Fact]
    public async Task ListarBoletosPeriodo_leaves_DataPagamento_null_when_the_boleto_is_unpaid()
    {
        const string json = """
            {
              "numero_paginas": 1, "total_registros": "1", "pagina_corrente": 0,
              "boletos": [
                {
                  "nosso_numero": 1, "valor_boleto": "100.00", "valor_pagamento": "0.00",
                  "codigo_associado": 1, "nome_associado": "A", "data_vencimento": "10/09/2026",
                  "data_pagamento": null, "veiculos": []
                }
              ]
            }
            """;
        var (client, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        var page = await client.ListarBoletosPeriodoAsync(
            "token-usuario", new HinovaBoletoPeriodoFiltro(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 100, 0), CancellationToken.None);

        Assert.Null(Assert.Single(page.Boletos).DataPagamento);
    }

    [Fact]
    public async Task ListarBoletosPeriodo_leaves_Vencimento_null_when_the_real_api_omits_it()
    {
        // Confirmed against the real Hinova API: some boletos come back with no data_vencimento
        // at all, contradicting the docs' own example -- the client has to tolerate this rather
        // than assume the field is always present.
        const string json = """
            {
              "numero_paginas": 1, "total_registros": "1", "pagina_corrente": 0,
              "boletos": [
                {
                  "nosso_numero": 1, "valor_boleto": "100.00", "valor_pagamento": "100.00",
                  "codigo_associado": 1, "nome_associado": "A",
                  "data_pagamento": "2026-09-09", "veiculos": []
                }
              ]
            }
            """;
        var (client, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        var page = await client.ListarBoletosPeriodoAsync(
            "token-usuario", new HinovaBoletoPeriodoFiltro(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 100, 0), CancellationToken.None);

        Assert.Null(Assert.Single(page.Boletos).Vencimento);
    }
}
