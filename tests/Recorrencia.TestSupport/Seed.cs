using Dapper;

namespace Recorrencia.TestSupport;

public sealed class Seed(PostgresFixture db)
{
    public static string UniqueSlug() => "t" + Guid.NewGuid().ToString("N")[..20];

    public async Task ExecAsync(string sql, object? param = null)
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        await conn.ExecuteAsync(sql, param);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        return (await conn.ExecuteScalarAsync<T>(sql, param))!;
    }

    public Task<Guid> TenantAsync(string? slug = null) =>
        ScalarAsync<Guid>(
            "insert into tenants (slug, name) values (@slug, 'Empresa de teste') returning id",
            new { slug = slug ?? UniqueSlug() });

    public Task<Guid> UserAsync(Guid tenantId, string name, Guid? supervisorId = null, string status = "ativo", string? email = null) =>
        ScalarAsync<Guid>(
            """
            insert into users (tenant_id, name, email, password_hash, status, supervisor_id)
            values (@tenantId, @name, @email, @hash, @status, @supervisorId)
            returning id
            """,
            new
            {
                tenantId,
                name,
                email = email ?? $"{Guid.NewGuid():N}@teste.local",
                hash = status == "convidado" ? null : "hash-de-teste",
                status,
                supervisorId,
            });
}
