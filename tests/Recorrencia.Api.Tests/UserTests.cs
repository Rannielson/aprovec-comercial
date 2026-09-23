using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class UserTests(ApiFixture api)
{
    public sealed record UserDto(Guid Id, string Name, string Email, string Status, Guid? SupervisorId, Guid[] RoleIds);
    public sealed record PermissionDto(string Key, string? Scope);
    public sealed record MeDto(List<PermissionDto> Permissions);
    public sealed record CreatedDto(Guid Id);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    private Task<Guid> RoleIdAsync(Guid tenantId, string template) =>
        api.SqlScalarAsync<Guid>("select id from roles where tenant_id = @tenantId and source_template_key = @template", new { tenantId, template });

    [Fact]
    public async Task Consultor_lists_self_and_direct_reports()
    {
        var s = await api.SeedAsync();
        var users = await (await LoginAsync(s, "joao")).GetJsonAsync<List<UserDto>>("/users");
        Assert.Equal(new[] { s.Joao, s.Maria }.OrderBy(x => x), users.Select(u => u.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Coordenador_lists_everyone()
    {
        var s = await api.SeedAsync();
        var users = await (await LoginAsync(s, "coordenacao")).GetJsonAsync<List<UserDto>>("/users");
        Assert.Equal(5, users.Count);
    }

    [Fact]
    public async Task Consultor_cannot_invite()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "joao")).PostAsync("/users", new { name = "X", email = $"x@{s.Slug}.local" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Invited_consultor_sets_a_password_and_appears_under_the_supervisor()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var email = $"nova@{s.Slug}.local";
        var consultor = await RoleIdAsync(s.TenantId, "consultor");

        var created = await admin.PostAsync("/users", new { name = "Nova Consultora", email, supervisorId = s.Joao, roleIds = new[] { consultor } });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        var token = CapturingEmailSender.ExtractToken(api.Emails.LastTo(email)!.Body);
        var nova = api.Client(s.Slug);
        await ApiClient.ExpectAsync(await nova.PostAsync("/auth/set-password", new { token, password = "senha-da-nova-1" }), HttpStatusCode.OK);
        await nova.LoginAsync(email, "senha-da-nova-1");

        var joaoSees = await (await LoginAsync(s, "joao")).GetJsonAsync<List<UserDto>>("/users");
        Assert.Contains(joaoSees, u => u.Id == id && u.Status == "ativo");
    }

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync("/users", new { name = "Outro João", email = $"JOAO@{s.Slug}.local" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("users.email_taken", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Supervisor_from_another_tenant_is_rejected()
    {
        var s = await api.SeedAsync();
        var other = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync("/users", new { name = "X", email = $"x@{s.Slug}.local", supervisorId = other.Joao });
        Assert.Equal("users.invalid_supervisor", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Cycles_are_rejected()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PutAsync($"/users/{s.Joao}/supervisor", new { supervisorId = s.Pedro });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hierarchy.cycle", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Changing_the_supervisor_is_audited()
    {
        var s = await api.SeedAsync();
        await ApiClient.ExpectAsync(
            await (await LoginAsync(s, "admin")).PutAsync($"/users/{s.Pedro}/supervisor", new { supervisorId = s.Joao }),
            HttpStatusCode.NoContent);

        Assert.Equal(1, await api.SqlScalarAsync<int>(
            "select count(*)::int from audit_log where entity_id = @id and action = 'users.change_supervisor'", new { id = s.Pedro }));
    }

    [Fact]
    public async Task Deactivating_a_user_revokes_the_sessions()
    {
        var s = await api.SeedAsync();
        var maria = await LoginAsync(s, "maria");

        await ApiClient.ExpectAsync(await (await LoginAsync(s, "admin")).PostAsync($"/users/{s.Maria}/deactivate"), HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.Unauthorized, (await maria.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Last_profile_manager_cannot_be_deactivated()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync($"/users/{s.Admin}/deactivate");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("role.last_admin", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Cannot_grant_a_role_beyond_own_permissions()
    {
        var s = await api.SeedAsync();
        var recrutador = Guid.NewGuid();
        await api.SqlAsync(
            """
            insert into roles (id, tenant_id, name) values (@recrutador, @tenant, 'Recrutador');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values
              (@tenant, @recrutador, 'usuarios.convidar', null),
              (@tenant, @recrutador, 'usuarios.gerenciar_perfis', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @maria, @recrutador);
            """,
            new { recrutador, tenant = s.TenantId, maria = s.Maria });
        var administrador = await RoleIdAsync(s.TenantId, "administrador");

        var response = await (await LoginAsync(s, "maria")).PostAsync("/users",
            new { name = "Novo admin", email = $"novo@{s.Slug}.local", roleIds = new[] { administrador } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("role.grant_exceeds_own", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Setting_roles_changes_effective_permissions()
    {
        var s = await api.SeedAsync();
        var roles = new[] { await RoleIdAsync(s.TenantId, "consultor"), await RoleIdAsync(s.TenantId, "coordenador") };

        await ApiClient.ExpectAsync(
            await (await LoginAsync(s, "admin")).PutAsync($"/users/{s.Joao}/roles", new { roleIds = roles }),
            HttpStatusCode.NoContent);

        var me = await (await LoginAsync(s, "joao")).GetJsonAsync<MeDto>("/me");
        Assert.Contains(new PermissionDto("carteira.visualizar", "tenant"), me.Permissions);
    }
}
