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
}
