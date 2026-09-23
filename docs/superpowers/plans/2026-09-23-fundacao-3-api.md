# Fundação 3 — API (ASP.NET Core): plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Construir a API interna em C#: resolução de tenant por subdomínio, login com Argon2id e sessões opacas, convites, autorização por permissões com escopo, gestão de usuários/perfis/planos, cálculo de comissões (ao vivo e snapshot), fechamento mensal e operações de plataforma.

**Architecture:** ASP.NET Core com minimal APIs, acessível só pelo BFF (header `X-Internal-Key`). Cada operação abre uma transação no Postgres e define `app.tenant_id`/`app.user_id` com `set_config(..., true)`, e o RLS do plano 1 filtra os dados. O acesso a dados usa Npgsql + Dapper (SQL explícito). O cálculo de comissões usa o motor do plano 2.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, Npgsql, Dapper, Konscious.Security.Cryptography.Argon2, xUnit v2, Microsoft.AspNetCore.Mvc.Testing, Testcontainers (via `Recorrencia.TestSupport`).

**Spec:** `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md`

**Ordem dos planos:** plano 3 de 4. Depende dos planos 1 (banco) e 2 (motor).

**Refinamento em relação ao spec:** o spec previa EF Core com um interceptor para o `SET LOCAL`. Este plano usa Dapper com uma unidade de trabalho explícita (`Database.InTenantAsync`). O efeito é o mesmo, e o controle fica maior: o RLS exige `INSERT` sem `RETURNING` em várias tabelas, e `app_user` não tem acesso de leitura a `password_hash`. O EF Core geraria as duas coisas implicitamente.

## Global Constraints

- Tudo das Global Constraints do plano 1 continua valendo (Colima, `DOCKER_HOST`, `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE`, `net10.0`, warnings como erro).
- A API nunca é exposta à internet. Toda requisição, exceto `GET /health`, exige `X-Internal-Key` igual a `Internal:Key` (mínimo de 32 caracteres). Sem a chave, a API responde 404 com corpo vazio.
- Headers vindos do BFF: `X-Tenant-Host` (slug do subdomínio, ou `admin` para a plataforma), `X-Client-Ip`, `X-Client-User-Agent` e `Authorization: Bearer <token>`.
- Erros seguem o formato Problem Details: `{ "type": "about:blank", "title": "<codigo>", "status": <n>, "code": "<codigo>", "correlationId": <string|null> }`, com `Content-Type: application/problem+json`.
- Códigos estáveis usados neste plano: `tenant.not_found`, `auth.unauthenticated`, `auth.forbidden`, `auth.invalid_credentials`, `auth.too_many_attempts`, `auth.tenant_mismatch`, `auth.weak_password`, `invite.invalid`, `users.email_taken`, `users.invalid_email`, `users.invalid_supervisor`, `users.not_found`, `users.name_required`, `hierarchy.cycle`, `role.last_admin`, `role.grant_exceeds_own`, `role.name_taken`, `role.name_required`, `role.not_found`, `role.invalid_permission`, `role.invalid_scope`, `role.scope_required`, `role.scope_not_allowed`, `role.duplicate_permission`, `roles.not_found`, `plan.*`, `group.*`, `commission.invalid_competencia`, `fechamento.*`, `tenant.slug_taken`, `tenant.invalid_slug`, `tenant.invalid_request`, `request.invalid`, `internal.error`.
- Enums em JSON usam snake_case minúsculo (`own`, `upline`, `global`).
- Valores monetários são `decimal` do começo ao fim.
- Ids novos são gerados na API com `Guid.CreateVersion7()`. Inserts não usam `RETURNING` em tabelas com RLS.
- Nunca selecione `password_hash` de `users` pela conexão `app_user`: o banco nega. Use as funções `app.find_login` e `app.rehash_own_password`.
- Senhas: de 10 a 128 caracteres.
- Nos testes, a senha de todos os usuários semeados é `senha-teste-123`, e o Argon2 roda com `MemoryKb=1024, Iterations=1` para ser rápido.
- Envio de e-mail em produção (SMTP) está fora deste plano. `IEmailSender` tem duas implementações: log (padrão) e arquivo (quando `Email:OutboxDir` está definido, usada no desenvolvimento e no teste E2E do plano 4).
- Commits: mensagens convencionais (`feat(api): ...`), terminando com a linha de atribuição indicada pelo ambiente.

## Mapa de arquivos

```
src/Recorrencia.Api/
  Recorrencia.Api.csproj, Program.cs, appsettings.json, appsettings.Development.json, Properties/launchSettings.json
  Options.cs                                AuthOptions, Argon2Options, WebOptions, InternalOptions, EmailOptions
  Infrastructure/DataSources.cs             NpgsqlDataSource app/superadmin
  Infrastructure/Database.cs                Tx + Database (transação com contexto)
  Infrastructure/ApiProblem.cs              ApiProblem + ErrorHandlingMiddleware
  Infrastructure/InternalKeyMiddleware.cs
  Security/PasswordHasher.cs, Tokens.cs, LoginThrottle.cs, PasswordPolicy.cs
  Tenancy/RequestContext.cs, TenantResolver.cs, HostContextMiddleware.cs, TenantEndpoints.cs
  Auth/SessionMiddleware.cs, Sessions.cs, AuthEndpoints.cs, PasswordEndpoints.cs, MeEndpoints.cs
  Authorization/PermissionSet.cs, CurrentPermissions.cs, EndpointAuthorization.cs, RoleGuards.cs
  Email/IEmailSender.cs, LogEmailSender.cs, FileEmailSender.cs, LinkBuilder.cs
  Audit/Audit.cs
  Users/UserEndpoints.cs
  Roles/RoleEndpoints.cs
  Commissions/CommissionPlanEndpoints.cs, CommissionService.cs, CommissionEndpoints.cs
  Fechamentos/FechamentoEndpoints.cs
  Platform/PlatformEndpoints.cs, PlatformAdmins.cs
  Cli/DevSeed.cs
tests/Recorrencia.Api.Tests/
  Recorrencia.Api.Tests.csproj, ApiCollection.cs, ApiFixture.cs, ApiClient.cs, CapturingEmailSender.cs
  InfrastructureTests.cs, SecurityTests.cs, TenancyTests.cs, AuthTests.cs, PasswordTests.cs,
  UserTests.cs, RoleTests.cs, CommissionPlanTests.cs, CommissionTests.cs, FechamentoTests.cs, PlatformTests.cs
```

---

### Task 1: Projeto da API, acesso a dados, erros e chave interna

**Files:**
- Create: `src/Recorrencia.Api/*` (template `web`), `Options.cs`, `Infrastructure/DataSources.cs`, `Infrastructure/Database.cs`, `Infrastructure/ApiProblem.cs`, `Infrastructure/InternalKeyMiddleware.cs`
- Modify: `src/Recorrencia.Api/Program.cs`, `appsettings.json`, `appsettings.Development.json`, `Properties/launchSettings.json`
- Create: `tests/Recorrencia.Api.Tests/{Recorrencia.Api.Tests.csproj,ApiCollection.cs,ApiFixture.cs,ApiClient.cs,InfrastructureTests.cs}`

**Interfaces:**
- Consumes: `Recorrencia.Db.DapperSetup.Configure()` (plano 1); `PostgresFixture` (plano 1, `Recorrencia.TestSupport`).
- Produces:
  - `DataSources(NpgsqlDataSource app, NpgsqlDataSource superadmin)` com `App` e `Superadmin`.
  - `Tx` com `Connection`, `Transaction`, `QueryAsync<T>`, `QuerySingleAsync<T>`, `QuerySingleOrDefaultAsync<T>`, `ExecuteScalarAsync<T>` e `ExecuteAsync`, todos `(string sql, object? param = null)`.
  - `Database.InTenantAsync<T>(Guid tenantId, Guid? userId, Func<Tx, Task<T>> work, CancellationToken ct)`, `Database.AnonymousAsync<T>(Func<Tx, Task<T>> work, CancellationToken ct)` e `Database.AsPlatformAsync<T>(Guid? tenantId, Func<Tx, Task<T>> work, CancellationToken ct)`.
  - `ApiProblem(int status, string code)` (exceção) e `ErrorHandlingMiddleware`.
  - Testes: `ApiFixture` (com `Db`, `Factory`, `const InternalKey`, `const Password`), `ApiClient` (com `Token`, `GetAsync`, `PostAsync`, `PutAsync`, `DeleteAsync`, `GetJsonAsync<T>`, `LoginAsync(email, password)`, `static ExpectAsync(response, status)`, `static CodeAsync(response)` e `static Json`), `ApiCollection.Name = "api"`.

- [ ] **Step 1: Criar os projetos**

```bash
dotnet new web -n Recorrencia.Api -o src/Recorrencia.Api
dotnet sln Recorrencia.slnx add src/Recorrencia.Api
dotnet add src/Recorrencia.Api package Npgsql
dotnet add src/Recorrencia.Api package Dapper
dotnet add src/Recorrencia.Api package Konscious.Security.Cryptography.Argon2
dotnet add src/Recorrencia.Api reference src/Recorrencia.Db src/Recorrencia.Domain
dotnet new classlib -n Recorrencia.Api.Tests -o tests/Recorrencia.Api.Tests
rm tests/Recorrencia.Api.Tests/Class1.cs
dotnet sln Recorrencia.slnx add tests/Recorrencia.Api.Tests
dotnet add tests/Recorrencia.Api.Tests package Microsoft.NET.Test.Sdk
dotnet add tests/Recorrencia.Api.Tests package xunit
dotnet add tests/Recorrencia.Api.Tests package xunit.runner.visualstudio
dotnet add tests/Recorrencia.Api.Tests package Microsoft.AspNetCore.Mvc.Testing
dotnet add tests/Recorrencia.Api.Tests package Microsoft.Extensions.TimeProvider.Testing
dotnet add tests/Recorrencia.Api.Tests reference src/Recorrencia.Api tests/Recorrencia.TestSupport
```

O `Program` de `Recorrencia.Db` usa top-level statements e, por isso, é `internal`. Não há conflito com o `public partial class Program` da API.

Em `tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj`, acrescente:

```xml
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <Using Include="Dapper" />
    <Using Include="Xunit" />
    <Using Include="System.Net" />
    <Using Include="System.Net.Http.Json" />
    <Using Include="Microsoft.Extensions.DependencyInjection" />
    <Using Include="Recorrencia.TestSupport" />
  </ItemGroup>
```

- [ ] **Step 2: Configurações**

`src/Recorrencia.Api/appsettings.json`:

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning" } },
  "AllowedHosts": "*",
  "ConnectionStrings": { "App": "", "Superadmin": "" },
  "Internal": { "Key": "" },
  "Auth": {
    "IdleTimeoutSeconds": 43200,
    "AbsoluteTimeoutSeconds": 604800,
    "InviteTtlSeconds": 259200,
    "ResetTtlSeconds": 3600,
    "MaxFailuresPerWindow": 10,
    "FailureWindowSeconds": 900
  },
  "Argon2": { "MemoryKb": 19456, "Iterations": 2, "Parallelism": 1 },
  "Web": { "Scheme": "https", "RootDomain": "" },
  "Email": { "OutboxDir": "" }
}
```

`src/Recorrencia.Api/appsettings.Development.json` (valores de desenvolvimento, iguais aos de `.env.example`):

```json
{
  "ConnectionStrings": {
    "App": "Host=127.0.0.1;Port=55432;Database=recorrencia;Username=app_user;Password=troque-me-app",
    "Superadmin": "Host=127.0.0.1;Port=55432;Database=recorrencia;Username=app_superadmin;Password=troque-me-super"
  },
  "Internal": { "Key": "chave-interna-de-desenvolvimento-32+" },
  "Web": { "Scheme": "http", "RootDomain": "localhost:3000" },
  "Email": { "OutboxDir": "../../tmp/emails" }
}
```

Em `src/Recorrencia.Api/Properties/launchSettings.json`, no perfil `http`, troque `applicationUrl` por `"http://127.0.0.1:5080"`.

`src/Recorrencia.Api/Options.cs`:

```csharp
namespace Recorrencia.Api;

public sealed class AuthOptions
{
    public int IdleTimeoutSeconds { get; set; } = 43200;
    public int AbsoluteTimeoutSeconds { get; set; } = 604800;
    public int InviteTtlSeconds { get; set; } = 259200;
    public int ResetTtlSeconds { get; set; } = 3600;
    public int MaxFailuresPerWindow { get; set; } = 10;
    public int FailureWindowSeconds { get; set; } = 900;
}

public sealed class Argon2Options
{
    public int MemoryKb { get; set; } = 19456;
    public int Iterations { get; set; } = 2;
    public int Parallelism { get; set; } = 1;
}

public sealed class WebOptions
{
    public string Scheme { get; set; } = "https";
    public string RootDomain { get; set; } = "";
}

public sealed class InternalOptions
{
    public string Key { get; set; } = "";
}

public sealed class EmailOptions
{
    public string OutboxDir { get; set; } = "";
}
```

- [ ] **Step 3: Escrever os testes (falhando)**

`tests/Recorrencia.Api.Tests/ApiCollection.cs`:

```csharp
namespace Recorrencia.Api.Tests;

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}
```

`tests/Recorrencia.Api.Tests/ApiFixture.cs`:

```csharp
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
```

`tests/Recorrencia.Api.Tests/ApiClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Recorrencia.Api.Tests;

public sealed record SessionDto(string Token, DateTimeOffset AbsoluteExpiresAt);

public sealed record ProblemDto(int Status, string Code, string? CorrelationId);

public sealed class ApiClient(HttpClient http, string host, string? ip = null)
{
    private static int _nextIp;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public string Ip { get; } = ip ?? NextIp();
    public string? Token { get; set; }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, bool withKey = true)
    {
        using var request = new HttpRequestMessage(method, path);
        if (withKey)
            request.Headers.Add("X-Internal-Key", ApiFixture.InternalKey);
        request.Headers.Add("X-Tenant-Host", host);
        request.Headers.Add("X-Client-Ip", Ip);
        request.Headers.Add("X-Client-User-Agent", "testes");
        if (Token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        return await http.SendAsync(request);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);
    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });
    public Task<HttpResponseMessage> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);
    public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path);

    public async Task<T> GetJsonAsync<T>(string path)
    {
        var response = await GetAsync(path);
        await ExpectAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task LoginAsync(string email, string password)
    {
        var response = await PostAsync("/auth/login", new { email, password });
        await ExpectAsync(response, HttpStatusCode.OK);
        Token = (await response.Content.ReadFromJsonAsync<SessionDto>(Json))!.Token;
    }

    public static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        if (response.StatusCode != status)
            Assert.Fail($"Esperado {(int)status}, recebido {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    public static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDto>(Json))?.Code;

    private static string NextIp()
    {
        var n = Interlocked.Increment(ref _nextIp);
        return $"10.{(n >> 16) & 255}.{(n >> 8) & 255}.{n & 255}";
    }
}
```

`tests/Recorrencia.Api.Tests/InfrastructureTests.cs`:

```csharp
using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class InfrastructureTests(ApiFixture api)
{
    [Fact]
    public async Task Health_does_not_require_the_internal_key()
    {
        var response = await api.Factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Database_sets_the_transaction_context()
    {
        var db = api.Service<Database>();
        var tenant = Guid.NewGuid();
        var user = Guid.NewGuid();

        var (t, u) = await db.InTenantAsync(tenant, user,
            tx => tx.QuerySingleAsync<(Guid?, Guid?)>("select app.current_tenant(), app.current_user_id()"),
            CancellationToken.None);

        Assert.Equal(tenant, t);
        Assert.Equal(user, u);
    }

    [Fact]
    public async Task Anonymous_transactions_have_no_context()
    {
        var db = api.Service<Database>();
        var tenant = await db.AnonymousAsync(tx => tx.ExecuteScalarAsync<Guid?>("select app.current_tenant()"), CancellationToken.None);
        Assert.Null(tenant);
    }
}
```

- [ ] **Step 4: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: FAIL de compilação (`Database` não existe).

- [ ] **Step 5: Implementar a infraestrutura**

`src/Recorrencia.Api/Infrastructure/DataSources.cs`:

```csharp
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
```

`src/Recorrencia.Api/Infrastructure/Database.cs`:

```csharp
using Dapper;
using Npgsql;

namespace Recorrencia.Api.Infrastructure;

public sealed class Tx(NpgsqlConnection connection, NpgsqlTransaction transaction)
{
    public NpgsqlConnection Connection { get; } = connection;
    public NpgsqlTransaction Transaction { get; } = transaction;

    public Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null) =>
        Connection.QueryAsync<T>(sql, param, Transaction);

    public Task<T> QuerySingleAsync<T>(string sql, object? param = null) =>
        Connection.QuerySingleAsync<T>(sql, param, Transaction);

    public Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null) =>
        Connection.QuerySingleOrDefaultAsync<T>(sql, param, Transaction);

    public Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null) =>
        Connection.ExecuteScalarAsync<T>(sql, param, Transaction);

    public Task<int> ExecuteAsync(string sql, object? param = null) =>
        Connection.ExecuteAsync(sql, param, Transaction);
}

public sealed class Database(DataSources sources)
{
    public Task<T> InTenantAsync<T>(Guid tenantId, Guid? userId, Func<Tx, Task<T>> work, CancellationToken ct) =>
        RunAsync(sources.App, tenantId, userId, work, ct);

    public Task<T> AnonymousAsync<T>(Func<Tx, Task<T>> work, CancellationToken ct) =>
        RunAsync(sources.App, null, null, work, ct);

    public Task<T> AsPlatformAsync<T>(Guid? tenantId, Func<Tx, Task<T>> work, CancellationToken ct) =>
        RunAsync(sources.Superadmin, tenantId, null, work, ct);

    private static async Task<T> RunAsync<T>(NpgsqlDataSource dataSource, Guid? tenantId, Guid? userId, Func<Tx, Task<T>> work, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(
            "select set_config('app.tenant_id', @t, true), set_config('app.user_id', @u, true)",
            new { t = tenantId?.ToString() ?? "", u = userId?.ToString() ?? "" },
            tx);
        var result = await work(new Tx(conn, tx));
        await tx.CommitAsync(ct);
        return result;
    }
}
```

`src/Recorrencia.Api/Infrastructure/ApiProblem.cs`:

```csharp
using System.Diagnostics;
using System.Text.Json;
using Npgsql;

namespace Recorrencia.Api.Infrastructure;

public sealed class ApiProblem(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    private static readonly Dictionary<string, int> DatabaseCodes = new()
    {
        ["hierarchy.cycle"] = StatusCodes.Status409Conflict,
        ["users.tenant_immutable"] = StatusCodes.Status409Conflict,
        ["plan.active_immutable"] = StatusCodes.Status409Conflict,
        ["plan.empty"] = StatusCodes.Status400BadRequest,
        ["plan.template_not_found"] = StatusCodes.Status400BadRequest,
        ["fechamento.invalid_transition"] = StatusCodes.Status409Conflict,
        ["fechamento.immutable"] = StatusCodes.Status409Conflict,
        ["invite.invalid"] = StatusCodes.Status400BadRequest,
        ["invite.invalid_status"] = StatusCodes.Status409Conflict,
        ["invite.user_not_found"] = StatusCodes.Status404NotFound,
        ["role.scope_required"] = StatusCodes.Status400BadRequest,
        ["role.scope_not_allowed"] = StatusCodes.Status400BadRequest,
        ["commission.invalid_competencia"] = StatusCodes.Status400BadRequest,
        ["auth.invalid_user"] = StatusCodes.Status401Unauthorized,
    };

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiProblem problem)
        {
            await WriteAsync(context, problem.Status, problem.Code);
        }
        catch (BadHttpRequestException)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest, "request.invalid");
        }
        catch (PostgresException pg) when (DatabaseCodes.TryGetValue(pg.MessageText, out var status))
        {
            await WriteAsync(context, status, pg.MessageText);
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            await WriteAsync(context, StatusCodes.Status403Forbidden, "auth.forbidden");
        }
        catch (Exception ex)
        {
            var correlationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            logger.LogError(ex, "Erro inesperado. Correlação {CorrelationId}", correlationId);
            await WriteAsync(context, StatusCodes.Status500InternalServerError, "internal.error", correlationId);
        }
    }

    private static Task WriteAsync(HttpContext context, int status, string code, string? correlationId = null)
    {
        if (context.Response.HasStarted)
            return Task.CompletedTask;
        context.Response.Clear();
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(
            new ProblemBody("about:blank", code, status, code, correlationId),
            (JsonSerializerOptions?)null,
            "application/problem+json");
    }

    private sealed record ProblemBody(string Type, string Title, int Status, string Code, string? CorrelationId);
}
```

`src/Recorrencia.Api/Infrastructure/InternalKeyMiddleware.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Infrastructure;

public sealed class InternalKeyMiddleware(RequestDelegate next, IOptions<InternalOptions> options)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
            return next(context);

        var provided = context.Request.Headers["X-Internal-Key"].ToString();
        if (!FixedTimeEquals(provided, options.Value.Key))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        return next(context);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
```

`src/Recorrencia.Api/Program.cs` (substitui o gerado):

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using Recorrencia.Api;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Db;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<AuthOptions>().BindConfiguration("Auth");
builder.Services.AddOptions<Argon2Options>().BindConfiguration("Argon2");
builder.Services.AddOptions<WebOptions>().BindConfiguration("Web");
builder.Services.AddOptions<EmailOptions>().BindConfiguration("Email");
builder.Services.AddOptions<InternalOptions>().BindConfiguration("Internal")
    .Validate(o => o.Key.Length >= 32, "Internal:Key precisa ter pelo menos 32 caracteres.")
    .ValidateOnStart();

DapperSetup.Configure();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new DataSources(
        NpgsqlDataSource.Create(RequiredSetting(config, "ConnectionStrings:App")),
        NpgsqlDataSource.Create(RequiredSetting(config, "ConnectionStrings:Superadmin")));
});
builder.Services.AddSingleton<Database>();
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)));

var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<InternalKeyMiddleware>();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

static string RequiredSetting(IConfiguration config, string key) =>
    config[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Configuração {key} ausente.");

public partial class Program { }
```

As tasks seguintes acrescentam linhas a este arquivo em pontos exatos: serviços antes de `var app = builder.Build();`, middlewares depois de `app.UseMiddleware<InternalKeyMiddleware>();` e endpoints depois de `app.MapGet("/health", ...)`.

- [ ] **Step 6: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS (3 testes).

- [ ] **Step 7: Commit**

```bash
git add Recorrencia.slnx src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): scaffold internal API with transactional data access and problem details"
```

---

### Task 2: Primitivas de segurança

**Files:**
- Create: `src/Recorrencia.Api/Security/{PasswordHasher.cs,Tokens.cs,LoginThrottle.cs,PasswordPolicy.cs}`
- Modify: `src/Recorrencia.Api/Program.cs`
- Test: `tests/Recorrencia.Api.Tests/SecurityTests.cs`

**Interfaces:**
- Produces:
  - `PasswordHasher(IOptions<Argon2Options>)` com `string Hash(string password)`, `bool Verify(string password, string encoded)`, `bool VerifyAgainstDummy(string password)` (sempre `false`, com o mesmo custo de uma verificação real) e `bool NeedsRehash(string encoded)`. Formato: `$argon2id$v=19$m=<kb>,t=<it>,p=<par>$<salt b64 sem padding>$<hash b64 sem padding>`.
  - `Tokens.New()` → string base64url de 32 bytes aleatórios (43 caracteres); `Tokens.Hash(string token)` → `byte[]` SHA-256.
  - `LoginThrottle(TimeProvider, IOptions<AuthOptions>)` com `bool IsBlocked(params string[] keys)`, `void RecordFailure(params string[] keys)` e `void Reset(string key)`. Bloqueia quando alguma chave chega a `MaxFailuresPerWindow` falhas dentro de `FailureWindowSeconds`.
  - `PasswordPolicy.Validate(string? password)` → lança `ApiProblem(400, "auth.weak_password")` fora de 10–128 caracteres.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Api.Tests/SecurityTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Tests;

public class PasswordHasherTests
{
    private static PasswordHasher Hasher(int memoryKb = 1024, int iterations = 1) =>
        new(Options.Create(new Argon2Options { MemoryKb = memoryKb, Iterations = iterations, Parallelism = 1 }));

    [Fact]
    public void Hash_uses_the_phc_format_and_verifies()
    {
        var hasher = Hasher();
        var encoded = hasher.Hash("senha-correta-1");

        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", encoded);
        Assert.True(hasher.Verify("senha-correta-1", encoded));
        Assert.False(hasher.Verify("senha-errada-01", encoded));
    }

    [Fact]
    public void Same_password_gets_a_different_salt()
    {
        var hasher = Hasher();
        Assert.NotEqual(hasher.Hash("mesma-senha-1"), hasher.Hash("mesma-senha-1"));
    }

    [Fact]
    public void Hashes_made_with_other_parameters_still_verify_and_need_rehash()
    {
        var old = Hasher(memoryKb: 2048, iterations: 2).Hash("senha-antiga-1");

        Assert.True(Hasher().Verify("senha-antiga-1", old));
        Assert.True(Hasher().NeedsRehash(old));
        Assert.False(Hasher(memoryKb: 2048, iterations: 2).NeedsRehash(old));
    }

    [Theory]
    [InlineData("")]
    [InlineData("texto-qualquer")]
    [InlineData("$argon2id$v=19$m=x,t=1,p=1$aaaa$bbbb")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$***$bbbb")]
    public void Malformed_hashes_never_verify(string encoded)
    {
        Assert.False(Hasher().Verify("qualquer-senha", encoded));
        Assert.True(Hasher().NeedsRehash(encoded));
    }

    [Fact]
    public void Dummy_verification_always_fails()
    {
        Assert.False(Hasher().VerifyAgainstDummy("qualquer-senha"));
    }
}

public class TokensTests
{
    [Fact]
    public void Tokens_are_url_safe_random_and_hash_to_32_bytes()
    {
        var a = Tokens.New();
        var b = Tokens.New();

        Assert.Equal(43, a.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
        Assert.NotEqual(a, b);
        Assert.Equal(32, Tokens.Hash(a).Length);
        Assert.Equal(Tokens.Hash(a), Tokens.Hash(a));
    }
}

public class LoginThrottleTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));

    private LoginThrottle Throttle() =>
        new(_time, Options.Create(new AuthOptions { MaxFailuresPerWindow = 3, FailureWindowSeconds = 60 }));

    [Fact]
    public void Blocks_after_the_limit_inside_the_window()
    {
        var throttle = Throttle();
        throttle.RecordFailure("ip:1", "email:a");
        throttle.RecordFailure("ip:1", "email:a");
        Assert.False(throttle.IsBlocked("ip:1"));

        throttle.RecordFailure("ip:1", "email:b");
        Assert.True(throttle.IsBlocked("ip:1"));
        Assert.True(throttle.IsBlocked("ip:2", "ip:1"));
        Assert.False(throttle.IsBlocked("email:a"));
    }

    [Fact]
    public void Releases_after_the_window()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++)
            throttle.RecordFailure("ip:1");

        _time.Advance(TimeSpan.FromSeconds(61));

        Assert.False(throttle.IsBlocked("ip:1"));
    }

    [Fact]
    public void Reset_clears_a_key()
    {
        var throttle = Throttle();
        for (var i = 0; i < 3; i++)
            throttle.RecordFailure("email:a");

        throttle.Reset("email:a");

        Assert.False(throttle.IsBlocked("email:a"));
    }
}

public class PasswordPolicyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123456789")]
    public void Short_passwords_are_rejected(string? password)
    {
        var ex = Assert.Throws<ApiProblem>(() => PasswordPolicy.Validate(password));
        Assert.Equal(("auth.weak_password", 400), (ex.Code, ex.Status));
    }

    [Fact]
    public void Limits_are_inclusive()
    {
        PasswordPolicy.Validate(new string('a', 10));
        PasswordPolicy.Validate(new string('a', 128));
        Assert.Throws<ApiProblem>(() => PasswordPolicy.Validate(new string('a', 129)));
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "FullyQualifiedName~PasswordHasherTests|FullyQualifiedName~TokensTests|FullyQualifiedName~LoginThrottleTests|FullyQualifiedName~PasswordPolicyTests"`
Expected: FAIL de compilação.

- [ ] **Step 3: Implementar**

`src/Recorrencia.Api/Security/PasswordHasher.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Security;

public sealed class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    private readonly Argon2Options _options;
    private readonly Lazy<string> _dummy;

    public PasswordHasher(IOptions<Argon2Options> options)
    {
        _options = options.Value;
        _dummy = new Lazy<string>(() => Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));
    }

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Compute(password, salt, _options.MemoryKb, _options.Iterations, _options.Parallelism, HashBytes);
        return $"$argon2id$v=19$m={_options.MemoryKb},t={_options.Iterations},p={_options.Parallelism}${Encode(salt)}${Encode(hash)}";
    }

    public bool Verify(string password, string encoded)
    {
        if (!TryParse(encoded, out var parsed))
            return false;
        var actual = Compute(password, parsed.Salt, parsed.MemoryKb, parsed.Iterations, parsed.Parallelism, parsed.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(actual, parsed.Hash);
    }

    public bool VerifyAgainstDummy(string password)
    {
        Verify(password, _dummy.Value);
        return false;
    }

    public bool NeedsRehash(string encoded) =>
        !TryParse(encoded, out var parsed)
        || parsed.MemoryKb != _options.MemoryKb
        || parsed.Iterations != _options.Iterations
        || parsed.Parallelism != _options.Parallelism;

    private static byte[] Compute(string password, byte[] salt, int memoryKb, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memoryKb,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] Decode(string value) =>
        Convert.FromBase64String(value.PadRight(value.Length + (4 - value.Length % 4) % 4, '='));

    private sealed record Parsed(int MemoryKb, int Iterations, int Parallelism, byte[] Salt, byte[] Hash);

    private static bool TryParse(string encoded, [NotNullWhen(true)] out Parsed? parsed)
    {
        parsed = null;
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19")
                return false;
            var settings = parts[3].Split(',')
                .Select(p => p.Split('='))
                .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
            if (!int.TryParse(settings.GetValueOrDefault("m"), out var m)
                || !int.TryParse(settings.GetValueOrDefault("t"), out var t)
                || !int.TryParse(settings.GetValueOrDefault("p"), out var p))
                return false;
            var salt = Decode(parts[4]);
            var hash = Decode(parts[5]);
            if (salt.Length == 0 || hash.Length == 0)
                return false;
            parsed = new Parsed(m, t, p, salt, hash);
            return true;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return false;
        }
    }
}
```

Se a classe `Argon2id` da versão instalada não implementar `IDisposable`, troque `using var argon = ...` por `var argon = ...`.

`src/Recorrencia.Api/Security/Tokens.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Recorrencia.Api.Security;

public static class Tokens
{
    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
```

`src/Recorrencia.Api/Security/LoginThrottle.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Security;

public sealed class LoginThrottle(TimeProvider time, IOptions<AuthOptions> options)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _failures = new();

    public bool IsBlocked(params string[] keys)
    {
        var now = time.GetUtcNow();
        return keys.Any(key => Count(key, now) >= options.Value.MaxFailuresPerWindow);
    }

    public void RecordFailure(params string[] keys)
    {
        var now = time.GetUtcNow();
        foreach (var key in keys)
        {
            var queue = _failures.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
            lock (queue)
                queue.Enqueue(now);
        }
    }

    public void Reset(string key) => _failures.TryRemove(key, out _);

    private int Count(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out var queue))
            return 0;
        var cutoff = now.AddSeconds(-options.Value.FailureWindowSeconds);
        lock (queue)
        {
            while (queue.Count > 0 && queue.Peek() <= cutoff)
                queue.Dequeue();
            if (queue.Count == 0)
                _failures.TryRemove(key, out _);
            return queue.Count;
        }
    }
}
```

`src/Recorrencia.Api/Security/PasswordPolicy.cs`:

```csharp
using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Security;

public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    public static void Validate(string? password)
    {
        if (password is null || password.Length < MinLength || password.Length > MaxLength)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "auth.weak_password");
    }
}
```

Em `Program.cs`, antes de `var app = builder.Build();`, acrescente:

```csharp
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<LoginThrottle>();
```

e, no topo, `using Recorrencia.Api.Security;`.

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): add argon2id hashing, session tokens, login throttling and password policy"
```

---

### Task 3: Resolução de tenant e dados de desenvolvimento

**Files:**
- Create: `src/Recorrencia.Api/Tenancy/{RequestContext.cs,TenantResolver.cs,HostContextMiddleware.cs,TenantEndpoints.cs}`
- Create: `src/Recorrencia.Api/Cli/DevSeed.cs`
- Modify: `src/Recorrencia.Api/Program.cs`, `tests/Recorrencia.Api.Tests/ApiFixture.cs`
- Test: `tests/Recorrencia.Api.Tests/TenancyTests.cs`

**Interfaces:**
- Consumes: `Database`, `ApiProblem` (Task 1); `PasswordHasher` (Task 2); `app.resolve_tenant`, `app.provision_tenant` (plano 1).
- Produces:
  - `RequestContext` (scoped) com `IsPlatformHost`, `TenantId`, `TenantSlug`, `TenantName`, `UserId`, `PlatformAdminId`, `SessionTokenHash`, `ClientIp`, `UserAgent`, `Guid RequireTenant()` (404 `tenant.not_found`), `Guid RequireUser()` (401 `auth.unauthenticated`) e `Guid RequirePlatformAdmin()` (401 `auth.unauthenticated`).
  - `TenantResolver` (singleton) com `Task<TenantInfo?> ResolveActiveAsync(string slug, CancellationToken ct)` e `void Invalidate(string slug)`; `record TenantInfo(Guid Id, string Name)`.
  - `HostContextMiddleware` com `const string PlatformHost = "admin"`.
  - `GET /tenant` → `{ slug, name, platform }`, sem autenticação.
  - `DevSeed.SeedTenantAsync(DataSources sources, PasswordHasher hasher, string slug, string password, CancellationToken ct = default) → Task<SeededTenant>`; `record SeededTenant(Guid TenantId, string Slug, Guid Admin, Guid Joao, Guid Maria, Guid Pedro, Guid Coord)`; `const string DevPassword = "senha-dev-123"`.
  - Dados semeados: tenant `slug` com o plano `aprovec` (vigência 2026-08-01). Usuários ativos: `admin@{slug}.local` (Administração, administrador), `joao@{slug}.local` (João Silva, consultor), `maria@{slug}.local` (Maria Oliveira, consultor, supervisor João), `pedro@{slug}.local` (Pedro Santos, consultor, supervisor Maria) e `coordenacao@{slug}.local` (Carla Coordenação, coordenador, membro do grupo "Coordenação"). Boletos de setembro/2026: João 70×200 + 20×300 recebidos, 12×300 em atraso e 4×200 cancelados; Maria 50×200; Pedro 25×200. Boletos de agosto/2026: João 90×200; Maria 45×200; Pedro 20×200. Total: 336 boletos.
  - Teste: `ApiFixture.SeedAsync() → Task<SeededTenant>` com slug único e a senha `ApiFixture.Password`.

- [ ] **Step 1: Escrever os testes (falhando)**

Acrescente a `ApiFixture` (e `using Recorrencia.Api.Cli; using Recorrencia.Api.Infrastructure; using Recorrencia.Api.Security;` no topo):

```csharp
    public Task<SeededTenant> SeedAsync() =>
        DevSeed.SeedTenantAsync(Service<DataSources>(), Service<PasswordHasher>(), Seed.UniqueSlug(), Password);
```

`tests/Recorrencia.Api.Tests/TenancyTests.cs`:

```csharp
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class TenancyTests(ApiFixture api)
{
    private sealed record TenantDto(string Slug, string Name, bool Platform);

    [Fact]
    public async Task Tenant_endpoint_describes_the_subdomain()
    {
        var s = await api.SeedAsync();
        var tenant = await api.Client(s.Slug).GetJsonAsync<TenantDto>("/tenant");
        Assert.Equal(new TenantDto(s.Slug, $"Empresa {s.Slug}", false), tenant);
    }

    [Fact]
    public async Task Platform_host_is_recognized()
    {
        var tenant = await api.Client("admin").GetJsonAsync<TenantDto>("/tenant");
        Assert.True(tenant.Platform);
    }

    [Theory]
    [InlineData("nao-existe")]
    [InlineData("")]
    public async Task Unknown_host_is_not_found(string host)
    {
        var response = await api.Client(host).GetAsync("/tenant");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("tenant.not_found", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Suspended_tenant_is_not_found()
    {
        var s = await api.SeedAsync();
        await api.SqlAsync("update tenants set status = 'suspenso' where id = @id", new { id = s.TenantId });
        api.Service<TenantResolver>().Invalidate(s.Slug);

        var response = await api.Client(s.Slug).GetAsync("/tenant");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Dev_seed_reproduces_the_reference_data()
    {
        var s = await api.SeedAsync();

        Assert.Equal(5, await api.SqlScalarAsync<int>("select count(*)::int from users where tenant_id = @id and status = 'ativo'", new { id = s.TenantId }));
        Assert.Equal(336, await api.SqlScalarAsync<int>("select count(*)::int from boletos where tenant_id = @id", new { id = s.TenantId }));
        Assert.Equal(35000m, await api.SqlScalarAsync<decimal>(
            "select sum(valor) from boletos where tenant_id = @id and status = 'recebido' and pago_em >= date '2026-09-01'", new { id = s.TenantId }));
        Assert.Equal(31000m, await api.SqlScalarAsync<decimal>(
            "select sum(valor) from boletos where tenant_id = @id and status = 'recebido' and pago_em < date '2026-09-01'", new { id = s.TenantId }));
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "FullyQualifiedName~TenancyTests"`
Expected: FAIL de compilação.

- [ ] **Step 3: Implementar o contexto e a resolução**

`src/Recorrencia.Api/Tenancy/RequestContext.cs`:

```csharp
using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Tenancy;

public sealed class RequestContext
{
    public bool IsPlatformHost { get; set; }
    public Guid? TenantId { get; set; }
    public string? TenantSlug { get; set; }
    public string? TenantName { get; set; }
    public Guid? UserId { get; set; }
    public Guid? PlatformAdminId { get; set; }
    public byte[]? SessionTokenHash { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }

    public Guid RequireTenant() =>
        TenantId ?? throw new ApiProblem(StatusCodes.Status404NotFound, "tenant.not_found");

    public Guid RequireUser() =>
        UserId ?? throw new ApiProblem(StatusCodes.Status401Unauthorized, "auth.unauthenticated");

    public Guid RequirePlatformAdmin() =>
        IsPlatformHost && PlatformAdminId is { } id
            ? id
            : throw new ApiProblem(StatusCodes.Status401Unauthorized, "auth.unauthenticated");
}
```

`src/Recorrencia.Api/Tenancy/TenantResolver.cs`:

```csharp
using Microsoft.Extensions.Caching.Memory;
using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Tenancy;

public sealed record TenantInfo(Guid Id, string Name);

public sealed class TenantResolver(Database db, IMemoryCache cache)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public async Task<TenantInfo?> ResolveActiveAsync(string slug, CancellationToken ct)
    {
        var key = CacheKey(slug);
        if (cache.TryGetValue(key, out TenantInfo? cached))
            return cached;

        var row = await db.AnonymousAsync(
            tx => tx.QuerySingleOrDefaultAsync<TenantRow>("select id, name, status from app.resolve_tenant(@slug)", new { slug }),
            ct);
        var info = row is { Status: "ativo" } ? new TenantInfo(row.Id, row.Name) : null;
        cache.Set(key, info, Ttl);
        return info;
    }

    public void Invalidate(string slug) => cache.Remove(CacheKey(slug));

    private static string CacheKey(string slug) => "tenant:" + slug.ToLowerInvariant();

    public sealed class TenantRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
    }
}
```

`src/Recorrencia.Api/Tenancy/HostContextMiddleware.cs`:

```csharp
using System.Net;
using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Tenancy;

public sealed class HostContextMiddleware(RequestDelegate next)
{
    public const string PlatformHost = "admin";

    public async Task InvokeAsync(HttpContext context, RequestContext request, TenantResolver resolver)
    {
        if (context.Request.Path.StartsWithSegments("/health"))
        {
            await next(context);
            return;
        }

        request.ClientIp = IPAddress.TryParse(context.Request.Headers["X-Client-Ip"].ToString(), out var ip) ? ip.ToString() : null;
        var agent = context.Request.Headers["X-Client-User-Agent"].ToString();
        request.UserAgent = agent.Length == 0 ? null : agent[..Math.Min(agent.Length, 512)];

        var host = context.Request.Headers["X-Tenant-Host"].ToString().Trim().ToLowerInvariant();
        if (host == PlatformHost)
        {
            request.IsPlatformHost = true;
        }
        else if (host.Length > 0 && await resolver.ResolveActiveAsync(host, context.RequestAborted) is { } tenant)
        {
            request.TenantId = tenant.Id;
            request.TenantSlug = host;
            request.TenantName = tenant.Name;
        }
        else
        {
            throw new ApiProblem(StatusCodes.Status404NotFound, "tenant.not_found");
        }

        await next(context);
    }
}
```

`src/Recorrencia.Api/Tenancy/TenantEndpoints.cs`:

```csharp
namespace Recorrencia.Api.Tenancy;

public static class TenantEndpoints
{
    public sealed record TenantResponse(string Slug, string Name, bool Platform);

    public static void MapTenantEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/tenant", (RequestContext request) => request.IsPlatformHost
            ? Results.Ok(new TenantResponse(HostContextMiddleware.PlatformHost, "Administração da plataforma", true))
            : Results.Ok(new TenantResponse(request.TenantSlug!, request.TenantName!, false)));
}
```

- [ ] **Step 4: Implementar o seed de desenvolvimento**

`src/Recorrencia.Api/Cli/DevSeed.cs`:

```csharp
using Dapper;
using Npgsql;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Cli;

public sealed record SeededTenant(Guid TenantId, string Slug, Guid Admin, Guid Joao, Guid Maria, Guid Pedro, Guid Coord);

public static partial class DevSeed
{
    public const string DevPassword = "senha-dev-123";

    public static async Task<SeededTenant> SeedTenantAsync(DataSources sources, PasswordHasher hasher, string slug, string password, CancellationToken ct = default)
    {
        var hash = hasher.Hash(password);
        await using var conn = await sources.Superadmin.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var (tenant, admin) = await conn.QuerySingleAsync<(Guid, Guid)>(
            """
            select tenant_id, admin_user_id
              from app.provision_tenant(@slug, @name, 'Administração', @adminEmail, 'aprovec', date '2026-08-01')
            """,
            new { slug, name = slug == "aprovec" ? "APROVEC" : $"Empresa {slug}", adminEmail = $"admin@{slug}.local" },
            tx);
        await conn.ExecuteAsync("update users set status = 'ativo', password_hash = @hash where id = @admin", new { hash, admin }, tx);

        async Task<Guid> UserAsync(string name, string login, Guid? supervisor, string template)
        {
            var id = Guid.CreateVersion7();
            await conn.ExecuteAsync(
                """
                insert into users (id, tenant_id, name, email, password_hash, status, supervisor_id)
                values (@id, @tenant, @name, @email, @hash, 'ativo', @supervisor)
                """,
                new { id, tenant, name, email = $"{login}@{slug}.local", hash, supervisor }, tx);
            await conn.ExecuteAsync(
                """
                insert into user_roles (tenant_id, user_id, role_id)
                select @tenant, @id, id from roles where tenant_id = @tenant and source_template_key = @template
                """,
                new { tenant, id, template }, tx);
            return id;
        }

        var joao = await UserAsync("João Silva", "joao", null, "consultor");
        var maria = await UserAsync("Maria Oliveira", "maria", joao, "consultor");
        var pedro = await UserAsync("Pedro Santos", "pedro", maria, "consultor");
        var coord = await UserAsync("Carla Coordenação", "coordenacao", null, "coordenador");
        await conn.ExecuteAsync(
            """
            insert into commission_group_members (tenant_id, group_id, user_id)
            select @tenant, id, @coord from commission_groups where tenant_id = @tenant and name = 'Coordenação'
            """,
            new { tenant, coord }, tx);

        await BoletosAsync(conn, tx, tenant, joao, "J7", 70, 200m, "2026-09-01");
        await BoletosAsync(conn, tx, tenant, joao, "J9", 20, 300m, "2026-09-01");
        await BoletosAsync(conn, tx, tenant, joao, "JA", 12, 300m, "2026-08-01", "atraso");
        await BoletosAsync(conn, tx, tenant, joao, "JC", 4, 200m, "2026-09-01", "cancelado");
        await BoletosAsync(conn, tx, tenant, maria, "M9", 50, 200m, "2026-09-01");
        await BoletosAsync(conn, tx, tenant, pedro, "P9", 25, 200m, "2026-09-01");
        await BoletosAsync(conn, tx, tenant, joao, "J8", 90, 200m, "2026-08-01");
        await BoletosAsync(conn, tx, tenant, maria, "M8", 45, 200m, "2026-08-01");
        await BoletosAsync(conn, tx, tenant, pedro, "P8", 20, 200m, "2026-08-01");

        await tx.CommitAsync(ct);
        return new SeededTenant(tenant, slug, admin, joao, maria, pedro, coord);
    }

    private static Task BoletosAsync(NpgsqlConnection conn, NpgsqlTransaction tx, Guid tenant, Guid owner, string prefix,
        int count, decimal valor, string month, string status = "recebido") =>
        conn.ExecuteAsync(
            """
            insert into boletos (tenant_id, participante_id, associado_ref, associado_nome, placa, valor, status, vencimento, pago_em)
            select @tenant, @owner, @prefix || '-' || g, 'Associado ' || @prefix || ' ' || g,
                   'TST' || lpad(g::text, 4, '0'), @valor, @status, @month::date + 4,
                   case when @status = 'recebido' then @month::date + (g % 10) end
              from generate_series(1, @count) g
            """,
            new { tenant, owner, prefix, count, valor, month, status }, tx);
}
```

A classe é `partial` porque a Task 11 acrescenta o comando `seed-dev` em outro arquivo.

- [ ] **Step 5: Registrar no `Program.cs`**

No topo: `using Recorrencia.Api.Tenancy;`. Antes de `var app = builder.Build();`:

```csharp
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<TenantResolver>();
builder.Services.AddScoped<RequestContext>();
```

Depois de `app.UseMiddleware<InternalKeyMiddleware>();`:

```csharp
app.UseMiddleware<HostContextMiddleware>();
```

Depois de `app.MapGet("/health", ...)`:

```csharp
app.MapTenantEndpoints();
```

- [ ] **Step 6: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): resolve tenants from the subdomain header and add development seed"
```

---

### Task 4: Login, sessões, permissões e `/me`

**Files:**
- Create: `src/Recorrencia.Api/Auth/{Sessions.cs,SessionMiddleware.cs,AuthEndpoints.cs,MeEndpoints.cs}`
- Create: `src/Recorrencia.Api/Authorization/{PermissionSet.cs,CurrentPermissions.cs,EndpointAuthorization.cs}`
- Modify: `src/Recorrencia.Api/Program.cs`
- Test: `tests/Recorrencia.Api.Tests/AuthTests.cs`

**Interfaces:**
- Consumes: `RequestContext`, `TenantResolver` (Task 3); `PasswordHasher`, `Tokens`, `LoginThrottle` (Task 2); funções de sessão do plano 1.
- Produces:
  - `record SessionResponse(string Token, DateTimeOffset AbsoluteExpiresAt)`.
  - `Sessions.CreateForUserAsync(Database db, RequestContext request, Guid tenantId, Guid userId, AuthOptions options, TimeProvider time, CancellationToken ct)` e `Sessions.CreateForPlatformAdminAsync(Database db, RequestContext request, Guid adminId, AuthOptions options, TimeProvider time, CancellationToken ct)`, os dois devolvendo `Task<SessionResponse>`.
  - `AuthEndpoints.LoginRow` (`UserId`, `PasswordHash`, `Status`) e `record LoginRequest(string? Email, string? Password)`.
  - `POST /auth/login` → 200 `SessionResponse`; `POST /auth/logout` → 204.
  - `GET /me` → `{ id, name, email, tenant: { id, slug, name }, permissions: [{ key, scope }], modules: [string] }`.
  - `Scopes.Ordered`, `Scopes.IsValid(string?)`, `Scopes.Rank(string?)`; `record PermissionGrant(string Key, string? Scope)`; `PermissionSet` com `Has`, `ScopeOf`, `CanGrant(string key, string? scope)` e `All`.
  - `CurrentPermissions` (scoped) com `Task<PermissionSet> GetAsync(CancellationToken ct)`.
  - Extensões de `RouteHandlerBuilder`: `RequireUser()`, `RequirePermission(string permission)` e `RequirePlatformAdmin()`.
  - Chaves do throttle: `ip:{ip}` e `email:{tenantId}:{email}`.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Api.Tests/AuthTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFixture api)
{
    public sealed record PermissionDto(string Key, string? Scope);
    public sealed record TenantDto(Guid Id, string Slug, string Name);
    public sealed record MeDto(Guid Id, string Name, string Email, TenantDto Tenant, List<PermissionDto> Permissions, List<string> Modules);

    [Fact]
    public async Task Requests_without_the_internal_key_look_like_not_found()
    {
        var s = await api.SeedAsync();
        var client = api.Client(s.Slug);

        var withoutKey = await client.SendAsync(HttpMethod.Get, "/me", withKey: false);
        Assert.Equal(HttpStatusCode.NotFound, withoutKey.StatusCode);

        var withKey = await client.GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, withKey.StatusCode);
        Assert.Equal("auth.unauthenticated", await ApiClient.CodeAsync(withKey));
    }

    [Fact]
    public async Task Login_returns_a_session_and_me_describes_the_user()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"JOAO@{s.Slug}.local", ApiFixture.Password);

        var me = await joao.GetJsonAsync<MeDto>("/me");

        Assert.Equal(("João Silva", s.Slug), (me.Name, me.Tenant.Slug));
        Assert.Contains(new PermissionDto("carteira.visualizar", "own"), me.Permissions);
        Assert.Contains(new PermissionDto("estrutura.visualizar", "direct"), me.Permissions);
        Assert.Equal(6, me.Modules.Count);
    }

    [Theory]
    [InlineData("joao", "senha-errada-123")]
    [InlineData("ninguem", ApiFixture.Password)]
    public async Task Wrong_credentials_get_the_same_answer(string login, string password)
    {
        var s = await api.SeedAsync();
        var response = await api.Client(s.Slug).PostAsync("/auth/login", new { email = $"{login}@{s.Slug}.local", password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("auth.invalid_credentials", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Invited_user_cannot_log_in()
    {
        var s = await api.SeedAsync();
        await api.SqlAsync("update users set status = 'convidado' where id = @id", new { id = s.Maria });

        var response = await api.Client(s.Slug).PostAsync("/auth/login", new { email = $"maria@{s.Slug}.local", password = ApiFixture.Password });
        Assert.Equal("auth.invalid_credentials", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Too_many_failures_block_even_the_right_password()
    {
        var s = await api.SeedAsync();
        var client = api.Client(s.Slug);
        for (var i = 0; i < 5; i++)
            await client.PostAsync("/auth/login", new { email = $"joao@{s.Slug}.local", password = "senha-errada-123" });

        var response = await client.PostAsync("/auth/login", new { email = $"joao@{s.Slug}.local", password = ApiFixture.Password });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("auth.too_many_attempts", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Session_cannot_be_used_on_another_tenant()
    {
        var a = await api.SeedAsync();
        var b = await api.SeedAsync();
        var joao = api.Client(a.Slug);
        await joao.LoginAsync($"joao@{a.Slug}.local", ApiFixture.Password);

        var other = api.Client(b.Slug);
        other.Token = joao.Token;
        var response = await other.GetAsync("/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.tenant_mismatch", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        await ApiClient.ExpectAsync(await joao.PostAsync("/auth/logout"), HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.Unauthorized, (await joao.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Login_upgrades_hashes_made_with_old_parameters()
    {
        var s = await api.SeedAsync();
        var oldHasher = new PasswordHasher(Options.Create(new Argon2Options { MemoryKb = 2048, Iterations = 1, Parallelism = 1 }));
        await api.SqlAsync("update users set password_hash = @hash where id = @id", new { hash = oldHasher.Hash(ApiFixture.Password), id = s.Joao });

        await api.Client(s.Slug).LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var stored = await api.SqlScalarAsync<string>("select password_hash from users where id = @id", new { id = s.Joao });
        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", stored);
    }
}

public class PermissionSetTests
{
    private static readonly PermissionSet Set = new(new Dictionary<string, string?>
    {
        ["carteira.visualizar"] = "direct",
        ["usuarios.convidar"] = null,
    });

    [Theory]
    [InlineData("carteira.visualizar", "own", true)]
    [InlineData("carteira.visualizar", "direct", true)]
    [InlineData("carteira.visualizar", "subtree", false)]
    [InlineData("carteira.visualizar", "tenant", false)]
    [InlineData("usuarios.convidar", null, true)]
    [InlineData("usuarios.desligar", null, false)]
    public void Can_grant_only_what_it_has(string key, string? scope, bool expected)
    {
        Assert.Equal(expected, Set.CanGrant(key, scope));
    }

    [Fact]
    public void Exposes_grants_in_key_order()
    {
        Assert.Equal(
            new[] { new PermissionGrant("carteira.visualizar", "direct"), new PermissionGrant("usuarios.convidar", null) },
            Set.All);
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "FullyQualifiedName~AuthTests|FullyQualifiedName~PermissionSetTests"`
Expected: FAIL de compilação.

- [ ] **Step 3: Implementar a autorização**

`src/Recorrencia.Api/Authorization/PermissionSet.cs`:

```csharp
namespace Recorrencia.Api.Authorization;

public static class Scopes
{
    public static readonly string[] Ordered = ["own", "direct", "subtree", "tenant"];

    public static bool IsValid(string? scope) => scope is null || Ordered.Contains(scope);

    public static int Rank(string? scope) => scope is null ? -1 : Array.IndexOf(Ordered, scope);
}

public sealed record PermissionGrant(string Key, string? Scope);

public sealed class PermissionSet(IReadOnlyDictionary<string, string?> grants)
{
    public bool Has(string key) => grants.ContainsKey(key);

    public string? ScopeOf(string key) => grants.GetValueOrDefault(key);

    public bool CanGrant(string key, string? scope) =>
        grants.TryGetValue(key, out var own) && Scopes.Rank(own) >= Scopes.Rank(scope);

    public IReadOnlyList<PermissionGrant> All =>
        grants.OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new PermissionGrant(g.Key, g.Value)).ToList();
}
```

`src/Recorrencia.Api/Authorization/CurrentPermissions.cs`:

```csharp
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Authorization;

public sealed class CurrentPermissions(RequestContext request, Database db)
{
    private PermissionSet? _cached;

    public async Task<PermissionSet> GetAsync(CancellationToken ct)
    {
        if (_cached is not null)
            return _cached;
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var rows = await db.InTenantAsync(tenant, user,
            tx => tx.QueryAsync<PermissionRow>("select permission_key, scope from app.effective_permissions()"), ct);
        return _cached = new PermissionSet(rows.ToDictionary(r => r.PermissionKey, r => r.Scope));
    }

    public sealed class PermissionRow
    {
        public string PermissionKey { get; set; } = "";
        public string? Scope { get; set; }
    }
}
```

`src/Recorrencia.Api/Authorization/EndpointAuthorization.cs`:

```csharp
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Authorization;

public static class EndpointAuthorization
{
    public static RouteHandlerBuilder RequireUser(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.RequestServices.GetRequiredService<RequestContext>().RequireUser();
            return await next(context);
        });

    public static RouteHandlerBuilder RequirePermission(this RouteHandlerBuilder builder, string permission) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var permissions = await context.HttpContext.RequestServices
                .GetRequiredService<CurrentPermissions>()
                .GetAsync(context.HttpContext.RequestAborted);
            if (!permissions.Has(permission))
                throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");
            return await next(context);
        });

    public static RouteHandlerBuilder RequirePlatformAdmin(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.RequestServices.GetRequiredService<RequestContext>().RequirePlatformAdmin();
            return await next(context);
        });
}
```

- [ ] **Step 4: Implementar sessões e login**

`src/Recorrencia.Api/Auth/Sessions.cs`:

```csharp
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Auth;

public sealed record SessionResponse(string Token, DateTimeOffset AbsoluteExpiresAt);

public static class Sessions
{
    public static async Task<SessionResponse> CreateForUserAsync(Database db, RequestContext request, Guid tenantId, Guid userId,
        AuthOptions options, TimeProvider time, CancellationToken ct)
    {
        var token = Tokens.New();
        await db.AnonymousAsync(tx => tx.ExecuteScalarAsync<Guid>(
            "select app.create_session(@hash, @tenantId, @userId, @idle, @absolute, @ip, @agent)",
            new
            {
                hash = Tokens.Hash(token),
                tenantId,
                userId,
                idle = options.IdleTimeoutSeconds,
                absolute = options.AbsoluteTimeoutSeconds,
                ip = request.ClientIp,
                agent = request.UserAgent,
            }), ct);
        return new SessionResponse(token, time.GetUtcNow().AddSeconds(options.AbsoluteTimeoutSeconds));
    }

    public static async Task<SessionResponse> CreateForPlatformAdminAsync(Database db, RequestContext request, Guid adminId,
        AuthOptions options, TimeProvider time, CancellationToken ct)
    {
        var token = Tokens.New();
        await db.AnonymousAsync(tx => tx.ExecuteScalarAsync<Guid>(
            "select app.create_platform_session(@hash, @adminId, @idle, @absolute, @ip, @agent)",
            new
            {
                hash = Tokens.Hash(token),
                adminId,
                idle = options.IdleTimeoutSeconds,
                absolute = options.AbsoluteTimeoutSeconds,
                ip = request.ClientIp,
                agent = request.UserAgent,
            }), ct);
        return new SessionResponse(token, time.GetUtcNow().AddSeconds(options.AbsoluteTimeoutSeconds));
    }
}
```

`src/Recorrencia.Api/Auth/SessionMiddleware.cs`:

```csharp
using Microsoft.Extensions.Options;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Auth;

public sealed class SessionMiddleware(RequestDelegate next, IOptions<AuthOptions> options)
{
    public async Task InvokeAsync(HttpContext context, RequestContext request, Database db)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            var hash = Tokens.Hash(header["Bearer ".Length..].Trim());
            var session = await db.AnonymousAsync(tx => tx.QuerySingleOrDefaultAsync<SessionRow>(
                "select session_id, kind, tenant_id, user_id, platform_admin_id from app.resolve_session(@hash, @idle)",
                new { hash, idle = options.Value.IdleTimeoutSeconds }), context.RequestAborted);

            if (session is not null)
            {
                var platformSession = session.Kind == "platform";
                if (request.IsPlatformHost != platformSession || (!platformSession && session.TenantId != request.TenantId))
                    throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.tenant_mismatch");

                request.UserId = session.UserId;
                request.PlatformAdminId = session.PlatformAdminId;
                request.SessionTokenHash = hash;
            }
        }

        await next(context);
    }

    public sealed class SessionRow
    {
        public Guid SessionId { get; set; }
        public string Kind { get; set; } = "";
        public Guid? TenantId { get; set; }
        public Guid? UserId { get; set; }
        public Guid? PlatformAdminId { get; set; }
    }
}
```

`src/Recorrencia.Api/Auth/AuthEndpoints.cs`:

```csharp
using Microsoft.Extensions.Options;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Auth;

public static class AuthEndpoints
{
    public sealed record LoginRequest(string? Email, string? Password);

    public sealed class LoginRow
    {
        public Guid UserId { get; set; }
        public string? PasswordHash { get; set; }
        public string Status { get; set; } = "";
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/login", LoginAsync);
        app.MapPost("/auth/logout", LogoutAsync);
    }

    public static string NormalizeEmail(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static async Task<IResult> LoginAsync(LoginRequest body, RequestContext request, Database db, PasswordHasher hasher,
        LoginThrottle throttle, IOptions<AuthOptions> options, TimeProvider time, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var email = NormalizeEmail(body.Email);
        var password = body.Password ?? "";
        var ipKey = $"ip:{request.ClientIp}";
        var emailKey = $"email:{tenant}:{email}";
        if (throttle.IsBlocked(ipKey, emailKey))
            throw new ApiProblem(StatusCodes.Status429TooManyRequests, "auth.too_many_attempts");

        var login = await db.AnonymousAsync(tx => tx.QuerySingleOrDefaultAsync<LoginRow>(
            "select user_id, password_hash, status from app.find_login(@tenant, @email)", new { tenant, email }), ct);

        var valid = login is { Status: "ativo", PasswordHash: not null }
            ? hasher.Verify(password, login.PasswordHash)
            : hasher.VerifyAgainstDummy(password);
        if (!valid)
        {
            throttle.RecordFailure(ipKey, emailKey);
            throw new ApiProblem(StatusCodes.Status401Unauthorized, "auth.invalid_credentials");
        }

        throttle.Reset(emailKey);
        var session = await Sessions.CreateForUserAsync(db, request, tenant, login!.UserId, options.Value, time, ct);

        if (hasher.NeedsRehash(login.PasswordHash!))
        {
            var newHash = hasher.Hash(password);
            await db.InTenantAsync(tenant, login.UserId,
                tx => tx.ExecuteAsync("select app.rehash_own_password(@newHash)", new { newHash }), ct);
        }

        return Results.Ok(session);
    }

    private static async Task<IResult> LogoutAsync(RequestContext request, Database db, CancellationToken ct)
    {
        if (request.SessionTokenHash is { } hash)
            await db.AnonymousAsync(tx => tx.ExecuteAsync("select app.revoke_session(@hash)", new { hash }), ct);
        return Results.NoContent();
    }
}
```

`src/Recorrencia.Api/Auth/MeEndpoints.cs`:

```csharp
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Auth;

public static class MeEndpoints
{
    public sealed record TenantSummary(Guid Id, string Slug, string Name);

    public sealed record MeResponse(Guid Id, string Name, string Email, TenantSummary Tenant,
        IReadOnlyList<PermissionGrant> Permissions, IReadOnlyList<string> Modules);

    public sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public static void MapMeEndpoints(this IEndpointRouteBuilder app) =>
        app.MapGet("/me", async (RequestContext request, Database db, CurrentPermissions permissions, CancellationToken ct) =>
        {
            var tenant = request.RequireTenant();
            var userId = request.RequireUser();
            var granted = await permissions.GetAsync(ct);
            var (user, modules) = await db.InTenantAsync(tenant, userId, async tx =>
            (
                await tx.QuerySingleAsync<UserRow>("select id, name, email from users where id = @userId", new { userId }),
                (await tx.QueryAsync<string>("select module_key from tenant_modules where enabled order by module_key")).ToList()
            ), ct);
            return Results.Ok(new MeResponse(user.Id, user.Name, user.Email,
                new TenantSummary(tenant, request.TenantSlug!, request.TenantName!), granted.All, modules));
        }).RequireUser();
}
```

- [ ] **Step 5: Registrar no `Program.cs`**

No topo: `using Recorrencia.Api.Auth;` e `using Recorrencia.Api.Authorization;`. Antes de `var app = builder.Build();`:

```csharp
builder.Services.AddScoped<CurrentPermissions>();
```

Depois de `app.UseMiddleware<HostContextMiddleware>();`:

```csharp
app.UseMiddleware<SessionMiddleware>();
```

Depois de `app.MapTenantEndpoints();`:

```csharp
app.MapAuthEndpoints();
app.MapMeEndpoints();
```

- [ ] **Step 6: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): add login, opaque sessions, permission filters and /me"
```

---

### Task 5: E-mail, definição e redefinição de senha

**Files:**
- Create: `src/Recorrencia.Api/Email/{IEmailSender.cs,LogEmailSender.cs,FileEmailSender.cs,LinkBuilder.cs}`
- Create: `src/Recorrencia.Api/Auth/PasswordEndpoints.cs`
- Create: `tests/Recorrencia.Api.Tests/CapturingEmailSender.cs`
- Modify: `src/Recorrencia.Api/Program.cs`, `tests/Recorrencia.Api.Tests/ApiFixture.cs`
- Test: `tests/Recorrencia.Api.Tests/PasswordTests.cs`

**Interfaces:**
- Consumes: `Sessions`, `AuthEndpoints.LoginRow`, `AuthEndpoints.NormalizeEmail` (Task 4); `app.create_invite`, `app.consume_invite` (plano 1).
- Produces:
  - `IEmailSender.SendAsync(string to, string subject, string body, CancellationToken ct)`.
  - `LinkBuilder.SetPassword(string slug, string token)` → `{Scheme}://{slug}.{RootDomain}/definir-senha/{token}`.
  - `POST /auth/set-password` `{ token, password }` → 200 `SessionResponse` (convite ou redefinição, com login automático).
  - `POST /auth/password-reset` `{ email }` → sempre 202. Chave do throttle: `reset-ip:{ip}`.
  - Testes: `ApiFixture.Emails` (`CapturingEmailSender`) com `LastTo(string to)` e `static ExtractToken(string body)`.

- [ ] **Step 1: Criar o capturador de e-mails e ajustar a fixture**

`tests/Recorrencia.Api.Tests/CapturingEmailSender.cs`:

```csharp
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Recorrencia.Api.Email;

namespace Recorrencia.Api.Tests;

public sealed record SentEmail(string To, string Subject, string Body);

public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<SentEmail> _sent = new();

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        _sent.Enqueue(new SentEmail(to, subject, body));
        return Task.CompletedTask;
    }

    public SentEmail? LastTo(string to) => _sent.LastOrDefault(e => e.To == to);

    public static string ExtractToken(string body) => TokenPattern().Match(body).Groups[1].Value;

    [GeneratedRegex(@"/definir-senha/([A-Za-z0-9_-]+)")]
    private static partial Regex TokenPattern();
}
```

Em `ApiFixture`, acrescente a propriedade:

```csharp
    public CapturingEmailSender Emails { get; } = new();
```

e, dentro de `WithWebHostBuilder(builder => { ... })`, depois dos `UseSetting`:

```csharp
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<Recorrencia.Api.Email.IEmailSender>();
                services.AddSingleton<Recorrencia.Api.Email.IEmailSender>(Emails);
            });
```

com `using Microsoft.Extensions.DependencyInjection.Extensions;` no topo.

- [ ] **Step 2: Escrever os testes (falhando)**

`tests/Recorrencia.Api.Tests/PasswordTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Recorrencia.Api.Email;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class PasswordTests(ApiFixture api)
{
    [Fact]
    public async Task Reset_sends_a_link_that_sets_a_new_password()
    {
        var s = await api.SeedAsync();
        var email = $"joao@{s.Slug}.local";
        var client = api.Client(s.Slug);

        await ApiClient.ExpectAsync(await client.PostAsync("/auth/password-reset", new { email }), HttpStatusCode.Accepted);

        var sent = api.Emails.LastTo(email);
        Assert.NotNull(sent);
        Assert.Contains($"http://{s.Slug}.localhost:3000/definir-senha/", sent.Body);

        var token = CapturingEmailSender.ExtractToken(sent.Body);
        var response = await client.PostAsync("/auth/set-password", new { token, password = "nova-senha-segura" });
        await ApiClient.ExpectAsync(response, HttpStatusCode.OK);

        await api.Client(s.Slug).LoginAsync(email, "nova-senha-segura");
        var old = await api.Client(s.Slug).PostAsync("/auth/login", new { email, password = ApiFixture.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
    }

    [Fact]
    public async Task Reset_for_unknown_email_is_accepted_silently()
    {
        var s = await api.SeedAsync();
        var email = $"ninguem@{s.Slug}.local";

        await ApiClient.ExpectAsync(await api.Client(s.Slug).PostAsync("/auth/password-reset", new { email }), HttpStatusCode.Accepted);

        Assert.Null(api.Emails.LastTo(email));
    }

    [Fact]
    public async Task Weak_password_is_rejected()
    {
        var s = await api.SeedAsync();
        var response = await api.Client(s.Slug).PostAsync("/auth/set-password", new { token = "qualquer", password = "curta" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.weak_password", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Invalid_token_is_rejected()
    {
        var s = await api.SeedAsync();
        var response = await api.Client(s.Slug).PostAsync("/auth/set-password", new { token = "token-inexistente", password = "senha-valida-123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invite.invalid", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task File_sender_writes_one_file_per_email()
    {
        var dir = Path.Combine(Path.GetTempPath(), "emails-" + Guid.NewGuid().ToString("N"));
        var sender = new FileEmailSender(
            Options.Create(new EmailOptions { OutboxDir = dir }),
            api.Service<Microsoft.Extensions.Hosting.IHostEnvironment>());

        await sender.SendAsync("a@b.c", "Assunto", "Corpo com link", CancellationToken.None);

        var file = Assert.Single(Directory.GetFiles(dir));
        var content = await File.ReadAllTextAsync(file);
        Assert.Contains("Para: a@b.c", content);
        Assert.Contains("Corpo com link", content);
    }
}
```

- [ ] **Step 3: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "FullyQualifiedName~PasswordTests"`
Expected: FAIL de compilação.

- [ ] **Step 4: Implementar o e-mail**

`src/Recorrencia.Api/Email/IEmailSender.cs`:

```csharp
namespace Recorrencia.Api.Email;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}
```

`src/Recorrencia.Api/Email/LogEmailSender.cs`:

```csharp
namespace Recorrencia.Api.Email;

public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        logger.LogInformation("E-mail para {To} — {Subject}\n{Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
```

`src/Recorrencia.Api/Email/FileEmailSender.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Email;

public sealed class FileEmailSender(IOptions<EmailOptions> options, IHostEnvironment environment) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var directory = Path.GetFullPath(options.Value.OutboxDir, environment.ContentRootPath);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(file, $"Para: {to}\nAssunto: {subject}\n\n{body}\n", ct);
    }
}
```

`src/Recorrencia.Api/Email/LinkBuilder.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Email;

public sealed class LinkBuilder(IOptions<WebOptions> options)
{
    public string SetPassword(string slug, string token) =>
        $"{options.Value.Scheme}://{slug}.{options.Value.RootDomain}/definir-senha/{Uri.EscapeDataString(token)}";
}
```

- [ ] **Step 5: Implementar os endpoints de senha**

`src/Recorrencia.Api/Auth/PasswordEndpoints.cs`:

```csharp
using Microsoft.Extensions.Options;
using Recorrencia.Api.Email;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Auth;

public static class PasswordEndpoints
{
    public sealed record SetPasswordRequest(string? Token, string? Password);

    public sealed record ResetRequest(string? Email);

    public static void MapPasswordEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/auth/set-password", SetPasswordAsync);
        app.MapPost("/auth/password-reset", RequestResetAsync);
    }

    private static async Task<IResult> SetPasswordAsync(SetPasswordRequest body, RequestContext request, Database db,
        PasswordHasher hasher, IOptions<AuthOptions> options, TimeProvider time, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        PasswordPolicy.Validate(body.Password);
        var hash = hasher.Hash(body.Password!);
        var userId = await db.AnonymousAsync(tx => tx.ExecuteScalarAsync<Guid>(
            "select app.consume_invite(@tenant, @tokenHash, @hash)",
            new { tenant, tokenHash = Tokens.Hash(body.Token ?? ""), hash }), ct);
        return Results.Ok(await Sessions.CreateForUserAsync(db, request, tenant, userId, options.Value, time, ct));
    }

    private static async Task<IResult> RequestResetAsync(ResetRequest body, RequestContext request, Database db,
        LoginThrottle throttle, IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var ipKey = $"reset-ip:{request.ClientIp}";
        if (throttle.IsBlocked(ipKey))
            throw new ApiProblem(StatusCodes.Status429TooManyRequests, "auth.too_many_attempts");
        throttle.RecordFailure(ipKey);

        var address = AuthEndpoints.NormalizeEmail(body.Email);
        var login = await db.AnonymousAsync(tx => tx.QuerySingleOrDefaultAsync<AuthEndpoints.LoginRow>(
            "select user_id, password_hash, status from app.find_login(@tenant, @address)", new { tenant, address }), ct);

        if (login is { Status: "ativo" })
        {
            var token = Tokens.New();
            await db.InTenantAsync(tenant, null, tx => tx.ExecuteAsync(
                "select app.create_invite(@userId, @hash, 'redefinicao', @ttl)",
                new { userId = login.UserId, hash = Tokens.Hash(token), ttl = options.Value.ResetTtlSeconds }), ct);
            await email.SendAsync(address, "Redefinição de senha",
                $"""
                Recebemos um pedido para redefinir sua senha.

                Defina uma nova senha em: {links.SetPassword(request.TenantSlug!, token)}

                O link vale por 1 hora. Se você não fez esse pedido, ignore este e-mail.
                """, ct);
        }

        return Results.Accepted();
    }
}
```

- [ ] **Step 6: Registrar no `Program.cs`**

No topo: `using Microsoft.Extensions.Options;` e `using Recorrencia.Api.Email;`. Antes de `var app = builder.Build();`:

```csharp
builder.Services.AddSingleton<LinkBuilder>();
builder.Services.AddSingleton<IEmailSender>(sp =>
    string.IsNullOrEmpty(sp.GetRequiredService<IOptions<EmailOptions>>().Value.OutboxDir)
        ? ActivatorUtilities.CreateInstance<LogEmailSender>(sp)
        : ActivatorUtilities.CreateInstance<FileEmailSender>(sp));
```

Depois de `app.MapMeEndpoints();`:

```csharp
app.MapPasswordEndpoints();
```

- [ ] **Step 7: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): add password set/reset flow with log and file email senders"
```

---

### Task 6: Usuários, estrutura e auditoria

**Files:**
- Create: `src/Recorrencia.Api/Audit/Audit.cs`, `src/Recorrencia.Api/Authorization/RoleGuards.cs`, `src/Recorrencia.Api/Users/UserEndpoints.cs`
- Modify: `src/Recorrencia.Api/Program.cs`
- Test: `tests/Recorrencia.Api.Tests/UserTests.cs`

**Interfaces:**
- Consumes: `CurrentPermissions`, `PermissionSet`, `RequirePermission` (Task 4); `IEmailSender`, `LinkBuilder` (Task 5); `app.create_invite`, `app.revoke_user_sessions`, `app.count_active_with_permission` (plano 1).
- Produces:
  - `Audit.WriteAsync(Tx tx, Guid tenantId, Guid userId, string action, string entity, Guid? entityId, object? before, object? after)`.
  - `RoleGuards.EnsureCanGrantRolesAsync(Tx tx, PermissionSet mine, IReadOnlyCollection<Guid> roleIds)` — 400 `roles.not_found`, 403 `role.grant_exceeds_own`.
  - `RoleGuards.EnsureProfileManagerRemainsAsync(Tx tx)` — 409 `role.last_admin`.
  - `GET /users` [estrutura.visualizar] → `[{ id, name, email, status, supervisorId, roleIds }]`.
  - `POST /users` [usuarios.convidar] `{ name, email, supervisorId?, roleIds? }` → 201 `{ id }` e e-mail de convite.
  - `PUT /users/{id}/supervisor` [estrutura.editar] `{ supervisorId }` → 204.
  - `POST /users/{id}/deactivate` [usuarios.desligar] → 204.
  - `PUT /users/{id}/roles` [usuarios.gerenciar_perfis] `{ roleIds }` → 204.
  - Ações de auditoria: `users.invite`, `users.change_supervisor`, `users.deactivate`, `users.set_roles`.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Api.Tests/UserTests.cs`:

```csharp
using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class UserTests(ApiFixture api)
{
    public sealed record UserDto(Guid Id, string Name, string Email, string Status, Guid? SupervisorId, Guid[] RoleIds);
    public sealed record PermissionDto(string Key, string? Scope);
    public sealed record MeDto(List<PermissionDto> Permissions);
    public sealed record CreatedDto(Guid Id);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    private Task<Guid> RoleIdAsync(Guid tenantId, string template) =>
        api.SqlScalarAsync<Guid>("select id from roles where tenant_id = @tenantId and source_template_key = @template", new { tenantId, template });

    [Fact]
    public async Task Consultor_lists_self_and_direct_reports()
    {
        var s = await api.SeedAsync();
        var users = await (await LoginAsync(s, "joao")).GetJsonAsync<List<UserDto>>("/users");
        Assert.Equal(new[] { s.Joao, s.Maria }.OrderBy(x => x), users.Select(u => u.Id).OrderBy(x => x));
    }

    [Fact]
    public async Task Coordenador_lists_everyone()
    {
        var s = await api.SeedAsync();
        var users = await (await LoginAsync(s, "coordenacao")).GetJsonAsync<List<UserDto>>("/users");
        Assert.Equal(5, users.Count);
    }

    [Fact]
    public async Task Consultor_cannot_invite()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "joao")).PostAsync("/users", new { name = "X", email = $"x@{s.Slug}.local" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Invited_consultor_sets_a_password_and_appears_under_the_supervisor()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var email = $"nova@{s.Slug}.local";
        var consultor = await RoleIdAsync(s.TenantId, "consultor");

        var created = await admin.PostAsync("/users", new { name = "Nova Consultora", email, supervisorId = s.Joao, roleIds = new[] { consultor } });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        var token = CapturingEmailSender.ExtractToken(api.Emails.LastTo(email)!.Body);
        var nova = api.Client(s.Slug);
        await ApiClient.ExpectAsync(await nova.PostAsync("/auth/set-password", new { token, password = "senha-da-nova-1" }), HttpStatusCode.OK);
        await nova.LoginAsync(email, "senha-da-nova-1");

        var joaoSees = await (await LoginAsync(s, "joao")).GetJsonAsync<List<UserDto>>("/users");
        Assert.Contains(joaoSees, u => u.Id == id && u.Status == "ativo");
    }

    [Fact]
    public async Task Duplicate_email_is_rejected()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync("/users", new { name = "Outro João", email = $"JOAO@{s.Slug}.local" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("users.email_taken", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Supervisor_from_another_tenant_is_rejected()
    {
        var s = await api.SeedAsync();
        var other = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync("/users", new { name = "X", email = $"x@{s.Slug}.local", supervisorId = other.Joao });
        Assert.Equal("users.invalid_supervisor", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Cycles_are_rejected()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PutAsync($"/users/{s.Joao}/supervisor", new { supervisorId = s.Pedro });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hierarchy.cycle", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Changing_the_supervisor_is_audited()
    {
        var s = await api.SeedAsync();
        await ApiClient.ExpectAsync(
            await (await LoginAsync(s, "admin")).PutAsync($"/users/{s.Pedro}/supervisor", new { supervisorId = s.Joao }),
            HttpStatusCode.NoContent);

        Assert.Equal(1, await api.SqlScalarAsync<int>(
            "select count(*)::int from audit_log where entity_id = @id and action = 'users.change_supervisor'", new { id = s.Pedro }));
    }

    [Fact]
    public async Task Deactivating_a_user_revokes_the_sessions()
    {
        var s = await api.SeedAsync();
        var maria = await LoginAsync(s, "maria");

        await ApiClient.ExpectAsync(await (await LoginAsync(s, "admin")).PostAsync($"/users/{s.Maria}/deactivate"), HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.Unauthorized, (await maria.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task Last_profile_manager_cannot_be_deactivated()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync($"/users/{s.Admin}/deactivate");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("role.last_admin", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Cannot_grant_a_role_beyond_own_permissions()
    {
        var s = await api.SeedAsync();
        var recrutador = Guid.NewGuid();
        await api.SqlAsync(
            """
            insert into roles (id, tenant_id, name) values (@recrutador, @tenant, 'Recrutador');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values
              (@tenant, @recrutador, 'usuarios.convidar', null),
              (@tenant, @recrutador, 'usuarios.gerenciar_perfis', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @maria, @recrutador);
            """,
            new { recrutador, tenant = s.TenantId, maria = s.Maria });
        var administrador = await RoleIdAsync(s.TenantId, "administrador");

        var response = await (await LoginAsync(s, "maria")).PostAsync("/users",
            new { name = "Novo admin", email = $"novo@{s.Slug}.local", roleIds = new[] { administrador } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("role.grant_exceeds_own", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Setting_roles_changes_effective_permissions()
    {
        var s = await api.SeedAsync();
        var roles = new[] { await RoleIdAsync(s.TenantId, "consultor"), await RoleIdAsync(s.TenantId, "coordenador") };

        await ApiClient.ExpectAsync(
            await (await LoginAsync(s, "admin")).PutAsync($"/users/{s.Joao}/roles", new { roleIds = roles }),
            HttpStatusCode.NoContent);

        var me = await (await LoginAsync(s, "joao")).GetJsonAsync<MeDto>("/me");
        Assert.Contains(new PermissionDto("carteira.visualizar", "tenant"), me.Permissions);
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "FullyQualifiedName~UserTests"`
Expected: FAIL (404 nos endpoints `/users`).

- [ ] **Step 3: Implementar auditoria e guardas de perfil**

`src/Recorrencia.Api/Audit/Audit.cs`:

```csharp
using System.Text.Json;
using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Audit;

public static class Audit
{
    public static Task WriteAsync(Tx tx, Guid tenantId, Guid userId, string action, string entity, Guid? entityId, object? before, object? after) =>
        tx.ExecuteAsync(
            """
            insert into audit_log (tenant_id, user_id, action, entity, entity_id, before, after)
            values (@tenantId, @userId, @action, @entity, @entityId, @before::jsonb, @after::jsonb)
            """,
            new { tenantId, userId, action, entity, entityId, before = Serialize(before), after = Serialize(after) });

    private static string? Serialize(object? value) =>
        value is null ? null : JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
```

`src/Recorrencia.Api/Authorization/RoleGuards.cs`:

```csharp
using Recorrencia.Api.Infrastructure;

namespace Recorrencia.Api.Authorization;

public static class RoleGuards
{
    public static async Task EnsureCanGrantRolesAsync(Tx tx, PermissionSet mine, IReadOnlyCollection<Guid> roleIds)
    {
        if (roleIds.Count == 0)
            return;
        var distinct = roleIds.Distinct().ToArray();
        var found = await tx.ExecuteScalarAsync<int>("select count(*)::int from roles where id = any(@distinct)", new { distinct });
        if (found != distinct.Length)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "roles.not_found");

        var grants = await tx.QueryAsync<GrantRow>(
            "select permission_key, scope from role_permissions where role_id = any(@distinct)", new { distinct });
        if (grants.Any(g => !mine.CanGrant(g.PermissionKey, g.Scope)))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "role.grant_exceeds_own");
    }

    public static async Task EnsureProfileManagerRemainsAsync(Tx tx)
    {
        var count = await tx.ExecuteScalarAsync<int>("select app.count_active_with_permission('usuarios.gerenciar_perfis')");
        if (count == 0)
            throw new ApiProblem(StatusCodes.Status409Conflict, "role.last_admin");
    }

    public sealed class GrantRow
    {
        public string PermissionKey { get; set; } = "";
        public string? Scope { get; set; }
    }
}
```

- [ ] **Step 4: Implementar os endpoints de usuários**

`src/Recorrencia.Api/Users/UserEndpoints.cs`:

```csharp
using System.Net.Mail;
using Microsoft.Extensions.Options;
using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Email;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Users;

public static class UserEndpoints
{
    public sealed record InviteUserRequest(string? Name, string? Email, Guid? SupervisorId, Guid[]? RoleIds);
    public sealed record SupervisorRequest(Guid? SupervisorId);
    public sealed record UserRolesRequest(Guid[]? RoleIds);

    public sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Status { get; set; } = "";
        public Guid? SupervisorId { get; set; }
        public Guid[] RoleIds { get; set; } = [];
    }

    public sealed class SupervisorRow
    {
        public Guid? SupervisorId { get; set; }
    }

    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/users", ListAsync).RequirePermission("estrutura.visualizar");
        app.MapPost("/users", InviteAsync).RequirePermission("usuarios.convidar");
        app.MapPut("/users/{id:guid}/supervisor", ChangeSupervisorAsync).RequirePermission("estrutura.editar");
        app.MapPost("/users/{id:guid}/deactivate", DeactivateAsync).RequirePermission("usuarios.desligar");
        app.MapPut("/users/{id:guid}/roles", SetRolesAsync).RequirePermission("usuarios.gerenciar_perfis");
    }

    private static async Task<IResult> ListAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var users = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), tx => tx.QueryAsync<UserRow>(
            """
            select u.id, u.name, u.email, u.status, u.supervisor_id,
                   coalesce(array_agg(ur.role_id) filter (where ur.role_id is not null), '{}')::uuid[] as role_ids
              from users u
              left join user_roles ur on ur.user_id = u.id
             group by u.id, u.name, u.email, u.status, u.supervisor_id
             order by u.name
            """), ct);
        return Results.Ok(users);
    }

    private static async Task<IResult> InviteAsync(InviteUserRequest body, RequestContext request, Database db,
        CurrentPermissions permissions, IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var name = (body.Name ?? "").Trim();
        var address = (body.Email ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.name_required");
        if (!MailAddress.TryCreate(address, out _))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_email");

        var roleIds = body.RoleIds ?? [];
        var mine = await permissions.GetAsync(ct);
        if (roleIds.Length > 0 && !mine.Has("usuarios.gerenciar_perfis"))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");

        var id = Guid.CreateVersion7();
        var token = Tokens.New();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            await RoleGuards.EnsureCanGrantRolesAsync(tx, mine, roleIds);
            try
            {
                await tx.ExecuteAsync(
                    "insert into users (id, tenant_id, name, email, supervisor_id) values (@id, @tenant, @name, @address, @supervisor)",
                    new { id, tenant, name, address, supervisor = body.SupervisorId });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "users.email_taken");
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_supervisor");
            }

            foreach (var roleId in roleIds.Distinct())
                await tx.ExecuteAsync("insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @id, @roleId)", new { tenant, id, roleId });

            await tx.ExecuteAsync("select app.create_invite(@id, @hash, 'convite', @ttl)",
                new { id, hash = Tokens.Hash(token), ttl = options.Value.InviteTtlSeconds });
            await WriteAsync(tx, tenant, actor, "users.invite", "users", id, null, new { name, email = address, body.SupervisorId, roleIds });
            return 0;
        }, ct);

        await email.SendAsync(address, "Convite de acesso",
            $"""
            Você foi convidado para acessar a plataforma de {request.TenantName}.

            Defina sua senha em: {links.SetPassword(request.TenantSlug!, token)}

            O link vale por 72 horas.
            """, ct);
        return Results.Created($"/users/{id}", new { id });
    }

    private static async Task<IResult> ChangeSupervisorAsync(Guid id, SupervisorRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var current = await tx.QuerySingleOrDefaultAsync<SupervisorRow>(
                "select supervisor_id from users where id = @id for update", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            try
            {
                await tx.ExecuteAsync("update users set supervisor_id = @supervisor where id = @id", new { supervisor = body.SupervisorId, id });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_supervisor");
            }
            await WriteAsync(tx, tenant, actor, "users.change_supervisor", "users", id,
                new { current.SupervisorId }, new { body.SupervisorId });
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeactivateAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var status = await tx.QuerySingleOrDefaultAsync<string>("select status from users where id = @id for update", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            if (status == "desligado")
                return 0;
            await tx.ExecuteAsync("update users set status = 'desligado' where id = @id", new { id });
            await RoleGuards.EnsureProfileManagerRemainsAsync(tx);
            await tx.ExecuteAsync("select app.revoke_user_sessions(@id)", new { id });
            await WriteAsync(tx, tenant, actor, "users.deactivate", "users", id, new { status }, new { status = "desligado" });
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetRolesAsync(Guid id, UserRolesRequest body, RequestContext request, Database db,
        CurrentPermissions permissions, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var mine = await permissions.GetAsync(ct);
        var desired = (body.RoleIds ?? []).Distinct().ToArray();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            _ = await tx.QuerySingleOrDefaultAsync<Guid?>("select id from users where id = @id", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            var current = (await tx.QueryAsync<Guid>("select role_id from user_roles where user_id = @id", new { id })).ToArray();
            var added = desired.Except(current).ToArray();
            await RoleGuards.EnsureCanGrantRolesAsync(tx, mine, added);

            foreach (var roleId in added)
                await tx.ExecuteAsync("insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @id, @roleId)", new { tenant, id, roleId });
            await tx.ExecuteAsync("delete from user_roles where user_id = @id and not (role_id = any(@desired))", new { id, desired });

            await RoleGuards.EnsureProfileManagerRemainsAsync(tx);
            await WriteAsync(tx, tenant, actor, "users.set_roles", "users", id, new { roleIds = current }, new { roleIds = desired });
            return 0;
        }, ct);
        return Results.NoContent();
    }
}
```

Observações:
- `select status ... for update` exige a permissão de UPDATE do RLS (`estrutura.editar` ou `usuarios.desligar`), que o endpoint já exige. Sem ela, a linha não aparece e a resposta é 404.
- Em `SetRolesAsync`, as inserções vêm antes da remoção pelo mesmo motivo explicado em `RoleEndpoints.UpdateAsync` (Task 7): o ator pode estar retirando o próprio perfil de administrador.

- [ ] **Step 5: Registrar no `Program.cs`**

No topo: `using Recorrencia.Api.Users;`. Depois de `app.MapPasswordEndpoints();`:

```csharp
app.MapUserEndpoints();
```

- [ ] **Step 6: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): manage users, supervisors and role assignments with auditing"
```

---

### Task 7: Perfis e catálogo de permissões

**Files:**
- Create: `src/Recorrencia.Api/Roles/RoleEndpoints.cs`
- Modify: `src/Recorrencia.Api/Program.cs`
- Test: `tests/Recorrencia.Api.Tests/RoleTests.cs`

**Interfaces:**
- Consumes: `RoleGuards`, `Audit`, `PermissionSet`, `Scopes` (Tasks 4 e 6).
- Produces:
  - `GET /permissions` [usuarios.gerenciar_perfis] → `[{ key, name, enabled, permissions: [{ key, name, scoped }] }]` (módulos em `sort_order`).
  - `GET /roles` [usuarios.gerenciar_perfis] → `[{ id, name, sourceTemplateKey, permissions: [{ key, scope }] }]`.
  - `POST /roles` `{ name, permissions: [{ key, scope }] }` → 201 `{ id }`.
  - `PUT /roles/{id}` (mesmo corpo) → 204.
  - `DELETE /roles/{id}` → 204.
  - Validação em ordem: nome (`role.name_required`), chaves repetidas (`role.duplicate_permission`), chave inexistente (`role.invalid_permission`), escopo inválido (`role.invalid_scope`), escopo exigido/proibido (`role.scope_required`/`role.scope_not_allowed`), limite do próprio usuário (`role.grant_exceeds_own`). Nome repetido: 409 `role.name_taken`. Perfil inexistente: 404 `role.not_found`.
  - Auditoria: `roles.create`, `roles.update`, `roles.delete`.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Api.Tests/RoleTests.cs`:

```csharp
using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class RoleTests(ApiFixture api)
{
    public sealed record CatalogPermission(string Key, string Name, bool Scoped);
    public sealed record CatalogModule(string Key, string Name, bool Enabled, List<CatalogPermission> Permissions);
    public sealed record RolePermission(string Key, string? Scope);
    public sealed record RoleDto(Guid Id, string Name, string? SourceTemplateKey, List<RolePermission> Permissions);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    [Fact]
    public async Task Catalog_lists_modules_and_permissions()
    {
        var s = await api.SeedAsync();
        var catalog = await (await LoginAsync(s, "admin")).GetJsonAsync<List<CatalogModule>>("/permissions");

        Assert.Equal(new[] { "carteira", "comissoes", "fechamento", "estrutura", "regras_comissao", "usuarios" }, catalog.Select(m => m.Key));
        Assert.Equal(14, catalog.Sum(m => m.Permissions.Count));

        var consultor = await (await LoginAsync(s, "joao")).GetAsync("/permissions");
        Assert.Equal(HttpStatusCode.Forbidden, consultor.StatusCode);
    }

    [Fact]
    public async Task Admin_creates_a_custom_role()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var created = await admin.PostAsync("/roles", new
        {
            name = "Supervisor",
            permissions = new object[]
            {
                new { key = "carteira.visualizar", scope = "direct" },
                new { key = "estrutura.visualizar", scope = "subtree" },
                new { key = "fechamento.visualizar", scope = (string?)null },
            },
        });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);

        var roles = await admin.GetJsonAsync<List<RoleDto>>("/roles");
        var supervisor = Assert.Single(roles, r => r.Name == "Supervisor");
        Assert.Contains(new RolePermission("estrutura.visualizar", "subtree"), supervisor.Permissions);
        Assert.Equal(4, roles.Count);
    }

    [Theory]
    [InlineData("Consultor", "carteira.visualizar", "own", HttpStatusCode.Conflict, "role.name_taken")]
    [InlineData("Novo", "carteira.visualizar", null, HttpStatusCode.BadRequest, "role.scope_required")]
    [InlineData("Novo", "usuarios.convidar", "own", HttpStatusCode.BadRequest, "role.scope_not_allowed")]
    [InlineData("Novo", "carteira.visualizar", "universo", HttpStatusCode.BadRequest, "role.invalid_scope")]
    [InlineData("Novo", "carteira.apagar", null, HttpStatusCode.BadRequest, "role.invalid_permission")]
    [InlineData(" ", "carteira.visualizar", "own", HttpStatusCode.BadRequest, "role.name_required")]
    public async Task Invalid_roles_are_rejected(string name, string key, string? scope, HttpStatusCode status, string code)
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync("/roles", new { name, permissions = new[] { new { key, scope } } });

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Removing_the_last_profile_manager_permission_is_blocked()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var adminRole = (await admin.GetJsonAsync<List<RoleDto>>("/roles")).Single(r => r.SourceTemplateKey == "administrador");

        var response = await admin.PutAsync($"/roles/{adminRole.Id}", new
        {
            name = adminRole.Name,
            permissions = adminRole.Permissions.Where(p => p.Key != "usuarios.gerenciar_perfis").Select(p => new { key = p.Key, scope = p.Scope }),
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("role.last_admin", await ApiClient.CodeAsync(response));
        var unchanged = (await admin.GetJsonAsync<List<RoleDto>>("/roles")).Single(r => r.Id == adminRole.Id);
        Assert.Contains(unchanged.Permissions, p => p.Key == "usuarios.gerenciar_perfis");
    }

    [Fact]
    public async Task Deleting_the_only_admin_role_is_blocked()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var adminRole = (await admin.GetJsonAsync<List<RoleDto>>("/roles")).Single(r => r.SourceTemplateKey == "administrador");

        var response = await admin.DeleteAsync($"/roles/{adminRole.Id}");
        Assert.Equal("role.last_admin", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Cannot_create_a_role_broader_than_own_permissions()
    {
        var s = await api.SeedAsync();
        var gestor = Guid.NewGuid();
        await api.SqlAsync(
            """
            insert into roles (id, tenant_id, name) values (@gestor, @tenant, 'Gestor de perfis');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @gestor, 'usuarios.gerenciar_perfis', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @maria, @gestor);
            """,
            new { gestor, tenant = s.TenantId, maria = s.Maria });

        var response = await (await LoginAsync(s, "maria")).PostAsync("/roles",
            new { name = "Amplo", permissions = new[] { new { key = "carteira.visualizar", scope = "tenant" } } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("role.grant_exceeds_own", await ApiClient.CodeAsync(response));
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "FullyQualifiedName~RoleTests"`
Expected: FAIL (404 em `/permissions` e `/roles`).

- [ ] **Step 3: Implementar**

`src/Recorrencia.Api/Roles/RoleEndpoints.cs`:

```csharp
using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Roles;

public static class RoleEndpoints
{
    public sealed record PermissionInput(string? Key, string? Scope);
    public sealed record RoleRequest(string? Name, PermissionInput[]? Permissions);
    public sealed record CatalogPermission(string Key, string Name, bool Scoped);
    public sealed record CatalogModule(string Key, string Name, bool Enabled, IReadOnlyList<CatalogPermission> Permissions);
    public sealed record RolePermission(string Key, string? Scope);
    public sealed record RoleResponse(Guid Id, string Name, string? SourceTemplateKey, IReadOnlyList<RolePermission> Permissions);

    public sealed class ModuleRow
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
    }

    public sealed class PermissionRow
    {
        public string Key { get; set; } = "";
        public string ModuleKey { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Scoped { get; set; }
    }

    public sealed class RoleRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? SourceTemplateKey { get; set; }
        public string? PermissionKey { get; set; }
        public string? Scope { get; set; }
    }

    public static void MapRoleEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/permissions", CatalogAsync).RequirePermission("usuarios.gerenciar_perfis");
        app.MapGet("/roles", ListAsync).RequirePermission("usuarios.gerenciar_perfis");
        app.MapPost("/roles", CreateAsync).RequirePermission("usuarios.gerenciar_perfis");
        app.MapPut("/roles/{id:guid}", UpdateAsync).RequirePermission("usuarios.gerenciar_perfis");
        app.MapDelete("/roles/{id:guid}", DeleteAsync).RequirePermission("usuarios.gerenciar_perfis");
    }

    private static async Task<IResult> CatalogAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var catalog = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            var modules = await tx.QueryAsync<ModuleRow>(
                "select m.key, m.name, tm.enabled from modules m join tenant_modules tm on tm.module_key = m.key order by m.sort_order");
            var permissions = (await tx.QueryAsync<PermissionRow>("select key, module_key, name, scoped from permissions order by key")).ToList();
            return modules.Select(m => new CatalogModule(m.Key, m.Name, m.Enabled,
                permissions.Where(p => p.ModuleKey == m.Key).Select(p => new CatalogPermission(p.Key, p.Name, p.Scoped)).ToList())).ToList();
        }, ct);
        return Results.Ok(catalog);
    }

    private static async Task<IResult> ListAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var rows = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), tx => tx.QueryAsync<RoleRow>(
            """
            select r.id, r.name, r.source_template_key, rp.permission_key, rp.scope
              from roles r
              left join role_permissions rp on rp.role_id = r.id
             order by r.name, rp.permission_key
            """), ct);
        var roles = rows.GroupBy(r => (r.Id, r.Name, r.SourceTemplateKey))
            .Select(g => new RoleResponse(g.Key.Id, g.Key.Name, g.Key.SourceTemplateKey,
                g.Where(r => r.PermissionKey is not null).Select(r => new RolePermission(r.PermissionKey!, r.Scope)).ToList()))
            .ToList();
        return Results.Ok(roles);
    }

    private static async Task<IResult> CreateAsync(RoleRequest body, RequestContext request, Database db, CurrentPermissions permissions, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var mine = await permissions.GetAsync(ct);
        var id = Guid.CreateVersion7();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var (name, grants) = await ValidateAsync(tx, body, mine);
            try
            {
                await tx.ExecuteAsync("insert into roles (id, tenant_id, name) values (@id, @tenant, @name)", new { id, tenant, name });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "role.name_taken");
            }
            await InsertPermissionsAsync(tx, tenant, id, grants);
            await WriteAsync(tx, tenant, actor, "roles.create", "roles", id, null, new { name, permissions = grants });
            return 0;
        }, ct);
        return Results.Created($"/roles/{id}", new { id });
    }

    private static async Task<IResult> UpdateAsync(Guid id, RoleRequest body, RequestContext request, Database db, CurrentPermissions permissions, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var mine = await permissions.GetAsync(ct);
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var before = (await tx.QueryAsync<RoleRow>(
                "select r.id, r.name, rp.permission_key, rp.scope from roles r left join role_permissions rp on rp.role_id = r.id where r.id = @id",
                new { id })).ToList();
            if (before.Count == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "role.not_found");

            var (name, grants) = await ValidateAsync(tx, body, mine);
            try
            {
                await tx.ExecuteAsync("update roles set name = @name where id = @id", new { name, id });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "role.name_taken");
            }

            // Remoções por último: sem gerenciar_perfis no meio da transação, o RLS bloquearia as escritas seguintes.
            var current = before.Where(r => r.PermissionKey is not null).ToDictionary(r => r.PermissionKey!, r => r.Scope);
            await InsertPermissionsAsync(tx, tenant, id, grants.Where(g => !current.ContainsKey(g.Key)).ToList());
            foreach (var changed in grants.Where(g => current.TryGetValue(g.Key, out var scope) && scope != g.Scope))
            {
                await tx.ExecuteAsync(
                    "update role_permissions set scope = @scope where role_id = @id and permission_key = @key",
                    new { scope = changed.Scope, id, key = changed.Key });
            }
            var removed = current.Keys.Except(grants.Select(g => g.Key)).ToArray();
            if (removed.Length > 0)
                await tx.ExecuteAsync("delete from role_permissions where role_id = @id and permission_key = any(@removed)", new { id, removed });

            await RoleGuards.EnsureProfileManagerRemainsAsync(tx);
            await WriteAsync(tx, tenant, actor, "roles.update", "roles", id,
                new { name = before[0].Name, permissions = before.Where(r => r.PermissionKey is not null).Select(r => new { r.PermissionKey, r.Scope }) },
                new { name, permissions = grants });
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var name = await tx.QuerySingleOrDefaultAsync<string>("select name from roles where id = @id", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "role.not_found");
            await tx.ExecuteAsync("delete from roles where id = @id", new { id });
            await RoleGuards.EnsureProfileManagerRemainsAsync(tx);
            await WriteAsync(tx, tenant, actor, "roles.delete", "roles", id, new { name }, null);
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<(string Name, IReadOnlyList<RolePermission> Grants)> ValidateAsync(Tx tx, RoleRequest body, PermissionSet mine)
    {
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "role.name_required");

        var inputs = body.Permissions ?? [];
        var keys = inputs.Select(p => p.Key ?? "").ToArray();
        if (keys.Distinct().Count() != keys.Length)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "role.duplicate_permission");

        var catalog = (await tx.QueryAsync<PermissionRow>("select key, module_key, name, scoped from permissions where key = any(@keys)", new { keys }))
            .ToDictionary(p => p.Key);

        foreach (var input in inputs)
        {
            if (!catalog.TryGetValue(input.Key ?? "", out var permission))
                throw new ApiProblem(StatusCodes.Status400BadRequest, "role.invalid_permission");
            if (!Scopes.IsValid(input.Scope))
                throw new ApiProblem(StatusCodes.Status400BadRequest, "role.invalid_scope");
            if (permission.Scoped && input.Scope is null)
                throw new ApiProblem(StatusCodes.Status400BadRequest, "role.scope_required");
            if (!permission.Scoped && input.Scope is not null)
                throw new ApiProblem(StatusCodes.Status400BadRequest, "role.scope_not_allowed");
        }

        if (inputs.Any(p => !mine.CanGrant(p.Key!, p.Scope)))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "role.grant_exceeds_own");

        return (name, inputs.Select(p => new RolePermission(p.Key!, p.Scope)).ToList());
    }

    private static async Task InsertPermissionsAsync(Tx tx, Guid tenant, Guid roleId, IReadOnlyList<RolePermission> grants)
    {
        foreach (var grant in grants)
        {
            await tx.ExecuteAsync(
                "insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @roleId, @key, @scope)",
                new { tenant, roleId, key = grant.Key, scope = grant.Scope });
        }
    }
}
```

- [ ] **Step 4: Registrar no `Program.cs`**

No topo: `using Recorrencia.Api.Roles;`. Depois de `app.MapUserEndpoints();`:

```csharp
app.MapRoleEndpoints();
```

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): manage tenant roles with scoped permissions and last-admin protection"
```

---

### Task 8: Planos e grupos de comissão

**Files:**
- Create: `src/Recorrencia.Api/Commissions/CommissionPlanEndpoints.cs`
- Modify: `src/Recorrencia.Api/Program.cs`
- Test: `tests/Recorrencia.Api.Tests/CommissionPlanTests.cs`

**Interfaces:**
- Consumes: `Audit`, `RequirePermission` (Tasks 4 e 6); triggers e índices de comissão do plano 1.
- Produces:
  - `GET /commission-plans` [regras_comissao.visualizar] → `[{ id, name, effectiveFrom, status, rules: [{ id, type, rate, level, groupId }] }]`, do mais recente para o mais antigo.
  - `POST /commission-plans` [regras_comissao.editar] `{ name, effectiveFrom: "yyyy-MM-dd", rules: [{ type, rate, level?, groupId? }] }` → 201 com o plano em rascunho.
  - `POST /commission-plans/{id}/activate` [regras_comissao.editar] → 200 com o plano ativo.
  - `DELETE /commission-plans/{id}` [regras_comissao.editar] → 204 (só rascunho).
  - `GET /commission-groups` [regras_comissao.visualizar] → `[{ id, name, members: [guid] }]`.
  - `POST /commission-groups` [regras_comissao.editar] `{ name }` → 201 `{ id }`.
  - `PUT /commission-groups/{id}/members` [regras_comissao.editar] `{ userIds }` → 204.
  - Códigos: `plan.name_required`, `plan.invalid_effective_from`, `plan.empty`, `plan.invalid_rule`, `plan.duplicate_rule`, `plan.invalid_group`, `plan.effective_conflict`, `plan.not_found`, `plan.active_immutable`, `group.name_required`, `group.name_taken`, `group.not_found`, `group.invalid_member`.
  - Auditoria: `commission_plans.create`, `commission_plans.activate`, `commission_plans.delete`, `commission_groups.create`, `commission_groups.set_members`.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Api.Tests/CommissionPlanTests.cs`:

```csharp
using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class CommissionPlanTests(ApiFixture api)
{
    public sealed record RuleDto(Guid Id, string Type, decimal Rate, int? Level, Guid? GroupId);
    public sealed record PlanDto(Guid Id, string Name, DateOnly EffectiveFrom, string Status, List<RuleDto> Rules);
    public sealed record GroupDto(Guid Id, string Name, List<Guid> Members);
    public sealed record CreatedDto(Guid Id);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    private static object ThreeLevels(string effectiveFrom = "2026-10-01") => new
    {
        name = "Três níveis",
        effectiveFrom,
        rules = new object[]
        {
            new { type = "own", rate = 0.05m },
            new { type = "upline", rate = 0.03m, level = 1 },
            new { type = "upline", rate = 0.02m, level = 2 },
            new { type = "upline", rate = 0.01m, level = 3 },
        },
    };

    [Fact]
    public async Task Admin_sees_the_provisioned_plan_and_consultor_does_not()
    {
        var s = await api.SeedAsync();
        var plans = await (await LoginAsync(s, "admin")).GetJsonAsync<List<PlanDto>>("/commission-plans");

        var plan = Assert.Single(plans);
        Assert.Equal(("ativo", new DateOnly(2026, 8, 1), 3), (plan.Status, plan.EffectiveFrom, plan.Rules.Count));

        var consultor = await (await LoginAsync(s, "joao")).GetAsync("/commission-plans");
        Assert.Equal(HttpStatusCode.Forbidden, consultor.StatusCode);
    }

    [Fact]
    public async Task New_plan_starts_as_draft_and_can_be_activated()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var created = await admin.PostAsync("/commission-plans", ThreeLevels());
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var draft = (await created.Content.ReadFromJsonAsync<PlanDto>(ApiClient.Json))!;
        Assert.Equal(("rascunho", 4), (draft.Status, draft.Rules.Count));

        var activated = await admin.PostAsync($"/commission-plans/{draft.Id}/activate");
        await ApiClient.ExpectAsync(activated, HttpStatusCode.OK);
        Assert.Equal("ativo", (await activated.Content.ReadFromJsonAsync<PlanDto>(ApiClient.Json))!.Status);

        var delete = await admin.DeleteAsync($"/commission-plans/{draft.Id}");
        Assert.Equal("plan.active_immutable", await ApiClient.CodeAsync(delete));
    }

    [Fact]
    public async Task Two_active_plans_cannot_share_the_effective_date()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var created = await admin.PostAsync("/commission-plans", ThreeLevels("2026-08-01"));
        var draft = (await created.Content.ReadFromJsonAsync<PlanDto>(ApiClient.Json))!;

        var response = await admin.PostAsync($"/commission-plans/{draft.Id}/activate");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("plan.effective_conflict", await ApiClient.CodeAsync(response));
    }

    public static TheoryData<object, string> InvalidPlans => new()
    {
        { new { name = "X", effectiveFrom = "2026-10-15", rules = new[] { new { type = "own", rate = 0.05m } } }, "plan.invalid_effective_from" },
        { new { name = "X", effectiveFrom = "2026-10-01", rules = Array.Empty<object>() }, "plan.empty" },
        { new { name = "X", effectiveFrom = "2026-10-01", rules = new[] { new { type = "upline", rate = 0.02m } } }, "plan.invalid_rule" },
        { new { name = "X", effectiveFrom = "2026-10-01", rules = new[] { new { type = "own", rate = 0m } } }, "plan.invalid_rule" },
        { new { name = "X", effectiveFrom = "2026-10-01", rules = new[] { new { type = "own", rate = 0.12345m } } }, "plan.invalid_rule" },
        { new { name = "X", effectiveFrom = "2026-10-01", rules = new[] { new { type = "bonus", rate = 0.01m } } }, "plan.invalid_rule" },
        { new { name = " ", effectiveFrom = "2026-10-01", rules = new[] { new { type = "own", rate = 0.05m } } }, "plan.name_required" },
    };

    [Theory]
    [MemberData(nameof(InvalidPlans))]
    public async Task Invalid_plans_are_rejected(object body, string code)
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "admin")).PostAsync("/commission-plans", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(code, await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Groups_can_be_created_and_filled()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var created = await admin.PostAsync("/commission-groups", new { name = "Regional Norte" });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        await ApiClient.ExpectAsync(await admin.PutAsync($"/commission-groups/{id}/members", new { userIds = new[] { s.Joao } }), HttpStatusCode.NoContent);

        var groups = await admin.GetJsonAsync<List<GroupDto>>("/commission-groups");
        Assert.Equal(new List<Guid> { s.Joao }, groups.Single(g => g.Id == id).Members);
        Assert.Equal(new List<Guid> { s.Coord }, groups.Single(g => g.Name == "Coordenação").Members);
    }

    [Fact]
    public async Task Group_member_from_another_tenant_is_rejected()
    {
        var s = await api.SeedAsync();
        var other = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var groups = await admin.GetJsonAsync<List<GroupDto>>("/commission-groups");

        var response = await admin.PutAsync($"/commission-groups/{groups[0].Id}/members", new { userIds = new[] { other.Joao } });
        Assert.Equal("group.invalid_member", await ApiClient.CodeAsync(response));
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "FullyQualifiedName~CommissionPlanTests"`
Expected: FAIL (404 nos endpoints).

- [ ] **Step 3: Implementar**

`src/Recorrencia.Api/Commissions/CommissionPlanEndpoints.cs`:

```csharp
using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Commissions;

public static class CommissionPlanEndpoints
{
    public sealed record RuleInput(string? Type, decimal Rate, int? Level, Guid? GroupId);
    public sealed record PlanRequest(string? Name, DateOnly EffectiveFrom, RuleInput[]? Rules);
    public sealed record GroupRequest(string? Name);
    public sealed record MembersRequest(Guid[]? UserIds);
    public sealed record RuleResponse(Guid Id, string Type, decimal Rate, int? Level, Guid? GroupId);
    public sealed record PlanResponse(Guid Id, string Name, DateOnly EffectiveFrom, string Status, IReadOnlyList<RuleResponse> Rules);
    public sealed record GroupResponse(Guid Id, string Name, IReadOnlyList<Guid> Members);

    public sealed class PlanRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public DateOnly EffectiveFrom { get; set; }
        public string Status { get; set; } = "";
        public Guid? RuleId { get; set; }
        public string? Type { get; set; }
        public decimal? Rate { get; set; }
        public int? Level { get; set; }
        public Guid? GroupId { get; set; }
    }

    public sealed class GroupRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public Guid? UserId { get; set; }
    }

    private static readonly string[] RuleTypes = ["own", "upline", "global"];

    public static void MapCommissionPlanEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/commission-plans", ListPlansAsync).RequirePermission("regras_comissao.visualizar");
        app.MapPost("/commission-plans", CreatePlanAsync).RequirePermission("regras_comissao.editar");
        app.MapPost("/commission-plans/{id:guid}/activate", ActivatePlanAsync).RequirePermission("regras_comissao.editar");
        app.MapDelete("/commission-plans/{id:guid}", DeletePlanAsync).RequirePermission("regras_comissao.editar");
        app.MapGet("/commission-groups", ListGroupsAsync).RequirePermission("regras_comissao.visualizar");
        app.MapPost("/commission-groups", CreateGroupAsync).RequirePermission("regras_comissao.editar");
        app.MapPut("/commission-groups/{id:guid}/members", SetMembersAsync).RequirePermission("regras_comissao.editar");
    }

    private const string PlansSql = """
        select p.id, p.name, p.effective_from, p.status,
               r.id as rule_id, r.type, r.rate, r.level, r.group_id
          from commission_plans p
          left join commission_rules r on r.plan_id = p.id
         where (@id::uuid is null or p.id = @id)
         order by p.effective_from desc, p.created_at desc, r.type, r.level
        """;

    private static async Task<IReadOnlyList<PlanResponse>> LoadPlansAsync(Tx tx, Guid? id) =>
        (await tx.QueryAsync<PlanRow>(PlansSql, new { id }))
            .GroupBy(r => (r.Id, r.Name, r.EffectiveFrom, r.Status))
            .Select(g => new PlanResponse(g.Key.Id, g.Key.Name, g.Key.EffectiveFrom, g.Key.Status,
                g.Where(r => r.RuleId is not null)
                 .Select(r => new RuleResponse(r.RuleId!.Value, r.Type!, r.Rate!.Value, r.Level, r.GroupId))
                 .ToList()))
            .ToList();

    private static async Task<IResult> ListPlansAsync(RequestContext request, Database db, CancellationToken ct) =>
        Results.Ok(await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), tx => LoadPlansAsync(tx, null), ct));

    private static async Task<IResult> CreatePlanAsync(PlanRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.name_required");
        if (body.EffectiveFrom.Day != 1)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.invalid_effective_from");
        var rules = body.Rules ?? [];
        if (rules.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.empty");
        if (rules.Any(r => !IsValidRule(r)))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.invalid_rule");

        var id = Guid.CreateVersion7();
        var plan = await db.InTenantAsync(tenant, actor, async tx =>
        {
            await tx.ExecuteAsync(
                "insert into commission_plans (id, tenant_id, name, effective_from) values (@id, @tenant, @name, @effectiveFrom)",
                new { id, tenant, name, effectiveFrom = body.EffectiveFrom });
            try
            {
                foreach (var rule in rules)
                {
                    await tx.ExecuteAsync(
                        """
                        insert into commission_rules (id, tenant_id, plan_id, type, rate, level, group_id)
                        values (@ruleId, @tenant, @id, @type, @rate, @level, @groupId)
                        """,
                        new { ruleId = Guid.CreateVersion7(), tenant, id, type = rule.Type, rate = rule.Rate, level = rule.Level, groupId = rule.GroupId });
                }
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.duplicate_rule");
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.invalid_group");
            }
            await WriteAsync(tx, tenant, actor, "commission_plans.create", "commission_plans", id, null, body);
            return (await LoadPlansAsync(tx, id)).Single();
        }, ct);
        return Results.Created($"/commission-plans/{id}", plan);
    }

    private static bool IsValidRule(RuleInput rule) =>
        rule.Type is not null
        && RuleTypes.Contains(rule.Type)
        && rule.Rate > 0 && rule.Rate <= 1 && rule.Rate == Math.Round(rule.Rate, 4)
        && rule.Type switch
        {
            "own" => rule.Level is null && rule.GroupId is null,
            "upline" => rule.Level is >= 1 && rule.GroupId is null,
            "global" => rule.Level is null && rule.GroupId is not null,
            _ => false,
        };

    private static async Task<IResult> ActivatePlanAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var plan = await db.InTenantAsync(tenant, actor, async tx =>
        {
            int affected;
            try
            {
                affected = await tx.ExecuteAsync(
                    "update commission_plans set status = 'ativo', activated_at = now() where id = @id", new { id });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "plan.effective_conflict");
            }
            if (affected == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "plan.not_found");
            await WriteAsync(tx, tenant, actor, "commission_plans.activate", "commission_plans", id, null, null);
            return (await LoadPlansAsync(tx, id)).Single();
        }, ct);
        return Results.Ok(plan);
    }

    private static async Task<IResult> DeletePlanAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var affected = await tx.ExecuteAsync("delete from commission_plans where id = @id", new { id });
            if (affected == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "plan.not_found");
            await WriteAsync(tx, tenant, actor, "commission_plans.delete", "commission_plans", id, null, null);
            return 0;
        }, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListGroupsAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var rows = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), tx => tx.QueryAsync<GroupRow>(
            """
            select g.id, g.name, m.user_id
              from commission_groups g
              left join commission_group_members m on m.group_id = g.id
             order by g.name, m.user_id
            """), ct);
        return Results.Ok(rows.GroupBy(r => (r.Id, r.Name))
            .Select(g => new GroupResponse(g.Key.Id, g.Key.Name, g.Where(r => r.UserId is not null).Select(r => r.UserId!.Value).ToList()))
            .ToList());
    }

    private static async Task<IResult> CreateGroupAsync(GroupRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "group.name_required");
        var id = Guid.CreateVersion7();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            try
            {
                await tx.ExecuteAsync("insert into commission_groups (id, tenant_id, name) values (@id, @tenant, @name)", new { id, tenant, name });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "group.name_taken");
            }
            await WriteAsync(tx, tenant, actor, "commission_groups.create", "commission_groups", id, null, new { name });
            return 0;
        }, ct);
        return Results.Created($"/commission-groups/{id}", new { id });
    }

    private static async Task<IResult> SetMembersAsync(Guid id, MembersRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var userIds = (body.UserIds ?? []).Distinct().ToArray();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            _ = await tx.QuerySingleOrDefaultAsync<Guid?>("select id from commission_groups where id = @id", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "group.not_found");
            await tx.ExecuteAsync("delete from commission_group_members where group_id = @id", new { id });
            try
            {
                foreach (var userId in userIds)
                {
                    await tx.ExecuteAsync(
                        "insert into commission_group_members (tenant_id, group_id, user_id) values (@tenant, @id, @userId)",
                        new { tenant, id, userId });
                }
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new ApiProblem(StatusCodes.Status400BadRequest, "group.invalid_member");
            }
            await WriteAsync(tx, tenant, actor, "commission_groups.set_members", "commission_groups", id, null, new { userIds });
            return 0;
        }, ct);
        return Results.NoContent();
    }
}
```

- [ ] **Step 4: Registrar no `Program.cs`**

No topo: `using Recorrencia.Api.Commissions;`. Depois de `app.MapRoleEndpoints();`:

```csharp
app.MapCommissionPlanEndpoints();
```

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): manage versioned commission plans and commission groups"
```

---

### Task 9: Consulta de comissões

**Files:**
- Create: `src/Recorrencia.Api/Commissions/CommissionService.cs`, `src/Recorrencia.Api/Commissions/CommissionEndpoints.cs`
- Modify: `src/Recorrencia.Api/Program.cs`
- Test: `tests/Recorrencia.Api.Tests/CommissionTests.cs`

**Interfaces:**
- Consumes: `CommissionEngine`, `CommissionSummary`, `CommissionEntry`, `RuleType` (plano 2); `app.commission_beneficiaries`, `app.commission_plan_for`, `app.commission_source_boletos`, `app.commission_source_paths`, `app.fechamento_status` (plano 1).
- Produces:
  - `CommissionService.ParseCompetencia(string value) → DateOnly` (`yyyy-MM`; senão 400 `commission.invalid_competencia`).
  - `CommissionService.LoadAsync(Tx tx, DateOnly competencia, Guid[] beneficiaries) → Task<LoadedCommissions>`; `record LoadedCommissions(string Status, string Source, IReadOnlyList<CommissionEntry> Entries)`. `Source` é `live` ou `snapshot`. `Status` é o status do fechamento, ou `apuracao` quando ainda não existe.
  - `CommissionService.CalculateLiveAsync(Tx tx, DateOnly competencia, Guid[] beneficiaries) → Task<IReadOnlyList<CommissionEntry>>`.
  - `CommissionService.ToDb(RuleType)` e `CommissionService.ParseRuleType(string)`.
  - `GET /commissions/{competencia}` [comissoes.visualizar] → `{ competencia, status, source, total, beneficiaries: [{ userId, name, total, byRule: [{ ruleType, level, groupId, groupName, rate, base, amount }] }] }`.
  - `GET /commissions/{competencia}/entries?beneficiaryId=` [comissoes.visualizar] → `[{ boletoId, originUserId, originName, ruleType, level, rate, base, amount, associadoNome, placa, pagoEm }]`. Os dados do associado vêm só quando o boleto é visível pelo escopo de `carteira.visualizar`. Beneficiário fora do escopo: 403 `auth.forbidden`.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Api.Tests/CommissionTests.cs`:

```csharp
using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class CommissionTests(ApiFixture api)
{
    public sealed record RuleTotalDto(string RuleType, int? Level, Guid? GroupId, string? GroupName, decimal Rate, decimal Base, decimal Amount);
    public sealed record BeneficiaryDto(Guid UserId, string? Name, decimal Total, List<RuleTotalDto> ByRule);
    public sealed record CommissionsDto(string Competencia, string Status, string Source, decimal Total, List<BeneficiaryDto> Beneficiaries);
    public sealed record EntryDto(Guid BoletoId, Guid OriginUserId, string? OriginName, string RuleType, int? Level, decimal Rate,
        decimal Base, decimal Amount, string? AssociadoNome, string? Placa, DateOnly? PagoEm);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    [Fact]
    public async Task Consultor_sees_own_and_first_level_commission()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CommissionsDto>("/commissions/2026-09");

        Assert.Equal(("2026-09", "apuracao", "live", 1600.00m), (result.Competencia, result.Status, result.Source, result.Total));
        var joao = Assert.Single(result.Beneficiaries);
        Assert.Equal(("João Silva", 1600.00m), (joao.Name, joao.Total));
        Assert.Equal(
            new[]
            {
                new RuleTotalDto("own", null, null, null, 0.07m, 20000m, 1400.00m),
                new RuleTotalDto("upline", 1, null, null, 0.02m, 10000m, 200.00m),
            },
            joao.ByRule);
    }

    [Fact]
    public async Task Coordenador_sees_the_whole_pdf_example()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "coordenacao")).GetJsonAsync<CommissionsDto>("/commissions/2026-09");

        Assert.Equal(3100.00m, result.Total);
        var totals = result.Beneficiaries.ToDictionary(b => b.UserId, b => b.Total);
        Assert.Equal(1600.00m, totals[s.Joao]);
        Assert.Equal(800.00m, totals[s.Maria]);
        Assert.Equal(350.00m, totals[s.Pedro]);
        Assert.Equal(350.00m, totals[s.Coord]);
        Assert.Equal("Coordenação", result.Beneficiaries.Single(b => b.UserId == s.Coord).ByRule.Single().GroupName);
    }

    [Fact]
    public async Task August_matches_the_history()
    {
        var s = await api.SeedAsync();
        var result = await (await LoginAsync(s, "joao")).GetJsonAsync<CommissionsDto>("/commissions/2026-08");
        Assert.Equal(1440.00m, result.Total);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("setembro")]
    public async Task Invalid_competencia_is_rejected(string competencia)
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "joao")).GetAsync($"/commissions/{competencia}");
        Assert.Equal("commission.invalid_competencia", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Consultor_cannot_read_someone_else_entries()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "joao")).GetAsync($"/commissions/2026-09/entries?beneficiaryId={s.Maria}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Entries_hide_associate_data_outside_the_carteira_scope()
    {
        var s = await api.SeedAsync();
        var entries = await (await LoginAsync(s, "joao"))
            .GetJsonAsync<List<EntryDto>>($"/commissions/2026-09/entries?beneficiaryId={s.Joao}");

        Assert.Equal(140, entries.Count);
        Assert.All(entries.Where(e => e.RuleType == "own"), e => Assert.NotNull(e.AssociadoNome));
        var upline = entries.Where(e => e.RuleType == "upline").ToList();
        Assert.Equal(50, upline.Count);
        Assert.All(upline, e =>
        {
            Assert.Equal("Maria Oliveira", e.OriginName);
            Assert.Null(e.AssociadoNome);
            Assert.Null(e.Placa);
        });
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "FullyQualifiedName~CommissionTests"`
Expected: FAIL (404 em `/commissions/...`).

- [ ] **Step 3: Implementar o serviço**

`src/Recorrencia.Api/Commissions/CommissionService.cs`:

```csharp
using System.Globalization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Domain.Commissions;

namespace Recorrencia.Api.Commissions;

public static class CommissionService
{
    public sealed record LoadedCommissions(string Status, string Source, IReadOnlyList<CommissionEntry> Entries);

    public sealed class FechamentoRow
    {
        public Guid FechamentoId { get; set; }
        public string Status { get; set; } = "";
    }

    public sealed class RuleRow
    {
        public Guid Id { get; set; }
        public string Type { get; set; } = "";
        public decimal Rate { get; set; }
        public int? Level { get; set; }
        public Guid? GroupId { get; set; }
    }

    public sealed class SourceBoletoRow
    {
        public Guid BoletoId { get; set; }
        public Guid ParticipanteId { get; set; }
        public decimal Valor { get; set; }
    }

    public sealed class PathRow
    {
        public Guid AncestorId { get; set; }
        public Guid DescendantId { get; set; }
        public int Depth { get; set; }
    }

    public sealed class MemberRow
    {
        public Guid GroupId { get; set; }
        public Guid UserId { get; set; }
    }

    public sealed class SnapshotRow
    {
        public Guid BeneficiarioId { get; set; }
        public Guid BoletoId { get; set; }
        public Guid OrigemParticipanteId { get; set; }
        public Guid RuleId { get; set; }
        public string RuleType { get; set; } = "";
        public int? Level { get; set; }
        public Guid? GroupId { get; set; }
        public decimal Rate { get; set; }
        public decimal Base { get; set; }
        public decimal Valor { get; set; }
    }

    public static DateOnly ParseCompetencia(string value) =>
        DateOnly.TryParseExact(value + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new ApiProblem(StatusCodes.Status400BadRequest, "commission.invalid_competencia");

    public static async Task<LoadedCommissions> LoadAsync(Tx tx, DateOnly competencia, Guid[] beneficiaries)
    {
        var fechamento = await tx.QuerySingleOrDefaultAsync<FechamentoRow>(
            "select fechamento_id, status from app.fechamento_status(@competencia)", new { competencia });

        if (fechamento is { Status: "confirmado" or "provisionado" })
            return new LoadedCommissions(fechamento.Status, "snapshot", await LoadSnapshotAsync(tx, fechamento.FechamentoId, beneficiaries));

        return new LoadedCommissions(fechamento?.Status ?? "apuracao", "live", await CalculateLiveAsync(tx, competencia, beneficiaries));
    }

    public static async Task<IReadOnlyList<CommissionEntry>> CalculateLiveAsync(Tx tx, DateOnly competencia, Guid[] beneficiaries)
    {
        if (beneficiaries.Length == 0)
            return [];
        var planId = await tx.ExecuteScalarAsync<Guid?>("select app.commission_plan_for(@competencia)", new { competencia });
        if (planId is null)
            return [];

        var rules = await tx.QueryAsync<RuleRow>(
            "select id, type, rate, level, group_id from commission_rules where plan_id = @planId", new { planId });
        var boletos = await tx.QueryAsync<SourceBoletoRow>(
            "select boleto_id, participante_id, valor from app.commission_source_boletos(@competencia, @beneficiaries)",
            new { competencia, beneficiaries });
        var paths = await tx.QueryAsync<PathRow>(
            "select ancestor_id, descendant_id, depth from app.commission_source_paths(@competencia, @beneficiaries)",
            new { competencia, beneficiaries });
        var members = await tx.QueryAsync<MemberRow>("select group_id, user_id from commission_group_members");

        var plan = new CommissionPlan(planId.Value, competencia,
            rules.Select(r => new CommissionRule(r.Id, ParseRuleType(r.Type), r.Rate, r.Level, r.GroupId)).ToList());
        var wanted = beneficiaries.ToHashSet();

        return CommissionEngine.Calculate(
                plan,
                boletos.Select(b => new PaidBoleto(b.BoletoId, b.ParticipanteId, b.Valor)),
                paths.Select(p => new HierarchyPath(p.AncestorId, p.DescendantId, p.Depth)),
                members.Select(m => new GroupMembership(m.GroupId, m.UserId)))
            .Where(e => wanted.Contains(e.BeneficiaryId))
            .ToList();
    }

    private static async Task<IReadOnlyList<CommissionEntry>> LoadSnapshotAsync(Tx tx, Guid fechamentoId, Guid[] beneficiaries) =>
        (await tx.QueryAsync<SnapshotRow>(
            """
            select beneficiario_id, boleto_id, origem_participante_id, rule_id, rule_type, level, group_id, rate, base, valor
              from fechamento_detalhes
             where fechamento_id = @fechamentoId and beneficiario_id = any(@beneficiaries)
            """,
            new { fechamentoId, beneficiaries }))
        .Select(r => new CommissionEntry(r.BeneficiarioId, r.BoletoId, r.OrigemParticipanteId, r.RuleId,
            ParseRuleType(r.RuleType), r.Level, r.GroupId, r.Rate, r.Base, r.Valor))
        .ToList();

    public static RuleType ParseRuleType(string value) => value switch
    {
        "own" => RuleType.Own,
        "upline" => RuleType.Upline,
        "global" => RuleType.Global,
        _ => throw new InvalidOperationException($"Tipo de regra desconhecido: {value}"),
    };

    public static string ToDb(RuleType type) => type switch
    {
        RuleType.Own => "own",
        RuleType.Upline => "upline",
        RuleType.Global => "global",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
```

- [ ] **Step 4: Implementar os endpoints**

`src/Recorrencia.Api/Commissions/CommissionEndpoints.cs`:

```csharp
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;
using Recorrencia.Domain.Commissions;

namespace Recorrencia.Api.Commissions;

public static class CommissionEndpoints
{
    public sealed record RuleTotalResponse(RuleType RuleType, int? Level, Guid? GroupId, string? GroupName, decimal Rate, decimal Base, decimal Amount);
    public sealed record BeneficiaryResponse(Guid UserId, string? Name, decimal Total, IReadOnlyList<RuleTotalResponse> ByRule);
    public sealed record CommissionsResponse(string Competencia, string Status, string Source, decimal Total, IReadOnlyList<BeneficiaryResponse> Beneficiaries);
    public sealed record EntryResponse(Guid BoletoId, Guid OriginUserId, string? OriginName, RuleType RuleType, int? Level,
        decimal Rate, decimal Base, decimal Amount, string? AssociadoNome, string? Placa, DateOnly? PagoEm);

    public sealed class NameRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
    }

    public sealed class VisibleBoletoRow
    {
        public Guid Id { get; set; }
        public string AssociadoNome { get; set; } = "";
        public string? Placa { get; set; }
        public DateOnly? PagoEm { get; set; }
    }

    public static void MapCommissionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/commissions/{competencia}", SummaryAsync).RequirePermission("comissoes.visualizar");
        app.MapGet("/commissions/{competencia}/entries", EntriesAsync).RequirePermission("comissoes.visualizar");
    }

    private static async Task<IResult> SummaryAsync(string competencia, RequestContext request, Database db, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var response = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            var beneficiaries = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            var loaded = await CommissionService.LoadAsync(tx, month, beneficiaries);
            var summary = CommissionSummary.ByBeneficiary(loaded.Entries);
            var names = await NamesAsync(tx, summary.Select(s => s.BeneficiaryId));
            var groups = (await tx.QueryAsync<NameRow>("select id, name from commission_groups")).ToDictionary(g => g.Id, g => g.Name);

            return new CommissionsResponse(
                month.ToString("yyyy-MM"),
                loaded.Status,
                loaded.Source,
                summary.Sum(s => s.Total),
                summary.Select(s => new BeneficiaryResponse(
                    s.BeneficiaryId,
                    names.GetValueOrDefault(s.BeneficiaryId),
                    s.Total,
                    s.ByRule.Select(r => new RuleTotalResponse(
                        r.RuleType, r.Level, r.GroupId,
                        r.GroupId is { } groupId ? groups.GetValueOrDefault(groupId) : null,
                        r.Rate, r.Base, r.Amount)).ToList())).ToList());
        }, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> EntriesAsync(string competencia, Guid beneficiaryId, RequestContext request, Database db, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var response = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            var allowed = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            if (!allowed.Contains(beneficiaryId))
                throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");

            var loaded = await CommissionService.LoadAsync(tx, month, [beneficiaryId]);
            var boletoIds = loaded.Entries.Select(e => e.BoletoId).Distinct().ToArray();
            var visible = (await tx.QueryAsync<VisibleBoletoRow>(
                    "select id, associado_nome, placa, pago_em from boletos where id = any(@boletoIds)", new { boletoIds }))
                .ToDictionary(b => b.Id);
            var names = await NamesAsync(tx, loaded.Entries.Select(e => e.OwnerId));

            return loaded.Entries
                .Select(e =>
                {
                    var boleto = visible.GetValueOrDefault(e.BoletoId);
                    return new EntryResponse(e.BoletoId, e.OwnerId, names.GetValueOrDefault(e.OwnerId), e.RuleType, e.Level,
                        e.Rate, e.Base, e.Amount, boleto?.AssociadoNome, boleto?.Placa, boleto?.PagoEm);
                })
                .OrderBy(e => e.RuleType)
                .ThenBy(e => e.OriginName)
                .ThenBy(e => e.BoletoId)
                .ToList();
        }, ct);
        return Results.Ok(response);
    }

    private static async Task<Dictionary<Guid, string>> NamesAsync(Tx tx, IEnumerable<Guid> ids)
    {
        var distinct = ids.Distinct().ToArray();
        return (await tx.QueryAsync<NameRow>("select id, name from users where id = any(@distinct)", new { distinct }))
            .ToDictionary(r => r.Id, r => r.Name);
    }
}
```

- [ ] **Step 5: Registrar no `Program.cs`**

Depois de `app.MapCommissionPlanEndpoints();`:

```csharp
app.MapCommissionEndpoints();
```

- [ ] **Step 6: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): expose live and snapshot commission summaries and entries"
```

---

### Task 10: Fechamento mensal

**Files:**
- Create: `src/Recorrencia.Api/Fechamentos/FechamentoEndpoints.cs`
- Modify: `src/Recorrencia.Api/Program.cs`
- Test: `tests/Recorrencia.Api.Tests/FechamentoTests.cs`

**Interfaces:**
- Consumes: `CommissionService.CalculateLiveAsync`, `CommissionService.ParseCompetencia`, `CommissionService.ToDb` (Task 9); `CurrentPermissions` (Task 4); `Audit` (Task 6).
- Produces:
  - `GET /fechamentos` [fechamento.visualizar] → `[{ competencia, status, confirmadoEm, provisionadoEm }]`, do mais recente para o mais antigo.
  - `POST /fechamentos/{competencia}/confirm` [fechamento.confirmar + fechamento.visualizar] → 200 `{ competencia, status, entries, total }`. Exige escopo `tenant` em `comissoes.visualizar` (senão 403 `fechamento.requires_full_visibility`). Já confirmado: 409 `fechamento.already_confirmed`.
  - `POST /fechamentos/{competencia}/provision` [fechamento.provisionar + fechamento.visualizar] → 200 com o mesmo formato de item da lista. Inexistente: 404 `fechamento.not_found`. Fora de `confirmado`: 409 `fechamento.invalid_transition`.
  - Auditoria: `fechamentos.confirm`, `fechamentos.provision`.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Api.Tests/FechamentoTests.cs`:

```csharp
using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class FechamentoTests(ApiFixture api)
{
    public sealed record ConfirmDto(string Competencia, string Status, int Entries, decimal Total);
    public sealed record FechamentoDto(string Competencia, string Status, DateTimeOffset? ConfirmadoEm, DateTimeOffset? ProvisionadoEm);
    public sealed record CommissionsDto(string Status, string Source, decimal Total);
    public sealed record PlanDto(Guid Id);

    private async Task<ApiClient> LoginAsync(SeededTenant s, string login)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"{login}@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    [Fact]
    public async Task Confirming_freezes_the_month_against_new_plan_versions()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var confirm = await admin.PostAsync("/fechamentos/2026-08/confirm");
        await ApiClient.ExpectAsync(confirm, HttpStatusCode.OK);
        var confirmed = (await confirm.Content.ReadFromJsonAsync<ConfirmDto>(ApiClient.Json))!;
        Assert.Equal(("confirmado", 2740.00m), (confirmed.Status, confirmed.Total));

        var plan = await admin.PostAsync("/commission-plans", new
        {
            name = "Setembro com 10%",
            effectiveFrom = "2026-09-01",
            rules = new object[] { new { type = "own", rate = 0.10m } },
        });
        var planId = (await plan.Content.ReadFromJsonAsync<PlanDto>(ApiClient.Json))!.Id;
        await ApiClient.ExpectAsync(await admin.PostAsync($"/commission-plans/{planId}/activate"), HttpStatusCode.OK);

        var joao = await LoginAsync(s, "joao");
        var august = await joao.GetJsonAsync<CommissionsDto>("/commissions/2026-08");
        Assert.Equal(("confirmado", "snapshot", 1440.00m), (august.Status, august.Source, august.Total));

        var september = await joao.GetJsonAsync<CommissionsDto>("/commissions/2026-09");
        Assert.Equal(("live", 2000.00m), (september.Source, september.Total));
    }

    [Fact]
    public async Task A_month_is_confirmed_only_once()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        await ApiClient.ExpectAsync(await admin.PostAsync("/fechamentos/2026-08/confirm"), HttpStatusCode.OK);

        var again = await admin.PostAsync("/fechamentos/2026-08/confirm");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("fechamento.already_confirmed", await ApiClient.CodeAsync(again));
    }

    [Fact]
    public async Task Consultor_cannot_confirm()
    {
        var s = await api.SeedAsync();
        var response = await (await LoginAsync(s, "joao")).PostAsync("/fechamentos/2026-08/confirm");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Provisioning_follows_confirmation()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        Assert.Equal("fechamento.not_found", await ApiClient.CodeAsync(await admin.PostAsync("/fechamentos/2026-08/provision")));

        await ApiClient.ExpectAsync(await admin.PostAsync("/fechamentos/2026-08/confirm"), HttpStatusCode.OK);
        var provision = await admin.PostAsync("/fechamentos/2026-08/provision");
        await ApiClient.ExpectAsync(provision, HttpStatusCode.OK);

        var list = await (await LoginAsync(s, "joao")).GetJsonAsync<List<FechamentoDto>>("/fechamentos");
        var august = Assert.Single(list);
        Assert.Equal(("2026-08", "provisionado"), (august.Competencia, august.Status));
        Assert.NotNull(august.ConfirmadoEm);
        Assert.NotNull(august.ProvisionadoEm);

        var twice = await admin.PostAsync("/fechamentos/2026-08/provision");
        Assert.Equal("fechamento.invalid_transition", await ApiClient.CodeAsync(twice));
    }
}
```

Conferência: agosto soma 1.440 + 710 + 280 + 310 = 2.740. Com o plano de setembro em 10% só sobre a carteira própria, João recebe 20.000 × 10% = 2.000.

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "FullyQualifiedName~FechamentoTests"`
Expected: FAIL (404 em `/fechamentos/...`).

- [ ] **Step 3: Implementar**

`src/Recorrencia.Api/Fechamentos/FechamentoEndpoints.cs`:

```csharp
using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Commissions;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.Fechamentos;

public static class FechamentoEndpoints
{
    public sealed record ConfirmResponse(string Competencia, string Status, int Entries, decimal Total);
    public sealed record FechamentoResponse(string Competencia, string Status, DateTime? ConfirmadoEm, DateTime? ProvisionadoEm);

    public sealed class FechamentoRow
    {
        public Guid Id { get; set; }
        public DateOnly Competencia { get; set; }
        public string Status { get; set; } = "";
        public DateTime? ConfirmadoEm { get; set; }
        public DateTime? ProvisionadoEm { get; set; }

        public FechamentoResponse ToResponse() => new(Competencia.ToString("yyyy-MM"), Status, ConfirmadoEm, ProvisionadoEm);
    }

    public static void MapFechamentoEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/fechamentos", ListAsync).RequirePermission("fechamento.visualizar");
        app.MapPost("/fechamentos/{competencia}/confirm", ConfirmAsync)
            .RequirePermission("fechamento.confirmar")
            .RequirePermission("fechamento.visualizar");
        app.MapPost("/fechamentos/{competencia}/provision", ProvisionAsync)
            .RequirePermission("fechamento.provisionar")
            .RequirePermission("fechamento.visualizar");
    }

    private const string SelectFechamento = """
        select id, competencia, status, confirmado_em, provisionado_em
          from fechamentos
         where tenant_id = @tenant and competencia = @month
         for update
        """;

    private static async Task<IResult> ListAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var rows = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), tx => tx.QueryAsync<FechamentoRow>(
            "select id, competencia, status, confirmado_em, provisionado_em from fechamentos order by competencia desc"), ct);
        return Results.Ok(rows.Select(r => r.ToResponse()).ToList());
    }

    private static async Task<IResult> ConfirmAsync(string competencia, RequestContext request, Database db,
        CurrentPermissions permissions, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        if ((await permissions.GetAsync(ct)).ScopeOf("comissoes.visualizar") != "tenant")
            throw new ApiProblem(StatusCodes.Status403Forbidden, "fechamento.requires_full_visibility");

        var result = await db.InTenantAsync(tenant, user, async tx =>
        {
            await tx.ExecuteAsync(
                "insert into fechamentos (tenant_id, competencia) values (@tenant, @month) on conflict (tenant_id, competencia) do nothing",
                new { tenant, month });
            var fechamento = await tx.QuerySingleAsync<FechamentoRow>(SelectFechamento, new { tenant, month });
            if (fechamento.Status is "confirmado" or "provisionado")
                throw new ApiProblem(StatusCodes.Status409Conflict, "fechamento.already_confirmed");

            var beneficiaries = await tx.ExecuteScalarAsync<Guid[]>("select app.commission_beneficiaries()") ?? [];
            var entries = await CommissionService.CalculateLiveAsync(tx, month, beneficiaries);

            await tx.ExecuteAsync(
                """
                insert into fechamento_detalhes
                  (tenant_id, fechamento_id, beneficiario_id, origem_participante_id, boleto_id, rule_id, rule_type, level, group_id, rate, base, valor)
                select @tenant, @fechamentoId, d.beneficiario, d.origem, d.boleto, d.rule_id, d.rule_type, d.level, d.group_id, d.rate, d.base, d.valor
                  from unnest(@beneficiarios, @origens, @boletos, @ruleIds, @ruleTypes, @levels, @groups, @rates, @bases, @valores)
                    as d(beneficiario, origem, boleto, rule_id, rule_type, level, group_id, rate, base, valor)
                """,
                new
                {
                    tenant,
                    fechamentoId = fechamento.Id,
                    beneficiarios = entries.Select(e => e.BeneficiaryId).ToArray(),
                    origens = entries.Select(e => e.OwnerId).ToArray(),
                    boletos = entries.Select(e => e.BoletoId).ToArray(),
                    ruleIds = entries.Select(e => e.RuleId).ToArray(),
                    ruleTypes = entries.Select(e => CommissionService.ToDb(e.RuleType)).ToArray(),
                    levels = entries.Select(e => e.Level).ToArray(),
                    groups = entries.Select(e => e.GroupId).ToArray(),
                    rates = entries.Select(e => e.Rate).ToArray(),
                    bases = entries.Select(e => e.Base).ToArray(),
                    valores = entries.Select(e => e.Amount).ToArray(),
                });

            await tx.ExecuteAsync(
                "update fechamentos set status = 'confirmado', confirmado_em = now(), confirmado_por = @user where id = @id",
                new { user, id = fechamento.Id });

            var total = entries.Sum(e => e.Amount);
            await WriteAsync(tx, tenant, user, "fechamentos.confirm", "fechamentos", fechamento.Id, null,
                new { competencia = month, entries = entries.Count, total });
            return new ConfirmResponse(month.ToString("yyyy-MM"), "confirmado", entries.Count, total);
        }, ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> ProvisionAsync(string competencia, RequestContext request, Database db, CancellationToken ct)
    {
        var month = CommissionService.ParseCompetencia(competencia);
        var tenant = request.RequireTenant();
        var user = request.RequireUser();

        var result = await db.InTenantAsync(tenant, user, async tx =>
        {
            var fechamento = await tx.QuerySingleOrDefaultAsync<FechamentoRow>(SelectFechamento, new { tenant, month })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "fechamento.not_found");
            if (fechamento.Status != "confirmado")
                throw new ApiProblem(StatusCodes.Status409Conflict, "fechamento.invalid_transition");

            await tx.ExecuteAsync(
                "update fechamentos set status = 'provisionado', provisionado_em = now(), provisionado_por = @user where id = @id",
                new { user, id = fechamento.Id });
            await WriteAsync(tx, tenant, user, "fechamentos.provision", "fechamentos", fechamento.Id, null, new { competencia = month });
            return (await tx.QuerySingleAsync<FechamentoRow>(SelectFechamento, new { tenant, month })).ToResponse();
        }, ct);
        return Results.Ok(result);
    }
}
```

- [ ] **Step 4: Registrar no `Program.cs`**

No topo: `using Recorrencia.Api.Fechamentos;`. Depois de `app.MapCommissionEndpoints();`:

```csharp
app.MapFechamentoEndpoints();
```

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): confirm monthly closings with frozen commission details and provisioning"
```

---

### Task 11: Plataforma e comandos de linha de comando

**Files:**
- Create: `src/Recorrencia.Api/Platform/PlatformAdmins.cs`, `src/Recorrencia.Api/Platform/PlatformEndpoints.cs`, `src/Recorrencia.Api/Cli/DevSeedCommand.cs`
- Modify: `src/Recorrencia.Api/Program.cs`
- Test: `tests/Recorrencia.Api.Tests/PlatformTests.cs`

**Interfaces:**
- Consumes: `Sessions.CreateForPlatformAdminAsync`, `AuthEndpoints.LoginRequest`, `AuthEndpoints.NormalizeEmail` (Task 4); `TenantResolver.Invalidate` (Task 3); `IEmailSender`, `LinkBuilder` (Task 5); `DevSeed.SeedTenantAsync` (Task 3); `app.find_platform_login`, `app.provision_tenant`, `app.create_invite` (plano 1).
- Produces:
  - `PlatformAdmins.CreateAsync(DataSources sources, PasswordHasher hasher, string email, string password, CancellationToken ct = default) → Task<Guid>` (cria ou redefine a senha).
  - `PlatformAdmins.RunFromCommandLineAsync(IServiceProvider services, string email)` — lê `PLATFORM_ADMIN_PASSWORD`.
  - `DevSeed.RunAsync(IServiceProvider services)` — só em `Development`. Idempotente: não faz nada se o tenant `aprovec` existir. Cria o tenant `aprovec` e `admin@plataforma.local`, todos com a senha `senha-dev-123`.
  - `POST /platform/auth/login` `{ email, password }` → 200 `SessionResponse` (só no host `admin`; em outro host, 404 `tenant.not_found`). Chaves do throttle: `ip:{ip}` e `platform-email:{email}`.
  - `GET /platform/me` → `{ id, email }`.
  - `GET /platform/tenants` → `[{ id, slug, name, status, createdAt }]`.
  - `POST /platform/tenants` `{ slug, name, adminName, adminEmail, planTemplate?, planEffectiveFrom? }` → 201 `{ id, slug }` e convite ao administrador.
  - `POST /platform/tenants/{id}/status` `{ status }` (`ativo` ou `suspenso`) → 204.
  - Linha de comando, sempre a partir de `src/Recorrencia.Api`: `dotnet run --launch-profile http -- seed-dev` e `PLATFORM_ADMIN_PASSWORD=... dotnet run -- create-platform-admin <email>`.

- [ ] **Step 1: Escrever os testes (falhando)**

`tests/Recorrencia.Api.Tests/PlatformTests.cs`:

```csharp
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Platform;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class PlatformTests(ApiFixture api)
{
    public sealed record TenantDto(Guid Id, string Slug, string Name, string Status, DateTimeOffset CreatedAt);
    public sealed record PermissionDto(string Key, string? Scope);
    public sealed record MeDto(List<PermissionDto> Permissions);

    private async Task<ApiClient> PlatformClientAsync()
    {
        var email = $"root-{Guid.NewGuid():N}@plataforma.local";
        await PlatformAdmins.CreateAsync(api.Service<DataSources>(), api.Service<PasswordHasher>(), email, ApiFixture.Password);
        var client = api.Client("admin");
        var response = await client.PostAsync("/platform/auth/login", new { email, password = ApiFixture.Password });
        await ApiClient.ExpectAsync(response, HttpStatusCode.OK);
        client.Token = (await response.Content.ReadFromJsonAsync<SessionDto>(ApiClient.Json))!.Token;
        return client;
    }

    [Fact]
    public async Task Platform_admin_lists_tenants()
    {
        var s = await api.SeedAsync();
        var tenants = await (await PlatformClientAsync()).GetJsonAsync<List<TenantDto>>("/platform/tenants");
        Assert.Contains(tenants, t => t.Slug == s.Slug && t.Status == "ativo");
    }

    [Fact]
    public async Task Sessions_do_not_cross_between_platform_and_tenants()
    {
        var s = await api.SeedAsync();
        var platform = await PlatformClientAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var tenantWithPlatformToken = api.Client(s.Slug);
        tenantWithPlatformToken.Token = platform.Token;
        Assert.Equal("auth.tenant_mismatch", await ApiClient.CodeAsync(await tenantWithPlatformToken.GetAsync("/me")));

        var platformWithTenantToken = api.Client("admin");
        platformWithTenantToken.Token = joao.Token;
        Assert.Equal("auth.tenant_mismatch", await ApiClient.CodeAsync(await platformWithTenantToken.GetAsync("/platform/tenants")));

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client("admin").GetAsync("/platform/tenants")).StatusCode);
    }

    [Fact]
    public async Task New_tenant_admin_receives_an_invite_and_gets_full_permissions()
    {
        var platform = await PlatformClientAsync();
        var slug = Seed.UniqueSlug();
        var adminEmail = $"dona@{slug}.local";

        var created = await platform.PostAsync("/platform/tenants", new
        {
            slug,
            name = "Nova Associação",
            adminName = "Dona da Empresa",
            adminEmail,
            planTemplate = "aprovec",
            planEffectiveFrom = "2026-10-01",
        });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);

        var sent = api.Emails.LastTo(adminEmail);
        Assert.NotNull(sent);
        Assert.Contains($"http://{slug}.localhost:3000/definir-senha/", sent.Body);

        var admin = api.Client(slug);
        var session = await admin.PostAsync("/auth/set-password", new { token = CapturingEmailSender.ExtractToken(sent.Body), password = "senha-da-dona-1" });
        await ApiClient.ExpectAsync(session, HttpStatusCode.OK);
        admin.Token = (await session.Content.ReadFromJsonAsync<SessionDto>(ApiClient.Json))!.Token;

        var me = await admin.GetJsonAsync<MeDto>("/me");
        Assert.Equal(14, me.Permissions.Count);
    }

    [Theory]
    [InlineData("Maiuscula", HttpStatusCode.BadRequest, "tenant.invalid_slug")]
    [InlineData("admin", HttpStatusCode.BadRequest, "tenant.invalid_slug")]
    public async Task Invalid_slugs_are_rejected(string slug, HttpStatusCode status, string code)
    {
        var response = await (await PlatformClientAsync()).PostAsync("/platform/tenants",
            new { slug, name = "X", adminName = "X", adminEmail = "x@x.local" });
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Duplicate_slug_is_rejected()
    {
        var s = await api.SeedAsync();
        var response = await (await PlatformClientAsync()).PostAsync("/platform/tenants",
            new { slug = s.Slug, name = "X", adminName = "X", adminEmail = "x@x.local" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("tenant.slug_taken", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Suspended_tenant_disappears_until_reactivated()
    {
        var s = await api.SeedAsync();
        var platform = await PlatformClientAsync();

        await ApiClient.ExpectAsync(await platform.PostAsync($"/platform/tenants/{s.TenantId}/status", new { status = "suspenso" }), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client(s.Slug).GetAsync("/tenant")).StatusCode);

        await ApiClient.ExpectAsync(await platform.PostAsync($"/platform/tenants/{s.TenantId}/status", new { status = "ativo" }), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.OK, (await api.Client(s.Slug).GetAsync("/tenant")).StatusCode);
    }
}
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "FullyQualifiedName~PlatformTests"`
Expected: FAIL de compilação (`PlatformAdmins` não existe).

- [ ] **Step 3: Implementar os administradores da plataforma**

`src/Recorrencia.Api/Platform/PlatformAdmins.cs`:

```csharp
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
```

- [ ] **Step 4: Implementar os endpoints de plataforma**

`src/Recorrencia.Api/Platform/PlatformEndpoints.cs`:

```csharp
using System.Net.Mail;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Npgsql;
using Recorrencia.Api.Auth;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Email;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Platform;

public static partial class PlatformEndpoints
{
    public sealed record CreateTenantRequest(string? Slug, string? Name, string? AdminName, string? AdminEmail, string? PlanTemplate, DateOnly? PlanEffectiveFrom);
    public sealed record StatusRequest(string? Status);

    public sealed class PlatformLoginRow
    {
        public Guid AdminId { get; set; }
        public string PasswordHash { get; set; } = "";
    }

    public sealed class TenantRow
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    public sealed class AdminRow
    {
        public Guid Id { get; set; }
        public string Email { get; set; } = "";
    }

    private static readonly string[] ReservedSlugs = ["admin", "www", "api"];

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex SlugPattern();

    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/platform/auth/login", LoginAsync);
        app.MapGet("/platform/me", MeAsync).RequirePlatformAdmin();
        app.MapGet("/platform/tenants", ListTenantsAsync).RequirePlatformAdmin();
        app.MapPost("/platform/tenants", CreateTenantAsync).RequirePlatformAdmin();
        app.MapPost("/platform/tenants/{id:guid}/status", SetStatusAsync).RequirePlatformAdmin();
    }

    private static async Task<IResult> LoginAsync(AuthEndpoints.LoginRequest body, RequestContext request, Database db, PasswordHasher hasher,
        LoginThrottle throttle, IOptions<AuthOptions> options, TimeProvider time, CancellationToken ct)
    {
        if (!request.IsPlatformHost)
            throw new ApiProblem(StatusCodes.Status404NotFound, "tenant.not_found");

        var email = AuthEndpoints.NormalizeEmail(body.Email);
        var password = body.Password ?? "";
        var ipKey = $"ip:{request.ClientIp}";
        var emailKey = $"platform-email:{email}";
        if (throttle.IsBlocked(ipKey, emailKey))
            throw new ApiProblem(StatusCodes.Status429TooManyRequests, "auth.too_many_attempts");

        var login = await db.AnonymousAsync(tx => tx.QuerySingleOrDefaultAsync<PlatformLoginRow>(
            "select admin_id, password_hash from app.find_platform_login(@email)", new { email }), ct);
        var valid = login is not null ? hasher.Verify(password, login.PasswordHash) : hasher.VerifyAgainstDummy(password);
        if (!valid)
        {
            throttle.RecordFailure(ipKey, emailKey);
            throw new ApiProblem(StatusCodes.Status401Unauthorized, "auth.invalid_credentials");
        }

        throttle.Reset(emailKey);
        return Results.Ok(await Sessions.CreateForPlatformAdminAsync(db, request, login!.AdminId, options.Value, time, ct));
    }

    private static async Task<IResult> MeAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var id = request.RequirePlatformAdmin();
        var admin = await db.AsPlatformAsync(null, tx => tx.QuerySingleAsync<AdminRow>(
            "select id, email from platform_admins where id = @id", new { id }), ct);
        return Results.Ok(new { admin.Id, admin.Email });
    }

    private static async Task<IResult> ListTenantsAsync(Database db, CancellationToken ct) =>
        Results.Ok(await db.AsPlatformAsync(null, tx => tx.QueryAsync<TenantRow>(
            "select id, slug, name, status, created_at from tenants order by name"), ct));

    private static async Task<IResult> CreateTenantAsync(CreateTenantRequest body, Database db, TenantResolver resolver,
        IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var slug = body.Slug?.Trim() ?? "";
        if (!SlugPattern().IsMatch(slug) || ReservedSlugs.Contains(slug))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "tenant.invalid_slug");
        var name = body.Name?.Trim() ?? "";
        var adminName = body.AdminName?.Trim() ?? "";
        if (name.Length == 0 || adminName.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "tenant.invalid_request");
        var adminEmail = AuthEndpoints.NormalizeEmail(body.AdminEmail);
        if (!MailAddress.TryCreate(adminEmail, out _))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_email");
        if (body.PlanTemplate is not null && body.PlanEffectiveFrom is not { Day: 1 })
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plan.invalid_effective_from");

        var token = Tokens.New();
        var created = await db.AsPlatformAsync(null, async tx =>
        {
            (Guid TenantId, Guid AdminUserId) ids;
            try
            {
                ids = await tx.QuerySingleAsync<(Guid, Guid)>(
                    """
                    select tenant_id, admin_user_id
                      from app.provision_tenant(@slug, @name, @adminName, @adminEmail, @planTemplate, @effectiveFrom::date)
                    """,
                    new { slug, name, adminName, adminEmail, planTemplate = body.PlanTemplate, effectiveFrom = body.PlanEffectiveFrom?.ToString("yyyy-MM-dd") });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "tenant.slug_taken");
            }
            await tx.ExecuteAsync("select set_config('app.tenant_id', @tenant, true)", new { tenant = ids.TenantId.ToString() });
            await tx.ExecuteAsync("select app.create_invite(@admin, @hash, 'convite', @ttl)",
                new { admin = ids.AdminUserId, hash = Tokens.Hash(token), ttl = options.Value.InviteTtlSeconds });
            return ids;
        }, ct);

        resolver.Invalidate(slug);
        await email.SendAsync(adminEmail, "Sua empresa foi criada",
            $"""
            A empresa {name} foi criada na plataforma.

            Defina sua senha de administrador em: {links.SetPassword(slug, token)}

            O link vale por 72 horas.
            """, ct);
        return Results.Created($"/platform/tenants/{created.TenantId}", new { id = created.TenantId, slug });
    }

    private static async Task<IResult> SetStatusAsync(Guid id, StatusRequest body, Database db, TenantResolver resolver, CancellationToken ct)
    {
        if (body.Status is not ("ativo" or "suspenso"))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "tenant.invalid_request");
        var slug = await db.AsPlatformAsync(null, tx => tx.QuerySingleOrDefaultAsync<string>(
            "update tenants set status = @status where id = @id returning slug", new { status = body.Status, id }), ct)
            ?? throw new ApiProblem(StatusCodes.Status404NotFound, "tenant.not_found");
        resolver.Invalidate(slug);
        return Results.NoContent();
    }
}
```

- [ ] **Step 5: Implementar o `seed-dev`**

`src/Recorrencia.Api/Cli/DevSeedCommand.cs`:

```csharp
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
```

- [ ] **Step 6: Registrar no `Program.cs`**

No topo: `using Recorrencia.Api.Cli;` e `using Recorrencia.Api.Platform;`. Logo depois de `var app = builder.Build();`:

```csharp
if (args is ["seed-dev"])
{
    await DevSeed.RunAsync(app.Services);
    return;
}

if (args is ["create-platform-admin", var platformAdminEmail])
{
    await PlatformAdmins.RunFromCommandLineAsync(app.Services, platformAdminEmail);
    return;
}
```

Depois de `app.MapFechamentoEndpoints();`:

```csharp
app.MapPlatformEndpoints();
```

- [ ] **Step 7: Rodar a suíte completa**

Run: `dotnet test`
Expected: PASS em todos os projetos de teste (Db, Domain, Api).

- [ ] **Step 8: Verificar os comandos contra o banco do compose**

```bash
docker compose up -d db
set -a; source .env; set +a
dotnet run --project src/Recorrencia.Db -- bootstrap
dotnet run --project src/Recorrencia.Db -- migrate
(cd src/Recorrencia.Api && dotnet run --launch-profile http -- seed-dev)
```

O comando roda dentro de `src/Recorrencia.Api` porque o ASP.NET usa o diretório atual como content root: a partir da raiz, o `appsettings.Development.json` não seria encontrado. O perfil `http` já define `ASPNETCORE_ENVIRONMENT=Development`.

Expected: a mensagem "Dados de desenvolvimento criados." com os logins. Rodar `seed-dev` de novo imprime "Os dados de desenvolvimento já existem.".

- [ ] **Step 9: Commit**

```bash
git add src/Recorrencia.Api tests/Recorrencia.Api.Tests
git commit -m "feat(api): add platform login, tenant provisioning and CLI commands"
```
