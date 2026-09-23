using Dapper;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Platform;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Cli;

public static partial class DevSeed
{
    public static async Task RunAsync(IServiceProvider services)
    {
        if (!services.GetRequiredService<IHostEnvironment>().IsDevelopment())
            throw new InvalidOperationException("seed-dev só roda no ambiente Development.");

        var sources = services.GetRequiredService<DataSources>();
        var hasher = services.GetRequiredService<PasswordHasher>();
        await using (var conn = await sources.Superadmin.OpenConnectionAsync())
        {
            if (await conn.ExecuteScalarAsync<bool>("select exists (select 1 from tenants where slug = 'aprovec')"))
            {
                Console.WriteLine("Os dados de desenvolvimento já existem.");
                return;
            }
        }

        await SeedTenantAsync(sources, hasher, "aprovec", DevPassword);
        await PlatformAdmins.CreateAsync(sources, hasher, "admin@plataforma.local", DevPassword);
        Console.WriteLine(
            $"""
            Dados de desenvolvimento criados. Senha de todos os usuários: {DevPassword}
            http://aprovec.localhost:3000 — admin@aprovec.local, joao@aprovec.local, maria@aprovec.local, pedro@aprovec.local, coordenacao@aprovec.local
            http://admin.localhost:3000 — admin@plataforma.local
            """);
    }
}
