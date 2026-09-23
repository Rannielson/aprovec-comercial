using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class CommissionPlanTests(ApiFixture api)
{
    public sealed record RuleDto(Guid Id, string Type, decimal Rate, int? Level, Guid? GroupId);
    public sealed record PlanDto(Guid Id, string Name, DateOnly EffectiveFrom, string Status, List<RuleDto> Rules);
    public sealed record GroupDto(Guid Id, string Name, List<Guid> Members);
    public sealed record CreatedDto(Guid Id);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    private static object ThreeLevels(string effectiveFrom = "2026-10-01") => new
    {
        name = "Três níveis",
        effectiveFrom,
        rules = new object[]
        {
            new { type = "own", rate = 0.05m },
            new { type = "upline", rate = 0.03m, level = 1 },
            new { type = "upline", rate = 0.02m, level = 2 },
            new { type = "upline", rate = 0.01m, level = 3 },
        },
    };

    [Fact]
    public async Task Admin_sees_the_provisioned_plan_and_consultor_does_not()
    {
        var s = await api.SeedAsync();
        var plans = await (await LoginAsync(s, "admin")).GetJsonAsync<List<PlanDto>>("/commission-plans");

        var plan = Assert.Single(plans);
        Assert.Equal(("ativo", new DateOnly(2026, 8, 1), 3), (plan.Status, plan.EffectiveFrom, plan.Rules.Count));

        var consultor = await (await LoginAsync(s, "joao")).GetAsync("/commission-plans");
        Assert.Equal(HttpStatusCode.Forbidden, consultor.StatusCode);
    }

    [Fact]
    public async Task New_plan_starts_as_draft_and_can_be_activated()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var created = await admin.PostAsync("/commission-plans", ThreeLevels());
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var draft = (await created.Content.ReadFromJsonAsync<PlanDto>(ApiClient.Json))!;
        Assert.Equal(("rascunho", 4), (draft.Status, draft.Rules.Count));

        var activated = await admin.PostAsync($"/commission-plans/{draft.Id}/activate");
        await ApiClient.ExpectAsync(activated, HttpStatusCode.OK);
        Assert.Equal("ativo", (await activated.Content.ReadFromJsonAsync<PlanDto>(ApiClient.Json))!.Status);

        var delete = await admin.DeleteAsync($"/commission-plans/{draft.Id}");
        Assert.Equal("plan.active_immutable", await ApiClient.CodeAsync(delete));
    }

    [Fact]
    public async Task Two_active_plans_cannot_share_the_effective_date()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var created = await admin.PostAsync("/commission-plans", ThreeLevels("2026-08-01"));
        var draft = (await created.Content.ReadFromJsonAsync<PlanDto>(ApiClient.Json))!;

        var response = await admin.PostAsync($"/commission-plans/{draft.Id}/activate");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("plan.effective_conflict", await ApiClient.CodeAsync(response));
    }

    public static TheoryData<object, string> InvalidPlans => new()
    {
        { new { name = "X", effectiveFrom = "2026-10-15", rules = new[] { new { type = "own", rate = 0.05m } } }, "plan.invalid_effective_from" },
        { new { name = "X", effectiveFrom = "2026-10-01", rules = Array.Empty<object>() }, "plan.empty" },
        { new { name = "X", effectiveFrom = "2026-10-01", rules = new[] { new { type = "upline", rate = 0.02m } } }, "plan.invalid_rule" },
        { new { name = "X", effectiveFrom = "2026-10-01", rules = new[] { new { type = "own", rate = 0m } } }, "plan.invalid_rule" },
        { new { name = "X", effectiveFrom = "2026-10-01", rules = new[] { new { type = "own", rate = 0.12345m } } }, "plan.invalid_rule" },
        { new { name = "X", effectiveFrom = "2026-10-01", rules = new[] { new { type = "bonus", rate = 0.01m } } }, "plan.invalid_rule" },
        { new { name = " ", effectiveFrom = "2026-10-01", rules = new[] { new { type = "own", rate = 0.05m } } }, "plan.name_required" },
    };

    [Theory]
    [MemberData(nameof(InvalidPlans))]
    public async Task Invalid_plans_are_rejected(object body, string code)
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync("/commission-plans", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Groups_can_be_created_and_filled()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var created = await admin.PostAsync("/commission-groups", new { name = "Regional Norte" });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        await ApiClient.ExpectAsync(await admin.PutAsync($"/commission-groups/{id}/members", new { userIds = new[] { s.Joao } }), HttpStatusCode.NoContent);

        var groups = await admin.GetJsonAsync<List<GroupDto>>("/commission-groups");
        Assert.Equal(new List<Guid> { s.Joao }, groups.Single(g => g.Id == id).Members);
        Assert.Equal(new List<Guid> { s.Coord }, groups.Single(g => g.Name == "Coordenação").Members);
    }

    [Fact]
    public async Task Group_member_from_another_tenant_is_rejected()
    {
        var s = await api.SeedAsync();
        var other = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var groups = await admin.GetJsonAsync<List<GroupDto>>("/commission-groups");

        var response = await admin.PutAsync($"/commission-groups/{groups[0].Id}/members", new { userIds = new[] { other.Joao } });
        Assert.Equal("group.invalid_member", await ApiClient.CodeAsync(response));
    }
}
