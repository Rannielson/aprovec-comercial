# Fundação 1 — Banco de dados e RLS: plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Criar o schema PostgreSQL completo do SaaS (multi-tenant, RBAC modular, motor de comissão, fechamentos), com RLS forçado, funções `SECURITY DEFINER` para autenticação e testes de integração contra um Postgres real.

**Architecture:** Migrações SQL puras, embutidas em um console .NET (`Recorrencia.Db`) e aplicadas com DbUp. Um comando `bootstrap`, executado como superusuário, cria as roles do Postgres e as extensões. Os testes sobem `pgvector/pgvector:pg17` com Testcontainers, aplicam bootstrap + migrações e verificam o comportamento de cada role.

**Tech Stack:** .NET 10, PostgreSQL 17 + pgvector, citext, Npgsql, DbUp (`dbup-postgresql`), Dapper, xUnit v2, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md`

**Ordem dos planos:** este é o plano 1 de 4. O plano 2 (motor de comissão) não depende deste e pode rodar em paralelo. O plano 3 (API) depende dos planos 1 e 2. O plano 4 (web) depende do 3.

## Global Constraints

- Target framework: `net10.0`. Nullable habilitado e warnings tratados como erro (exceto auditoria NuGet `NU1901`–`NU1904`).
- Imagem do banco: `pgvector/pgvector:pg17`. Extensões: `vector` e `citext`.
- Dinheiro: `numeric(14,2)`. Taxas: `numeric(7,4)`. Nunca `float`/`double`/`real`.
- Roles do Postgres: `app_owner` (dono do schema, `BYPASSRLS`, só migrações e dono das funções `SECURITY DEFINER`), `app_user` (API, **sem** `BYPASSRLS`), `app_superadmin` (`BYPASSRLS`, só operações de plataforma).
- Toda tabela de tenant tem `tenant_id uuid not null` e `FORCE ROW LEVEL SECURITY`.
- Contexto da transação: `app.tenant_id` e `app.user_id`, definidos com `set_config(..., true)`.
- Funções `SECURITY DEFINER` sempre com `set search_path = pg_catalog, public, app`.
- Erros de regra levantados no banco usam `raise exception '<codigo>' using errcode = 'P0001'`, onde `<codigo>` é o código estável da API (ex.: `hierarchy.cycle`).
- Tokens de sessão e de convite: o banco guarda só o SHA-256 (`bytea`).
- Durações são passadas em segundos (`int`) para as funções, nunca como `interval`.
- Parâmetros de e-mail e IP chegam como `text` e são convertidos dentro da função (`::citext`, `::inet`).
- Nenhum script de migração usa delimitador `$nome$`: só `$$` (a substituição de variáveis do DbUp fica desligada, mas o padrão evita surpresas).
- **Docker/Colima:** antes de rodar testes, `colima start` e exporte:
  ```bash
  export DOCKER_HOST="unix://$HOME/.colima/default/docker.sock"
  export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock
  ```
- Commits: mensagens convencionais (`feat(db): ...`, `test(db): ...`), terminando com a linha de atribuição indicada pelo ambiente.

## Mapa de arquivos

```
Directory.Build.props                         configurações comuns de build
Recorrencia.slnx                              solução
docker-compose.yml                            serviço db (dev)
.env.example                                  variáveis de ambiente de exemplo
src/Recorrencia.Db/
  Recorrencia.Db.csproj
  Program.cs                                  comandos bootstrap | migrate
  DbBootstrapper.cs                           roles + extensões (superusuário)
  Migrator.cs                                 DbUp
  DapperSetup.cs                              snake_case + DateOnly
  Scripts/0001_app_schema.sql … 0012_commission_sources.sql
tests/Recorrencia.TestSupport/
  PostgresFixture.cs                          container + bootstrap + migrações
  DbExtensions.cs                             conexões como app_user/superadmin com contexto
  Seed.cs                                     dados de teste via app_owner
  RlsScenario.cs                              cenário João/Maria/Pedro/coordenação
tests/Recorrencia.Db.Tests/
  DbCollection.cs
  BootstrapTests.cs, TenantTests.cs, HierarchyTests.cs, AuthFunctionTests.cs,
  RbacTests.cs, CommissionSchemaTests.cs, ProvisioningTests.cs,
  BoletoFechamentoTests.cs, RlsTests.cs, CommissionSourceTests.cs
```

---

### Task 1: Solução, bootstrap de roles, migrador e fixture de testes

**Files:**
- Create: `Directory.Build.props`, `Recorrencia.slnx`, `docker-compose.yml`, `.env.example`
- Modify: `.gitignore`
- Create: `src/Recorrencia.Db/{Recorrencia.Db.csproj,Program.cs,DbBootstrapper.cs,Migrator.cs,DapperSetup.cs}`
- Create: `src/Recorrencia.Db/Scripts/0001_app_schema.sql`
- Create: `tests/Recorrencia.TestSupport/{Recorrencia.TestSupport.csproj,PostgresFixture.cs,DbExtensions.cs}`
- Create: `tests/Recorrencia.Db.Tests/{Recorrencia.Db.Tests.csproj,DbCollection.cs,BootstrapTests.cs}`

**Interfaces:**
- Produces: `DbBootstrapper.BootstrapAsync(string superuserConnectionString, DbRolePasswords passwords, CancellationToken ct = default)`; `record DbRolePasswords(string Owner, string AppUser, string Superadmin)`; `Migrator.Run(string ownerConnectionString, bool log = true)`; `DapperSetup.Configure()`.
- Produces (testes): `PostgresFixture` com `SuperuserConnectionString`, `OwnerConnectionString`, `AppUserConnectionString`, `SuperadminConnectionString`; extensões `db.OpenAsync(cs)`, `db.AsAppUserAsync(tenantId, userId, (conn, tx) => ...)`, `db.AsSuperadminAsync(tenantId, (conn, tx) => ...)`, `DbExtensions.ThrowsPgAsync(Func<Task>)`; `DbCollection.Name = "db"`.
- Produces (SQL): schema `app`; `app.current_tenant() returns uuid`; `app.current_user_id() returns uuid`; `app.touch_updated_at()` (trigger).

- [ ] **Step 1: Iniciar o Docker e criar a estrutura**

```bash
colima start
export DOCKER_HOST="unix://$HOME/.colima/default/docker.sock"
export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock
docker info --format '{{.ServerVersion}}'
dotnet new sln -n Recorrencia --format slnx
dotnet new console -n Recorrencia.Db -o src/Recorrencia.Db
dotnet new classlib -n Recorrencia.TestSupport -o tests/Recorrencia.TestSupport
dotnet new classlib -n Recorrencia.Db.Tests -o tests/Recorrencia.Db.Tests
rm tests/Recorrencia.TestSupport/Class1.cs tests/Recorrencia.Db.Tests/Class1.cs
dotnet sln Recorrencia.slnx add src/Recorrencia.Db tests/Recorrencia.TestSupport tests/Recorrencia.Db.Tests
dotnet add src/Recorrencia.Db package Npgsql
dotnet add src/Recorrencia.Db package dbup-postgresql
dotnet add src/Recorrencia.Db package Dapper
dotnet add tests/Recorrencia.TestSupport package Testcontainers.PostgreSql
dotnet add tests/Recorrencia.TestSupport package xunit
dotnet add tests/Recorrencia.TestSupport reference src/Recorrencia.Db
dotnet add tests/Recorrencia.Db.Tests package Microsoft.NET.Test.Sdk
dotnet add tests/Recorrencia.Db.Tests package xunit
dotnet add tests/Recorrencia.Db.Tests package xunit.runner.visualstudio
dotnet add tests/Recorrencia.Db.Tests reference tests/Recorrencia.TestSupport
```

Expected: `docker info` imprime a versão do servidor. Os comandos `dotnet` terminam sem erro. Se `docker info` falhar, pare e resolva o Colima antes de continuar.

- [ ] **Step 2: Configurações comuns**

`Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>NU1901;NU1902;NU1903;NU1904</WarningsNotAsErrors>
  </PropertyGroup>
</Project>
```

Acrescente ao `.gitignore` (mantendo as linhas existentes):

```
.env
bin/
obj/
TestResults/
node_modules/
.next/
playwright-report/
test-results/
```

`src/Recorrencia.Db/Recorrencia.Db.csproj`: mantenha o conteúdo gerado e acrescente dentro de `<Project>`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Scripts\*.sql" />
  </ItemGroup>
```

`tests/Recorrencia.Db.Tests/Recorrencia.Db.Tests.csproj`: acrescente:

```xml
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Dapper" />
    <Using Include="Npgsql" />
    <Using Include="Xunit" />
    <Using Include="Recorrencia.TestSupport" />
  </ItemGroup>
```

- [ ] **Step 3: Escrever o teste de bootstrap (falhando)**

`tests/Recorrencia.Db.Tests/DbCollection.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[CollectionDefinition(Name)]
public sealed class DbCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "db";
}
```

`tests/Recorrencia.Db.Tests/BootstrapTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class BootstrapTests(PostgresFixture db)
{
    [Fact]
    public async Task Roles_have_expected_rls_attributes()
    {
        await using var conn = await db.OpenAsync(db.SuperuserConnectionString);
        var roles = (await conn.QueryAsync<(string Name, bool Bypass)>(
                "select rolname, rolbypassrls from pg_roles where rolname in ('app_owner', 'app_user', 'app_superadmin')"))
            .ToDictionary(r => r.Name, r => r.Bypass);

        Assert.False(roles["app_user"]);
        Assert.True(roles["app_superadmin"]);
        Assert.True(roles["app_owner"]);
    }

    [Fact]
    public async Task Extensions_are_installed()
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        var extensions = (await conn.QueryAsync<string>("select extname from pg_extension")).ToList();

        Assert.Contains("vector", extensions);
        Assert.Contains("citext", extensions);
    }

    [Fact]
    public async Task App_user_has_no_context_by_default()
    {
        var (tenant, user) = await db.AsAppUserAsync(null, null, (c, t) =>
            c.QuerySingleAsync<(Guid?, Guid?)>("select app.current_tenant(), app.current_user_id()", transaction: t));

        Assert.Null(tenant);
        Assert.Null(user);
    }

    [Fact]
    public async Task Context_is_visible_inside_the_transaction()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var (tenant, user) = await db.AsAppUserAsync(tenantId, userId, (c, t) =>
            c.QuerySingleAsync<(Guid?, Guid?)>("select app.current_tenant(), app.current_user_id()", transaction: t));

        Assert.Equal(tenantId, tenant);
        Assert.Equal(userId, user);
    }

    [Fact]
    public void Migrations_are_idempotent()
    {
        Recorrencia.Db.Migrator.Run(db.OwnerConnectionString, log: false);
    }
}
```

- [ ] **Step 4: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Db.Tests`
Expected: FAIL de compilação (`PostgresFixture`, `DbBootstrapper` e `Migrator` não existem).

- [ ] **Step 5: Implementar o projeto Db**

`src/Recorrencia.Db/DbBootstrapper.cs`:

```csharp
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
```

`src/Recorrencia.Db/Migrator.cs`:

```csharp
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
```

`src/Recorrencia.Db/DapperSetup.cs`:

```csharp
using System.Data;
using Dapper;
using Npgsql;
using NpgsqlTypes;

namespace Recorrencia.Db;

public static class DapperSetup
{
    private static int _configured;

    public static void Configure()
    {
        if (Interlocked.Exchange(ref _configured, 1) == 1)
            return;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateOnlyHandler());
    }

    private sealed class DateOnlyHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.Value = value;
            if (parameter is NpgsqlParameter npgsql)
                npgsql.NpgsqlDbType = NpgsqlDbType.Date;
        }

        public override DateOnly Parse(object value) => value switch
        {
            DateOnly date => date,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => throw new InvalidCastException($"Não é possível converter {value.GetType()} em DateOnly."),
        };
    }
}
```

`src/Recorrencia.Db/Program.cs` (substitui o gerado):

```csharp
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
```

`src/Recorrencia.Db/Scripts/0001_app_schema.sql`:

```sql
alter default privileges for role app_owner revoke execute on functions from public;
alter default privileges for role app_owner in schema public
  grant select, insert, update, delete on tables to app_superadmin;

create schema app;
revoke all on schema app from public;
grant usage on schema app to app_user, app_superadmin;
grant usage on schema public to app_user, app_superadmin;

create function app.current_tenant() returns uuid
language sql stable
as $$ select nullif(current_setting('app.tenant_id', true), '')::uuid $$;

create function app.current_user_id() returns uuid
language sql stable
as $$ select nullif(current_setting('app.user_id', true), '')::uuid $$;

create function app.touch_updated_at() returns trigger
language plpgsql
as $$
begin
  new.updated_at := now();
  return new;
end
$$;

grant execute on function app.current_tenant(), app.current_user_id() to app_user, app_superadmin;
```

- [ ] **Step 6: Implementar o suporte de testes**

`tests/Recorrencia.TestSupport/PostgresFixture.cs`:

```csharp
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

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("pgvector/pgvector:pg17")
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
```

Se o construtor sem parâmetros de `PostgreSqlBuilder` estiver marcado como obsoleto na versão instalada (o que viraria erro de build), use `new PostgreSqlBuilder("pgvector/pgvector:pg17")` e remova `.WithImage(...)`.

`tests/Recorrencia.TestSupport/DbExtensions.cs`:

```csharp
using Dapper;
using Npgsql;
using Xunit;

namespace Recorrencia.TestSupport;

public static class DbExtensions
{
    public static async Task<NpgsqlConnection> OpenAsync(this PostgresFixture db, string connectionString)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    public static Task<T> AsAppUserAsync<T>(this PostgresFixture db, Guid? tenantId, Guid? userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body) =>
        RunAsync(db.AppUserConnectionString, tenantId, userId, body);

    public static Task AsAppUserAsync(this PostgresFixture db, Guid? tenantId, Guid? userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task> body) =>
        RunAsync(db.AppUserConnectionString, tenantId, userId, async (c, t) => { await body(c, t); return 0; });

    public static Task<T> AsSuperadminAsync<T>(this PostgresFixture db, Guid? tenantId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body) =>
        RunAsync(db.SuperadminConnectionString, tenantId, null, body);

    public static async Task<PostgresException> ThrowsPgAsync(Func<Task> action) =>
        await Assert.ThrowsAsync<PostgresException>(action);

    private static async Task<T> RunAsync<T>(string connectionString, Guid? tenantId, Guid? userId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await conn.ExecuteAsync(
            "select set_config('app.tenant_id', @t, true), set_config('app.user_id', @u, true)",
            new { t = tenantId?.ToString() ?? "", u = userId?.ToString() ?? "" },
            tx);
        var result = await body(conn, tx);
        await tx.CommitAsync();
        return result;
    }
}
```

- [ ] **Step 7: Compose e variáveis de exemplo**

`docker-compose.yml`:

```yaml
services:
  db:
    image: pgvector/pgvector:pg17
    environment:
      POSTGRES_DB: recorrencia
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?defina POSTGRES_PASSWORD no .env}
    ports:
      - "127.0.0.1:55432:5432"
    volumes:
      - db-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d recorrencia"]
      interval: 5s
      timeout: 3s
      retries: 20

volumes:
  db-data:
```

`.env.example`:

```
POSTGRES_PASSWORD=troque-me
DB_OWNER_PASSWORD=troque-me-owner
DB_APP_USER_PASSWORD=troque-me-app
DB_SUPERADMIN_PASSWORD=troque-me-super
DB_SUPERUSER_CONNECTION=Host=127.0.0.1;Port=55432;Database=recorrencia;Username=postgres;Password=troque-me
DB_OWNER_CONNECTION=Host=127.0.0.1;Port=55432;Database=recorrencia;Username=app_owner;Password=troque-me-owner
```

- [ ] **Step 8: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Db.Tests`
Expected: PASS (5 testes). A primeira execução baixa a imagem do Postgres.

- [ ] **Step 9: Verificar o runner contra o compose**

```bash
cp .env.example .env
docker compose up -d db
set -a; source .env; set +a
dotnet run --project src/Recorrencia.Db -- bootstrap
dotnet run --project src/Recorrencia.Db -- migrate
```

Expected: `Bootstrap concluído.` e o log do DbUp aplicando `0001_app_schema.sql`. Rodar `migrate` de novo não aplica nada.

- [ ] **Step 10: Commit**

```bash
git add Directory.Build.props Recorrencia.slnx docker-compose.yml .env.example .gitignore src/Recorrencia.Db tests/Recorrencia.TestSupport tests/Recorrencia.Db.Tests
git commit -m "feat(db): scaffold solution, role bootstrap and DbUp migrator"
```

---

### Task 2: Tenants, administradores da plataforma, usuários e hierarquia (closure table)

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0002_tenants_platform.sql`
- Create: `src/Recorrencia.Db/Scripts/0003_users_hierarchy.sql`
- Create: `tests/Recorrencia.TestSupport/Seed.cs`
- Test: `tests/Recorrencia.Db.Tests/TenantTests.cs`, `tests/Recorrencia.Db.Tests/HierarchyTests.cs`

**Interfaces:**
- Consumes: `app.touch_updated_at()`, `PostgresFixture`, `DbExtensions` (Task 1).
- Produces (SQL): tabelas `tenants(id, slug, name, status, created_at, updated_at)`, `platform_admins(id, email, password_hash, …)`, `users(id, tenant_id, name, email, password_hash, status, supervisor_id, …)`, `hierarchy_paths(tenant_id, ancestor_id, descendant_id, depth)`. Constraints únicas `(tenant_id, id)` em `users` para FKs compostas. Erros: `hierarchy.cycle`, `users.tenant_immutable`.
- Produces (testes): `Seed(PostgresFixture)` com `TenantAsync(string? slug = null)`, `UserAsync(Guid tenantId, string name, Guid? supervisorId = null, string status = "ativo", string? email = null)`, `ExecAsync(string sql, object? param = null)`, `static UniqueSlug()`.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.TestSupport/Seed.cs`:

```csharp
using Dapper;

namespace Recorrencia.TestSupport;

public sealed class Seed(PostgresFixture db)
{
    public static string UniqueSlug() => "t" + Guid.NewGuid().ToString("N")[..20];

    public async Task ExecAsync(string sql, object? param = null)
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        await conn.ExecuteAsync(sql, param);
    }

    public async Task<T> ScalarAsync<T>(string sql, object? param = null)
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        return (await conn.ExecuteScalarAsync<T>(sql, param))!;
    }

    public Task<Guid> TenantAsync(string? slug = null) =>
        ScalarAsync<Guid>(
            "insert into tenants (slug, name) values (@slug, 'Empresa de teste') returning id",
            new { slug = slug ?? UniqueSlug() });

    public Task<Guid> UserAsync(Guid tenantId, string name, Guid? supervisorId = null, string status = "ativo", string? email = null) =>
        ScalarAsync<Guid>(
            """
            insert into users (tenant_id, name, email, password_hash, status, supervisor_id)
            values (@tenantId, @name, @email, @hash, @status, @supervisorId)
            returning id
            """,
            new
            {
                tenantId,
                name,
                email = email ?? $"{Guid.NewGuid():N}@teste.local",
                hash = status == "convidado" ? null : "hash-de-teste",
                status,
                supervisorId,
            });
}
```

`tests/Recorrencia.Db.Tests/TenantTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class TenantTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    [Theory]
    [InlineData("admin")]
    [InlineData("www")]
    [InlineData("api")]
    [InlineData("Maiuscula")]
    [InlineData("-hifen-inicial")]
    [InlineData("com_underscore")]
    public async Task Invalid_slugs_are_rejected(string slug)
    {
        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.TenantAsync(slug));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Email_is_unique_per_tenant_ignoring_case()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        await _seed.UserAsync(t1, "João", email: "Joao@Empresa.com");

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.UserAsync(t1, "Outro João", email: "joao@empresa.com"));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);

        await _seed.UserAsync(t2, "João de outra empresa", email: "joao@empresa.com");
    }

    [Fact]
    public async Task Active_user_requires_password_hash()
    {
        var t = await _seed.TenantAsync();
        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "insert into users (tenant_id, name, email, status) values (@t, 'Sem senha', 'x@teste.local', 'ativo')",
            new { t }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Tenant_of_a_user_cannot_change()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t1, "Fixo");

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update users set tenant_id = @t2 where id = @u", new { t2, u }));
        Assert.Equal("users.tenant_immutable", ex.MessageText);
    }
}
```

`tests/Recorrencia.Db.Tests/HierarchyTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class HierarchyTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private async Task<HashSet<(Guid, Guid, int)>> PathsAsync(Guid tenantId)
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        var rows = await conn.QueryAsync<(Guid, Guid, int)>(
            "select ancestor_id, descendant_id, depth from hierarchy_paths where tenant_id = @tenantId",
            new { tenantId });
        return rows.ToHashSet();
    }

    [Fact]
    public async Task Inserting_users_builds_closure_paths()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);

        var expected = new HashSet<(Guid, Guid, int)>
        {
            (joao, joao, 0), (maria, maria, 0), (pedro, pedro, 0),
            (joao, maria, 1), (maria, pedro, 1), (joao, pedro, 2),
        };
        Assert.True(expected.SetEquals(await PathsAsync(t)));
    }

    [Fact]
    public async Task Moving_a_user_moves_the_whole_subtree()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);
        var bruno = await _seed.UserAsync(t, "Bruno");

        await _seed.ExecAsync("update users set supervisor_id = @bruno where id = @maria", new { bruno, maria });

        var expected = new HashSet<(Guid, Guid, int)>
        {
            (joao, joao, 0), (bruno, bruno, 0), (maria, maria, 0), (pedro, pedro, 0),
            (maria, pedro, 1), (bruno, maria, 1), (bruno, pedro, 2),
        };
        Assert.True(expected.SetEquals(await PathsAsync(t)));
    }

    [Fact]
    public async Task Removing_the_supervisor_detaches_the_subtree()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);

        await _seed.ExecAsync("update users set supervisor_id = null where id = @maria", new { maria });

        var expected = new HashSet<(Guid, Guid, int)> { (joao, joao, 0), (maria, maria, 0) };
        Assert.True(expected.SetEquals(await PathsAsync(t)));
    }

    [Fact]
    public async Task Cycles_are_rejected()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);

        var ex = await DbExtensions.ThrowsPgAsync(() =>
            _seed.ExecAsync("update users set supervisor_id = @pedro where id = @joao", new { pedro, joao }));
        Assert.Equal("hierarchy.cycle", ex.MessageText);
    }

    [Fact]
    public async Task Self_supervision_is_rejected()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");

        var ex = await DbExtensions.ThrowsPgAsync(() =>
            _seed.ExecAsync("update users set supervisor_id = @joao where id = @joao", new { joao }));
        Assert.Equal("hierarchy.cycle", ex.MessageText);
    }

    [Fact]
    public async Task Supervisor_must_belong_to_the_same_tenant()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var a = await _seed.UserAsync(t1, "A");

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.UserAsync(t2, "B", a));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Db.Tests --filter "FullyQualifiedName~TenantTests|FullyQualifiedName~HierarchyTests"`
Expected: FAIL com `relation "tenants" does not exist`.

- [ ] **Step 3: Implementar as migrações**

`src/Recorrencia.Db/Scripts/0002_tenants_platform.sql`:

```sql
create table tenants (
  id uuid primary key default gen_random_uuid(),
  slug text not null unique
    check (slug ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$' and slug not in ('admin', 'www', 'api')),
  name text not null check (length(btrim(name)) > 0),
  status text not null default 'ativo' check (status in ('ativo', 'suspenso')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create trigger tenants_touch before update on tenants
  for each row execute function app.touch_updated_at();

create table platform_admins (
  id uuid primary key default gen_random_uuid(),
  email citext not null unique,
  password_hash text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create trigger platform_admins_touch before update on platform_admins
  for each row execute function app.touch_updated_at();
```

`src/Recorrencia.Db/Scripts/0003_users_hierarchy.sql`:

```sql
create table users (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  name text not null check (length(btrim(name)) > 0),
  email citext not null,
  password_hash text,
  status text not null default 'convidado' check (status in ('convidado', 'ativo', 'desligado')),
  supervisor_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, email),
  unique (tenant_id, id),
  foreign key (tenant_id, supervisor_id) references users (tenant_id, id),
  check (supervisor_id is null or supervisor_id <> id),
  check (status <> 'ativo' or password_hash is not null)
);

create index users_supervisor_idx on users (supervisor_id);

create trigger users_touch before update on users
  for each row execute function app.touch_updated_at();

create table hierarchy_paths (
  tenant_id uuid not null,
  ancestor_id uuid not null,
  descendant_id uuid not null,
  depth int not null check (depth >= 0),
  primary key (ancestor_id, descendant_id),
  foreign key (tenant_id, ancestor_id) references users (tenant_id, id) on delete cascade,
  foreign key (tenant_id, descendant_id) references users (tenant_id, id) on delete cascade
);

create index hierarchy_paths_descendant_idx on hierarchy_paths (descendant_id, depth);

create function app.users_guard() returns trigger
language plpgsql
as $$
begin
  if new.tenant_id <> old.tenant_id then
    raise exception 'users.tenant_immutable' using errcode = 'P0001';
  end if;
  return new;
end
$$;

create trigger users_guard before update of tenant_id on users
  for each row execute function app.users_guard();

create function app.hierarchy_after_insert() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  insert into hierarchy_paths (tenant_id, ancestor_id, descendant_id, depth)
  values (new.tenant_id, new.id, new.id, 0);

  if new.supervisor_id is not null then
    insert into hierarchy_paths (tenant_id, ancestor_id, descendant_id, depth)
    select new.tenant_id, p.ancestor_id, new.id, p.depth + 1
      from hierarchy_paths p
     where p.descendant_id = new.supervisor_id;
  end if;
  return null;
end
$$;

create function app.hierarchy_before_update() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  perform pg_advisory_xact_lock(hashtext('hierarchy:' || new.tenant_id::text));
  if new.supervisor_id is not null and exists (
       select 1 from hierarchy_paths
        where ancestor_id = new.id and descendant_id = new.supervisor_id) then
    raise exception 'hierarchy.cycle' using errcode = 'P0001';
  end if;
  return new;
end
$$;

create function app.hierarchy_after_update() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  delete from hierarchy_paths d
   where d.descendant_id in (select descendant_id from hierarchy_paths where ancestor_id = new.id)
     and d.ancestor_id not in (select descendant_id from hierarchy_paths where ancestor_id = new.id);

  if new.supervisor_id is not null then
    insert into hierarchy_paths (tenant_id, ancestor_id, descendant_id, depth)
    select new.tenant_id, sup.ancestor_id, sub.descendant_id, sup.depth + sub.depth + 1
      from hierarchy_paths sup
      cross join hierarchy_paths sub
     where sup.descendant_id = new.supervisor_id
       and sub.ancestor_id = new.id;
  end if;
  return null;
end
$$;

create trigger users_hierarchy_insert after insert on users
  for each row execute function app.hierarchy_after_insert();

create trigger users_hierarchy_check before update of supervisor_id on users
  for each row when (new.supervisor_id is distinct from old.supervisor_id)
  execute function app.hierarchy_before_update();

create trigger users_hierarchy_move after update of supervisor_id on users
  for each row when (new.supervisor_id is distinct from old.supervisor_id)
  execute function app.hierarchy_after_update();

grant select (id, tenant_id, name, email, status, supervisor_id, created_at, updated_at) on users to app_user;
grant insert (id, tenant_id, name, email, supervisor_id) on users to app_user;
grant update (name, email, status, supervisor_id) on users to app_user;
grant select on hierarchy_paths to app_user;
```

Observação: `app_user` nunca lê nem grava `password_hash` diretamente. Isso passa só pelas funções da Task 3.

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Db.Tests`
Expected: PASS (todos, incluindo os da Task 1).

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Db/Scripts tests/Recorrencia.TestSupport/Seed.cs tests/Recorrencia.Db.Tests
git commit -m "feat(db): add tenants, users and closure-table hierarchy"
```

---

### Task 3: Sessões, convites e funções de autenticação

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0004_auth_tables.sql`
- Create: `src/Recorrencia.Db/Scripts/0005_auth_functions.sql`
- Test: `tests/Recorrencia.Db.Tests/AuthFunctionTests.cs`

**Interfaces:**
- Consumes: `tenants`, `users`, `platform_admins` (Task 2); `Seed` (Task 2).
- Produces (SQL, todas `SECURITY DEFINER`, executáveis por `app_user` e `app_superadmin`):
  - `app.resolve_tenant(p_slug text) returns table (id uuid, name text, status text)`
  - `app.find_login(p_tenant uuid, p_email text) returns table (user_id uuid, password_hash text, status text)`
  - `app.create_session(p_token_hash bytea, p_tenant uuid, p_user uuid, p_idle_seconds int, p_absolute_seconds int, p_ip text, p_user_agent text) returns uuid` — erro `auth.invalid_user`
  - `app.find_platform_login(p_email text) returns table (admin_id uuid, password_hash text)`
  - `app.create_platform_session(p_token_hash bytea, p_admin uuid, p_idle_seconds int, p_absolute_seconds int, p_ip text, p_user_agent text) returns uuid`
  - `app.resolve_session(p_token_hash bytea, p_idle_seconds int) returns table (session_id uuid, kind text, tenant_id uuid, user_id uuid, platform_admin_id uuid)`
  - `app.revoke_session(p_token_hash bytea) returns void`
  - `app.revoke_user_sessions(p_user uuid) returns int` — só no tenant do contexto
  - `app.create_invite(p_user uuid, p_token_hash bytea, p_purpose text, p_ttl_seconds int) returns void` — exige `app.tenant_id`; erros `invite.user_not_found`, `invite.invalid_status`
  - `app.consume_invite(p_tenant uuid, p_token_hash bytea, p_password_hash text) returns uuid` — erro `invite.invalid`
  - `app.rehash_own_password(p_hash text) returns void` — só o usuário do contexto

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Db.Tests/AuthFunctionTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class AuthFunctionTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    public sealed class SessionRow
    {
        public Guid SessionId { get; set; }
        public string Kind { get; set; } = "";
        public Guid? TenantId { get; set; }
        public Guid? UserId { get; set; }
        public Guid? PlatformAdminId { get; set; }
    }

    private static byte[] H(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private Task<Guid> CreateSessionAsync(Guid tenant, Guid user, string token, int idle = 3600, int absolute = 86400) =>
        db.AsAppUserAsync(null, null, (c, t) => c.ExecuteScalarAsync<Guid>(
            "select app.create_session(@hash, @tenant, @user, @idle, @absolute, @ip, @ua)",
            new { hash = H(token), tenant, user, idle, absolute, ip = "203.0.113.7", ua = "teste" }, t));

    private Task<SessionRow?> ResolveAsync(string token, int idle = 3600) =>
        db.AsAppUserAsync(null, null, (c, t) => c.QuerySingleOrDefaultAsync<SessionRow>(
            "select session_id, kind, tenant_id, user_id, platform_admin_id from app.resolve_session(@hash, @idle)",
            new { hash = H(token), idle }, t));

    private Task CreateInviteAsync(Guid tenant, Guid user, string token, string purpose = "convite", int ttl = 3600) =>
        db.AsAppUserAsync(tenant, null, (c, t) => c.ExecuteAsync(
            "select app.create_invite(@user, @hash, @purpose, @ttl)",
            new { user, hash = H(token), purpose, ttl }, t));

    private Task<Guid> ConsumeAsync(Guid tenant, string token, string passwordHash = "novo-hash") =>
        db.AsAppUserAsync(null, null, (c, t) => c.ExecuteScalarAsync<Guid>(
            "select app.consume_invite(@tenant, @hash, @passwordHash)",
            new { tenant, hash = H(token), passwordHash }, t));

    [Fact]
    public async Task App_user_cannot_read_auth_tables_directly()
    {
        foreach (var table in new[] { "tenants", "sessions", "invite_tokens", "platform_admins" })
        {
            var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(null, null, (c, t) =>
                c.ExecuteScalarAsync<long>($"select count(*) from {table}", transaction: t)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Fact]
    public async Task Resolve_tenant_ignores_case()
    {
        var slug = Seed.UniqueSlug();
        var tenant = await _seed.TenantAsync(slug);

        var row = await db.AsAppUserAsync(null, null, (c, t) => c.QuerySingleAsync<(Guid, string, string)>(
            "select id, name, status from app.resolve_tenant(@s)", new { s = slug.ToUpperInvariant() }, t));

        Assert.Equal((tenant, "Empresa de teste", "ativo"), row);
    }

    [Fact]
    public async Task Find_login_matches_email_ignoring_case()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João", email: "joao@empresa.com");

        var row = await db.AsAppUserAsync(null, null, (c, tx) => c.QuerySingleAsync<(Guid, string, string)>(
            "select user_id, password_hash, status from app.find_login(@t, @e)", new { t, e = "JOAO@empresa.com" }, tx));

        Assert.Equal((u, "hash-de-teste", "ativo"), row);
    }

    [Fact]
    public async Task Session_round_trip_returns_identity()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-1");

        var s = await ResolveAsync("tok-1");

        Assert.NotNull(s);
        Assert.Equal("tenant", s.Kind);
        Assert.Equal(t, s.TenantId);
        Assert.Equal(u, s.UserId);
        Assert.Null(s.PlatformAdminId);
    }

    [Fact]
    public async Task Idle_expired_session_is_rejected_and_deleted()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-idle");
        await _seed.ExecAsync("update sessions set expires_at = now() - interval '1 second' where token_hash = @h", new { h = H("tok-idle") });

        Assert.Null(await ResolveAsync("tok-idle"));
        Assert.Equal(0L, await _seed.ScalarAsync<long>("select count(*) from sessions where token_hash = @h", new { h = H("tok-idle") }));
    }

    [Fact]
    public async Task Sliding_never_passes_the_absolute_limit()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-abs", idle: 3600, absolute: 60);

        await ResolveAsync("tok-abs", idle: 3600);

        Assert.True(await _seed.ScalarAsync<bool>(
            "select expires_at = absolute_expires_at from sessions where token_hash = @h", new { h = H("tok-abs") }));
    }

    [Fact]
    public async Task Session_of_deactivated_user_is_rejected()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-off");
        await _seed.ExecAsync("update users set status = 'desligado' where id = @u", new { u });

        Assert.Null(await ResolveAsync("tok-off"));
    }

    [Fact]
    public async Task Session_of_suspended_tenant_is_rejected()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-susp");
        await _seed.ExecAsync("update tenants set status = 'suspenso' where id = @t", new { t });

        Assert.Null(await ResolveAsync("tok-susp"));
    }

    [Fact]
    public async Task Invited_user_cannot_get_a_session()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Convidado", status: "convidado");

        var ex = await DbExtensions.ThrowsPgAsync(() => CreateSessionAsync(t, u, "tok-inv"));
        Assert.Equal("auth.invalid_user", ex.MessageText);
    }

    [Fact]
    public async Task Revoked_session_no_longer_resolves()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-rev");

        await db.AsAppUserAsync(null, null, (c, tx) => c.ExecuteAsync("select app.revoke_session(@h)", new { h = H("tok-rev") }, tx));

        Assert.Null(await ResolveAsync("tok-rev"));
    }

    [Fact]
    public async Task Invite_activates_user_only_once()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Nova", status: "convidado");
        await CreateInviteAsync(t, u, "conv-1");

        Assert.Equal(u, await ConsumeAsync(t, "conv-1"));
        Assert.Equal("ativo|novo-hash", await _seed.ScalarAsync<string>(
            "select status || '|' || password_hash from users where id = @u", new { u }));

        var ex = await DbExtensions.ThrowsPgAsync(() => ConsumeAsync(t, "conv-1"));
        Assert.Equal("invite.invalid", ex.MessageText);
    }

    [Fact]
    public async Task Expired_invite_is_rejected()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Nova", status: "convidado");
        await CreateInviteAsync(t, u, "conv-exp");
        await _seed.ExecAsync("update invite_tokens set expires_at = now() - interval '1 second' where token_hash = @h", new { h = H("conv-exp") });

        var ex = await DbExtensions.ThrowsPgAsync(() => ConsumeAsync(t, "conv-exp"));
        Assert.Equal("invite.invalid", ex.MessageText);
    }

    [Fact]
    public async Task Invite_cannot_be_consumed_on_another_tenant()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t1, "Nova", status: "convidado");
        await CreateInviteAsync(t1, u, "conv-x");

        var ex = await DbExtensions.ThrowsPgAsync(() => ConsumeAsync(t2, "conv-x"));
        Assert.Equal("invite.invalid", ex.MessageText);
    }

    [Fact]
    public async Task New_invite_replaces_the_previous_unused_one()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Nova", status: "convidado");
        await CreateInviteAsync(t, u, "conv-a");
        await CreateInviteAsync(t, u, "conv-b");

        var ex = await DbExtensions.ThrowsPgAsync(() => ConsumeAsync(t, "conv-a"));
        Assert.Equal("invite.invalid", ex.MessageText);
        Assert.Equal(u, await ConsumeAsync(t, "conv-b"));
    }

    [Fact]
    public async Task Password_reset_requires_an_active_user()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Nova", status: "convidado");

        var ex = await DbExtensions.ThrowsPgAsync(() => CreateInviteAsync(t, u, "reset-1", "redefinicao"));
        Assert.Equal("invite.invalid_status", ex.MessageText);
    }

    [Fact]
    public async Task Creating_an_invite_requires_tenant_context()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Nova", status: "convidado");

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(null, null, (c, tx) => c.ExecuteAsync(
            "select app.create_invite(@u, @h, 'convite', 3600)", new { u, h = H("sem-contexto") }, tx)));
        Assert.Equal("invite.user_not_found", ex.MessageText);
    }

    [Fact]
    public async Task Password_reset_revokes_existing_sessions()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        await CreateSessionAsync(t, u, "tok-before-reset");
        await CreateInviteAsync(t, u, "reset-ok", "redefinicao");

        await ConsumeAsync(t, "reset-ok");

        Assert.Null(await ResolveAsync("tok-before-reset"));
    }

    [Fact]
    public async Task Revoke_user_sessions_is_limited_to_the_current_tenant()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t1, "João");
        await CreateSessionAsync(t1, u, "tok-scope");

        var fromOther = await db.AsAppUserAsync(t2, null, (c, tx) => c.ExecuteScalarAsync<int>("select app.revoke_user_sessions(@u)", new { u }, tx));
        Assert.Equal(0, fromOther);
        Assert.NotNull(await ResolveAsync("tok-scope"));

        var fromOwn = await db.AsAppUserAsync(t1, null, (c, tx) => c.ExecuteScalarAsync<int>("select app.revoke_user_sessions(@u)", new { u }, tx));
        Assert.Equal(1, fromOwn);
    }

    [Fact]
    public async Task Rehash_changes_only_the_current_user()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");
        var other = await _seed.UserAsync(t, "Maria");

        await db.AsAppUserAsync(t, u, (c, tx) => c.ExecuteAsync("select app.rehash_own_password('hash-novo')", transaction: tx));

        Assert.Equal("hash-novo", await _seed.ScalarAsync<string>("select password_hash from users where id = @u", new { u }));
        Assert.Equal("hash-de-teste", await _seed.ScalarAsync<string>("select password_hash from users where id = @other", new { other }));
    }

    [Fact]
    public async Task Platform_session_round_trip()
    {
        var email = $"root-{Guid.NewGuid():N}@plataforma.local";
        var admin = await _seed.ScalarAsync<Guid>(
            "insert into platform_admins (email, password_hash) values (@email, 'hash-root') returning id", new { email });

        var login = await db.AsAppUserAsync(null, null, (c, t) => c.QuerySingleAsync<(Guid, string)>(
            "select admin_id, password_hash from app.find_platform_login(@e)", new { e = email.ToUpperInvariant() }, t));
        Assert.Equal((admin, "hash-root"), login);

        await db.AsAppUserAsync(null, null, (c, t) => c.ExecuteScalarAsync<Guid>(
            "select app.create_platform_session(@hash, @admin, 3600, 86400, null, null)",
            new { hash = H("tok-root"), admin }, t));

        var s = await ResolveAsync("tok-root");
        Assert.NotNull(s);
        Assert.Equal("platform", s.Kind);
        Assert.Equal(admin, s.PlatformAdminId);
        Assert.Null(s.TenantId);
        Assert.Null(s.UserId);
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Db.Tests --filter "FullyQualifiedName~AuthFunctionTests"`
Expected: FAIL com `relation "sessions" does not exist` ou `function app.resolve_tenant(text) does not exist`.

- [ ] **Step 3: Implementar as tabelas**

`src/Recorrencia.Db/Scripts/0004_auth_tables.sql`:

```sql
create table sessions (
  id uuid primary key default gen_random_uuid(),
  token_hash bytea not null unique,
  kind text not null check (kind in ('tenant', 'platform')),
  tenant_id uuid references tenants (id) on delete cascade,
  user_id uuid references users (id) on delete cascade,
  platform_admin_id uuid references platform_admins (id) on delete cascade,
  created_at timestamptz not null default now(),
  last_seen_at timestamptz not null default now(),
  expires_at timestamptz not null,
  absolute_expires_at timestamptz not null,
  ip inet,
  user_agent text,
  check (expires_at <= absolute_expires_at),
  check (
    (kind = 'tenant' and tenant_id is not null and user_id is not null and platform_admin_id is null)
    or (kind = 'platform' and tenant_id is null and user_id is null and platform_admin_id is not null)
  )
);

create index sessions_user_idx on sessions (user_id);

create table invite_tokens (
  id uuid primary key default gen_random_uuid(),
  token_hash bytea not null unique,
  tenant_id uuid not null references tenants (id) on delete cascade,
  user_id uuid not null references users (id) on delete cascade,
  purpose text not null check (purpose in ('convite', 'redefinicao')),
  expires_at timestamptz not null,
  used_at timestamptz,
  created_at timestamptz not null default now()
);

create index invite_tokens_user_idx on invite_tokens (user_id, purpose);
```

- [ ] **Step 4: Implementar as funções**

`src/Recorrencia.Db/Scripts/0005_auth_functions.sql`:

```sql
create function app.resolve_tenant(p_slug text)
returns table (id uuid, name text, status text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select t.id, t.name, t.status from tenants t where t.slug = lower(p_slug)
$$;

create function app.find_login(p_tenant uuid, p_email text)
returns table (user_id uuid, password_hash text, status text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select u.id, u.password_hash, u.status
    from users u
   where u.tenant_id = p_tenant and u.email = p_email::citext
$$;

create function app.create_session(
  p_token_hash bytea, p_tenant uuid, p_user uuid,
  p_idle_seconds int, p_absolute_seconds int, p_ip text, p_user_agent text)
returns uuid
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v_id uuid;
begin
  if not exists (
       select 1 from users u join tenants t on t.id = u.tenant_id
        where u.id = p_user and u.tenant_id = p_tenant and u.status = 'ativo' and t.status = 'ativo') then
    raise exception 'auth.invalid_user' using errcode = 'P0001';
  end if;

  insert into sessions (token_hash, kind, tenant_id, user_id, expires_at, absolute_expires_at, ip, user_agent)
  values (
    p_token_hash, 'tenant', p_tenant, p_user,
    now() + make_interval(secs => least(p_idle_seconds, p_absolute_seconds)),
    now() + make_interval(secs => p_absolute_seconds),
    p_ip::inet, p_user_agent)
  returning id into v_id;
  return v_id;
end
$$;

create function app.find_platform_login(p_email text)
returns table (admin_id uuid, password_hash text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select a.id, a.password_hash from platform_admins a where a.email = p_email::citext
$$;

create function app.create_platform_session(
  p_token_hash bytea, p_admin uuid, p_idle_seconds int, p_absolute_seconds int, p_ip text, p_user_agent text)
returns uuid
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v_id uuid;
begin
  if not exists (select 1 from platform_admins where id = p_admin) then
    raise exception 'auth.invalid_user' using errcode = 'P0001';
  end if;

  insert into sessions (token_hash, kind, platform_admin_id, expires_at, absolute_expires_at, ip, user_agent)
  values (
    p_token_hash, 'platform', p_admin,
    now() + make_interval(secs => least(p_idle_seconds, p_absolute_seconds)),
    now() + make_interval(secs => p_absolute_seconds),
    p_ip::inet, p_user_agent)
  returning id into v_id;
  return v_id;
end
$$;

create function app.resolve_session(p_token_hash bytea, p_idle_seconds int)
returns table (session_id uuid, kind text, tenant_id uuid, user_id uuid, platform_admin_id uuid)
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
#variable_conflict use_column
declare
  s sessions%rowtype;
begin
  select * into s from sessions where token_hash = p_token_hash for update;
  if not found then
    return;
  end if;

  if s.expires_at <= now() or s.absolute_expires_at <= now() then
    delete from sessions where id = s.id;
    return;
  end if;

  if s.kind = 'tenant' and not exists (
       select 1 from users u join tenants t on t.id = u.tenant_id
        where u.id = s.user_id and u.status = 'ativo' and t.status = 'ativo') then
    delete from sessions where id = s.id;
    return;
  end if;

  update sessions
     set last_seen_at = now(),
         expires_at = least(now() + make_interval(secs => p_idle_seconds), absolute_expires_at)
   where id = s.id;

  return query select s.id, s.kind, s.tenant_id, s.user_id, s.platform_admin_id;
end
$$;

create function app.revoke_session(p_token_hash bytea)
returns void
language sql volatile security definer set search_path = pg_catalog, public, app
as $$
  delete from sessions where token_hash = p_token_hash
$$;

create function app.revoke_user_sessions(p_user uuid)
returns int
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v_count int;
begin
  delete from sessions where user_id = p_user and tenant_id = app.current_tenant();
  get diagnostics v_count = row_count;
  return v_count;
end
$$;

create function app.create_invite(p_user uuid, p_token_hash bytea, p_purpose text, p_ttl_seconds int)
returns void
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v_status text;
begin
  select status into v_status from users where id = p_user and tenant_id = app.current_tenant();
  if not found then
    raise exception 'invite.user_not_found' using errcode = 'P0001';
  end if;

  if (p_purpose = 'convite' and v_status <> 'convidado')
     or (p_purpose = 'redefinicao' and v_status <> 'ativo') then
    raise exception 'invite.invalid_status' using errcode = 'P0001';
  end if;

  delete from invite_tokens where user_id = p_user and purpose = p_purpose and used_at is null;

  insert into invite_tokens (token_hash, tenant_id, user_id, purpose, expires_at)
  values (p_token_hash, app.current_tenant(), p_user, p_purpose, now() + make_interval(secs => p_ttl_seconds));
end
$$;

create function app.consume_invite(p_tenant uuid, p_token_hash bytea, p_password_hash text)
returns uuid
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v invite_tokens%rowtype;
  v_status text;
begin
  select i.* into v
    from invite_tokens i
    join tenants t on t.id = i.tenant_id
   where i.token_hash = p_token_hash and i.tenant_id = p_tenant and t.status = 'ativo'
   for update of i;

  if not found or v.used_at is not null or v.expires_at <= now() then
    raise exception 'invite.invalid' using errcode = 'P0001';
  end if;

  select status into v_status from users where id = v.user_id for update;
  if (v.purpose = 'convite' and v_status <> 'convidado')
     or (v.purpose = 'redefinicao' and v_status <> 'ativo') then
    raise exception 'invite.invalid' using errcode = 'P0001';
  end if;

  update users set password_hash = p_password_hash, status = 'ativo' where id = v.user_id;
  update invite_tokens set used_at = now() where id = v.id;
  delete from sessions where user_id = v.user_id;
  return v.user_id;
end
$$;

create function app.rehash_own_password(p_hash text)
returns void
language sql volatile security definer set search_path = pg_catalog, public, app
as $$
  update users set password_hash = p_hash
   where id = app.current_user_id() and tenant_id = app.current_tenant() and status = 'ativo'
$$;

grant execute on function
  app.resolve_tenant(text),
  app.find_login(uuid, text),
  app.create_session(bytea, uuid, uuid, int, int, text, text),
  app.find_platform_login(text),
  app.create_platform_session(bytea, uuid, int, int, text, text),
  app.resolve_session(bytea, int),
  app.revoke_session(bytea),
  app.revoke_user_sessions(uuid),
  app.create_invite(uuid, bytea, text, int),
  app.consume_invite(uuid, bytea, text),
  app.rehash_own_password(text)
to app_user, app_superadmin;
```

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Db.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Db/Scripts tests/Recorrencia.Db.Tests/AuthFunctionTests.cs
git commit -m "feat(db): add sessions, invites and security-definer auth functions"
```

---

### Task 4: Catálogo de permissões, perfis por empresa e funções de escopo

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0006_rbac_catalog.sql`
- Create: `src/Recorrencia.Db/Scripts/0007_rbac_tenant.sql`
- Modify: `tests/Recorrencia.TestSupport/Seed.cs`
- Test: `tests/Recorrencia.Db.Tests/RbacTests.cs`

**Interfaces:**
- Consumes: `users`, `hierarchy_paths`, `tenants` (Task 2); `Seed` (Task 2).
- Produces (SQL): tabelas globais `modules(key, name, sort_order)`, `permissions(key, module_key, name, scoped)`, `role_templates(key, name, description)`, `role_template_permissions(template_key, permission_key, scope)`; tabelas de tenant `tenant_modules(tenant_id, module_key, enabled)`, `roles(id, tenant_id, name, source_template_key)`, `role_permissions(tenant_id, role_id, permission_key, scope)`, `user_roles(tenant_id, user_id, role_id)`. Erros `role.scope_required`, `role.scope_not_allowed`.
- Produces (funções `SECURITY DEFINER`, executáveis por `app_user`):
  - `app.effective_permissions() returns table (permission_key text, scope text)`
  - `app.scope_for(p_permission text) returns text` (`null` = sem permissão ou permissão sem escopo)
  - `app.has_permission(p_permission text) returns boolean`
  - `app.visible_owner_ids(p_scope text) returns uuid[]` (`own`/`direct`/`subtree`; `tenant` e `null` devolvem vazio)
  - `app.count_active_with_permission(p_permission text) returns int`
- Produces (testes): `Seed.EnableModulesAsync(Guid tenantId)`, `Seed.RoleAsync(Guid tenantId, string name, params (string Key, string? Scope)[] permissions)`, `Seed.AssignRoleAsync(Guid tenantId, Guid userId, Guid roleId)`.
- Catálogo exato (key → módulo, com escopo?):
  `carteira.visualizar` (sim), `carteira.exportar` (não), `comissoes.visualizar` (sim), `comissoes.exportar` (não), `fechamento.visualizar` (não), `fechamento.confirmar` (não), `fechamento.provisionar` (não), `estrutura.visualizar` (sim), `estrutura.editar` (não), `regras_comissao.visualizar` (não), `regras_comissao.editar` (não), `usuarios.convidar` (não), `usuarios.desligar` (não), `usuarios.gerenciar_perfis` (não).
- Templates: `consultor` = carteira.visualizar `own`, comissoes.visualizar `own`, fechamento.visualizar, estrutura.visualizar `direct`. `coordenador` = carteira.visualizar `tenant`, carteira.exportar, comissoes.visualizar `tenant`, comissoes.exportar, fechamento.visualizar, estrutura.visualizar `tenant`, regras_comissao.visualizar. `administrador` = todas as 14, com `tenant` nas que têm escopo.

- [ ] **Step 1: Estender o `Seed`**

Acrescente à classe `Seed` em `tests/Recorrencia.TestSupport/Seed.cs`:

```csharp
    public Task EnableModulesAsync(Guid tenantId) =>
        ExecAsync(
            "insert into tenant_modules (tenant_id, module_key) select @tenantId, key from modules on conflict do nothing",
            new { tenantId });

    public async Task<Guid> RoleAsync(Guid tenantId, string name, params (string Key, string? Scope)[] permissions)
    {
        var roleId = await ScalarAsync<Guid>(
            "insert into roles (tenant_id, name) values (@tenantId, @name) returning id", new { tenantId, name });
        foreach (var (key, scope) in permissions)
        {
            await ExecAsync(
                "insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenantId, @roleId, @key, @scope)",
                new { tenantId, roleId, key, scope });
        }
        return roleId;
    }

    public Task AssignRoleAsync(Guid tenantId, Guid userId, Guid roleId) =>
        ExecAsync(
            "insert into user_roles (tenant_id, user_id, role_id) values (@tenantId, @userId, @roleId)",
            new { tenantId, userId, roleId });
```

- [ ] **Step 2: Escrever os testes (falhando)**

`tests/Recorrencia.Db.Tests/RbacTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class RbacTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task<string?> ScopeForAsync(Guid tenant, Guid user, string permission) =>
        db.AsAppUserAsync(tenant, user, (c, t) => c.ExecuteScalarAsync<string?>("select app.scope_for(@permission)", new { permission }, t));

    private Task<bool> HasAsync(Guid tenant, Guid user, string permission) =>
        db.AsAppUserAsync(tenant, user, (c, t) => c.ExecuteScalarAsync<bool>("select app.has_permission(@permission)", new { permission }, t));

    [Fact]
    public async Task Catalog_and_templates_are_seeded()
    {
        await using var conn = await db.OpenAsync(db.OwnerConnectionString);
        var rows = (await conn.QueryAsync<(string Template, string Permission, string? Scope)>(
            "select template_key, permission_key, scope from role_template_permissions")).ToList();

        var consultor = rows.Where(r => r.Template == "consultor").Select(r => (r.Permission, r.Scope)).ToHashSet();
        var expectedConsultor = new HashSet<(string, string?)>
        {
            ("carteira.visualizar", "own"), ("comissoes.visualizar", "own"),
            ("fechamento.visualizar", null), ("estrutura.visualizar", "direct"),
        };
        Assert.True(expectedConsultor.SetEquals(consultor));
        Assert.Equal(7, rows.Count(r => r.Template == "coordenador"));
        Assert.Equal(14, await conn.ExecuteScalarAsync<int>("select count(*) from permissions"));
        Assert.Equal(14, rows.Count(r => r.Template == "administrador"));
        Assert.All(rows.Where(r => r.Template == "administrador" && r.Scope is not null), r => Assert.Equal("tenant", r.Scope));
    }

    [Fact]
    public async Task Scoped_permission_requires_a_scope()
    {
        var t = await _seed.TenantAsync();
        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.RoleAsync(t, "Sem escopo", ("carteira.visualizar", null)));
        Assert.Equal("role.scope_required", ex.MessageText);
    }

    [Fact]
    public async Task Unscoped_permission_rejects_a_scope()
    {
        var t = await _seed.TenantAsync();
        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.RoleAsync(t, "Com escopo", ("usuarios.convidar", "own")));
        Assert.Equal("role.scope_not_allowed", ex.MessageText);
    }

    [Fact]
    public async Task Effective_scope_is_the_largest_among_roles()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "João");
        var r1 = await _seed.RoleAsync(t, "Próprio", ("carteira.visualizar", "own"));
        var r2 = await _seed.RoleAsync(t, "Estrutura", ("carteira.visualizar", "subtree"), ("carteira.exportar", null));
        await _seed.AssignRoleAsync(t, u, r1);
        await _seed.AssignRoleAsync(t, u, r2);

        Assert.Equal("subtree", await ScopeForAsync(t, u, "carteira.visualizar"));
        Assert.True(await HasAsync(t, u, "carteira.exportar"));
        Assert.Null(await ScopeForAsync(t, u, "carteira.exportar"));
        Assert.False(await HasAsync(t, u, "usuarios.convidar"));
    }

    [Fact]
    public async Task Disabled_module_removes_its_permissions()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "João");
        await _seed.AssignRoleAsync(t, u, await _seed.RoleAsync(t, "Carteira", ("carteira.visualizar", "own")));
        await _seed.ExecAsync("update tenant_modules set enabled = false where tenant_id = @t and module_key = 'carteira'", new { t });

        Assert.Null(await ScopeForAsync(t, u, "carteira.visualizar"));
        Assert.False(await HasAsync(t, u, "carteira.visualizar"));
    }

    [Fact]
    public async Task Deactivated_user_has_no_permissions()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "João");
        await _seed.AssignRoleAsync(t, u, await _seed.RoleAsync(t, "Carteira", ("carteira.visualizar", "own")));
        await _seed.ExecAsync("update users set status = 'desligado' where id = @u", new { u });

        Assert.False(await HasAsync(t, u, "carteira.visualizar"));
    }

    [Fact]
    public async Task Without_context_there_are_no_permissions()
    {
        var count = await db.AsAppUserAsync(null, null, (c, t) =>
            c.ExecuteScalarAsync<long>("select count(*) from app.effective_permissions()", transaction: t));
        Assert.Equal(0L, count);
    }

    [Fact]
    public async Task Visible_owner_ids_follow_the_scope()
    {
        var t = await _seed.TenantAsync();
        var joao = await _seed.UserAsync(t, "João");
        var maria = await _seed.UserAsync(t, "Maria", joao);
        var pedro = await _seed.UserAsync(t, "Pedro", maria);

        async Task<HashSet<Guid>> Visible(string scope) =>
            (await db.AsAppUserAsync(t, joao, (c, tx) =>
                c.ExecuteScalarAsync<Guid[]>("select app.visible_owner_ids(@scope)", new { scope }, tx)))!.ToHashSet();

        Assert.True(new HashSet<Guid> { joao }.SetEquals(await Visible("own")));
        Assert.True(new HashSet<Guid> { joao, maria }.SetEquals(await Visible("direct")));
        Assert.True(new HashSet<Guid> { joao, maria, pedro }.SetEquals(await Visible("subtree")));
        Assert.Empty(await Visible("tenant"));
    }

    [Fact]
    public async Task Count_active_with_permission_ignores_deactivated_users()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var role = await _seed.RoleAsync(t, "Gestor", ("usuarios.gerenciar_perfis", null));
        var a = await _seed.UserAsync(t, "A");
        var b = await _seed.UserAsync(t, "B");
        await _seed.AssignRoleAsync(t, a, role);
        await _seed.AssignRoleAsync(t, b, role);
        await _seed.ExecAsync("update users set status = 'desligado' where id = @b", new { b });

        var count = await db.AsAppUserAsync(t, a, (c, tx) =>
            c.ExecuteScalarAsync<int>("select app.count_active_with_permission('usuarios.gerenciar_perfis')", transaction: tx));
        Assert.Equal(1, count);
    }
}
```

- [ ] **Step 3: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Db.Tests --filter "FullyQualifiedName~RbacTests"`
Expected: FAIL com `relation "role_template_permissions" does not exist`.

- [ ] **Step 4: Implementar o catálogo**

`src/Recorrencia.Db/Scripts/0006_rbac_catalog.sql`:

```sql
create table modules (
  key text primary key,
  name text not null,
  sort_order int not null
);

create table permissions (
  key text primary key,
  module_key text not null references modules (key),
  name text not null,
  scoped boolean not null,
  check (key like module_key || '.%')
);

create table role_templates (
  key text primary key,
  name text not null,
  description text not null
);

create table role_template_permissions (
  template_key text not null references role_templates (key) on delete cascade,
  permission_key text not null references permissions (key),
  scope text check (scope in ('own', 'direct', 'subtree', 'tenant')),
  primary key (template_key, permission_key)
);

create function app.check_permission_scope() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
declare
  v_scoped boolean;
begin
  select scoped into v_scoped from permissions where key = new.permission_key;
  if v_scoped and new.scope is null then
    raise exception 'role.scope_required' using errcode = 'P0001';
  end if;
  if not v_scoped and new.scope is not null then
    raise exception 'role.scope_not_allowed' using errcode = 'P0001';
  end if;
  return new;
end
$$;

create trigger role_template_permissions_scope before insert or update on role_template_permissions
  for each row execute function app.check_permission_scope();

insert into modules (key, name, sort_order) values
  ('carteira', 'Carteira', 1),
  ('comissoes', 'Comissões', 2),
  ('fechamento', 'Fechamento', 3),
  ('estrutura', 'Estrutura', 4),
  ('regras_comissao', 'Regras de comissão', 5),
  ('usuarios', 'Usuários e perfis', 6);

insert into permissions (key, module_key, name, scoped) values
  ('carteira.visualizar', 'carteira', 'Ver boletos', true),
  ('carteira.exportar', 'carteira', 'Exportar boletos', false),
  ('comissoes.visualizar', 'comissoes', 'Ver comissões', true),
  ('comissoes.exportar', 'comissoes', 'Exportar comissões', false),
  ('fechamento.visualizar', 'fechamento', 'Ver fechamentos', false),
  ('fechamento.confirmar', 'fechamento', 'Confirmar fechamento', false),
  ('fechamento.provisionar', 'fechamento', 'Marcar como provisionado', false),
  ('estrutura.visualizar', 'estrutura', 'Ver estrutura', true),
  ('estrutura.editar', 'estrutura', 'Alterar supervisores', false),
  ('regras_comissao.visualizar', 'regras_comissao', 'Ver regras de comissão', false),
  ('regras_comissao.editar', 'regras_comissao', 'Editar regras de comissão', false),
  ('usuarios.convidar', 'usuarios', 'Convidar usuários', false),
  ('usuarios.desligar', 'usuarios', 'Desligar usuários', false),
  ('usuarios.gerenciar_perfis', 'usuarios', 'Gerenciar perfis', false);

insert into role_templates (key, name, description) values
  ('consultor', 'Consultor', 'Vê a própria carteira e as próprias comissões.'),
  ('coordenador', 'Coordenador', 'Acompanha toda a operação da empresa.'),
  ('administrador', 'Administrador', 'Configura a empresa, perfis, estrutura e regras.');

insert into role_template_permissions (template_key, permission_key, scope) values
  ('consultor', 'carteira.visualizar', 'own'),
  ('consultor', 'comissoes.visualizar', 'own'),
  ('consultor', 'fechamento.visualizar', null),
  ('consultor', 'estrutura.visualizar', 'direct'),
  ('coordenador', 'carteira.visualizar', 'tenant'),
  ('coordenador', 'carteira.exportar', null),
  ('coordenador', 'comissoes.visualizar', 'tenant'),
  ('coordenador', 'comissoes.exportar', null),
  ('coordenador', 'fechamento.visualizar', null),
  ('coordenador', 'estrutura.visualizar', 'tenant'),
  ('coordenador', 'regras_comissao.visualizar', null);

insert into role_template_permissions (template_key, permission_key, scope)
select 'administrador', key, case when scoped then 'tenant' end from permissions;

grant select on modules, permissions, role_templates, role_template_permissions to app_user;
```

- [ ] **Step 5: Implementar perfis por empresa e funções de escopo**

`src/Recorrencia.Db/Scripts/0007_rbac_tenant.sql`:

```sql
create table tenant_modules (
  tenant_id uuid not null references tenants (id) on delete cascade,
  module_key text not null references modules (key),
  enabled boolean not null default true,
  primary key (tenant_id, module_key)
);

create table roles (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  name text not null check (length(btrim(name)) > 0),
  source_template_key text references role_templates (key),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, name),
  unique (tenant_id, id)
);

create trigger roles_touch before update on roles
  for each row execute function app.touch_updated_at();

create table role_permissions (
  tenant_id uuid not null,
  role_id uuid not null,
  permission_key text not null references permissions (key),
  scope text check (scope in ('own', 'direct', 'subtree', 'tenant')),
  primary key (role_id, permission_key),
  foreign key (tenant_id, role_id) references roles (tenant_id, id) on delete cascade
);

create trigger role_permissions_scope before insert or update on role_permissions
  for each row execute function app.check_permission_scope();

create table user_roles (
  tenant_id uuid not null,
  user_id uuid not null,
  role_id uuid not null,
  primary key (user_id, role_id),
  foreign key (tenant_id, user_id) references users (tenant_id, id) on delete cascade,
  foreign key (tenant_id, role_id) references roles (tenant_id, id) on delete cascade
);

create index user_roles_role_idx on user_roles (role_id);

create function app.effective_permissions()
returns table (permission_key text, scope text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select rp.permission_key,
         (array_agg(rp.scope order by array_position(array['own', 'direct', 'subtree', 'tenant'], rp.scope) desc nulls last))[1]
    from user_roles ur
    join users u on u.id = ur.user_id and u.status = 'ativo'
    join role_permissions rp on rp.role_id = ur.role_id
    join permissions p on p.key = rp.permission_key
    join tenant_modules tm on tm.tenant_id = ur.tenant_id and tm.module_key = p.module_key and tm.enabled
   where ur.user_id = app.current_user_id()
     and ur.tenant_id = app.current_tenant()
   group by rp.permission_key
$$;

create function app.scope_for(p_permission text)
returns text
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select e.scope from app.effective_permissions() e where e.permission_key = p_permission
$$;

create function app.has_permission(p_permission text)
returns boolean
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select exists (select 1 from app.effective_permissions() e where e.permission_key = p_permission)
$$;

create function app.visible_owner_ids(p_scope text)
returns uuid[]
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select case
    when p_scope = 'own' then array[app.current_user_id()]
    when p_scope in ('direct', 'subtree') then coalesce((
      select array_agg(hp.descendant_id)
        from hierarchy_paths hp
       where hp.ancestor_id = app.current_user_id()
         and hp.tenant_id = app.current_tenant()
         and (p_scope = 'subtree' or hp.depth <= 1)), array[]::uuid[])
    else array[]::uuid[]
  end
$$;

create function app.count_active_with_permission(p_permission text)
returns int
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select count(distinct u.id)::int
    from users u
    join user_roles ur on ur.user_id = u.id
    join role_permissions rp on rp.role_id = ur.role_id and rp.permission_key = p_permission
    join permissions p on p.key = rp.permission_key
    join tenant_modules tm on tm.tenant_id = u.tenant_id and tm.module_key = p.module_key and tm.enabled
   where u.tenant_id = app.current_tenant()
     and u.status = 'ativo'
$$;

grant execute on function
  app.effective_permissions(),
  app.scope_for(text),
  app.has_permission(text),
  app.visible_owner_ids(text),
  app.count_active_with_permission(text)
to app_user, app_superadmin;

grant select on tenant_modules to app_user;
grant select, insert, update, delete on roles, role_permissions, user_roles to app_user;
```

- [ ] **Step 6: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Db.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Recorrencia.Db/Scripts tests/Recorrencia.TestSupport/Seed.cs tests/Recorrencia.Db.Tests/RbacTests.cs
git commit -m "feat(db): add modular RBAC catalog, tenant roles and scope functions"
```

---

### Task 5: Tabelas do motor de comissão e provisionamento de empresa

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0008_commission.sql`
- Create: `src/Recorrencia.Db/Scripts/0009_provisioning.sql`
- Modify: `tests/Recorrencia.TestSupport/Seed.cs`
- Test: `tests/Recorrencia.Db.Tests/CommissionSchemaTests.cs`, `tests/Recorrencia.Db.Tests/ProvisioningTests.cs`

**Interfaces:**
- Consumes: tabelas de RBAC (Task 4), `users` (Task 2).
- Produces (SQL): `plan_templates(key, name)`, `plan_template_rules(template_key, position, type, rate, level, group_name)`, `commission_groups(id, tenant_id, name)`, `commission_group_members(tenant_id, group_id, user_id)`, `commission_plans(id, tenant_id, name, effective_from, status, source_template_key, activated_at)`, `commission_rules(id, tenant_id, plan_id, type, rate, level, group_id)`. Índice único parcial `commission_plans_active_effective` em `(tenant_id, effective_from) where status = 'ativo'`. Erros: `plan.active_immutable`, `plan.empty`, `plan.template_not_found`.
- Produces: `app.provision_tenant(p_slug text, p_name text, p_admin_name text, p_admin_email text, p_plan_template text, p_plan_effective_from date) returns table (tenant_id uuid, admin_user_id uuid)`, executável **só** por `app_superadmin`.
- Produces (testes): `Seed.ProvisionAsync(string? slug = null, string? planTemplate = "aprovec", string effectiveFrom = "2026-08-01") → Task<(Guid TenantId, Guid AdminId)>`; `Seed.AssignTemplateRoleAsync(Guid tenantId, Guid userId, string templateKey)`.
- Plano sugerido `aprovec` ("Modelo APROVEC"): `own` 0.0700; `upline` nível 1, 0.0200; `global` grupo "Coordenação", 0.0100.

- [ ] **Step 1: Estender o `Seed`**

Acrescente à classe `Seed`:

```csharp
    public Task<(Guid TenantId, Guid AdminId)> ProvisionAsync(string? slug = null, string? planTemplate = "aprovec", string effectiveFrom = "2026-08-01") =>
        db.AsSuperadminAsync(null, (c, t) => c.QuerySingleAsync<(Guid, Guid)>(
            """
            select tenant_id, admin_user_id
              from app.provision_tenant(@slug, 'Empresa provisionada', 'Admin', @adminEmail, @planTemplate, @effectiveFrom::date)
            """,
            new { slug = slug ?? UniqueSlug(), adminEmail = $"admin-{Guid.NewGuid():N}@teste.local", planTemplate, effectiveFrom },
            t));

    public Task AssignTemplateRoleAsync(Guid tenantId, Guid userId, string templateKey) =>
        ExecAsync(
            """
            insert into user_roles (tenant_id, user_id, role_id)
            select @tenantId, @userId, id from roles where tenant_id = @tenantId and source_template_key = @templateKey
            """,
            new { tenantId, userId, templateKey });
```

- [ ] **Step 2: Escrever os testes (falhando)**

`tests/Recorrencia.Db.Tests/ProvisioningTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class ProvisioningTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    [Fact]
    public async Task Provisioning_creates_modules_roles_admin_and_plan()
    {
        var (t, admin) = await _seed.ProvisionAsync();

        Assert.Equal(6, await _seed.ScalarAsync<int>("select count(*)::int from tenant_modules where tenant_id = @t and enabled", new { t }));
        Assert.Equal(3, await _seed.ScalarAsync<int>("select count(*)::int from roles where tenant_id = @t", new { t }));
        Assert.Equal(25, await _seed.ScalarAsync<int>("select count(*)::int from role_permissions where tenant_id = @t", new { t }));
        Assert.Equal("convidado|administrador", await _seed.ScalarAsync<string>(
            """
            select u.status || '|' || r.source_template_key
              from users u join user_roles ur on ur.user_id = u.id join roles r on r.id = ur.role_id
             where u.id = @admin
            """, new { admin }));

        var rules = (await (await db.OpenAsync(db.OwnerConnectionString)).QueryAsync<(string Type, decimal Rate, int? Level, string? Group)>(
            """
            select r.type, r.rate, r.level, g.name
              from commission_plans p
              join commission_rules r on r.plan_id = p.id
              left join commission_groups g on g.id = r.group_id
             where p.tenant_id = @t and p.status = 'ativo' and p.effective_from = date '2026-08-01'
             order by r.type
            """, new { t })).ToList();

        Assert.Equal(
            new List<(string, decimal, int?, string?)> { ("global", 0.0100m, null, "Coordenação"), ("own", 0.0700m, null, null), ("upline", 0.0200m, 1, null) },
            rules.Select(r => (r.Type, r.Rate, r.Level, r.Group)).ToList());
    }

    [Fact]
    public async Task Provisioning_without_plan_template_creates_no_plan()
    {
        var (t, _) = await _seed.ProvisionAsync(planTemplate: null);
        Assert.Equal(0, await _seed.ScalarAsync<int>("select count(*)::int from commission_plans where tenant_id = @t", new { t }));
    }

    [Fact]
    public async Task Unknown_plan_template_rolls_everything_back()
    {
        var slug = Seed.UniqueSlug();
        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ProvisionAsync(slug, planTemplate: "inexistente"));

        Assert.Equal("plan.template_not_found", ex.MessageText);
        Assert.Equal(0, await _seed.ScalarAsync<int>("select count(*)::int from tenants where slug = @slug", new { slug }));
    }

    [Fact]
    public async Task App_user_cannot_provision_tenants()
    {
        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(null, null, (c, t) => c.ExecuteAsync(
            "select * from app.provision_tenant('x', 'X', 'Admin', 'a@b.c', null, null)", transaction: t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }
}
```

`tests/Recorrencia.Db.Tests/CommissionSchemaTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class CommissionSchemaTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task<Guid> DraftPlanAsync(Guid tenant, string effectiveFrom = "2026-10-01") =>
        _seed.ScalarAsync<Guid>(
            "insert into commission_plans (tenant_id, name, effective_from) values (@tenant, 'Rascunho', @effectiveFrom::date) returning id",
            new { tenant, effectiveFrom });

    [Fact]
    public async Task Active_plan_rules_cannot_change()
    {
        var (t, _) = await _seed.ProvisionAsync();

        var update = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update commission_rules set rate = 0.0800 where tenant_id = @t and type = 'own'", new { t }));
        Assert.Equal("plan.active_immutable", update.MessageText);

        var delete = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "delete from commission_plans where tenant_id = @t", new { t }));
        Assert.Equal("plan.active_immutable", delete.MessageText);
    }

    [Fact]
    public async Task Empty_plan_cannot_be_activated()
    {
        var (t, _) = await _seed.ProvisionAsync(planTemplate: null);
        var plan = await DraftPlanAsync(t);

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update commission_plans set status = 'ativo', activated_at = now() where id = @plan", new { plan }));
        Assert.Equal("plan.empty", ex.MessageText);
    }

    [Fact]
    public async Task Upline_rule_requires_level_and_levels_are_unique()
    {
        var (t, _) = await _seed.ProvisionAsync(planTemplate: null);
        var plan = await DraftPlanAsync(t);

        var noLevel = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "insert into commission_rules (tenant_id, plan_id, type, rate) values (@t, @plan, 'upline', 0.02)", new { t, plan }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, noLevel.SqlState);

        await _seed.ExecAsync("insert into commission_rules (tenant_id, plan_id, type, rate, level) values (@t, @plan, 'upline', 0.02, 1)", new { t, plan });
        var duplicate = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "insert into commission_rules (tenant_id, plan_id, type, rate, level) values (@t, @plan, 'upline', 0.01, 1)", new { t, plan }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [Fact]
    public async Task Rate_must_be_between_zero_and_one()
    {
        var (t, _) = await _seed.ProvisionAsync(planTemplate: null);
        var plan = await DraftPlanAsync(t);

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "insert into commission_rules (tenant_id, plan_id, type, rate) values (@t, @plan, 'own', 1.5)", new { t, plan }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Two_active_plans_cannot_share_the_effective_date()
    {
        var (t, _) = await _seed.ProvisionAsync();
        var plan = await DraftPlanAsync(t, "2026-08-01");
        await _seed.ExecAsync("insert into commission_rules (tenant_id, plan_id, type, rate) values (@t, @plan, 'own', 0.05)", new { t, plan });

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update commission_plans set status = 'ativo', activated_at = now() where id = @plan", new { plan }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Effective_date_must_be_the_first_day_of_a_month()
    {
        var (t, _) = await _seed.ProvisionAsync(planTemplate: null);
        var ex = await DbExtensions.ThrowsPgAsync(() => DraftPlanAsync(t, "2026-10-15"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }
}
```

O total de 25 em `role_permissions` = 4 (consultor) + 7 (coordenador) + 14 (administrador).

- [ ] **Step 3: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Db.Tests --filter "FullyQualifiedName~ProvisioningTests|FullyQualifiedName~CommissionSchemaTests"`
Expected: FAIL com `function app.provision_tenant(...) does not exist`.

- [ ] **Step 4: Implementar as tabelas de comissão**

`src/Recorrencia.Db/Scripts/0008_commission.sql`:

```sql
create table plan_templates (
  key text primary key,
  name text not null
);

create table plan_template_rules (
  template_key text not null references plan_templates (key) on delete cascade,
  position int not null,
  type text not null check (type in ('own', 'upline', 'global')),
  rate numeric(7,4) not null check (rate > 0 and rate <= 1),
  level int,
  group_name text,
  primary key (template_key, position),
  check (
    (type = 'own' and level is null and group_name is null)
    or (type = 'upline' and level >= 1 and group_name is null)
    or (type = 'global' and level is null and group_name is not null)
  )
);

insert into plan_templates (key, name) values ('aprovec', 'Modelo APROVEC');
insert into plan_template_rules (template_key, position, type, rate, level, group_name) values
  ('aprovec', 1, 'own', 0.0700, null, null),
  ('aprovec', 2, 'upline', 0.0200, 1, null),
  ('aprovec', 3, 'global', 0.0100, null, 'Coordenação');

create table commission_groups (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  name text not null check (length(btrim(name)) > 0),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, name),
  unique (tenant_id, id)
);

create trigger commission_groups_touch before update on commission_groups
  for each row execute function app.touch_updated_at();

create table commission_group_members (
  tenant_id uuid not null,
  group_id uuid not null,
  user_id uuid not null,
  created_at timestamptz not null default now(),
  primary key (group_id, user_id),
  foreign key (tenant_id, group_id) references commission_groups (tenant_id, id) on delete cascade,
  foreign key (tenant_id, user_id) references users (tenant_id, id) on delete cascade
);

create table commission_plans (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  name text not null check (length(btrim(name)) > 0),
  effective_from date not null check (extract(day from effective_from) = 1),
  status text not null default 'rascunho' check (status in ('rascunho', 'ativo')),
  source_template_key text references plan_templates (key),
  activated_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, id),
  check ((status = 'ativo') = (activated_at is not null))
);

create unique index commission_plans_active_effective
  on commission_plans (tenant_id, effective_from) where status = 'ativo';

create trigger commission_plans_touch before update on commission_plans
  for each row execute function app.touch_updated_at();

create table commission_rules (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null,
  plan_id uuid not null,
  type text not null check (type in ('own', 'upline', 'global')),
  rate numeric(7,4) not null check (rate > 0 and rate <= 1),
  level int,
  group_id uuid,
  foreign key (tenant_id, plan_id) references commission_plans (tenant_id, id) on delete cascade,
  foreign key (tenant_id, group_id) references commission_groups (tenant_id, id),
  check (
    (type = 'own' and level is null and group_id is null)
    or (type = 'upline' and level >= 1 and group_id is null)
    or (type = 'global' and level is null and group_id is not null)
  )
);

create unique index commission_rules_one_own on commission_rules (plan_id) where type = 'own';
create unique index commission_rules_unique_level on commission_rules (plan_id, level) where type = 'upline';
create unique index commission_rules_unique_group on commission_rules (plan_id, group_id) where type = 'global';

create function app.commission_plans_guard() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  if old.status = 'ativo' then
    raise exception 'plan.active_immutable' using errcode = 'P0001';
  end if;
  if tg_op = 'DELETE' then
    return old;
  end if;
  if new.status = 'ativo' and not exists (select 1 from commission_rules where plan_id = new.id) then
    raise exception 'plan.empty' using errcode = 'P0001';
  end if;
  return new;
end
$$;

create trigger commission_plans_guard before update or delete on commission_plans
  for each row execute function app.commission_plans_guard();

create function app.commission_rules_guard() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
declare
  v_plan uuid := case when tg_op = 'DELETE' then old.plan_id else new.plan_id end;
begin
  if exists (select 1 from commission_plans where id = v_plan and status = 'ativo') then
    raise exception 'plan.active_immutable' using errcode = 'P0001';
  end if;
  if tg_op = 'DELETE' then
    return old;
  end if;
  return new;
end
$$;

create trigger commission_rules_guard before insert or update or delete on commission_rules
  for each row execute function app.commission_rules_guard();

grant select on plan_templates, plan_template_rules to app_user;
grant select, insert, update, delete on commission_groups, commission_group_members, commission_plans, commission_rules to app_user;
```

- [ ] **Step 5: Implementar o provisionamento**

`src/Recorrencia.Db/Scripts/0009_provisioning.sql`:

```sql
create function app.provision_tenant(
  p_slug text, p_name text, p_admin_name text, p_admin_email text,
  p_plan_template text, p_plan_effective_from date)
returns table (tenant_id uuid, admin_user_id uuid)
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
#variable_conflict use_column
declare
  v_tenant uuid;
  v_admin uuid;
  v_admin_role uuid;
  v_plan uuid;
begin
  insert into tenants (slug, name) values (lower(p_slug), p_name) returning id into v_tenant;

  insert into tenant_modules (tenant_id, module_key) select v_tenant, key from modules;

  insert into roles (tenant_id, name, source_template_key)
  select v_tenant, name, key from role_templates;

  insert into role_permissions (tenant_id, role_id, permission_key, scope)
  select v_tenant, r.id, tp.permission_key, tp.scope
    from roles r
    join role_template_permissions tp on tp.template_key = r.source_template_key
   where r.tenant_id = v_tenant;

  insert into users (tenant_id, name, email) values (v_tenant, p_admin_name, p_admin_email::citext)
  returning id into v_admin;

  select id into v_admin_role from roles where tenant_id = v_tenant and source_template_key = 'administrador';
  insert into user_roles (tenant_id, user_id, role_id) values (v_tenant, v_admin, v_admin_role);

  if p_plan_template is not null then
    if not exists (select 1 from plan_templates where key = p_plan_template) then
      raise exception 'plan.template_not_found' using errcode = 'P0001';
    end if;

    insert into commission_groups (tenant_id, name)
    select distinct v_tenant, group_name
      from plan_template_rules
     where template_key = p_plan_template and group_name is not null;

    insert into commission_plans (tenant_id, name, effective_from, source_template_key)
    select v_tenant, name, p_plan_effective_from, key from plan_templates where key = p_plan_template
    returning id into v_plan;

    insert into commission_rules (tenant_id, plan_id, type, rate, level, group_id)
    select v_tenant, v_plan, tr.type, tr.rate, tr.level, g.id
      from plan_template_rules tr
      left join commission_groups g on g.tenant_id = v_tenant and g.name = tr.group_name
     where tr.template_key = p_plan_template
     order by tr.position;

    update commission_plans set status = 'ativo', activated_at = now() where id = v_plan;
  end if;

  return query select v_tenant, v_admin;
end
$$;

grant execute on function app.provision_tenant(text, text, text, text, text, date) to app_superadmin;
```

- [ ] **Step 6: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Db.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Recorrencia.Db/Scripts tests/Recorrencia.TestSupport/Seed.cs tests/Recorrencia.Db.Tests
git commit -m "feat(db): add commission plans, groups and tenant provisioning"
```

---

### Task 6: Boletos, fechamentos, snapshot e auditoria

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0010_boletos_fechamentos.sql`
- Modify: `tests/Recorrencia.TestSupport/Seed.cs`
- Test: `tests/Recorrencia.Db.Tests/BoletoFechamentoTests.cs`

**Interfaces:**
- Consumes: `users`, `tenants` (Task 2).
- Produces (SQL): `boletos(id, tenant_id, participante_id, associado_ref, associado_nome, placa, valor, status, vencimento, pago_em)`; `fechamentos(id, tenant_id, competencia, status, confirmado_em, confirmado_por, provisionado_em, provisionado_por)` único por `(tenant_id, competencia)`; `fechamento_detalhes(id, tenant_id, fechamento_id, beneficiario_id, origem_participante_id, boleto_id, rule_id, rule_type, level, group_id, rate, base, valor)`; `audit_log(id, tenant_id, user_id, action, entity, entity_id, before, after, created_at)`. Erros: `fechamento.invalid_initial_status`, `fechamento.invalid_transition`, `fechamento.immutable`.
- Transições permitidas: `apuracao→conferencia`, `apuracao→confirmado`, `conferencia→apuracao`, `conferencia→confirmado`, `confirmado→provisionado`.
- Produces (testes): `Seed.BoletoAsync(Guid tenantId, Guid ownerId, decimal valor, string status, string? paidOn) → Task<Guid>`.

- [ ] **Step 1: Estender o `Seed`**

```csharp
    public Task<Guid> BoletoAsync(Guid tenantId, Guid ownerId, decimal valor, string status, string? paidOn) =>
        ScalarAsync<Guid>(
            """
            insert into boletos (tenant_id, participante_id, associado_ref, associado_nome, placa, valor, status, vencimento, pago_em)
            values (@tenantId, @ownerId, @reference, 'Associado de teste', 'TST0A00', @valor, @status,
                    coalesce(@paidOn::date, date '2026-09-01'), @paidOn::date)
            returning id
            """,
            new { tenantId, ownerId, reference = "APV-" + Guid.NewGuid().ToString("N")[..8], valor, status, paidOn });
```

- [ ] **Step 2: Escrever os testes (falhando)**

`tests/Recorrencia.Db.Tests/BoletoFechamentoTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class BoletoFechamentoTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task<Guid> FechamentoAsync(Guid tenant, string competencia = "2026-08-01") =>
        _seed.ScalarAsync<Guid>(
            "insert into fechamentos (tenant_id, competencia) values (@tenant, @competencia::date) returning id",
            new { tenant, competencia });

    [Fact]
    public async Task Paid_date_is_required_exactly_when_received()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "João");

        var missing = await DbExtensions.ThrowsPgAsync(() => _seed.BoletoAsync(t, u, 200m, "recebido", null));
        Assert.Equal(PostgresErrorCodes.CheckViolation, missing.SqlState);

        var unexpected = await DbExtensions.ThrowsPgAsync(() => _seed.BoletoAsync(t, u, 200m, "atraso", "2026-09-01"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, unexpected.SqlState);
    }

    [Fact]
    public async Task Boleto_owner_must_belong_to_the_tenant()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t1, "João");

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.BoletoAsync(t2, u, 200m, "recebido", "2026-09-01"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Fechamento_starts_in_apuracao_and_competencia_is_a_month()
    {
        var t = await _seed.TenantAsync();

        var status = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "insert into fechamentos (tenant_id, competencia, status) values (@t, date '2026-08-01', 'conferencia')", new { t }));
        Assert.Equal("fechamento.invalid_initial_status", status.MessageText);

        var day = await DbExtensions.ThrowsPgAsync(() => FechamentoAsync(t, "2026-08-15"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, day.SqlState);
    }

    [Fact]
    public async Task Confirmation_requires_who_and_when_and_cannot_be_undone()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Admin");
        var f = await FechamentoAsync(t);

        var incomplete = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update fechamentos set status = 'confirmado' where id = @f", new { f }));
        Assert.Equal(PostgresErrorCodes.CheckViolation, incomplete.SqlState);

        await _seed.ExecAsync(
            "update fechamentos set status = 'confirmado', confirmado_em = now(), confirmado_por = @u where id = @f", new { f, u });

        var back = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(
            "update fechamentos set status = 'apuracao' where id = @f", new { f }));
        Assert.Equal("fechamento.invalid_transition", back.MessageText);

        await _seed.ExecAsync(
            "update fechamentos set status = 'provisionado', provisionado_em = now(), provisionado_por = @u where id = @f", new { f, u });
    }

    [Fact]
    public async Task Details_cannot_be_added_after_confirmation()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Admin");
        var boleto = await _seed.BoletoAsync(t, u, 200m, "recebido", "2026-08-05");
        var f = await FechamentoAsync(t);

        const string insertDetail = """
            insert into fechamento_detalhes
              (tenant_id, fechamento_id, beneficiario_id, origem_participante_id, boleto_id, rule_id, rule_type, level, group_id, rate, base, valor)
            values (@t, @f, @u, @u, @boleto, gen_random_uuid(), 'own', null, null, 0.07, 200, 14)
            """;
        await _seed.ExecAsync(insertDetail, new { t, f, u, boleto });

        await _seed.ExecAsync(
            "update fechamentos set status = 'confirmado', confirmado_em = now(), confirmado_por = @u where id = @f", new { f, u });

        var ex = await DbExtensions.ThrowsPgAsync(() => _seed.ExecAsync(insertDetail, new { t, f, u, boleto }));
        Assert.Equal("fechamento.immutable", ex.MessageText);
    }
}
```

- [ ] **Step 3: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Db.Tests --filter "FullyQualifiedName~BoletoFechamentoTests"`
Expected: FAIL com `relation "boletos" does not exist`.

- [ ] **Step 4: Implementar a migração**

`src/Recorrencia.Db/Scripts/0010_boletos_fechamentos.sql`:

```sql
create table boletos (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  participante_id uuid not null,
  associado_ref text not null,
  associado_nome text not null,
  placa text,
  valor numeric(14,2) not null check (valor > 0),
  status text not null check (status in ('a_vencer', 'recebido', 'atraso', 'cancelado')),
  vencimento date not null,
  pago_em date,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, id),
  foreign key (tenant_id, participante_id) references users (tenant_id, id),
  check ((status = 'recebido') = (pago_em is not null))
);

create index boletos_participante_idx on boletos (participante_id);
create index boletos_pagos_idx on boletos (tenant_id, pago_em) where status = 'recebido';

create trigger boletos_touch before update on boletos
  for each row execute function app.touch_updated_at();

create table fechamentos (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  competencia date not null check (extract(day from competencia) = 1),
  status text not null default 'apuracao'
    check (status in ('apuracao', 'conferencia', 'confirmado', 'provisionado')),
  confirmado_em timestamptz,
  confirmado_por uuid,
  provisionado_em timestamptz,
  provisionado_por uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, competencia),
  unique (tenant_id, id),
  foreign key (tenant_id, confirmado_por) references users (tenant_id, id),
  foreign key (tenant_id, provisionado_por) references users (tenant_id, id),
  check (status not in ('confirmado', 'provisionado') or (confirmado_em is not null and confirmado_por is not null)),
  check (status <> 'provisionado' or (provisionado_em is not null and provisionado_por is not null))
);

create trigger fechamentos_touch before update on fechamentos
  for each row execute function app.touch_updated_at();

create function app.fechamentos_guard() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  if tg_op = 'INSERT' then
    if new.status <> 'apuracao' then
      raise exception 'fechamento.invalid_initial_status' using errcode = 'P0001';
    end if;
    return new;
  end if;

  if new.competencia <> old.competencia then
    raise exception 'fechamento.immutable' using errcode = 'P0001';
  end if;

  if new.status is distinct from old.status and not (
       (old.status = 'apuracao' and new.status in ('conferencia', 'confirmado'))
    or (old.status = 'conferencia' and new.status in ('apuracao', 'confirmado'))
    or (old.status = 'confirmado' and new.status = 'provisionado')) then
    raise exception 'fechamento.invalid_transition' using errcode = 'P0001';
  end if;

  if old.status in ('confirmado', 'provisionado')
     and (new.confirmado_em is distinct from old.confirmado_em or new.confirmado_por is distinct from old.confirmado_por) then
    raise exception 'fechamento.immutable' using errcode = 'P0001';
  end if;

  return new;
end
$$;

create trigger fechamentos_guard before insert or update on fechamentos
  for each row execute function app.fechamentos_guard();

create table fechamento_detalhes (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null,
  fechamento_id uuid not null,
  beneficiario_id uuid not null,
  origem_participante_id uuid not null,
  boleto_id uuid not null,
  rule_id uuid not null,
  rule_type text not null check (rule_type in ('own', 'upline', 'global')),
  level int,
  group_id uuid,
  rate numeric(7,4) not null,
  base numeric(14,2) not null,
  valor numeric(14,2) not null,
  created_at timestamptz not null default now(),
  foreign key (tenant_id, fechamento_id) references fechamentos (tenant_id, id),
  foreign key (tenant_id, beneficiario_id) references users (tenant_id, id),
  foreign key (tenant_id, origem_participante_id) references users (tenant_id, id),
  foreign key (tenant_id, boleto_id) references boletos (tenant_id, id)
);

create index fechamento_detalhes_beneficiario_idx on fechamento_detalhes (fechamento_id, beneficiario_id);

create function app.fechamento_detalhes_guard() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  if exists (select 1 from fechamentos where id = new.fechamento_id and status in ('confirmado', 'provisionado')) then
    raise exception 'fechamento.immutable' using errcode = 'P0001';
  end if;
  return new;
end
$$;

create trigger fechamento_detalhes_guard before insert on fechamento_detalhes
  for each row execute function app.fechamento_detalhes_guard();

create table audit_log (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  user_id uuid not null,
  action text not null,
  entity text not null,
  entity_id uuid,
  before jsonb,
  after jsonb,
  created_at timestamptz not null default now(),
  foreign key (tenant_id, user_id) references users (tenant_id, id)
);

create index audit_log_entity_idx on audit_log (tenant_id, entity, entity_id);

grant select on boletos to app_user;
grant select, insert, update on fechamentos to app_user;
grant select, insert on fechamento_detalhes to app_user;
grant insert on audit_log to app_user;
```

`group_id` e `rule_id` em `fechamento_detalhes` não têm FK de propósito: o snapshot precisa sobreviver a mudanças nos grupos e planos.

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Db.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Db/Scripts tests/Recorrencia.TestSupport/Seed.cs tests/Recorrencia.Db.Tests/BoletoFechamentoTests.cs
git commit -m "feat(db): add boletos, monthly closings with frozen details and audit log"
```

---

### Task 7: Row Level Security

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0011_rls.sql`
- Create: `tests/Recorrencia.TestSupport/RlsScenario.cs`
- Test: `tests/Recorrencia.Db.Tests/RlsTests.cs`

**Interfaces:**
- Consumes: todas as tabelas de tenant (Tasks 2–6); `app.scope_for`, `app.has_permission`, `app.visible_owner_ids` (Task 4); `Seed.ProvisionAsync`, `Seed.AssignTemplateRoleAsync`, `Seed.BoletoAsync`, `Seed.RoleAsync`, `Seed.AssignRoleAsync` (Tasks 4–6).
- Produces (testes): `RlsScenario.CreateAsync(PostgresFixture db)` → `record RlsScenario(Guid TenantA, Guid TenantB, Guid Admin, Guid Joao, Guid Maria, Guid Pedro, Guid Coord, Guid OutsiderAdmin)`.
  - Tenant A provisionado com o plano `aprovec` (vigência 2026-08-01). Admin ativo (perfil administrador). João, Maria (supervisor João) e Pedro (supervisor Maria) com perfil consultor. Coord com perfil coordenador e membro do grupo "Coordenação".
  - Boletos em A: João 100 e 100 recebidos em 2026-09-05/06, 300 em atraso, 80 recebido em 2026-08-10; Maria 50 e 50 recebidos em 2026-09-07/08; Pedro 40 recebido em 2026-09-09. Total em A: 7 boletos.
  - Tenant B: admin ativo com 1 boleto de 999 recebido em 2026-09-05.
- Produces (SQL): RLS habilitado e forçado em `users`, `hierarchy_paths`, `tenant_modules`, `roles`, `role_permissions`, `user_roles`, `commission_groups`, `commission_group_members`, `commission_plans`, `commission_rules`, `boletos`, `fechamentos`, `fechamento_detalhes`, `audit_log`. Cada tabela tem uma policy **restrictive** de isolamento por `tenant_id` e policies permissivas por comando, conforme a tabela abaixo.

| Tabela | SELECT | INSERT/UPDATE/DELETE |
|---|---|---|
| `users` | a própria linha, ou escopo de `estrutura.visualizar` | INSERT: `usuarios.convidar`; UPDATE: `estrutura.editar` ou `usuarios.desligar` |
| `hierarchy_paths` | ancestral e descendente dentro do escopo de `estrutura.visualizar` | nenhum (só triggers) |
| `tenant_modules` | todas do tenant | nenhum |
| `roles`, `role_permissions` | todas do tenant | `usuarios.gerenciar_perfis` |
| `user_roles` | as próprias, ou todas com `usuarios.gerenciar_perfis` | `usuarios.gerenciar_perfis` |
| `commission_*` | todas do tenant | `regras_comissao.editar` |
| `boletos` | escopo de `carteira.visualizar` pelo `participante_id` | nenhum |
| `fechamentos` | `fechamento.visualizar` | `fechamento.confirmar` ou `fechamento.provisionar` (sem DELETE) |
| `fechamento_detalhes` | escopo de `comissoes.visualizar` pelo `beneficiario_id` | INSERT: `fechamento.confirmar` |
| `audit_log` | nenhum | INSERT com `user_id = app.current_user_id()` |

- [ ] **Step 1: Criar o cenário de testes**

`tests/Recorrencia.TestSupport/RlsScenario.cs`:

```csharp
namespace Recorrencia.TestSupport;

public sealed record RlsScenario(
    Guid TenantA, Guid TenantB, Guid Admin, Guid Joao, Guid Maria, Guid Pedro, Guid Coord, Guid OutsiderAdmin)
{
    public static async Task<RlsScenario> CreateAsync(PostgresFixture db)
    {
        var seed = new Seed(db);
        var (a, admin) = await seed.ProvisionAsync();
        var (b, outsider) = await seed.ProvisionAsync();
        await seed.ExecAsync(
            "update users set status = 'ativo', password_hash = 'hash-de-teste' where id = any(@ids)",
            new { ids = new[] { admin, outsider } });

        var joao = await seed.UserAsync(a, "João");
        var maria = await seed.UserAsync(a, "Maria", joao);
        var pedro = await seed.UserAsync(a, "Pedro", maria);
        var coord = await seed.UserAsync(a, "Coordenação");
        foreach (var consultor in new[] { joao, maria, pedro })
            await seed.AssignTemplateRoleAsync(a, consultor, "consultor");
        await seed.AssignTemplateRoleAsync(a, coord, "coordenador");
        await seed.ExecAsync(
            """
            insert into commission_group_members (tenant_id, group_id, user_id)
            select @a, id, @coord from commission_groups where tenant_id = @a and name = 'Coordenação'
            """,
            new { a, coord });

        await seed.BoletoAsync(a, joao, 100m, "recebido", "2026-09-05");
        await seed.BoletoAsync(a, joao, 100m, "recebido", "2026-09-06");
        await seed.BoletoAsync(a, joao, 300m, "atraso", null);
        await seed.BoletoAsync(a, joao, 80m, "recebido", "2026-08-10");
        await seed.BoletoAsync(a, maria, 50m, "recebido", "2026-09-07");
        await seed.BoletoAsync(a, maria, 50m, "recebido", "2026-09-08");
        await seed.BoletoAsync(a, pedro, 40m, "recebido", "2026-09-09");
        await seed.BoletoAsync(b, outsider, 999m, "recebido", "2026-09-05");

        return new RlsScenario(a, b, admin, joao, maria, pedro, coord, outsider);
    }
}
```

- [ ] **Step 2: Escrever os testes (falhando)**

`tests/Recorrencia.Db.Tests/RlsTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class RlsTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task<long> CountAsync(Guid? tenant, Guid? user, string table) =>
        db.AsAppUserAsync(tenant, user, (c, t) => c.ExecuteScalarAsync<long>($"select count(*) from {table}", transaction: t));

    private Task<HashSet<Guid>> BoletoOwnersAsync(Guid tenant, Guid user) =>
        db.AsAppUserAsync(tenant, user, async (c, t) =>
            (await c.QueryAsync<Guid>("select distinct participante_id from boletos", transaction: t)).ToHashSet());

    [Fact]
    public async Task Without_context_nothing_is_visible()
    {
        await RlsScenario.CreateAsync(db);
        Assert.Equal(0L, await CountAsync(null, null, "users"));
        Assert.Equal(0L, await CountAsync(null, null, "boletos"));
        Assert.Equal(0L, await CountAsync(null, null, "roles"));
    }

    [Fact]
    public async Task Consultor_sees_only_own_boletos()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Equal(4L, await CountAsync(s.TenantA, s.Joao, "boletos"));
        Assert.True(new HashSet<Guid> { s.Joao }.SetEquals(await BoletoOwnersAsync(s.TenantA, s.Joao)));
    }

    [Fact]
    public async Task Largest_scope_among_roles_wins()
    {
        var s = await RlsScenario.CreateAsync(db);
        var direct = await _seed.RoleAsync(s.TenantA, "Supervisor direto", ("carteira.visualizar", "direct"));
        await _seed.AssignRoleAsync(s.TenantA, s.Joao, direct);

        Assert.Equal(6L, await CountAsync(s.TenantA, s.Joao, "boletos"));
        Assert.True(new HashSet<Guid> { s.Joao, s.Maria }.SetEquals(await BoletoOwnersAsync(s.TenantA, s.Joao)));
    }

    [Fact]
    public async Task Subtree_scope_includes_every_level_below()
    {
        var s = await RlsScenario.CreateAsync(db);
        var subtree = await _seed.RoleAsync(s.TenantA, "Gerente", ("carteira.visualizar", "subtree"));
        await _seed.AssignRoleAsync(s.TenantA, s.Joao, subtree);

        Assert.Equal(7L, await CountAsync(s.TenantA, s.Joao, "boletos"));
    }

    [Fact]
    public async Task Tenant_scope_never_crosses_tenants()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Equal(7L, await CountAsync(s.TenantA, s.Coord, "boletos"));
        Assert.Equal(1L, await CountAsync(s.TenantB, s.OutsiderAdmin, "boletos"));
    }

    [Fact]
    public async Task Context_with_mismatched_tenant_sees_nothing()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Equal(0L, await CountAsync(s.TenantB, s.Coord, "boletos"));
    }

    [Fact]
    public async Task Users_follow_the_estrutura_scope()
    {
        var s = await RlsScenario.CreateAsync(db);

        var joaoSees = await db.AsAppUserAsync(s.TenantA, s.Joao, async (c, t) =>
            (await c.QueryAsync<Guid>("select id from users", transaction: t)).ToHashSet());
        Assert.True(new HashSet<Guid> { s.Joao, s.Maria }.SetEquals(joaoSees));

        var pedroSees = await db.AsAppUserAsync(s.TenantA, s.Pedro, async (c, t) =>
            (await c.QueryAsync<Guid>("select id from users", transaction: t)).ToHashSet());
        Assert.True(new HashSet<Guid> { s.Pedro }.SetEquals(pedroSees));

        Assert.Equal(5L, await CountAsync(s.TenantA, s.Coord, "users"));
    }

    [Fact]
    public async Task Hierarchy_paths_follow_the_estrutura_scope()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Equal(3L, await CountAsync(s.TenantA, s.Joao, "hierarchy_paths"));
    }

    [Fact]
    public async Task Disabled_module_hides_its_rows()
    {
        var s = await RlsScenario.CreateAsync(db);
        await _seed.ExecAsync("update tenant_modules set enabled = false where tenant_id = @t and module_key = 'carteira'", new { t = s.TenantA });
        Assert.Equal(0L, await CountAsync(s.TenantA, s.Coord, "boletos"));
    }

    [Fact]
    public async Task App_user_cannot_write_boletos()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Admin, (c, t) => c.ExecuteAsync(
            """
            insert into boletos (tenant_id, participante_id, associado_ref, associado_nome, valor, status, vencimento)
            values (@a, @a2, 'X', 'X', 10, 'a_vencer', date '2026-10-01')
            """, new { a = s.TenantA, a2 = s.Admin }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Consultor_cannot_create_roles()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) => c.ExecuteAsync(
            "insert into roles (tenant_id, name) values (@a, 'Invasor')", new { a = s.TenantA }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Admin_cannot_write_into_another_tenant()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Admin, (c, t) => c.ExecuteAsync(
            "insert into roles (tenant_id, name) values (@b, 'Perfil alheio')", new { b = s.TenantB }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        await db.AsAppUserAsync(s.TenantA, s.Admin, (c, t) => c.ExecuteAsync(
            "insert into roles (tenant_id, name) values (@a, 'Perfil próprio')", new { a = s.TenantA }, t));
    }

    [Fact]
    public async Task Consultor_updates_to_users_affect_no_rows()
    {
        var s = await RlsScenario.CreateAsync(db);
        var affected = await db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) => c.ExecuteAsync(
            "update users set name = 'Alterado' where id = @m", new { m = s.Maria }, t));
        Assert.Equal(0, affected);
    }

    [Fact]
    public async Task Audit_entries_must_belong_to_the_current_user()
    {
        var s = await RlsScenario.CreateAsync(db);
        const string sql = "insert into audit_log (tenant_id, user_id, action, entity) values (@a, @u, 'teste', 'users')";

        await db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) => c.ExecuteAsync(sql, new { a = s.TenantA, u = s.Joao }, t));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) =>
            c.ExecuteAsync(sql, new { a = s.TenantA, u = s.Maria }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Commission_tables_are_readable_by_everyone_but_writable_only_with_permission()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Equal(3L, await CountAsync(s.TenantA, s.Joao, "commission_rules"));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) => c.ExecuteAsync(
            "insert into commission_groups (tenant_id, name) values (@a, 'Grupo do João')", new { a = s.TenantA }, t)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        await db.AsAppUserAsync(s.TenantA, s.Admin, (c, t) => c.ExecuteAsync(
            "insert into commission_groups (tenant_id, name) values (@a, 'Grupo do admin')", new { a = s.TenantA }, t));
    }

    [Fact]
    public async Task Frozen_details_follow_the_comissoes_scope()
    {
        var s = await RlsScenario.CreateAsync(db);
        var f = await _seed.ScalarAsync<Guid>(
            "insert into fechamentos (tenant_id, competencia) values (@a, date '2026-09-01') returning id", new { a = s.TenantA });
        await _seed.ExecAsync(
            """
            insert into fechamento_detalhes
              (tenant_id, fechamento_id, beneficiario_id, origem_participante_id, boleto_id, rule_id, rule_type, rate, base, valor)
            select @a, @f, b.participante_id, b.participante_id, b.id, gen_random_uuid(), 'own', 0.07, b.valor, round(b.valor * 0.07, 2)
              from boletos b
             where b.tenant_id = @a and b.participante_id in (@j, @m) and b.status = 'recebido' and b.pago_em >= date '2026-09-01'
            """, new { a = s.TenantA, f, j = s.Joao, m = s.Maria });

        Assert.Equal(2L, await CountAsync(s.TenantA, s.Joao, "fechamento_detalhes"));
        Assert.Equal(4L, await CountAsync(s.TenantA, s.Coord, "fechamento_detalhes"));
    }

    [Fact]
    public async Task Fechamentos_require_permission_to_read()
    {
        var s = await RlsScenario.CreateAsync(db);
        await _seed.ExecAsync("insert into fechamentos (tenant_id, competencia) values (@a, date '2026-09-01')", new { a = s.TenantA });
        var noAccess = await _seed.UserAsync(s.TenantA, "Sem perfil");

        Assert.Equal(1L, await CountAsync(s.TenantA, s.Joao, "fechamentos"));
        Assert.Equal(0L, await CountAsync(s.TenantA, noAccess, "fechamentos"));
    }
}
```

- [ ] **Step 3: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Db.Tests --filter "FullyQualifiedName~RlsTests"`
Expected: FAIL (sem RLS, `Without_context_nothing_is_visible` encontra linhas e `Consultor_sees_only_own_boletos` vê 8).

- [ ] **Step 4: Implementar as policies**

`src/Recorrencia.Db/Scripts/0011_rls.sql`:

```sql
do $$
declare
  t text;
begin
  foreach t in array array[
    'users', 'hierarchy_paths', 'tenant_modules', 'roles', 'role_permissions', 'user_roles',
    'commission_groups', 'commission_group_members', 'commission_plans', 'commission_rules',
    'boletos', 'fechamentos', 'fechamento_detalhes', 'audit_log']
  loop
    execute format('alter table %I enable row level security', t);
    execute format('alter table %I force row level security', t);
    execute format(
      'create policy %I on %I as restrictive for all to app_user using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant())',
      t || '_tenant_isolation', t);
  end loop;
end
$$;

-- users
create policy users_select on users for select to app_user using (
  id = app.current_user_id()
  or (select app.scope_for('estrutura.visualizar')) = 'tenant'
  or id = any ((select app.visible_owner_ids((select app.scope_for('estrutura.visualizar')))))
);
create policy users_insert on users for insert to app_user
  with check ((select app.has_permission('usuarios.convidar')));
create policy users_update on users for update to app_user
  using ((select app.has_permission('estrutura.editar')) or (select app.has_permission('usuarios.desligar')))
  with check ((select app.has_permission('estrutura.editar')) or (select app.has_permission('usuarios.desligar')));

-- hierarchy_paths
create policy hierarchy_paths_select on hierarchy_paths for select to app_user using (
  (select app.scope_for('estrutura.visualizar')) = 'tenant'
  or (
    ancestor_id = any ((select app.visible_owner_ids((select app.scope_for('estrutura.visualizar')))))
    and descendant_id = any ((select app.visible_owner_ids((select app.scope_for('estrutura.visualizar')))))
  )
);

-- tenant_modules
create policy tenant_modules_select on tenant_modules for select to app_user using (true);

-- roles / role_permissions / user_roles
create policy roles_select on roles for select to app_user using (true);
create policy roles_write on roles for all to app_user
  using ((select app.has_permission('usuarios.gerenciar_perfis')))
  with check ((select app.has_permission('usuarios.gerenciar_perfis')));

create policy role_permissions_select on role_permissions for select to app_user using (true);
create policy role_permissions_write on role_permissions for all to app_user
  using ((select app.has_permission('usuarios.gerenciar_perfis')))
  with check ((select app.has_permission('usuarios.gerenciar_perfis')));

create policy user_roles_select on user_roles for select to app_user using (
  user_id = app.current_user_id() or (select app.has_permission('usuarios.gerenciar_perfis'))
);
create policy user_roles_write on user_roles for all to app_user
  using ((select app.has_permission('usuarios.gerenciar_perfis')))
  with check ((select app.has_permission('usuarios.gerenciar_perfis')));

-- commission tables
create policy commission_groups_select on commission_groups for select to app_user using (true);
create policy commission_groups_write on commission_groups for all to app_user
  using ((select app.has_permission('regras_comissao.editar')))
  with check ((select app.has_permission('regras_comissao.editar')));

create policy commission_group_members_select on commission_group_members for select to app_user using (true);
create policy commission_group_members_write on commission_group_members for all to app_user
  using ((select app.has_permission('regras_comissao.editar')))
  with check ((select app.has_permission('regras_comissao.editar')));

create policy commission_plans_select on commission_plans for select to app_user using (true);
create policy commission_plans_write on commission_plans for all to app_user
  using ((select app.has_permission('regras_comissao.editar')))
  with check ((select app.has_permission('regras_comissao.editar')));

create policy commission_rules_select on commission_rules for select to app_user using (true);
create policy commission_rules_write on commission_rules for all to app_user
  using ((select app.has_permission('regras_comissao.editar')))
  with check ((select app.has_permission('regras_comissao.editar')));

-- boletos
create policy boletos_select on boletos for select to app_user using (
  (select app.scope_for('carteira.visualizar')) = 'tenant'
  or participante_id = any ((select app.visible_owner_ids((select app.scope_for('carteira.visualizar')))))
);

-- fechamentos
create policy fechamentos_select on fechamentos for select to app_user
  using ((select app.has_permission('fechamento.visualizar')));
create policy fechamentos_insert on fechamentos for insert to app_user
  with check ((select app.has_permission('fechamento.confirmar')));
create policy fechamentos_update on fechamentos for update to app_user
  using ((select app.has_permission('fechamento.confirmar')) or (select app.has_permission('fechamento.provisionar')))
  with check ((select app.has_permission('fechamento.confirmar')) or (select app.has_permission('fechamento.provisionar')));

-- fechamento_detalhes
create policy fechamento_detalhes_select on fechamento_detalhes for select to app_user using (
  (select app.scope_for('comissoes.visualizar')) = 'tenant'
  or beneficiario_id = any ((select app.visible_owner_ids((select app.scope_for('comissoes.visualizar')))))
);
create policy fechamento_detalhes_insert on fechamento_detalhes for insert to app_user
  with check ((select app.has_permission('fechamento.confirmar')));

-- audit_log
create policy audit_log_insert on audit_log for insert to app_user
  with check (user_id = app.current_user_id());
```

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Db.Tests`
Expected: PASS (todos os anteriores continuam passando, porque o `Seed` usa `app_owner`, que tem `BYPASSRLS`).

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Db/Scripts/0011_rls.sql tests/Recorrencia.TestSupport/RlsScenario.cs tests/Recorrencia.Db.Tests/RlsTests.cs
git commit -m "feat(db): enforce row level security with tenant isolation and scoped visibility"
```

---

### Task 8: Funções de origem das comissões

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0012_commission_sources.sql`
- Test: `tests/Recorrencia.Db.Tests/CommissionSourceTests.cs`

**Interfaces:**
- Consumes: `RlsScenario` (Task 7); `app.scope_for`, `app.visible_owner_ids` (Task 4); tabelas de comissão (Task 5) e boletos (Task 6).
- Produces (funções `SECURITY DEFINER`, executáveis por `app_user`):
  - `app.commission_plan_for(p_competencia date) returns uuid` — último plano `ativo` do tenant com `effective_from <= p_competencia`.
  - `app.commission_beneficiaries() returns uuid[]` — quem o usuário pode ver como beneficiário: todos do tenant quando o escopo de `comissoes.visualizar` é `tenant`, senão `visible_owner_ids(escopo)`, e vazio sem permissão.
  - `app.commission_source_boletos(p_competencia date, p_beneficiaries uuid[]) returns table (boleto_id uuid, participante_id uuid, valor numeric, pago_em date)` — boletos `recebido` pagos na competência que geram comissão para algum dos beneficiários: os próprios e os da estrutura abaixo até o maior `level` `upline` do plano, ou todos do tenant se algum beneficiário for membro de um grupo com regra `global`.
  - `app.commission_source_paths(p_competencia date, p_beneficiaries uuid[]) returns table (ancestor_id uuid, descendant_id uuid, depth int)` — caminhos com `ancestor_id` entre os beneficiários e `1 <= depth <= maior level`.
  - `app.fechamento_status(p_competencia date) returns table (fechamento_id uuid, status text)` — status do fechamento da competência no tenant do contexto, sem depender da permissão `fechamento.visualizar`. É usado para decidir entre cálculo ao vivo e snapshot.
  - Erros: `commission.invalid_competencia` (não é o dia 1), SQLSTATE `42501` com mensagem `forbidden` (beneficiário fora de `commission_beneficiaries()`).

Esta é a implementação da `app.commission_sources` do spec, com dois refinamentos: recebe vários beneficiários de uma vez (para a visão do coordenador) e separa boletos e caminhos em duas funções, que o motor do plano 2 consome como entradas independentes.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Db.Tests/CommissionSourceTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class CommissionSourceTests(PostgresFixture db)
{
    private static readonly DateOnly Setembro = new(2026, 9, 1);

    private Task<List<(Guid BoletoId, Guid Owner, decimal Valor)>> SourcesAsync(Guid tenant, Guid user, DateOnly competencia, params Guid[] beneficiaries) =>
        db.AsAppUserAsync(tenant, user, async (c, t) => (await c.QueryAsync<(Guid, Guid, decimal)>(
            "select boleto_id, participante_id, valor from app.commission_source_boletos(@competencia, @beneficiaries)",
            new { competencia, beneficiaries }, t)).ToList());

    [Fact]
    public async Task Consultor_gets_own_and_first_level_boletos_of_the_month()
    {
        var s = await RlsScenario.CreateAsync(db);
        var rows = await SourcesAsync(s.TenantA, s.Joao, Setembro, s.Joao);

        Assert.Equal(4, rows.Count);
        Assert.Equal(300m, rows.Sum(r => r.Valor));
        Assert.DoesNotContain(rows, r => r.Owner == s.Pedro);
    }

    [Fact]
    public async Task Member_of_a_global_group_gets_every_paid_boleto_of_the_tenant()
    {
        var s = await RlsScenario.CreateAsync(db);
        var rows = await SourcesAsync(s.TenantA, s.Coord, Setembro, s.Coord);

        Assert.Equal(5, rows.Count);
        Assert.Equal(340m, rows.Sum(r => r.Valor));
    }

    [Fact]
    public async Task Consultor_cannot_ask_for_someone_else()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ex = await DbExtensions.ThrowsPgAsync(() => SourcesAsync(s.TenantA, s.Joao, Setembro, s.Maria));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Equal("forbidden", ex.MessageText);
    }

    [Fact]
    public async Task Competencia_must_be_the_first_day_of_the_month()
    {
        var s = await RlsScenario.CreateAsync(db);
        var ex = await DbExtensions.ThrowsPgAsync(() => SourcesAsync(s.TenantA, s.Joao, new DateOnly(2026, 9, 15), s.Joao));
        Assert.Equal("commission.invalid_competencia", ex.MessageText);
    }

    [Fact]
    public async Task Months_before_any_plan_have_no_sources()
    {
        var s = await RlsScenario.CreateAsync(db);
        Assert.Empty(await SourcesAsync(s.TenantA, s.Coord, new DateOnly(2026, 7, 1), s.Coord));
    }

    [Fact]
    public async Task Paths_stop_at_the_deepest_upline_level_of_the_plan()
    {
        var s = await RlsScenario.CreateAsync(db);
        var paths = await db.AsAppUserAsync(s.TenantA, s.Joao, async (c, t) => (await c.QueryAsync<(Guid, Guid, int)>(
            "select ancestor_id, descendant_id, depth from app.commission_source_paths(@competencia, @beneficiaries)",
            new { competencia = Setembro, beneficiaries = new[] { s.Joao } }, t)).ToList());

        Assert.Equal(new List<(Guid, Guid, int)> { (s.Joao, s.Maria, 1) }, paths);
    }

    [Fact]
    public async Task Beneficiaries_follow_the_comissoes_scope()
    {
        var s = await RlsScenario.CreateAsync(db);

        var joao = await db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) =>
            c.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()", transaction: t));
        Assert.Equal(new[] { s.Joao }, joao);

        var coord = await db.AsAppUserAsync(s.TenantA, s.Coord, (c, t) =>
            c.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()", transaction: t));
        Assert.Equal(5, coord!.Length);
    }

    [Fact]
    public async Task Plan_for_competencia_picks_the_latest_active_version()
    {
        var s = await RlsScenario.CreateAsync(db);
        var seed = new Seed(db);
        var october = await seed.ScalarAsync<Guid>(
            "insert into commission_plans (tenant_id, name, effective_from) values (@a, 'Outubro', date '2026-10-01') returning id",
            new { a = s.TenantA });
        await seed.ExecAsync("insert into commission_rules (tenant_id, plan_id, type, rate) values (@a, @october, 'own', 0.05)", new { a = s.TenantA, october });
        await seed.ExecAsync("update commission_plans set status = 'ativo', activated_at = now() where id = @october", new { october });

        async Task<Guid?> PlanFor(DateOnly competencia) => await db.AsAppUserAsync(s.TenantA, s.Joao, (c, t) =>
            c.ExecuteScalarAsync<Guid?>("select app.commission_plan_for(@competencia)", new { competencia }, t));

        Assert.NotEqual(october, await PlanFor(Setembro));
        Assert.Equal(october, await PlanFor(new DateOnly(2026, 10, 1)));
        Assert.Equal(october, await PlanFor(new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public async Task Fechamento_status_ignores_the_visualization_permission()
    {
        var s = await RlsScenario.CreateAsync(db);
        var seed = new Seed(db);
        var f = await seed.ScalarAsync<Guid>(
            "insert into fechamentos (tenant_id, competencia) values (@a, date '2026-08-01') returning id", new { a = s.TenantA });
        var semPerfil = await seed.UserAsync(s.TenantA, "Sem perfil");

        Task<List<(Guid, string)>> StatusAsync(Guid tenant, Guid user) =>
            db.AsAppUserAsync(tenant, user, async (c, t) => (await c.QueryAsync<(Guid, string)>(
                "select fechamento_id, status from app.fechamento_status(@competencia)",
                new { competencia = new DateOnly(2026, 8, 1) }, t)).ToList());

        Assert.Equal(new List<(Guid, string)> { (f, "apuracao") }, await StatusAsync(s.TenantA, semPerfil));
        Assert.Empty(await StatusAsync(s.TenantB, s.OutsiderAdmin));
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Db.Tests --filter "FullyQualifiedName~CommissionSourceTests"`
Expected: FAIL com `function app.commission_source_boletos(date, uuid[]) does not exist`.

- [ ] **Step 3: Implementar as funções**

`src/Recorrencia.Db/Scripts/0012_commission_sources.sql`:

```sql
create function app.commission_plan_for(p_competencia date)
returns uuid
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select p.id
    from commission_plans p
   where p.tenant_id = app.current_tenant()
     and p.status = 'ativo'
     and p.effective_from <= p_competencia
   order by p.effective_from desc
   limit 1
$$;

create function app.commission_beneficiaries()
returns uuid[]
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
declare
  v_scope text := app.scope_for('comissoes.visualizar');
begin
  if v_scope is null then
    return array[]::uuid[];
  end if;
  if v_scope = 'tenant' then
    return coalesce(
      (select array_agg(u.id order by u.id) from users u where u.tenant_id = app.current_tenant()),
      array[]::uuid[]);
  end if;
  return app.visible_owner_ids(v_scope);
end
$$;

create function app.assert_commission_access(p_competencia date, p_beneficiaries uuid[])
returns void
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
declare
  v_allowed uuid[] := app.commission_beneficiaries();
begin
  if p_competencia is null or extract(day from p_competencia) <> 1 then
    raise exception 'commission.invalid_competencia' using errcode = 'P0001';
  end if;
  if exists (
       select 1 from unnest(coalesce(p_beneficiaries, array[]::uuid[])) b(id)
        where b.id is null or not (b.id = any (v_allowed))) then
    raise exception 'forbidden' using errcode = '42501';
  end if;
end
$$;

create function app.commission_max_level(p_plan uuid)
returns int
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select coalesce(max(r.level), 0) from commission_rules r where r.plan_id = p_plan and r.type = 'upline'
$$;

create function app.commission_source_boletos(p_competencia date, p_beneficiaries uuid[])
returns table (boleto_id uuid, participante_id uuid, valor numeric, pago_em date)
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
#variable_conflict use_column
declare
  v_plan uuid;
  v_max_level int;
  v_has_global boolean;
begin
  perform app.assert_commission_access(p_competencia, p_beneficiaries);
  v_plan := app.commission_plan_for(p_competencia);
  if v_plan is null then
    return;
  end if;

  v_max_level := app.commission_max_level(v_plan);
  select exists (
    select 1
      from commission_rules r
      join commission_group_members m on m.group_id = r.group_id
     where r.plan_id = v_plan and r.type = 'global' and m.user_id = any (p_beneficiaries))
  into v_has_global;

  return query
    select b.id, b.participante_id, b.valor, b.pago_em
      from boletos b
     where b.tenant_id = app.current_tenant()
       and b.status = 'recebido'
       and b.pago_em >= p_competencia
       and b.pago_em < (p_competencia + interval '1 month')::date
       and (
         v_has_global
         or b.participante_id in (
           select hp.descendant_id
             from hierarchy_paths hp
            where hp.tenant_id = app.current_tenant()
              and hp.ancestor_id = any (p_beneficiaries)
              and hp.depth <= v_max_level));
end
$$;

create function app.commission_source_paths(p_competencia date, p_beneficiaries uuid[])
returns table (ancestor_id uuid, descendant_id uuid, depth int)
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
#variable_conflict use_column
declare
  v_plan uuid;
  v_max_level int;
begin
  perform app.assert_commission_access(p_competencia, p_beneficiaries);
  v_plan := app.commission_plan_for(p_competencia);
  if v_plan is null then
    return;
  end if;

  v_max_level := app.commission_max_level(v_plan);
  return query
    select hp.ancestor_id, hp.descendant_id, hp.depth
      from hierarchy_paths hp
     where hp.tenant_id = app.current_tenant()
       and hp.ancestor_id = any (p_beneficiaries)
       and hp.depth between 1 and v_max_level;
end
$$;

create function app.fechamento_status(p_competencia date)
returns table (fechamento_id uuid, status text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select f.id, f.status
    from fechamentos f
   where f.tenant_id = app.current_tenant() and f.competencia = p_competencia
$$;

grant execute on function
  app.commission_plan_for(date),
  app.commission_beneficiaries(),
  app.commission_source_boletos(date, uuid[]),
  app.commission_source_paths(date, uuid[]),
  app.fechamento_status(date)
to app_user, app_superadmin;
```

`app.assert_commission_access` e `app.commission_max_level` não recebem `grant`: são auxiliares internos, chamados só pelas funções acima, que rodam como `app_owner`.

- [ ] **Step 4: Rodar a suíte completa**

Run: `dotnet test tests/Recorrencia.Db.Tests`
Expected: PASS em todas as classes.

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Db/Scripts/0012_commission_sources.sql tests/Recorrencia.Db.Tests/CommissionSourceTests.cs
git commit -m "feat(db): add scoped commission source functions"
```
