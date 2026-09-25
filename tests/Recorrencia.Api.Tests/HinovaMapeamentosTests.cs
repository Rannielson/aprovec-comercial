using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class HinovaMapeamentosTests(ApiFixture api)
{
    public sealed record VoluntarioDto(string Codigo, string Nome, string Cpf, string? Telefone, List<string> Cooperativas, bool JaVinculado, string? VinculadoA);
    public sealed record MapeamentoDto(Guid UserId, string UserName, string CodigoVoluntario, string NomeHinova, string CpfHinova, DateTimeOffset MappedAt);

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

    [Fact]
    public async Task Listing_voluntarios_without_credentials_configured_is_a_bad_request()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var response = await admin.GetAsync("/integracoes/hinova/voluntarios");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("hinova.nao_configurado", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Listing_voluntarios_filters_by_name_and_marks_mapped_ones()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);

        var all = await admin.GetJsonAsync<List<VoluntarioDto>>("/integracoes/hinova/voluntarios");
        Assert.Equal(2, all.Count);
        var ana = all.Single(v => v.Codigo == "101");
        Assert.True(ana.JaVinculado);
        Assert.Equal("João Silva", ana.VinculadoA);
        Assert.Equal("(31)99111-2233", ana.Telefone);
        Assert.Equal(["Cooperativa Central"], ana.Cooperativas);

        var filtered = await admin.GetJsonAsync<List<VoluntarioDto>>("/integracoes/hinova/voluntarios?query=bruno");
        Assert.Equal("Bruno Costa Lima", Assert.Single(filtered).Nome);
    }

    [Fact]
    public async Task Mapping_the_same_user_twice_is_a_conflict()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);

        var response = await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "102", nomeHinova = "Bruno Costa Lima", cpfHinova = "22233344455" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hinova.vinculo_duplicado", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Listing_and_removing_mapeamentos()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);

        var list = await admin.GetJsonAsync<List<MapeamentoDto>>("/integracoes/hinova/mapeamentos");
        Assert.Equal("101", Assert.Single(list).CodigoVoluntario);

        await ApiClient.ExpectAsync(await admin.DeleteAsync($"/integracoes/hinova/mapeamentos/{s.Joao}"), HttpStatusCode.NoContent);

        var listAfter = await admin.GetJsonAsync<List<MapeamentoDto>>("/integracoes/hinova/mapeamentos");
        Assert.Empty(listAfter);
    }
}
