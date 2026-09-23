using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Recorrencia.Api.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    public const string InternalKey = "chave-interna-de-teste-com-32-caracteres";
    public const string Password = "senha-teste-123";

    public PostgresFixture Db { get; } = new();
    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

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
        });
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }

    public ApiClient Client(string host) => new(Factory.CreateClient(), host);

    public T Service<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    public Task SqlAsync(string sql, object? param = null) =>
        Db.AsSuperadminAsync(null, (c, t) => c.ExecuteAsync(sql, param, t));

    public Task<T> SqlScalarAsync<T>(string sql, object? param = null) =>
        Db.AsSuperadminAsync(null, async (c, t) => (await c.ExecuteScalarAsync<T>(sql, param, t))!);
}
