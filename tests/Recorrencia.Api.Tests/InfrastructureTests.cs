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
}
