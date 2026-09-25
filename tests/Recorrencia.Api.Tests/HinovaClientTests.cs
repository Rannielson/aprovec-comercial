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
}
