using Npgsql;

namespace Recorrencia.Db;

public sealed record DbRolePasswords(string Owner, string AppUser, string Superadmin);

public static class DbBootstrapper
{
    public static async Task BootstrapAsync(string superuserConnectionString, DbRolePasswords passwords, CancellationToken ct = default)
    {
        var database = new NpgsqlConnectionStringBuilder(superuserConnectionString).Database
            ?? throw new InvalidOperationException("A connection string do superusuário precisa informar o banco.");

        await using var conn = new NpgsqlConnection(superuserConnectionString);
        await conn.OpenAsync(ct);

        await EnsureRoleAsync(conn, "app_owner", passwords.Owner, "bypassrls", ct);
        await EnsureRoleAsync(conn, "app_user", passwords.AppUser, "nobypassrls", ct);
        await EnsureRoleAsync(conn, "app_superadmin", passwords.Superadmin, "bypassrls", ct);

        await ExecFormattedAsync(conn, "alter database %I owner to app_owner", ct, database);
        await ExecAsync(conn, "create extension if not exists citext", ct);
        await ExecAsync(conn, "create extension if not exists vector", ct);
        await ExecFormattedAsync(conn, "revoke all on database %I from public", ct, database);
        await ExecFormattedAsync(conn, "grant connect on database %I to app_user, app_superadmin", ct, database);
    }

    private static async Task EnsureRoleAsync(NpgsqlConnection conn, string role, string password, string rls, CancellationToken ct)
    {
        await using var check = new NpgsqlCommand("select exists (select 1 from pg_roles where rolname = @r)", conn);
        check.Parameters.AddWithValue("r", role);
        var exists = (bool)(await check.ExecuteScalarAsync(ct))!;
        var verb = exists ? "alter" : "create";
        await ExecFormattedAsync(conn, $"{verb} role %I with login nosuperuser nocreatedb nocreaterole {rls} password %L", ct, role, password);
    }

    private static async Task ExecFormattedAsync(NpgsqlConnection conn, string template, CancellationToken ct, params string[] args)
    {
        var placeholders = string.Concat(args.Select((_, i) => $", @a{i}"));
        await using var format = new NpgsqlCommand($"select format(@t{placeholders})", conn);
        format.Parameters.AddWithValue("t", template);
        for (var i = 0; i < args.Length; i++)
            format.Parameters.AddWithValue($"a{i}", args[i]);
        var sql = (string)(await format.ExecuteScalarAsync(ct))!;
        await ExecAsync(conn, sql, ct);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
