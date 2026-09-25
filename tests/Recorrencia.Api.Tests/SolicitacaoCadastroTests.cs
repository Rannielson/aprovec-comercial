using Recorrencia.Api.Cli;
using Recorrencia.Api.Integracoes;

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

    public sealed record SolicitacaoDto(Guid Id, string Nome, string Cpf, string Celular, string Email, string Cep,
        string Logradouro, string Numero, string? Complemento, string Bairro, string Cidade, string Estado,
        Guid IndicadorUserId, string IndicadorNome, DateTimeOffset CriadoEm);

    private async Task<(Guid Id, string Email)> SubmitPendingAsync(SeededTenant s, string token, string cpf = "11122233344")
    {
        var email = $"fulano-{Guid.NewGuid():N}@teste.local";
        var anonimo = api.Client(s.Slug);
        var corpo = new
        {
            nome = "Fulano de Tal", cpf, celular = "11999998888", email,
            cep = "30000-000", logradouro = "Rua X", numero = "10", bairro = "Bairro", cidade = "Cidade", estado = "UF",
        };
        var response = await anonimo.PostAsync($"/convite-links/{token}/solicitacoes", corpo);
        await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<IdDto>(ApiClient.Json);
        return (created!.Id, email);
    }

    public sealed record IdDto(Guid Id);

    [Fact]
    public async Task Admin_can_list_pending_solicitacoes()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "310");
        await SubmitPendingAsync(s, token);

        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        var pendentes = await admin.GetJsonAsync<List<SolicitacaoDto>>("/solicitacoes-cadastro");

        Assert.Contains(pendentes, p => p.IndicadorNome == "João Silva");
    }

    [Fact]
    public async Task Consultor_without_the_admin_permissions_cannot_list()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var response = await joao.GetAsync("/solicitacoes-cadastro");
        await ApiClient.ExpectAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Approving_creates_the_user_linked_to_the_indicador_and_sends_the_invite_email()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "311");
        var (id, email) = await SubmitPendingAsync(s, token, "99988877766");

        api.Hinova.ProximoCodigoCadastrado = "555";
        api.Hinova.BuscarPorChave["311"] = new HinovaVoluntarioDetalhe("311", "João Silva", "11111111111", ["1"]);
        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        await admin.PutAsync("/integracoes/hinova/credenciais", new { usuario = "usuario", senha = "senha", tokenSga = "token" });
        var response = await admin.PostAsync($"/solicitacoes-cadastro/{id}/aprovar");
        await ApiClient.ExpectAsync(response, HttpStatusCode.OK);

        var mapeamento = await api.SqlScalarAsync<string>(
            "select codigo_voluntario from hinova_voluntario_mapping where tenant_id = @tenant and codigo_voluntario = '555'",
            new { tenant = s.TenantId });
        Assert.Equal("555", mapeamento);

        var supervisor = await api.SqlScalarAsync<Guid>(
            "select supervisor_id from users where tenant_id = @tenant and email = @email", new { tenant = s.TenantId, email });
        Assert.Equal(s.Joao, supervisor);

        Assert.NotNull(api.Hinova.UltimoCadastro);
        Assert.Equal("311", api.Hinova.UltimoCadastro!.CodigoVoluntarioVinculado);

        Assert.Equal("Convite de acesso", api.Emails.LastTo(email)?.Subject);
    }

    [Fact]
    public async Task Approving_blocks_when_the_cpf_already_exists_in_hinova()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "312");
        var (id, _) = await SubmitPendingAsync(s, token, "88877766655");
        api.Hinova.BuscarPorChave["88877766655"] = new HinovaVoluntarioDetalhe("777", "Outra Pessoa", "88877766655", ["1"]);

        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        await admin.PutAsync("/integracoes/hinova/credenciais", new { usuario = "usuario", senha = "senha", tokenSga = "token" });
        var response = await admin.PostAsync($"/solicitacoes-cadastro/{id}/aprovar");

        await ApiClient.ExpectAsync(response, HttpStatusCode.Conflict);
        Assert.Equal("solicitacao.cpf_ja_cadastrado", await ApiClient.CodeAsync(response));

        var status = await api.SqlScalarAsync<string>("select status from solicitacoes_cadastro where id = @id", new { id });
        Assert.Equal("pendente", status);
    }

    [Fact]
    public async Task Rejecting_marks_the_solicitacao_and_creates_no_user()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "313");
        var (id, email) = await SubmitPendingAsync(s, token, "77766655544");

        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        var response = await admin.PostAsync($"/solicitacoes-cadastro/{id}/rejeitar");
        await ApiClient.ExpectAsync(response, HttpStatusCode.NoContent);

        var status = await api.SqlScalarAsync<string>("select status from solicitacoes_cadastro where id = @id", new { id });
        Assert.Equal("rejeitado", status);
        var count = await api.SqlScalarAsync<int>("select count(*) from users where tenant_id = @tenant and email = @email", new { tenant = s.TenantId, email });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Approving_an_already_resolved_solicitacao_returns_409()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "314");
        var (id, _) = await SubmitPendingAsync(s, token, "66655544433");
        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        await admin.PostAsync($"/solicitacoes-cadastro/{id}/rejeitar");

        var response = await admin.PostAsync($"/solicitacoes-cadastro/{id}/aprovar");
        await ApiClient.ExpectAsync(response, HttpStatusCode.Conflict);
        Assert.Equal("solicitacao.ja_resolvida", await ApiClient.CodeAsync(response));
    }
}
