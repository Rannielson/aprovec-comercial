namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class TenantTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    [Theory]
    [InlineData("admin")]
    [InlineData("www")]
    [InlineData("api")]
    [InlineData("Maiuscula")]
    [InlineData("-hifen-inicial")]
    [InlineData("com_underscore")]
    public async Task Invalid_slugs_are_rejected(string slug)
    {
        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.TenantAsync(slug));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Email_is_unique_per_tenant_ignoring_case()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        await _seed.UserAsync(t1, "João", email: "Joao@Empresa.com");

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.UserAsync(t1, "Outro João", email: "joao@empresa.com"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);

        await _seed.UserAsync(t2, "João de outra empresa", email: "joao@empresa.com");
    }

    [Fact]
    public async Task Active_user_requires_password_hash()
    {
        var t = await _seed.TenantAsync();
        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "insert into users (tenant_id, name, email, status) values (@t, 'Sem senha', 'x@teste.local', 'ativo')",
            new { t }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Tenant_of_a_user_cannot_change()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t1, "Fixo");

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update users set tenant_id = @t2 where id = @u", new { t2, u }));
        Assert.Equal("users.tenant_immutable", ex.MessageText);
    }
}
