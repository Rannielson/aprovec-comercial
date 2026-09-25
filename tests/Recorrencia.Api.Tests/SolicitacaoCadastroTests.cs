using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class SolicitacaoCadastroTests(ApiFixture api)
{
    private static readonly object CorpoValido = new
    {
        nome = "Fulano de Tal",
        cpf = "11122233344",
        celular = "11999998888",
        email = "fulano@teste.local",
        cep = "30000-000",
        logradouro = "Rua X",
        numero = "10",
        bairro = "Bairro",
        cidade = "Cidade",
        estado = "UF",
    };

    private async Task<string> LinkTokenAsync(SeededTenant s, Guid userId, string codigo)
    {
        await api.SqlAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @userId, @codigo, 'Nome Hinova', '11111111111', @userId)
            """,
            new { tenant = s.TenantId, userId, codigo });
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);
        var link = await joao.GetJsonAsync<ConviteLinkTests.ConviteLinkDto>("/convite-links/me");
        return link.Url.Split('/').Last();
    }

    [Fact]
    public async Task Submitting_creates_a_pending_solicitacao()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "301");
        var anonimo = api.Client(s.Slug);

        var response = await anonimo.PostAsync($"/convite-links/{token}/solicitacoes", CorpoValido);
        await ApiClient.ExpectAsync(response, HttpStatusCode.Created);

        var count = await api.SqlScalarAsync<int>(
            "select count(*) from solicitacoes_cadastro where tenant_id = @tenant and status = 'pendente'", new { tenant = s.TenantId });
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Submitting_with_an_unknown_token_returns_404()
    {
        var s = await api.SeedAsync();
        var anonimo = api.Client(s.Slug);

        var response = await anonimo.PostAsync("/convite-links/token-invalido/solicitacoes", CorpoValido);
        await ApiClient.ExpectAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("convite.link_invalido", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Submitting_without_a_required_field_returns_400()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "302");
        var anonimo = api.Client(s.Slug);

        var response = await anonimo.PostAsync($"/convite-links/{token}/solicitacoes", new { nome = "Fulano" });
        await ApiClient.ExpectAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("request.invalid", await ApiClient.CodeAsync(response));
    }
}
