using Dapper;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Platform;

public static class PlatformAdmins
{
    public static async Task<Guid> CreateAsync(DataSources sources, PasswordHasher hasher, string email, string password, CancellationToken ct = default)
    {
        PasswordPolicy.Validate(password);
        await using var conn = await sources.Superadmin.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<Guid>(
            """
            insert into platform_admins (email, password_hash) values (@email, @hash)
            on conflict (email) do update set password_hash = excluded.password_hash
            returning id
            """,
            new { email = email.Trim().ToLowerInvariant(), hash = hasher.Hash(password) });
    }

    public static async Task RunFromCommandLineAsync(IServiceProvider services, string email)
    {
        var password = Environment.GetEnvironmentVariable("PLATFORM_ADMIN_PASSWORD");
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Defina a variável PLATFORM_ADMIN_PASSWORD.");
        var id = await CreateAsync(services.GetRequiredService<DataSources>(), services.GetRequiredService<PasswordHasher>(), email, password);
        Console.WriteLine($"Administrador da plataforma pronto: {email} ({id})");
    }
}
