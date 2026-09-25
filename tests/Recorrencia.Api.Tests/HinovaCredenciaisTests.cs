using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class HinovaCredenciaisTests(ApiFixture api)
{
    public sealed record StatusDto(bool Configurado, DateTimeOffset? AtualizadoEm, string? AtualizadoPor);

    private async Task<ApiClient> LoginAsAdminAsync(SeededTenant s)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    [Fact]
    public async Task Status_starts_as_not_configured()
    {
        var s = await api.SeedAsync();
        var status = await (await LoginAsAdminAsync(s)).GetJsonAsync<StatusDto>("/integracoes/hinova/credenciais");
        Assert.False(status.Configurado);
    }

    [Fact]
    public async Task Saving_valid_credentials_marks_status_as_configured()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        await ApiClient.ExpectAsync(
            await admin.PutAsync("/integracoes/hinova/credenciais", new { usuario = "usuario", senha = "senha", tokenSga = "token" }),
            HttpStatusCode.NoContent);

        var status = await admin.GetJsonAsync<StatusDto>("/integracoes/hinova/credenciais");
        Assert.True(status.Configurado);
        Assert.Equal("Administração", status.AtualizadoPor);
    }

    [Fact]
    public async Task Saving_credentials_the_hinova_rejects_returns_bad_request_and_does_not_save()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        api.Hinova.RejectAuth = true;
        try
        {
            var response = await admin.PutAsync("/integracoes/hinova/credenciais", new { usuario = "usuario", senha = "senha", tokenSga = "token" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("hinova.credenciais_invalidas", await ApiClient.CodeAsync(response));

            var status = await admin.GetJsonAsync<StatusDto>("/integracoes/hinova/credenciais");
            Assert.False(status.Configurado);
        }
        finally
        {
            api.Hinova.RejectAuth = false;
        }
    }

    [Theory]
    [InlineData("GET", "/integracoes/hinova/credenciais")]
    [InlineData("PUT", "/integracoes/hinova/credenciais")]
    [InlineData("GET", "/integracoes/hinova/voluntarios")]
    [InlineData("GET", "/integracoes/hinova/mapeamentos")]
    [InlineData("POST", "/integracoes/hinova/mapeamentos")]
    [InlineData("DELETE", "/integracoes/hinova/mapeamentos/00000000-0000-0000-0000-000000000000")]
    public async Task Missing_permission_returns_forbidden_on_every_endpoint(string method, string path)
    {
        var s = await api.SeedAsync();
        var roleId = Guid.NewGuid();
        await api.SqlAsync(
            """
            delete from user_roles where tenant_id = @tenant and user_id = @pedro;
            insert into roles (id, tenant_id, name) values (@roleId, @tenant, 'Sem integrações');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @roleId, 'usuarios.convidar', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @pedro, @roleId);
            """,
            new { roleId, tenant = s.TenantId, pedro = s.Pedro });
        var pedro = api.Client(s.Slug);
        await pedro.LoginAsync($"pedro@{s.Slug}.local", ApiFixture.Password);

        // PUT/POST bind a required JSON body before the permission endpoint filter runs, so an
        // empty body would fail model binding with 400 before ever reaching the filter — send a
        // well-formed body so binding succeeds and the permission check is what actually fires.
        object? body = method switch
        {
            "PUT" => new { usuario = "u", senha = "s", tokenSga = "t" },
            "POST" => new { userId = Guid.NewGuid(), codigoVoluntario = "1", nomeHinova = "Nome", cpfHinova = "00000000000" },
            _ => null,
        };

        var response = await pedro.SendAsync(new HttpMethod(method), path, body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
