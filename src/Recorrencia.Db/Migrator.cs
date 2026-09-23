using DbUp;

namespace Recorrencia.Db;

public static class Migrator
{
    public static void Run(string ownerConnectionString, bool log = true)
    {
        var builder = DeployChanges.To
            .PostgresqlDatabase(ownerConnectionString)
            .WithScriptsEmbeddedInAssembly(typeof(Migrator).Assembly, name => name.Contains(".Scripts.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .WithVariablesDisabled();

        builder = log ? builder.LogToConsole() : builder.LogToNowhere();

        var result = builder.Build().PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException("Falha ao aplicar as migrações.", result.Error);
    }
}
