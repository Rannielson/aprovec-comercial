using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class RoleTests(ApiFixture api)
{
    public sealed record CatalogPermission(string Key, string Name, bool Scoped);
    public sealed record CatalogModule(string Key, string Name, bool Enabled, List<CatalogPermission> Permissions);
    public sealed record RolePermission(string Key, string? Scope);
    public sealed record RoleDto(Guid Id, string Name, string? SourceTemplateKey, List<RolePermission> Permissions);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    [Fact]
    public async Task Catalog_lists_modules_and_permissions()
    {
        var s = await api.SeedAsync();
        var catalog = await (await LoginAsync(s, "admin")).GetJsonAsync<List<CatalogModule>>("/permissions");

        Assert.Equal(new[] { "carteira", "comissoes", "fechamento", "estrutura", "regras_comissao", "usuarios" }, catalog.Select(m => m.Key));
        Assert.Equal(14, catalog.Sum(m => m.Permissions.Count));

        var consultor = await (await LoginAsync(s, "joao")).GetAsync("/permissions");
        Assert.Equal(HttpStatusCode.Forbidden, consultor.StatusCode);
    }

    [Fact]
    public async Task Admin_creates_a_custom_role()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var created = await admin.PostAsync("/roles", new
        {
            name = "Supervisor",
            permissions = new object[]
            {
                new { key = "carteira.visualizar", scope = "direct" },
                new { key = "estrutura.visualizar", scope = "subtree" },
                new { key = "fechamento.visualizar", scope = (string?)null },
            },
        });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);

        var roles = await admin.GetJsonAsync<List<RoleDto>>("/roles");
        var supervisor = Assert.Single(roles, r => r.Name == "Supervisor");
        Assert.Contains(new RolePermission("estrutura.visualizar", "subtree"), supervisor.Permissions);
        Assert.Equal(4, roles.Count);
    }

    [Theory]
    [InlineData("Consultor", "carteira.visualizar", "own", HttpStatusCode.Conflict, "role.name_taken")]
    [InlineData("Novo", "carteira.visualizar", null, HttpStatusCode.BadRequest, "role.scope_required")]
    [InlineData("Novo", "usuarios.convidar", "own", HttpStatusCode.BadRequest, "role.scope_not_allowed")]
    [InlineData("Novo", "carteira.visualizar", "universo", HttpStatusCode.BadRequest, "role.invalid_scope")]
    [InlineData("Novo", "carteira.apagar", null, HttpStatusCode.BadRequest, "role.invalid_permission")]
    [InlineData(" ", "carteira.visualizar", "own", HttpStatusCode.BadRequest, "role.name_required")]
    public async Task Invalid_roles_are_rejected(string name, string key, string? scope, HttpStatusCode status, string code)
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync("/roles", new { name, permissions = new[] { new { key, scope } } });

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Removing_the_last_profile_manager_permission_is_blocked()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var adminRole = (await admin.GetJsonAsync<List<RoleDto>>("/roles")).Single(r => r.SourceTemplateKey == "administrador");

        var response = await admin.PutAsync($"/roles/{adminRole.Id}", new
        {
            name = adminRole.Name,
            permissions = adminRole.Permissions.Where(p => p.Key != "usuarios.gerenciar_perfis").Select(p => new { key = p.Key, scope = p.Scope }),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("role.last_admin", await ApiClient.CodeAsync(response));
        var unchanged = (await admin.GetJsonAsync<List<RoleDto>>("/roles")).Single(r => r.Id == adminRole.Id);
        Assert.Contains(unchanged.Permissions, p => p.Key == "usuarios.gerenciar_perfis");
    }

    [Fact]
    public async Task Deleting_the_only_admin_role_is_blocked()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var adminRole = (await admin.GetJsonAsync<List<RoleDto>>("/roles")).Single(r => r.SourceTemplateKey == "administrador");

        var response = await admin.DeleteAsync($"/roles/{adminRole.Id}");
        Assert.Equal("role.last_admin", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Cannot_create_a_role_broader_than_own_permissions()
    {
        var s = await api.SeedAsync();
        var gestor = Guid.NewGuid();
        await api.SqlAsync(
            """
            insert into roles (id, tenant_id, name) values (@gestor, @tenant, 'Gestor de perfis');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @gestor, 'usuarios.gerenciar_perfis', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @maria, @gestor);
            """,
            new { gestor, tenant = s.TenantId, maria = s.Maria });

        var response = await (await LoginAsync(s, "maria")).PostAsync("/roles",
            new { name = "Amplo", permissions = new[] { new { key = "carteira.visualizar", scope = "tenant" } } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("role.grant_exceeds_own", await ApiClient.CodeAsync(response));
    }
}
