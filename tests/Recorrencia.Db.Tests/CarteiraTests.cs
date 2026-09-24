namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class CarteiraTests(PostgresFixture db)
{
    private Task<Guid[]?> OwnerIdsAsync(Guid tenant, Guid user) =>
        db.AsAppUserAsync(tenant, user, (c, tx) => c.ExecuteScalarAsync<Guid[]>("select app.carteira_owner_ids()", null, tx));

    [Fact]
    public async Task Consultor_sees_only_own_id()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ids = (await OwnerIdsAsync(s.TenantA, s.Joao))!.ToHashSet();
        Assert.True(new HashSet<Guid> { s.Joao }.SetEquals(ids));
    }

    [Fact]
    public async Task Coordenador_sees_the_whole_tenant()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ids = (await OwnerIdsAsync(s.TenantA, s.Coord))!.ToHashSet();
        Assert.True(new HashSet<Guid> { s.Admin, s.Joao, s.Maria, s.Pedro, s.Coord }.SetEquals(ids));
        Assert.DoesNotContain(s.OutsiderAdmin, ids);
    }
}
