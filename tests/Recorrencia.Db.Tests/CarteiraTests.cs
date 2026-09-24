namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class CarteiraTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

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

    [Fact]
    public async Task User_without_the_permission_gets_an_empty_array()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Sem carteira");
        await _seed.AssignRoleAsync(t, u, await _seed.RoleAsync(t, "Sem carteira", ("usuarios.convidar", null)));

        var ids = await OwnerIdsAsync(t, u);

        Assert.Empty(ids!);
    }
}
