using Npgsql;

namespace Recorrencia.Api.Infrastructure;

public sealed class DataSources(NpgsqlDataSource app, NpgsqlDataSource superadmin) : IAsyncDisposable
{
    public NpgsqlDataSource App { get; } = app;
    public NpgsqlDataSource Superadmin { get; } = superadmin;

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        await Superadmin.DisposeAsync();
    }
}
