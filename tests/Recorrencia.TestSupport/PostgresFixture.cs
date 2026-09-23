using Npgsql;
using Recorrencia.Db;
using Testcontainers.PostgreSql;
using Xunit;

namespace Recorrencia.TestSupport;

public sealed class PostgresFixture : IAsyncLifetime
{
    public const string OwnerPassword = "owner-test-pw";
    public const string AppUserPassword = "app-test-pw";
    public const string SuperadminPassword = "super-test-pw";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
        .WithDatabase("recorrencia")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public string SuperuserConnectionString => _container.GetConnectionString();
    public string OwnerConnectionString => As("app_owner", OwnerPassword);
    public string AppUserConnectionString => As("app_user", AppUserPassword);
    public string SuperadminConnectionString => As("app_superadmin", SuperadminPassword);

    public async Task InitializeAsync()
    {
        DapperSetup.Configure();
        await _container.StartAsync();
        await DbBootstrapper.BootstrapAsync(
            SuperuserConnectionString,
            new DbRolePasswords(OwnerPassword, AppUserPassword, SuperadminPassword));
        Migrator.Run(OwnerConnectionString, log: false);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private string As(string user, string password) =>
        new NpgsqlConnectionStringBuilder(SuperuserConnectionString)
        {
            Username = user,
            Password = password,
            Pooling = false,
        }.ConnectionString;
}
