using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class PlanoCarreiraTests(ApiFixture api)
{
    public sealed record FaixaDto(Guid Id, int QuantidadeMin, int? QuantidadeMax, decimal ValorPorPlaca);
    public sealed record RegraDto(Guid Id, string Tipo, int? Nivel, decimal Taxa);
    public sealed record PlanoDto(Guid Id, string Name, string Status, string Classificacao, int JanelaApuracaoDias,
        int? MetaMinimaContratos, string? MetaMinimaFonteData, decimal? BonusExtraValor, bool RecorrenciaAtiva,
        List<FaixaDto> Faixas, List<RegraDto> RegrasRecorrencia);

    private async Task<ApiClient> LoginAsAdminAsync(SeededTenant s)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    private static readonly object CorpoValido = new
    {
        name = "Consultor CLT Externo",
        classificacao = "clt_externo",
        janelaApuracaoDias = 30,
        metaMinimaContratos = (int?)null,
        metaMinimaFonteData = (string?)null,
        bonusExtraValor = 1500m,
        recorrenciaAtiva = true,
        faixas = new[]
        {
            new { quantidadeMin = 1, quantidadeMax = (int?)14, valorPorPlaca = 33m },
            new { quantidadeMin = 15, quantidadeMax = (int?)19, valorPorPlaca = 40m },
            new { quantidadeMin = 30, quantidadeMax = (int?)null, valorPorPlaca = 60m },
        },
        regrasRecorrencia = new[]
        {
            new { tipo = "propria", nivel = (int?)null, taxa = 0.07m },
            new { tipo = "upline", nivel = (int?)1, taxa = 0.02m },
        },
    };

    [Fact]
    public async Task Administrador_creates_and_lists_a_plano()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var created = await admin.PostAsync("/planos-carreira", CorpoValido);
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var plano = (await created.Content.ReadFromJsonAsync<PlanoDto>(ApiClient.Json))!;
        Assert.Equal("ativo", plano.Status);
        Assert.Equal(3, plano.Faixas.Count);
        Assert.Equal(2, plano.RegrasRecorrencia.Count);

        var list = await admin.GetJsonAsync<List<PlanoDto>>("/planos-carreira");
        Assert.Contains(list, p => p.Id == plano.Id);
    }

    [Fact]
    public async Task Creating_without_name_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var response = await admin.PostAsync("/planos-carreira", new { name = "", classificacao = "clt_interno", janelaApuracaoDias = 30, recorrenciaAtiva = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("plano_carreira.name_required", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Meta_minima_with_only_one_field_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var response = await admin.PostAsync("/planos-carreira", new
        {
            name = "X", classificacao = "clt_interno", janelaApuracaoDias = 30, recorrenciaAtiva = false,
            metaMinimaContratos = 10,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("plano_carreira.meta_minima_incompleta", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Recorrencia_ativa_without_regras_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var response = await admin.PostAsync("/planos-carreira", new
        {
            name = "X", classificacao = "clt_interno", janelaApuracaoDias = 30, recorrenciaAtiva = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("plano_carreira.recorrencia_inconsistente", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Consultor_cannot_create_a_plano()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var response = await joao.PostAsync("/planos-carreira", CorpoValido);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Updating_a_plano_replaces_its_faixas_and_regras()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        var created = await admin.PostAsync("/planos-carreira", CorpoValido);
        var plano = (await created.Content.ReadFromJsonAsync<PlanoDto>(ApiClient.Json))!;

        var response = await admin.PutAsync($"/planos-carreira/{plano.Id}", new
        {
            name = "Renomeado", status = "inativo", classificacao = "clt_externo", janelaApuracaoDias = 45,
            metaMinimaContratos = (int?)null, metaMinimaFonteData = (string?)null, bonusExtraValor = (decimal?)null,
            recorrenciaAtiva = false,
            faixas = new[] { new { quantidadeMin = 1, quantidadeMax = (int?)null, valorPorPlaca = 50m } },
            regrasRecorrencia = Array.Empty<object>(),
        });

        await ApiClient.ExpectAsync(response, HttpStatusCode.OK);
        var atualizado = (await response.Content.ReadFromJsonAsync<PlanoDto>(ApiClient.Json))!;
        Assert.Equal("Renomeado", atualizado.Name);
        Assert.Equal("inativo", atualizado.Status);
        Assert.Single(atualizado.Faixas);
        Assert.Empty(atualizado.RegrasRecorrencia);
    }

    [Fact]
    public async Task Deleting_a_plano_in_use_is_a_conflict()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        var created = await admin.PostAsync("/planos-carreira", CorpoValido);
        var plano = (await created.Content.ReadFromJsonAsync<PlanoDto>(ApiClient.Json))!;
        await ApiClient.ExpectAsync(
            await admin.PutAsync($"/users/{s.Joao}/plano-carreira", new { planoCarreiraId = plano.Id }),
            HttpStatusCode.NoContent);

        var response = await admin.DeleteAsync($"/planos-carreira/{plano.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("plano_carreira.em_uso", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Deleting_an_unused_plano_succeeds()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        var created = await admin.PostAsync("/planos-carreira", CorpoValido);
        var plano = (await created.Content.ReadFromJsonAsync<PlanoDto>(ApiClient.Json))!;

        await ApiClient.ExpectAsync(await admin.DeleteAsync($"/planos-carreira/{plano.Id}"), HttpStatusCode.NoContent);

        var list = await admin.GetJsonAsync<List<PlanoDto>>("/planos-carreira");
        Assert.DoesNotContain(list, p => p.Id == plano.Id);
    }
}
