using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class FechamentoTests(ApiFixture api)
{
    public sealed record ConfirmDto(string Competencia, string Status, int Entries, decimal Total);
    public sealed record FechamentoDto(string Competencia, string Status, DateTimeOffset? ConfirmadoEm, DateTimeOffset? ProvisionadoEm);
    public sealed record CommissionsDto(string Status, string Source, decimal Total);
    public sealed record PlanDto(Guid Id);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    [Fact]
    public async Task Confirming_freezes_the_month_against_new_plan_versions()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var confirm = await admin.PostAsync("/fechamentos/2026-08/confirm");
        await ApiClient.ExpectAsync(confirm, HttpStatusCode.OK);
        var confirmed = (await confirm.Content.ReadFromJsonAsync<ConfirmDto>(ApiClient.Json))!;
        Assert.Equal(("confirmado", 2740.00m), (confirmed.Status, confirmed.Total));

        var plan = await admin.PostAsync("/commission-plans", new
        {
            name = "Setembro com 10%",
            effectiveFrom = "2026-09-01",
            rules = new object[] { new { type = "own", rate = 0.10m } },
        });
        var planId = (await plan.Content.ReadFromJsonAsync<PlanDto>(ApiClient.Json))!.Id;
        await ApiClient.ExpectAsync(await admin.PostAsync($"/commission-plans/{planId}/activate"), HttpStatusCode.OK);

        var joao = await LoginAsync(s, "joao");
        var august = await joao.GetJsonAsync<CommissionsDto>("/commissions/2026-08");
        Assert.Equal(("confirmado", "snapshot", 1440.00m), (august.Status, august.Source, august.Total));

        var september = await joao.GetJsonAsync<CommissionsDto>("/commissions/2026-09");
        Assert.Equal(("live", 2000.00m), (september.Source, september.Total));
    }

    [Fact]
    public async Task A_month_is_confirmed_only_once()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        await ApiClient.ExpectAsync(await admin.PostAsync("/fechamentos/2026-08/confirm"), HttpStatusCode.OK);

        var again = await admin.PostAsync("/fechamentos/2026-08/confirm");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("fechamento.already_confirmed", await ApiClient.CodeAsync(again));
    }

    [Fact]
    public async Task Consultor_cannot_confirm()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "joao")).PostAsync("/fechamentos/2026-08/confirm");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Provisioning_follows_confirmation()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        Assert.Equal("fechamento.not_found", await ApiClient.CodeAsync(await admin.PostAsync("/fechamentos/2026-08/provision")));

        await ApiClient.ExpectAsync(await admin.PostAsync("/fechamentos/2026-08/confirm"), HttpStatusCode.OK);
        var provision = await admin.PostAsync("/fechamentos/2026-08/provision");
        await ApiClient.ExpectAsync(provision, HttpStatusCode.OK);

        var list = await (await LoginAsync(s, "joao")).GetJsonAsync<List<FechamentoDto>>("/fechamentos");
        var august = Assert.Single(list);
        Assert.Equal(("2026-08", "provisionado"), (august.Competencia, august.Status));
        Assert.NotNull(august.ConfirmadoEm);
        Assert.NotNull(august.ProvisionadoEm);

        var twice = await admin.PostAsync("/fechamentos/2026-08/provision");
        Assert.Equal("fechamento.invalid_transition", await ApiClient.CodeAsync(twice));
    }

    [Fact]
    public async Task Confirming_the_current_or_a_future_competencia_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        // FakeTimeProvider only moves forward (it refuses to rewind, same as a real clock), and
        // this instance is shared by the whole "api" test collection (tests in it run
        // sequentially), so this test only ever advances it - never sets it to a fixed literal
        // that could be earlier than wherever another test already pushed it.
        var now = api.Time.GetUtcNow();
        var currentCompetencia = new DateOnly(now.Year, now.Month, 1).ToString("yyyy-MM");

        // The still-open current competência may not be confirmed.
        var current = await admin.PostAsync($"/fechamentos/{currentCompetencia}/confirm");
        Assert.Equal(HttpStatusCode.BadRequest, current.StatusCode);
        Assert.Equal("fechamento.competencia_not_closed", await ApiClient.CodeAsync(current));

        // Nor may a competência that hasn't happened yet.
        var future = await admin.PostAsync("/fechamentos/2031-01/confirm");
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Equal("fechamento.competencia_not_closed", await ApiClient.CodeAsync(future));

        // Advance past the current competência: it is now closed and confirms as before.
        api.Time.SetUtcNow(now.AddMonths(1));
        var past = await admin.PostAsync($"/fechamentos/{currentCompetencia}/confirm");
        await ApiClient.ExpectAsync(past, HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_scope_narrowed_after_the_permission_filter_is_still_caught_inside_the_transaction()
    {
        var s = await api.SeedAsync();

        // Grant Maria a role with fechamento.confirmar/visualizar plus full-tenant comissoes
        // visibility - as if an admin had just set her up as a second closer.
        var roleId = Guid.NewGuid();
        await api.SqlAsync(
            """
            insert into roles (id, tenant_id, name) values (@roleId, @tenant, 'Confirmador Geral');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values
              (@tenant, @roleId, 'fechamento.confirmar', null),
              (@tenant, @roleId, 'fechamento.visualizar', null),
              (@tenant, @roleId, 'comissoes.visualizar', 'tenant');
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @maria, @roleId);
            """,
            new { roleId, tenant = s.TenantId, maria = s.Maria });

        var maria = await LoginAsync(s, "maria");

        // Her permission "looks fine" - she can confirm a closed month using her full-tenant grant.
        await ApiClient.ExpectAsync(await maria.PostAsync("/fechamentos/2026-07/confirm"), HttpStatusCode.OK);

        // Moments later, an admin narrows her comissoes.visualizar grant back down to 'own' -
        // simulating an edit landing in the window after any earlier, pre-transaction check.
        await api.SqlAsync(
            "update role_permissions set scope = 'own' where tenant_id = @tenant and role_id = @roleId and permission_key = 'comissoes.visualizar'",
            new { tenant = s.TenantId, roleId });

        var afterNarrowing = await maria.PostAsync("/fechamentos/2026-06/confirm");
        Assert.Equal(HttpStatusCode.Forbidden, afterNarrowing.StatusCode);
        Assert.Equal("fechamento.requires_full_visibility", await ApiClient.CodeAsync(afterNarrowing));
    }
}
