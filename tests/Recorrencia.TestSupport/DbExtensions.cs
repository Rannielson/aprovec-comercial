using Dapper;
using Npgsql;
using Xunit;

namespace Recorrencia.TestSupport;

public static class DbExtensions
{
    public static async Task<NpgsqlConnection> OpenAsync(this PostgresFixture db, string connectionString)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    public static Task<T> AsAppUserAsync<T>(this PostgresFixture db, Guid? tenantId, Guid? userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body) =>
        RunAsync(db.AppUserConnectionString, tenantId, userId, body);

    public static Task AsAppUserAsync(this PostgresFixture db, Guid? tenantId, Guid? userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> body) =>
        RunAsync(db.AppUserConnectionString, tenantId, userId, async (c, t) => { await body(c, t); return 0; });

    public static Task<T> AsSuperadminAsync<T>(this PostgresFixture db, Guid? tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body) =>
        RunAsync(db.SuperadminConnectionString, tenantId, null, body);

    public static async Task<PostgresException> ThrowsPgAsync(Func<Task> action) =>
        await Assert.ThrowsAsync<PostgresException>(action);

    private static async Task<T> RunAsync<T>(string connectionString, Guid? tenantId, Guid? userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            "select set_config('app.tenant_id', @t, true), set_config('app.user_id', @u, true)",
            new { t = tenantId?.ToString() ?? "", u = userId?.ToString() ?? "" },
            tx);
        var result = await body(conn, tx);
        await tx.CommitAsync();
        return result;
    }
}
