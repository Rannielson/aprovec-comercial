using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class InfrastructureTests(ApiFixture api)
{
    [Fact]
    public async Task Health_does_not_require_the_internal_key()
    {
        var response = await api.Factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Database_sets_the_transaction_context()
    {
        var db = api.Service<Database>();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        var (t, u) = await db.InTenantAsync(tenant, user,
            tx => tx.QuerySingleAsync<(Guid?, Guid?)>("select app.current_tenant(), app.current_user_id()"),
            CancellationToken.None);

        Assert.Equal(tenant, t);
        Assert.Equal(user, u);
    }

    [Fact]
    public async Task Anonymous_transactions_have_no_context()
    {
        var db = api.Service<Database>();
        var tenant = await db.AnonymousAsync(tx => tx.ExecuteScalarAsync<Guid?>("select app.current_tenant()"), CancellationToken.None);
        Assert.Null(tenant);
    }

    // RouteHandlerOptions.ThrowOnBadRequest defaults to true only in Development. The test
    // host runs under "Testing" (see ApiFixture), so this exercises exactly the non-Development
    // path the finding is about: without the fix, a malformed body bypasses
    // ErrorHandlingMiddleware entirely and comes back as a bare 400 with no body/content-type.
    [Fact]
    public async Task Malformed_json_body_is_still_problem_details_shaped()
    {
        var s = await api.SeedAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/login");
        request.Headers.Add("X-Internal-Key", ApiFixture.InternalKey);
        request.Headers.Add("X-Tenant-Host", s.Slug);
        request.Headers.Add("X-Client-Ip", "10.0.0.1");
        request.Content = new StringContent("{ isso nao e json", System.Text.Encoding.UTF8, "application/json");

        var response = await api.Factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("request.invalid", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Missing_required_query_parameter_is_problem_details_shaped()
    {
        var s = await api.SeedAsync();
        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);

        // beneficiaryId is a required (non-nullable Guid) query parameter with no `?` supplied.
        var response = await admin.GetAsync("/commissions/2026-09/entries");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("request.invalid", await ApiClient.CodeAsync(response));
    }
}
