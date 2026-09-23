namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class HierarchyTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private async Task<HashSet<(Guid, Guid, int)>> PathsAsync(Guid tenantId)
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        var rows = await conn.QueryAsync<(Guid, Guid, int)>(
            "select ancestor_id, descendant_id, depth from hierarchy_paths where tenant_id = @tenantId",
            new { tenantId });
        return rows.ToHashSet();
    }

    [Fact]
    public async Task Inserting_users_builds_closure_paths()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);

        var expected = new HashSet<(Guid, Guid, int)>
        {
            (joao, joao, 0), (maria, maria, 0), (pedro, pedro, 0),
            (joao, maria, 1), (maria, pedro, 1), (joao, pedro, 2),
        };
        Assert.True(expected.SetEquals(await PathsAsync(t)));
    }

    [Fact]
    public async Task Moving_a_user_moves_the_whole_subtree()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);
        var bruno = await _seed.UserAsync(t, "Bruno");

        await _seed.ExecAsync("update users set supervisor_id = @bruno where id = @maria", new { bruno, maria });

        var expected = new HashSet<(Guid, Guid, int)>
        {
            (joao, joao, 0), (bruno, bruno, 0), (maria, maria, 0), (pedro, pedro, 0),
            (maria, pedro, 1), (bruno, maria, 1), (bruno, pedro, 2),
        };
        Assert.True(expected.SetEquals(await PathsAsync(t)));
    }

    [Fact]
    public async Task Removing_the_supervisor_detaches_the_subtree()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);

        await _seed.ExecAsync("update users set supervisor_id = null where id = @maria", new { maria });

        var expected = new HashSet<(Guid, Guid, int)> { (joao, joao, 0), (maria, maria, 0) };
        Assert.True(expected.SetEquals(await PathsAsync(t)));
    }

    [Fact]
    public async Task Cycles_are_rejected()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);

        var ex = await DbExtensions.ThrowsPgAsync(() =>
            _seed.ExecAsync("update users set supervisor_id = @pedro where id = @joao", new { pedro, joao }));
        Assert.Equal("hierarchy.cycle", ex.MessageText);
    }

    [Fact]
    public async Task Self_supervision_is_rejected()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");

        var ex = await DbExtensions.ThrowsPgAsync(() =>
            _seed.ExecAsync("update users set supervisor_id = @joao where id = @joao", new { joao }));
        Assert.Equal("hierarchy.cycle", ex.MessageText);
    }

    [Fact]
    public async Task Supervisor_must_belong_to_the_same_tenant()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var a = await _seed.UserAsync(t1, "A");

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.UserAsync(t2, "B", a));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }
}
