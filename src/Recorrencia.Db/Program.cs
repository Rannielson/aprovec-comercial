using Recorrencia.Db;

switch (args.FirstOrDefault())
{
    case "bootstrap":
        await DbBootstrapper.BootstrapAsync(
            Env("DB_SUPERUSER_CONNECTION"),
            new DbRolePasswords(Env("DB_OWNER_PASSWORD"), Env("DB_APP_USER_PASSWORD"), Env("DB_SUPERADMIN_PASSWORD")));
        Console.WriteLine("Bootstrap concluído.");
        return 0;
    case "migrate":
        Migrator.Run(Env("DB_OWNER_CONNECTION"));
        return 0;
    default:
        Console.Error.WriteLine("Uso: Recorrencia.Db <bootstrap|migrate>");
        return 1;
}

static string Env(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Variável de ambiente {name} não definida.");
