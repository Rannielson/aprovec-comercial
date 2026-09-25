using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Recorrencia.Api.Cli;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    public const string InternalKey = "chave-interna-de-teste-com-32-caracteres";
    public const string Password = "senha-teste-123";

    public PostgresFixture Db { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public CapturingEmailSender Emails { get; } = new();
    public FakeHinovaClient Hinova { get; } = new();

    // Shared with the whole "api" test collection (tests in it run sequentially, never in
    // parallel). FakeTimeProvider.SetUtcNow refuses to rewind, so treat this clock as
    // forward-only: advance it, never reset it to an earlier fixed value.
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:App", Db.AppUserConnectionString);
            builder.UseSetting("ConnectionStrings:Superadmin", Db.SuperadminConnectionString);
            builder.UseSetting("Internal:Key", InternalKey);
            builder.UseSetting("Argon2:MemoryKb", "1024");
            builder.UseSetting("Argon2:Iterations", "1");
            builder.UseSetting("Auth:MaxFailuresPerWindow", "5");
            builder.UseSetting("Web:Scheme", "http");
            builder.UseSetting("Web:RootDomain", "localhost:3000");
            builder.UseSetting("Hinova:EncryptionKey", Convert.ToBase64String(new byte[32]));

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Recorrencia.Api.Email.IEmailSender>();
                services.AddSingleton<Recorrencia.Api.Email.IEmailSender>(Emails);
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Time);
                services.RemoveAll<Recorrencia.Api.Integracoes.IHinovaClient>();
                services.AddSingleton<Recorrencia.Api.Integracoes.IHinovaClient>(Hinova);
            });
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }

    public ApiClient Client(string host, string? ip = null) => new(Factory.CreateClient(), host, ip);

    public T Service<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    public Task SqlAsync(string sql, object? param = null) =>
        Db.AsSuperadminAsync(null, (c, t) => c.ExecuteAsync(sql, param, t));

    public Task<T> SqlScalarAsync<T>(string sql, object? param = null) =>
        Db.AsSuperadminAsync(null, async (c, t) => (await c.ExecuteScalarAsync<T>(sql, param, t))!);

    public Task<SeededTenant> SeedAsync() =>
        DevSeed.SeedTenantAsync(Service<DataSources>(), Service<PasswordHasher>(), Seed.UniqueSlug(), Password);
}
