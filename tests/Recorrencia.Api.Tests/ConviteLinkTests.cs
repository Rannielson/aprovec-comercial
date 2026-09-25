using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class ConviteLinkTests(ApiFixture api)
{
    public sealed record ConviteLinkDto(string Url);
    public sealed record ConviteLinkPublicoDto(string IndicadorNome, string TenantNome);

    private async Task VincularHinovaAsync(SeededTenant s, Guid userId, string codigo)
    {
        await api.SqlAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @userId, @codigo, 'Nome Hinova', '11111111111', @userId)
            """,
            new { tenant = s.TenantId, userId, codigo });
    }

    [Fact]
    public async Task Me_returns_409_when_the_user_has_no_hinova_mapping()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var response = await joao.GetAsync("/convite-links/me");
        await ApiClient.ExpectAsync(response, HttpStatusCode.Conflict);
        Assert.Equal("convite.indicador_sem_hinova", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Me_creates_and_then_reuses_the_same_link()
    {
        var s = await api.SeedAsync();
        await VincularHinovaAsync(s, s.Joao, "201");
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var first = await joao.GetJsonAsync<ConviteLinkDto>("/convite-links/me");
        var second = await joao.GetJsonAsync<ConviteLinkDto>("/convite-links/me");

        Assert.Equal(first.Url, second.Url);
        Assert.Contains("/indicar/", first.Url);
    }

    [Fact]
    public async Task Public_lookup_resolves_the_indicador_name_from_the_token()
    {
        var s = await api.SeedAsync();
        await VincularHinovaAsync(s, s.Joao, "202");
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);
        var link = await joao.GetJsonAsync<ConviteLinkDto>("/convite-links/me");
        var token = link.Url.Split('/').Last();

        var anonimo = api.Client(s.Slug);
        var found = await anonimo.GetJsonAsync<ConviteLinkPublicoDto>($"/convite-links/{token}");
        Assert.Equal("João Silva", found.IndicadorNome);
    }

    [Fact]
    public async Task Public_lookup_returns_404_for_an_unknown_token()
    {
        var s = await api.SeedAsync();
        var anonimo = api.Client(s.Slug);
        var response = await anonimo.GetAsync("/convite-links/token-que-nao-existe");
        await ApiClient.ExpectAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("convite.link_invalido", await ApiClient.CodeAsync(response));
    }
}
