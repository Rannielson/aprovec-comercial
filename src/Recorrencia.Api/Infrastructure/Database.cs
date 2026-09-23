using Dapper;
using Npgsql;

namespace Recorrencia.Api.Infrastructure;

public sealed class Tx(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    public NpgsqlConnection Connection { get; } = connection;
    public NpgsqlTransaction Transaction { get; } = transaction;

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null) =>
        Connection.QueryAsync<T>(sql, param, Transaction);

    public Task<T> QuerySingleAsync<T>(string sql, object? param = null) =>
        Connection.QuerySingleAsync<T>(sql, param, Transaction);

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null) =>
        Connection.QuerySingleOrDefaultAsync<T>(sql, param, Transaction);

    public Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null) =>
        Connection.ExecuteScalarAsync<T>(sql, param, Transaction);

    public Task<int> ExecuteAsync(string sql, object? param = null) =>
        Connection.ExecuteAsync(sql, param, Transaction);
}

public sealed class Database(DataSources sources)
{
    public Task<T> InTenantAsync<T>(Guid tenantId, Guid? userId, Func<Tx, Task<T>> work, CancellationToken ct) =>
        RunAsync(sources.App, tenantId, userId, work, ct);

    public Task<T> AnonymousAsync<T>(Func<Tx, Task<T>> work, CancellationToken ct) =>
        RunAsync(sources.App, null, null, work, ct);

    public Task<T> AsPlatformAsync<T>(Guid? tenantId, Func<Tx, Task<T>> work, CancellationToken ct) =>
        RunAsync(sources.Superadmin, tenantId, null, work, ct);

    private static async Task<T> RunAsync<T>(NpgsqlDataSource dataSource, Guid? tenantId, Guid? userId, Func<Tx, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(
            "select set_config('app.tenant_id', @t, true), set_config('app.user_id', @u, true)",
            new { t = tenantId?.ToString() ?? "", u = userId?.ToString() ?? "" },
            tx);
        var result = await work(new Tx(conn, tx));
        await tx.CommitAsync(ct);
        return result;
    }
}
