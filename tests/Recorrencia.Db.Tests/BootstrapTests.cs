namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class BootstrapTests(PostgresFixture db)
{
    [Fact]
    public async Task Roles_have_expected_rls_attributes()
    {
        await using var conn = await db.OpenAsync(db.SuperuserConnectionString);
        var roles = (await conn.QueryAsync<(string Name, bool Bypass)>(
                "select rolname, rolbypassrls from pg_roles where rolname in ('app_owner', 'app_user', 'app_superadmin')"))
            .ToDictionary(r => r.Name, r => r.Bypass);

        Assert.False(roles["app_user"]);
        Assert.True(roles["app_superadmin"]);
        Assert.True(roles["app_owner"]);
    }

    [Fact]
    public async Task Extensions_are_installed()
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        var extensions = (await conn.QueryAsync<string>("select extname from pg_extension")).ToList();

        Assert.Contains("vector", extensions);
        Assert.Contains("citext", extensions);
    }

    [Fact]
    public async Task App_user_has_no_context_by_default()
    {
        var (tenant, user) = await db.AsAppUserAsync(null, null, (c, t) =>
            c.QuerySingleAsync<(Guid?, Guid?)>("select app.current_tenant(), app.current_user_id()", transaction: t));

        Assert.Null(tenant);
        Assert.Null(user);
    }

    [Fact]
    public async Task Context_is_visible_inside_the_transaction()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var (tenant, user) = await db.AsAppUserAsync(tenantId, userId, (c, t) =>
            c.QuerySingleAsync<(Guid?, Guid?)>("select app.current_tenant(), app.current_user_id()", transaction: t));

        Assert.Equal(tenantId, tenant);
        Assert.Equal(userId, user);
    }

    [Fact]
    public void Migrations_are_idempotent()
    {
        Recorrencia.Db.Migrator.Run(db.OwnerConnectionString, log: false);
    }
}
