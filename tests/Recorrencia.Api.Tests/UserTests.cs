using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Cli;
using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class UserTests(ApiFixture api)
{
    public sealed record UserDto(Guid Id, string Name, string Email, string Status, Guid? SupervisorId, Guid[] RoleIds);
    public sealed record PermissionDto(string Key, string? Scope);
    public sealed record MeDto(List<PermissionDto> Permissions);
    public sealed record CreatedDto(Guid Id);
    public sealed record MapeamentoDto(Guid UserId, string UserName, string CodigoVoluntario, string NomeHinova, string CpfHinova, DateTimeOffset MappedAt);

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

    // MailAddress.TryCreate happily parses "Nome <x@y.com>" and, without this check, the
    // ORIGINAL string (display name included) would be stored as the email address.
    [Fact]
    public async Task Email_with_a_display_name_is_rejected()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync("/users",
            new { name = "X", email = $"Alguém <x@{s.Slug}.local>" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("users.invalid_email", await ApiClient.CodeAsync(response));
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
    public async Task Last_profile_manager_cannot_have_role_management_removed_via_set_roles()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PutAsync($"/users/{s.Admin}/roles", new { roleIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("role.last_admin", await ApiClient.CodeAsync(response));
    }

    // Reproduces the TOCTOU race the count-then-fail guard is exposed to without a lock:
    // two concurrent transactions each remove a DIFFERENT one of the tenant's last two
    // role-managers. Under plain READ COMMITTED, each transaction's own uncommitted removal
    // is invisible to the other until commit, so both counts could read "1 manager still
    // active" and both would pass -- leaving zero role-managers once both commit. This test
    // drives the two transactions directly (bypassing the HTTP layer, per the reviewer's
    // guidance) so the interleaving is guaranteed rather than merely likely: both removals are
    // applied -- uncommitted -- before either transaction's guard check runs, then both guard
    // checks run genuinely concurrently via Task.WhenAll. With the per-tenant advisory lock in
    // `RoleGuards.EnsureProfileManagerRemainsAsync`, exactly one of the two must fail.
    [Fact]
    public async Task Concurrently_removing_the_last_two_role_managers_lets_only_one_succeed()
    {
        var s = await api.SeedAsync();
        var administrador = await RoleIdAsync(s.TenantId, "administrador");

        // Give Joao the manager role too, so the tenant now has two active role-managers
        // (the seeded Admin, and Joao).
        await api.SqlAsync(
            "insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @joao, @administrador)",
            new { tenant = s.TenantId, joao = s.Joao, administrador });

        var sources = api.Service<DataSources>();

        async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction, Tx Wrapped)> BeginAsync(Guid actingUser)
        {
            var connection = await sources.App.OpenConnectionAsync();
            var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(
                "select set_config('app.tenant_id', @t, true), set_config('app.user_id', @u, true)",
                new { t = s.TenantId.ToString(), u = actingUser.ToString() }, transaction);
            return (connection, transaction, new Tx(connection, transaction));
        }

        var admin = await BeginAsync(s.Admin);
        var joao = await BeginAsync(s.Joao);

        // Each transaction removes a different manager's role-management permission --
        // uncommitted -- before either one runs its guard check.
        await admin.Wrapped.ExecuteAsync(
            "delete from user_roles where tenant_id = @t and user_id = @u and role_id = @r",
            new { t = s.TenantId, u = s.Admin, r = administrador });
        await joao.Wrapped.ExecuteAsync(
            "delete from user_roles where tenant_id = @t and user_id = @u and role_id = @r",
            new { t = s.TenantId, u = s.Joao, r = administrador });

        // Each side must commit or roll back its OWN transaction as soon as its OWN check
        // resolves -- not wait for the other side's result first. Whichever task wins the
        // advisory lock has to commit (releasing the lock) before the other can even acquire
        // it and proceed; gating both commits on the combined Task.WhenAll result would
        // deadlock the two transactions against each other.
        static async Task<bool> CheckThenFinishAsync(Tx tx, NpgsqlTransaction transaction)
        {
            try
            {
                await RoleGuards.EnsureProfileManagerRemainsAsync(tx);
                await transaction.CommitAsync();
                return true;
            }
            catch (ApiProblem e) when (e.Code == "role.last_admin")
            {
                await transaction.RollbackAsync();
                return false;
            }
        }

        var results = await Task.WhenAll(
            CheckThenFinishAsync(admin.Wrapped, admin.Transaction),
            CheckThenFinishAsync(joao.Wrapped, joao.Transaction));
        Assert.Equal(1, results.Count(ok => ok));

        await admin.Connection.DisposeAsync();
        await joao.Connection.DisposeAsync();

        var remainingManagers = await api.SqlScalarAsync<int>(
            """
            select count(distinct u.id)::int
              from users u
              join user_roles ur on ur.user_id = u.id and ur.role_id = @administrador
             where u.tenant_id = @tenant and u.status = 'ativo'
            """,
            new { tenant = s.TenantId, administrador });
        Assert.Equal(1, remainingManagers);
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

    // A role-manager holding ONLY usuarios.gerenciar_perfis must not be able to strip a role
    // assignment from a user when that role grants a permission the actor doesn't personally
    // hold -- removal must be gated exactly like granting is (Cannot_grant_a_role_beyond_own_permissions).
    private async Task<(Guid Gestor, Guid Financeiro)> SeedGestorAndFinanceiroAssignedToPedroAsync(SeededTenant s)
    {
        var gestor = Guid.NewGuid();
        var financeiro = Guid.NewGuid();
        await api.SqlAsync(
            """
            insert into roles (id, tenant_id, name) values (@gestor, @tenant, 'Gestor de perfis');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @gestor, 'usuarios.gerenciar_perfis', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @maria, @gestor);
            insert into roles (id, tenant_id, name) values (@financeiro, @tenant, 'Financeiro');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @financeiro, 'fechamento.confirmar', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @pedro, @financeiro);
            """,
            new { gestor, financeiro, tenant = s.TenantId, maria = s.Maria, pedro = s.Pedro });
        return (gestor, financeiro);
    }

    [Fact]
    public async Task Role_manager_cannot_remove_from_a_user_a_role_granting_a_permission_they_do_not_hold()
    {
        var s = await api.SeedAsync();
        var (_, financeiro) = await SeedGestorAndFinanceiroAssignedToPedroAsync(s);

        var response = await (await LoginAsync(s, "maria")).PutAsync($"/users/{s.Pedro}/roles", new { roleIds = Array.Empty<Guid>() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("role.grant_exceeds_own", await ApiClient.CodeAsync(response));
        var pedro = (await (await LoginAsync(s, "admin")).GetJsonAsync<List<UserDto>>("/users")).Single(u => u.Id == s.Pedro);
        Assert.Contains(financeiro, pedro.RoleIds);
    }

    [Fact]
    public async Task Role_manager_who_holds_the_permission_can_still_remove_the_role_from_a_user()
    {
        var s = await api.SeedAsync();
        var (gestor, financeiro) = await SeedGestorAndFinanceiroAssignedToPedroAsync(s);
        await api.SqlAsync(
            "insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @gestor, 'fechamento.confirmar', null)",
            new { tenant = s.TenantId, gestor });

        var response = await (await LoginAsync(s, "maria")).PutAsync($"/users/{s.Pedro}/roles", new { roleIds = Array.Empty<Guid>() });

        await ApiClient.ExpectAsync(response, HttpStatusCode.NoContent);
    }

    // usuarios.convidar alone must not let an inviter place a new user anywhere in the
    // hierarchy: supervisorId drives upline commission, so it needs estrutura.editar just
    // like assigning roles needs usuarios.gerenciar_perfis (the existing gate right above
    // this one in InviteAsync).
    [Fact]
    public async Task Inviter_without_estrutura_editar_cannot_set_a_supervisor()
    {
        var s = await api.SeedAsync();
        var recrutador = Guid.NewGuid();
        await api.SqlAsync(
            """
            insert into roles (id, tenant_id, name) values (@recrutador, @tenant, 'Recrutador');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @recrutador, 'usuarios.convidar', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @maria, @recrutador);
            """,
            new { recrutador, tenant = s.TenantId, maria = s.Maria });

        var response = await (await LoginAsync(s, "maria")).PostAsync("/users",
            new { name = "Nova", email = $"nova@{s.Slug}.local", supervisorId = s.Joao });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Inviter_without_estrutura_editar_can_still_invite_without_a_supervisor()
    {
        var s = await api.SeedAsync();
        var recrutador = Guid.NewGuid();
        await api.SqlAsync(
            """
            insert into roles (id, tenant_id, name) values (@recrutador, @tenant, 'Recrutador');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @recrutador, 'usuarios.convidar', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @maria, @recrutador);
            """,
            new { recrutador, tenant = s.TenantId, maria = s.Maria });

        var response = await (await LoginAsync(s, "maria")).PostAsync("/users",
            new { name = "Nova", email = $"nova@{s.Slug}.local" });

        await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
    }

    // Positive control: an inviter who DOES hold estrutura.editar (the seeded admin holds
    // every permission) can still set a supervisor exactly as before.
    [Fact]
    public async Task Inviter_with_estrutura_editar_can_set_a_supervisor()
    {
        var s = await api.SeedAsync();

        var response = await (await LoginAsync(s, "admin")).PostAsync("/users",
            new { name = "Nova", email = $"nova2@{s.Slug}.local", supervisorId = s.Joao });

        await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
    }

    [Fact]
    public async Task Inviting_with_hinova_fields_creates_the_mapping_in_the_same_call()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var consultor = await RoleIdAsync(s.TenantId, "consultor");
        var email = $"nova-hinova@{s.Slug}.local";

        var created = await admin.PostAsync("/users", new
        {
            name = "Nova Vinculada",
            email,
            roleIds = new[] { consultor },
            codigoVoluntario = "555",
            nomeHinova = "Nova Vinculada Hinova",
            cpfHinova = "11122233344",
        });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        var mapeamentos = await admin.GetJsonAsync<List<MapeamentoDto>>("/integracoes/hinova/mapeamentos");
        var mapping = Assert.Single(mapeamentos, m => m.UserId == id);
        Assert.Equal("555", mapping.CodigoVoluntario);
        Assert.Equal("Nova Vinculada Hinova", mapping.NomeHinova);
    }

    [Fact]
    public async Task Inviting_with_a_codigo_voluntario_already_linked_creates_neither_user_nor_mapping()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var consultor = await RoleIdAsync(s.TenantId, "consultor");

        await ApiClient.ExpectAsync(await admin.PostAsync("/users", new
        {
            name = "Primeiro",
            email = $"primeiro@{s.Slug}.local",
            roleIds = new[] { consultor },
            codigoVoluntario = "777",
            nomeHinova = "Primeiro Hinova",
            cpfHinova = "11111111111",
        }), HttpStatusCode.Created);

        var duplicateEmail = $"segundo@{s.Slug}.local";
        var response = await admin.PostAsync("/users", new
        {
            name = "Segundo",
            email = duplicateEmail,
            roleIds = new[] { consultor },
            codigoVoluntario = "777",
            nomeHinova = "Segundo Hinova",
            cpfHinova = "22222222222",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hinova.vinculo_duplicado", await ApiClient.CodeAsync(response));

        var users = await admin.GetJsonAsync<List<UserDto>>("/users");
        Assert.DoesNotContain(users, u => u.Email == duplicateEmail);
    }

    [Fact]
    public async Task Inviting_with_hinova_fields_without_the_permission_is_forbidden()
    {
        var s = await api.SeedAsync();
        var roleId = Guid.NewGuid();
        await api.SqlAsync(
            """
            delete from user_roles where tenant_id = @tenant and user_id = @pedro;
            insert into roles (id, tenant_id, name) values (@roleId, @tenant, 'Convida sem Hinova');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @roleId, 'usuarios.convidar', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @pedro, @roleId);
            """,
            new { roleId, tenant = s.TenantId, pedro = s.Pedro });
        var pedro = await LoginAsync(s, "pedro");

        var response = await pedro.PostAsync("/users", new
        {
            name = "Sem Permissao",
            email = $"sem-permissao@{s.Slug}.local",
            codigoVoluntario = "888",
            nomeHinova = "Sem Permissao Hinova",
            cpfHinova = "33333333333",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Inviting_without_hinova_fields_is_unchanged()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var consultor = await RoleIdAsync(s.TenantId, "consultor");

        var response = await admin.PostAsync("/users", new
        {
            name = "Sem Hinova",
            email = $"sem-hinova@{s.Slug}.local",
            roleIds = new[] { consultor },
        });

        await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;
        var mapeamentos = await admin.GetJsonAsync<List<MapeamentoDto>>("/integracoes/hinova/mapeamentos");
        Assert.DoesNotContain(mapeamentos, m => m.UserId == id);
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

    [Fact]
    public async Task Creating_with_hinova_fields_links_the_mapping_in_the_same_request()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var response = await admin.PostAsync("/users", new
        {
            name = "Ana Paula Ferreira",
            email = $"ana@{s.Slug}.local",
            supervisorId = s.Joao,
            codigoVoluntario = "101",
            nomeHinova = "Ana Paula Ferreira",
            cpfHinova = "11122233344",
        });
        await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        var mapped = await api.SqlScalarAsync<string>(
            "select codigo_voluntario from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @id",
            new { tenant = s.TenantId, id });
        Assert.Equal("101", mapped);
    }

    [Fact]
    public async Task Duplicate_codigo_voluntario_returns_conflict_and_creates_nothing()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        await ApiClient.ExpectAsync(await admin.PostAsync("/users", new
        {
            name = "Ana Paula Ferreira",
            email = $"ana@{s.Slug}.local",
            codigoVoluntario = "101",
            nomeHinova = "Ana Paula Ferreira",
            cpfHinova = "11122233344",
        }), HttpStatusCode.Created);

        var response = await admin.PostAsync("/users", new
        {
            name = "Outra Pessoa",
            email = $"outra@{s.Slug}.local",
            codigoVoluntario = "101",
            nomeHinova = "Ana Paula Ferreira",
            cpfHinova = "11122233344",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hinova.vinculo_duplicado", await ApiClient.CodeAsync(response));
        var count = await api.SqlScalarAsync<int>(
            "select count(*)::int from users where tenant_id = @tenant and email = @email",
            new { tenant = s.TenantId, email = $"outra@{s.Slug}.local" });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Hinova_fields_without_integracoes_gerenciar_are_forbidden()
    {
        var s = await api.SeedAsync();
        var recrutador = Guid.NewGuid();
        await api.SqlAsync(
            """
            insert into roles (id, tenant_id, name) values (@recrutador, @tenant, 'Recrutador');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @recrutador, 'usuarios.convidar', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @maria, @recrutador);
            """,
            new { recrutador, tenant = s.TenantId, maria = s.Maria });

        var response = await (await LoginAsync(s, "maria")).PostAsync("/users", new
        {
            name = "Nova",
            email = $"nova@{s.Slug}.local",
            codigoVoluntario = "101",
            nomeHinova = "Nova",
            cpfHinova = "11122233344",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Creating_without_hinova_fields_still_works_exactly_as_before()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var response = await admin.PostAsync("/users", new { name = "Sem Hinova", email = $"semhinova@{s.Slug}.local" });

        await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
    }

    [Fact]
    public async Task Partial_hinova_fields_are_rejected_with_a_clean_error()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var response = await admin.PostAsync("/users", new
        {
            name = "Incompleto",
            email = $"incompleto@{s.Slug}.local",
            codigoVoluntario = "999",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("hinova.campos_incompletos", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Creating_with_a_password_activates_immediately_without_email()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var email = $"direta@{s.Slug}.local";

        var created = await admin.PostAsync("/users", new { name = "Cadastro Direto", email, password = "senha-inicial-123" });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        Assert.Null(api.Emails.LastTo(email));

        var direta = api.Client(s.Slug);
        await direta.LoginAsync(email, "senha-inicial-123");

        var sees = await admin.GetJsonAsync<List<UserDto>>("/users");
        Assert.Contains(sees, u => u.Id == id && u.Status == "ativo");
    }

    [Fact]
    public async Task Creating_with_a_weak_password_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var email = $"fraca@{s.Slug}.local";

        var response = await admin.PostAsync("/users", new { name = "X", email, password = "curta" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.weak_password", await ApiClient.CodeAsync(response));
        Assert.Equal(0, await api.SqlScalarAsync<int>("select count(*) from users where email = @email", new { email }));
    }

    [Fact]
    public async Task Creating_with_a_password_and_hinova_fields_links_the_mapping_too()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var email = $"direta-hinova@{s.Slug}.local";

        var created = await admin.PostAsync("/users", new
        {
            name = "Direta Hinova", email, password = "senha-inicial-123",
            codigoVoluntario = "301", nomeHinova = "Direta Hinova", cpfHinova = "99988877766",
        });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        Assert.Equal("301", await api.SqlScalarAsync<string>(
            "select codigo_voluntario from hinova_voluntario_mapping where user_id = @id", new { id }));
    }
}
