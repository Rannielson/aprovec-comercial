# Integração Hinova (Configurações/Integrações) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a tenant admin store Hinova SGA v2 credentials and map Hinova voluntários to existing APROVEC users, under a new "Configurações > Integrações" area.

**Architecture:** New DB migration (RBAC permission + 2 tenant-scoped tables with RLS), a new `IHinovaClient` abstraction (real HTTP implementation + a dev/E2E fake selected by config) wrapping the real Hinova endpoints already verified against production, new API endpoints under `.RequirePermission("integracoes.gerenciar")`, and a new admin-only page in the Next.js BFF.

**Tech Stack:** C# ASP.NET Core minimal APIs, Dapper/Npgsql, Postgres RLS, Next.js App Router (Server Components + Server Actions), xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-24-hinova-integracao-design.md`

## Global Constraints

- Credentials (`usuario`, `senha`, `token_sga`) are encrypted at rest with AES-256-GCM in the API layer; the key comes from the `Hinova:EncryptionKey` config (env var `Hinova__EncryptionKey`), a base64-encoded 32-byte value — never committed to `appsettings.json`.
- `token_usuario` (the Hinova session token) is never persisted — every operation that talks to Hinova re-authenticates first.
- The Hinova `cooperativas[]` field and `codigo_classificacao` are not modeled anywhere in this plan — only `codigo_voluntario`, `nome`, `cpf` are read.
- A mapping is unique in both directions: one `user_id` maps to at most one `codigo_voluntario`, and vice versa (enforced by DB constraints, surfaced as HTTP 409 `hinova.vinculo_duplicado`).
- `Buscar Voluntário` and `Cadastrar Voluntário` are out of scope for this plan — do not implement them.
- Every new endpoint requires the permission `integracoes.gerenciar` (added to the RBAC catalog in Task 1); no other role template is granted it explicitly — it reaches `administrador` only through the existing catch-all in `0006_rbac_catalog.sql:90-91`.
- No automated test may call the real Hinova API. All API-layer tests substitute `IHinovaClient` with a test double.

---

## Task 1: DB migration — RBAC permission + `hinova_credenciais` / `hinova_voluntario_mapping` tables

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0014_hinova_integracao.sql`
- Modify: `tests/Recorrencia.Db.Tests/RbacTests.cs:29-30`
- Modify: `tests/Recorrencia.Db.Tests/ProvisioningTests.cs` (`Provisioning_creates_modules_roles_admin_and_plan`)
- Modify: `tests/Recorrencia.Api.Tests/RoleTests.cs` (`Catalog_lists_modules_and_permissions`)
- Create: `tests/Recorrencia.Db.Tests/HinovaIntegracaoTests.cs`

**Interfaces:**
- Produces: table `hinova_credenciais(tenant_id pk, usuario_enc bytea, senha_enc bytea, token_sga_enc bytea, updated_at, updated_by)`; table `hinova_voluntario_mapping(tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_at, mapped_by)`, pk `(tenant_id, user_id)`, unique `(tenant_id, codigo_voluntario)`; permission key `integracoes.gerenciar` (unscoped) usable by later tasks via `app.has_permission('integracoes.gerenciar')`.
- Consumes: `tenants(id)`, `users(id)`, `app.current_tenant()`, `app.has_permission(text)` — all already defined in earlier migrations.

- [ ] **Step 1: Write the migration**

Create `src/Recorrencia.Db/Scripts/0014_hinova_integracao.sql`:

```sql
insert into modules (key, name, sort_order) values
  ('integracoes', 'Integrações', 7);

insert into permissions (key, module_key, name, scoped) values
  ('integracoes.gerenciar', 'integracoes', 'Gerenciar integrações', false);

-- 0006_rbac_catalog.sql's catch-all (`insert into role_template_permissions select
-- 'administrador', key, ... from permissions;`) is a one-time backfill that ran against
-- the 14 permissions that existed when 0006 executed — it does not retroactively cover
-- permissions inserted by later migrations. Every new unscoped permission needs its own
-- explicit grant to 'administrador' here.
insert into role_template_permissions (template_key, permission_key, scope) values
  ('administrador', 'integracoes.gerenciar', null);

create table hinova_credenciais (
  tenant_id uuid primary key references tenants (id),
  usuario_enc bytea not null,
  senha_enc bytea not null,
  token_sga_enc bytea not null,
  updated_at timestamptz not null default now(),
  updated_by uuid not null references users (id)
);

create table hinova_voluntario_mapping (
  tenant_id uuid not null references tenants (id),
  user_id uuid not null references users (id),
  codigo_voluntario text not null,
  nome_hinova text not null,
  cpf_hinova text not null,
  mapped_at timestamptz not null default now(),
  mapped_by uuid not null references users (id),
  primary key (tenant_id, user_id),
  unique (tenant_id, codigo_voluntario)
);

do $$
declare
  t text;
begin
  foreach t in array array['hinova_credenciais', 'hinova_voluntario_mapping']
  loop
    execute format('alter table %I enable row level security', t);
    execute format('alter table %I force row level security', t);
    execute format(
      'create policy %I on %I as restrictive for all to app_user using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant())',
      t || '_tenant_isolation', t);
  end loop;
end
$$;

create policy hinova_credenciais_access on hinova_credenciais for all to app_user
  using ((select app.has_permission('integracoes.gerenciar')))
  with check ((select app.has_permission('integracoes.gerenciar')));

create policy hinova_voluntario_mapping_access on hinova_voluntario_mapping for all to app_user
  using ((select app.has_permission('integracoes.gerenciar')))
  with check ((select app.has_permission('integracoes.gerenciar')));

grant select, insert, update on hinova_credenciais to app_user;
grant select, insert, delete on hinova_voluntario_mapping to app_user;
```

- [ ] **Step 2: Update the existing hardcoded-catalog-count tests**

Adding a permission changes three counts that other tests hardcode. All three must be updated in this task, in the same commit as the migration — a partial update leaves the suite red.

In `tests/Recorrencia.Db.Tests/RbacTests.cs` (replaces the two `14`s on the existing lines 29-30; line 28 is unchanged):

```csharp
        Assert.Equal(7, rows.Count(r => r.Template == "coordenador"));
        Assert.Equal(15, await conn.ExecuteScalarAsync<int>("select count(*) from permissions"));
        Assert.Equal(15, rows.Count(r => r.Template == "administrador"));
```

In `tests/Recorrencia.Db.Tests/ProvisioningTests.cs` (replaces the existing lines inside `Provisioning_creates_modules_roles_admin_and_plan`): `provision_tenant` enables every row of `modules` into `tenant_modules`, and copies every `role_template_permissions` row into the new tenant's `role_permissions` — so the new module and the new `administrador` grant both flow through here too.

```csharp
        Assert.Equal(7, await _seed.ScalarAsync<int>("select count(*)::int from tenant_modules where tenant_id = @t and enabled", new { t }));
        Assert.Equal(3, await _seed.ScalarAsync<int>("select count(*)::int from roles where tenant_id = @t", new { t }));
        Assert.Equal(26, await _seed.ScalarAsync<int>("select count(*)::int from role_permissions where tenant_id = @t", new { t }));
```

(Only the `6` → `7` and `25` → `26` change; the `roles` count of `3` is unchanged.)

In `tests/Recorrencia.Api.Tests/RoleTests.cs` (replaces the existing two lines inside `Catalog_lists_modules_and_permissions`): the `/permissions` catalog is ordered by `modules.sort_order`, and `integracoes` was inserted with `sort_order = 7` — last.

```csharp
        Assert.Equal(new[] { "carteira", "comissoes", "fechamento", "estrutura", "regras_comissao", "usuarios", "integracoes" }, catalog.Select(m => m.Key));
        Assert.Equal(15, catalog.Sum(m => m.Permissions.Count));
```

- [ ] **Step 3: Run the DB and API test suites to confirm the migration applies and every updated count passes**

Run: `dotnet test tests/Recorrencia.Db.Tests`
Expected: all tests pass, including the updated `Catalog_and_templates_are_seeded` and `Provisioning_creates_modules_roles_admin_and_plan`.

Run: `dotnet test tests/Recorrencia.Api.Tests --filter RoleTests`
Expected: all tests pass, including the updated `Catalog_lists_modules_and_permissions`.

- [ ] **Step 4: Write the new DB tests**

Create `tests/Recorrencia.Db.Tests/HinovaIntegracaoTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class HinovaIntegracaoTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    // provision_tenant leaves the admin as status = 'convidado' (no password set yet) —
    // app.effective_permissions() only considers users with status = 'ativo', so every
    // permission check for a freshly-provisioned admin fails until activated. Same fix
    // RlsScenario.CreateAsync already applies for the same reason.
    private Task ActivateAsync(Guid userId) =>
        _seed.ExecAsync("update users set status = 'ativo', password_hash = 'hash-de-teste' where id = @userId", new { userId });

    [Fact]
    public async Task Administrador_can_write_and_read_credenciais()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);

        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
            values (@tenant, @bytes, @bytes, @bytes, @admin)
            """,
            new { tenant, bytes = new byte[] { 1, 2, 3 }, admin }, tx));

        var found = await db.AsAppUserAsync(tenant, admin, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from hinova_credenciais where tenant_id = @tenant", new { tenant }, tx));
        Assert.Equal(1, found);
    }

    [Fact]
    public async Task User_without_the_permission_cannot_write_credenciais()
    {
        var t = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(t);
        var u = await _seed.UserAsync(t, "Sem integrações");
        await _seed.AssignRoleAsync(t, u, await _seed.RoleAsync(t, "Sem integrações", ("usuarios.convidar", null)));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, u, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
            values (@t, @bytes, @bytes, @bytes, @u)
            """,
            new { t, bytes = new byte[] { 1 }, u }, tx)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task User_without_the_permission_sees_no_rows_on_select()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
            values (@tenant, @bytes, @bytes, @bytes, @admin)
            """,
            new { tenant, bytes = new byte[] { 1 }, admin }, tx));

        var u = await _seed.UserAsync(tenant, "Sem integrações");
        await _seed.AssignRoleAsync(tenant, u, await _seed.RoleAsync(tenant, "Sem integrações", ("usuarios.convidar", null)));

        var count = await db.AsAppUserAsync(tenant, u, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from hinova_credenciais where tenant_id = @tenant", new { tenant }, tx));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Tenant_isolation_hides_another_tenants_credenciais()
    {
        var (tenantA, adminA) = await _seed.ProvisionAsync();
        var (tenantB, adminB) = await _seed.ProvisionAsync();
        await ActivateAsync(adminA);
        await ActivateAsync(adminB);
        await db.AsAppUserAsync(tenantB, adminB, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
            values (@tenantB, @bytes, @bytes, @bytes, @adminB)
            """,
            new { tenantB, bytes = new byte[] { 1 }, adminB }, tx));

        var count = await db.AsAppUserAsync(tenantA, adminA, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from hinova_credenciais where tenant_id = @tenantB", new { tenantB }, tx));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A_user_cannot_be_mapped_to_two_voluntarios()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var vendedor = await _seed.UserAsync(tenant, "Vendedor");

        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @vendedor, '101', 'Nome Hinova', '11111111111', @admin)
            """,
            new { tenant, vendedor, admin }, tx));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @vendedor, '102', 'Outro Nome', '22222222222', @admin)
            """,
            new { tenant, vendedor, admin }, tx)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task A_codigo_voluntario_cannot_be_mapped_to_two_users()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var vendedorA = await _seed.UserAsync(tenant, "Vendedor A");
        var vendedorB = await _seed.UserAsync(tenant, "Vendedor B");

        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @vendedorA, '101', 'Nome Hinova', '11111111111', @admin)
            """,
            new { tenant, vendedorA, admin }, tx));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @vendedorB, '101', 'Nome Hinova', '11111111111', @admin)
            """,
            new { tenant, vendedorB, admin }, tx)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }
}
```

- [ ] **Step 5: Run the new tests**

Run: `dotnet test tests/Recorrencia.Db.Tests --filter HinovaIntegracaoTests`
Expected: all 6 tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Db/Scripts/0014_hinova_integracao.sql tests/Recorrencia.Db.Tests/RbacTests.cs tests/Recorrencia.Db.Tests/ProvisioningTests.cs tests/Recorrencia.Api.Tests/RoleTests.cs tests/Recorrencia.Db.Tests/HinovaIntegracaoTests.cs
git commit -m "feat(db): add hinova_credenciais/hinova_voluntario_mapping tables and integracoes.gerenciar permission"
```

---

## Task 2: `AesGcmCipher` + `HinovaOptions`

**Files:**
- Create: `src/Recorrencia.Api/Security/AesGcmCipher.cs`
- Modify: `src/Recorrencia.Api/Options.cs`
- Create: `tests/Recorrencia.Api.Tests/AesGcmCipherTests.cs`

**Interfaces:**
- Produces: `AesGcmCipher.Encrypt(string) -> byte[]`, `AesGcmCipher.Decrypt(byte[]) -> string`, both consumed by Task 5's endpoints. `HinovaOptions { EncryptionKey, UseFake }` bound from config section `Hinova`, consumed by Task 4's `Program.cs` wiring.
- Consumes: nothing beyond the BCL and `Microsoft.Extensions.Options`.

- [ ] **Step 1: Add `HinovaOptions`**

In `src/Recorrencia.Api/Options.cs`, append:

```csharp

public sealed class HinovaOptions
{
    public string EncryptionKey { get; set; } = "";
    public bool UseFake { get; set; }
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/Recorrencia.Api.Tests/AesGcmCipherTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using Recorrencia.Api;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Tests;

public class AesGcmCipherTests
{
    private static AesGcmCipher NewCipher() =>
        new(Options.Create(new HinovaOptions { EncryptionKey = Convert.ToBase64String(new byte[32]) }));

    [Fact]
    public void Round_trips_a_plaintext_value()
    {
        var cipher = NewCipher();
        var encrypted = cipher.Encrypt("usuario-secreto");
        Assert.Equal("usuario-secreto", cipher.Decrypt(encrypted));
    }

    [Fact]
    public void Two_encryptions_of_the_same_value_produce_different_bytes()
    {
        var cipher = NewCipher();
        var a = cipher.Encrypt("mesmo-valor");
        var b = cipher.Encrypt("mesmo-valor");
        Assert.False(a.SequenceEqual(b)); // nonce aleatório por chamada
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter AesGcmCipherTests`
Expected: FAIL — `AesGcmCipher` does not exist yet.

- [ ] **Step 4: Implement `AesGcmCipher`**

Create `src/Recorrencia.Api/Security/AesGcmCipher.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Recorrencia.Api.Security;

public sealed class AesGcmCipher
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _key;

    public AesGcmCipher(IOptions<HinovaOptions> options) => _key = Convert.FromBase64String(options.Value.EncryptionKey);

    public byte[] Encrypt(string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
        return [.. nonce, .. cipherBytes, .. tag];
    }

    public string Decrypt(byte[] combined)
    {
        var nonce = combined[..NonceBytes];
        var tag = combined[^TagBytes..];
        var cipherBytes = combined[NonceBytes..^TagBytes];
        var plainBytes = new byte[cipherBytes.Length];
        using var aes = new AesGcm(_key, TagBytes);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter AesGcmCipherTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Api/Security/AesGcmCipher.cs src/Recorrencia.Api/Options.cs tests/Recorrencia.Api.Tests/AesGcmCipherTests.cs
git commit -m "feat(api): add AES-256-GCM cipher and HinovaOptions"
```

---

## Task 3: `IHinovaClient` + real `HinovaClient`

**Files:**
- Create: `src/Recorrencia.Api/Integracoes/IHinovaClient.cs`
- Create: `src/Recorrencia.Api/Integracoes/HinovaClient.cs`
- Create: `tests/Recorrencia.Api.Tests/HinovaClientTests.cs`

**Interfaces:**
- Produces: `IHinovaClient` (`Task<string> AutenticarAsync(usuario, senha, tokenSga, ct)`, `Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(tokenUsuario, ct)`), `HinovaVoluntario(string Codigo, string Nome, string Cpf)`, `HinovaAuthException` — consumed by Task 4 (DI wiring), Task 5 and Task 6 (endpoints).
- Consumes: nothing new; uses a plain `HttpClient` injected via constructor (wired in Task 4).

The request/response shapes below were confirmed against the real Hinova SGA v2 API in this session (`POST /usuario/autenticar` and `GET /listar/voluntario/:situacao/:pagina`), not guessed from documentation alone.

- [ ] **Step 1: Define the interface and models**

Create `src/Recorrencia.Api/Integracoes/IHinovaClient.cs`:

```csharp
namespace Recorrencia.Api.Integracoes;

public interface IHinovaClient
{
    Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct);
    Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct);
}

public sealed record HinovaVoluntario(string Codigo, string Nome, string Cpf);

public sealed class HinovaAuthException() : Exception("Falha ao autenticar na Hinova.");
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Recorrencia.Api.Tests/HinovaClientTests.cs`:

```csharp
using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

public class HinovaClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static (HinovaClient Client, StubHandler Handler) NewClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.hinova.com.br/api/sga/v2/") };
        return (new HinovaClient(http), handler);
    }

    [Fact]
    public async Task Autenticar_sends_the_static_token_and_returns_token_usuario()
    {
        var (client, handler) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { mensagem = "OK", token_usuario = "abc123" }),
        });

        var token = await client.AutenticarAsync("usuario", "senha", "token-estatico", CancellationToken.None);

        Assert.Equal("abc123", token);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("token-estatico", handler.LastRequest.Headers.Authorization!.Parameter);
        Assert.Equal("https://api.hinova.com.br/api/sga/v2/usuario/autenticar", handler.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task Autenticar_throws_HinovaAuthException_on_a_non_success_response()
    {
        var (client, _) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<HinovaAuthException>(() =>
            client.AutenticarAsync("usuario", "senha", "token-estatico", CancellationToken.None));
    }

    [Fact]
    public async Task ListarVoluntarios_sends_the_user_token_and_parses_the_array()
    {
        var (client, handler) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new[]
            {
                new { codigo_voluntario = "101", nome = "Ana Paula", cpf = "11122233344" },
                new { codigo_voluntario = "102", nome = "Bruno Costa", cpf = "22233344455" },
            }),
        });

        var voluntarios = await client.ListarVoluntariosAsync("token-usuario", CancellationToken.None);

        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("token-usuario", handler.LastRequest.Headers.Authorization!.Parameter);
        Assert.Equal(2, voluntarios.Count);
        Assert.Equal(new HinovaVoluntario("101", "Ana Paula", "11122233344"), voluntarios[0]);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter HinovaClientTests`
Expected: FAIL — `HinovaClient` does not exist yet.

- [ ] **Step 4: Implement `HinovaClient`**

Create `src/Recorrencia.Api/Integracoes/HinovaClient.cs`:

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Recorrencia.Api.Integracoes;

public sealed class HinovaClient(HttpClient http) : IHinovaClient
{
    public async Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "usuario/autenticar")
        {
            Content = JsonContent.Create(new { usuario, senha }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenSga);

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HinovaAuthException();

        var body = await response.Content.ReadFromJsonAsync<AutenticarResponse>(cancellationToken: ct);
        return body?.TokenUsuario ?? throw new HinovaAuthException();
    }

    public async Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "listar/voluntario/ativo/0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsuario);

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var items = await response.Content.ReadFromJsonAsync<List<VoluntarioDto>>(cancellationToken: ct) ?? [];
        return items.Select(i => new HinovaVoluntario(i.CodigoVoluntario, i.Nome, i.Cpf)).ToList();
    }

    private sealed record AutenticarResponse([property: JsonPropertyName("token_usuario")] string TokenUsuario);

    private sealed record VoluntarioDto(
        [property: JsonPropertyName("codigo_voluntario")] string CodigoVoluntario,
        [property: JsonPropertyName("nome")] string Nome,
        [property: JsonPropertyName("cpf")] string Cpf);
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter HinovaClientTests`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Api/Integracoes/IHinovaClient.cs src/Recorrencia.Api/Integracoes/HinovaClient.cs tests/Recorrencia.Api.Tests/HinovaClientTests.cs
git commit -m "feat(api): add IHinovaClient and the real HTTP-backed HinovaClient"
```

---

## Task 4: DI wiring, `DevFakeHinovaClient`, and config plumbing

**Files:**
- Create: `src/Recorrencia.Api/Integracoes/DevFakeHinovaClient.cs`
- Modify: `src/Recorrencia.Api/Program.cs`
- Modify: `src/Recorrencia.Api/appsettings.Development.json`
- Modify: `docker-compose.yml`
- Modify: `.env.example`

**Interfaces:**
- Consumes: `IHinovaClient`, `HinovaOptions`, `AesGcmCipher` (Tasks 2-3).
- Produces: a running API with `IHinovaClient` resolvable from DI — either the real `HinovaClient` or `DevFakeHinovaClient`, selected by `Hinova:UseFake`. Task 5/6's `ApiFixture` wiring (test-only) overrides this again with its own controllable fake, independent of this task.

`DevFakeHinovaClient` is **not** test code — it is the deterministic stand-in used by local `docker compose` and by the E2E suite (Task 9), the same way `DevSeed` stands in for a real ERP feed elsewhere in this app. It ships in `src/`, gated by config, off by default.

- [ ] **Step 1: Implement `DevFakeHinovaClient`**

Create `src/Recorrencia.Api/Integracoes/DevFakeHinovaClient.cs`:

```csharp
namespace Recorrencia.Api.Integracoes;

/// <summary>
/// Deterministic stand-in for the real Hinova API, used when `Hinova:UseFake` is true
/// (local docker compose and E2E) so nothing outside this process ever calls the real
/// third-party service. Mirrors DevSeed's role for boletos.
/// </summary>
public sealed class DevFakeHinovaClient : IHinovaClient
{
    public static readonly IReadOnlyList<HinovaVoluntario> Voluntarios =
    [
        new HinovaVoluntario("101", "Ana Paula Ferreira", "11122233344"),
        new HinovaVoluntario("102", "Bruno Costa Lima", "22233344455"),
        new HinovaVoluntario("103", "Carla Souza Mendes", "33344455566"),
    ];

    public Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(senha) || string.IsNullOrWhiteSpace(tokenSga)
            ? throw new HinovaAuthException()
            : Task.FromResult("token-usuario-dev-fake");

    public Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct) =>
        Task.FromResult(Voluntarios);
}
```

- [ ] **Step 2: Wire options and DI in `Program.cs`**

In `src/Recorrencia.Api/Program.cs`, add the `using`:

```csharp
using Recorrencia.Api.Integracoes;
```

After the existing `builder.Services.AddOptions<InternalOptions>()...ValidateOnStart();` block, add:

```csharp
builder.Services.AddOptions<HinovaOptions>().BindConfiguration("Hinova")
    .Validate(o =>
    {
        try { return Convert.FromBase64String(o.EncryptionKey).Length == 32; }
        catch (FormatException) { return false; }
    }, "Hinova:EncryptionKey precisa ser uma chave base64 de 32 bytes.")
    .ValidateOnStart();
```

After the existing `builder.Services.AddSingleton<LoginThrottle>();` line, add:

```csharp
builder.Services.AddSingleton<AesGcmCipher>();
if (builder.Configuration.GetValue<bool>("Hinova:UseFake"))
{
    builder.Services.AddSingleton<IHinovaClient, DevFakeHinovaClient>();
}
else
{
    builder.Services.AddHttpClient<IHinovaClient, HinovaClient>(c =>
        c.BaseAddress = new Uri("https://api.hinova.com.br/api/sga/v2/"));
}
```

Near the other `app.Map*Endpoints();` calls, add:

```csharp
app.MapHinovaEndpoints();
```

(This line will not compile until Task 5 adds `MapHinovaEndpoints` — that is expected; Task 5 lands immediately after this one in the same branch.)

- [ ] **Step 3: Local dev config**

In `src/Recorrencia.Api/appsettings.Development.json`, add a `Hinova` section (any valid base64 32-byte string works for local dev; this one is arbitrary and not secret):

```json
  "Hinova": { "EncryptionKey": "MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU=", "UseFake": true },
```

(Insert it as a new top-level key, e.g. right after the `"Email"` entry, keeping the file valid JSON.)

- [ ] **Step 4: Docker Compose + `.env.example`**

In `docker-compose.yml`, under the `api` service's `environment:` block, add:

```yaml
      Hinova__EncryptionKey: ${HINOVA_ENCRYPTION_KEY:?defina HINOVA_ENCRYPTION_KEY no .env}
      Hinova__UseFake: ${HINOVA_USE_FAKE:-true}
```

In `.env.example`, add:

```
HINOVA_ENCRYPTION_KEY=MDEyMzQ1Njc4OTAxMjM0NTY3ODkwMTIzNDU=
HINOVA_USE_FAKE=true
```

- [ ] **Step 5: Build to confirm wiring compiles (endpoints stubbed in Task 5)**

This task alone will not build (`MapHinovaEndpoints` is undefined) — that is expected and resolved by Task 5. Do not attempt to build in isolation; proceed directly to Task 5 in the same branch before running `dotnet build`.

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Api/Integracoes/DevFakeHinovaClient.cs src/Recorrencia.Api/Program.cs src/Recorrencia.Api/appsettings.Development.json docker-compose.yml .env.example
git commit -m "feat(api): wire IHinovaClient DI (real vs dev fake) and Hinova config plumbing"
```

---

## Task 5: `HinovaEndpoints` — credenciais endpoints

**Files:**
- Create: `src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs` (this task adds only the credenciais half; Task 6 extends the same file)
- Create: `tests/Recorrencia.Api.Tests/FakeHinovaClient.cs`
- Modify: `tests/Recorrencia.Api.Tests/ApiFixture.cs`
- Create: `tests/Recorrencia.Api.Tests/HinovaCredenciaisTests.cs`

**Interfaces:**
- Consumes: `IHinovaClient`, `AesGcmCipher`, `RequestContext`, `Database`, `Audit.WriteAsync` (Tasks 1-4 and existing infrastructure).
- Produces: `GET /integracoes/hinova/credenciais`, `PUT /integracoes/hinova/credenciais`; `MapHinovaEndpoints(this IEndpointRouteBuilder app)` (Task 6 adds more routes to the same method).

- [ ] **Step 1: Add the test double**

Create `tests/Recorrencia.Api.Tests/FakeHinovaClient.cs`:

```csharp
using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

public sealed class FakeHinovaClient : IHinovaClient
{
    public bool RejectAuth { get; set; }
    public List<HinovaVoluntario> Voluntarios { get; } =
    [
        new HinovaVoluntario("101", "Ana Paula Ferreira", "11122233344"),
        new HinovaVoluntario("102", "Bruno Costa Lima", "22233344455"),
    ];

    public Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct) =>
        RejectAuth ? throw new HinovaAuthException() : Task.FromResult("token-usuario-fake");

    public Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HinovaVoluntario>>(Voluntarios);
}
```

- [ ] **Step 2: Wire the fake into `ApiFixture`**

In `tests/Recorrencia.Api.Tests/ApiFixture.cs`, add the field (next to `Emails`):

```csharp
    public FakeHinovaClient Hinova { get; } = new();
```

In `InitializeAsync`, add the settings (next to the other `builder.UseSetting(...)` calls):

```csharp
            builder.UseSetting("Hinova:EncryptionKey", Convert.ToBase64String(new byte[32]));
```

And inside the existing `builder.ConfigureServices(services => { ... })` block, add:

```csharp
                services.RemoveAll<Recorrencia.Api.Integracoes.IHinovaClient>();
                services.AddSingleton<Recorrencia.Api.Integracoes.IHinovaClient>(Hinova);
```

- [ ] **Step 3: Write the failing tests**

Create `tests/Recorrencia.Api.Tests/HinovaCredenciaisTests.cs`:

```csharp
using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class HinovaCredenciaisTests(ApiFixture api)
{
    public sealed record StatusDto(bool Configurado, DateTimeOffset? AtualizadoEm, string? AtualizadoPor);

    private async Task<ApiClient> LoginAsAdminAsync(SeededTenant s)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    [Fact]
    public async Task Status_starts_as_not_configured()
    {
        var s = await api.SeedAsync();
        var status = await (await LoginAsAdminAsync(s)).GetJsonAsync<StatusDto>("/integracoes/hinova/credenciais");
        Assert.False(status.Configurado);
    }

    [Fact]
    public async Task Saving_valid_credentials_marks_status_as_configured()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        await ApiClient.ExpectAsync(
            await admin.PutAsync("/integracoes/hinova/credenciais", new { usuario = "usuario", senha = "senha", tokenSga = "token" }),
            HttpStatusCode.NoContent);

        var status = await admin.GetJsonAsync<StatusDto>("/integracoes/hinova/credenciais");
        Assert.True(status.Configurado);
        Assert.Equal("Admin", status.AtualizadoPor);
    }

    [Fact]
    public async Task Saving_credentials_the_hinova_rejects_returns_bad_request_and_does_not_save()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        api.Hinova.RejectAuth = true;
        try
        {
            var response = await admin.PutAsync("/integracoes/hinova/credenciais", new { usuario = "usuario", senha = "senha", tokenSga = "token" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("hinova.credenciais_invalidas", await ApiClient.CodeAsync(response));

            var status = await admin.GetJsonAsync<StatusDto>("/integracoes/hinova/credenciais");
            Assert.False(status.Configurado);
        }
        finally
        {
            api.Hinova.RejectAuth = false;
        }
    }

    [Fact]
    public async Task Missing_permission_returns_forbidden_on_both_endpoints()
    {
        var s = await api.SeedAsync();
        var roleId = Guid.NewGuid();
        await api.SqlAsync(
            """
            delete from user_roles where tenant_id = @tenant and user_id = @pedro;
            insert into roles (id, tenant_id, name) values (@roleId, @tenant, 'Sem integrações');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @roleId, 'usuarios.convidar', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @pedro, @roleId);
            """,
            new { roleId, tenant = s.TenantId, pedro = s.Pedro });
        var pedro = api.Client(s.Slug);
        await pedro.LoginAsync($"pedro@{s.Slug}.local", ApiFixture.Password);

        var getResponse = await pedro.GetAsync("/integracoes/hinova/credenciais");
        Assert.Equal(HttpStatusCode.Forbidden, getResponse.StatusCode);

        var putResponse = await pedro.PutAsync("/integracoes/hinova/credenciais", new { usuario = "u", senha = "s", tokenSga = "t" });
        Assert.Equal(HttpStatusCode.Forbidden, putResponse.StatusCode);
    }
}
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter HinovaCredenciaisTests`
Expected: FAIL — `HinovaEndpoints`/`MapHinovaEndpoints` do not exist yet.

- [ ] **Step 5: Implement the endpoints**

Create `src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs`:

```csharp
using Npgsql;
using static Recorrencia.Api.Audit.Audit;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Integracoes;

public static class HinovaEndpoints
{
    public sealed record CredenciaisStatusResponse(bool Configurado, DateTimeOffset? AtualizadoEm, string? AtualizadoPor);
    public sealed record SalvarCredenciaisRequest(string Usuario, string Senha, string TokenSga);

    private sealed class StatusRow
    {
        public DateTimeOffset UpdatedAt { get; set; }
        public string UpdatedByName { get; set; } = "";
    }

    private sealed class CredenciaisRow
    {
        public byte[] UsuarioEnc { get; set; } = [];
        public byte[] SenhaEnc { get; set; } = [];
        public byte[] TokenSgaEnc { get; set; } = [];
    }

    public static void MapHinovaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/integracoes/hinova/credenciais", ObterCredenciaisStatusAsync).RequirePermission("integracoes.gerenciar");
        app.MapPut("/integracoes/hinova/credenciais", SalvarCredenciaisAsync).RequirePermission("integracoes.gerenciar");
    }

    private static async Task<IResult> ObterCredenciaisStatusAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var row = await db.InTenantAsync(tenant, user, tx => tx.QuerySingleOrDefaultAsync<StatusRow>(
            """
            select hc.updated_at, u.name as updated_by_name
              from hinova_credenciais hc join users u on u.id = hc.updated_by
             where hc.tenant_id = @tenant
            """,
            new { tenant }), ct);

        return Results.Ok(row is null
            ? new CredenciaisStatusResponse(false, null, null)
            : new CredenciaisStatusResponse(true, row.UpdatedAt, row.UpdatedByName));
    }

    private static async Task<IResult> SalvarCredenciaisAsync(SalvarCredenciaisRequest body, RequestContext request, Database db,
        IHinovaClient hinova, AesGcmCipher cipher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        if (string.IsNullOrWhiteSpace(body.Usuario) || string.IsNullOrWhiteSpace(body.Senha) || string.IsNullOrWhiteSpace(body.TokenSga))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "request.invalid");

        try
        {
            await hinova.AutenticarAsync(body.Usuario, body.Senha, body.TokenSga, ct);
        }
        catch (HinovaAuthException)
        {
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.credenciais_invalidas");
        }

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            await tx.ExecuteAsync(
                """
                insert into hinova_credenciais (tenant_id, usuario_enc, senha_enc, token_sga_enc, updated_by)
                values (@tenant, @usuario, @senha, @tokenSga, @actor)
                on conflict (tenant_id) do update set
                  usuario_enc = excluded.usuario_enc, senha_enc = excluded.senha_enc,
                  token_sga_enc = excluded.token_sga_enc, updated_at = now(), updated_by = excluded.updated_by
                """,
                new
                {
                    tenant, actor,
                    usuario = cipher.Encrypt(body.Usuario),
                    senha = cipher.Encrypt(body.Senha),
                    tokenSga = cipher.Encrypt(body.TokenSga),
                });
            await WriteAsync(tx, tenant, actor, "hinova.credenciais.salvar", "hinova_credenciais", null, null, new { body.Usuario });
            return 0;
        }, ct);

        return Results.NoContent();
    }
}
```

- [ ] **Step 6: Wire `MapHinovaEndpoints()` in `Program.cs`**

This was already added as text in Task 4, Step 2 — with `HinovaEndpoints.cs` now present, `dotnet build` should succeed. If Task 4 was executed by a different subagent and its `app.MapHinovaEndpoints();` line is missing from `Program.cs`, add it now next to the other `app.Map*Endpoints()` calls.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "HinovaCredenciaisTests|AesGcmCipherTests|HinovaClientTests"`
Expected: PASS (all of Task 2, 3, and 5's tests, plus no regressions in `dotnet test tests/Recorrencia.Api.Tests`).

- [ ] **Step 8: Commit**

```bash
git add src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs src/Recorrencia.Api/Program.cs tests/Recorrencia.Api.Tests/FakeHinovaClient.cs tests/Recorrencia.Api.Tests/ApiFixture.cs tests/Recorrencia.Api.Tests/HinovaCredenciaisTests.cs
git commit -m "feat(api): add GET/PUT /integracoes/hinova/credenciais"
```

---

## Task 6: `HinovaEndpoints` — voluntários + mapeamentos endpoints

**Files:**
- Modify: `src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs`
- Create: `tests/Recorrencia.Api.Tests/HinovaMapeamentosTests.cs`

**Interfaces:**
- Consumes: everything from Task 5's `HinovaEndpoints.cs`, plus `IHinovaClient.ListarVoluntariosAsync`.
- Produces: `GET /integracoes/hinova/voluntarios?query=`, `GET /integracoes/hinova/mapeamentos`, `POST /integracoes/hinova/mapeamentos`, `DELETE /integracoes/hinova/mapeamentos/{userId}` — consumed by the frontend in Task 8.

- [ ] **Step 1: Write the failing tests**

Create `tests/Recorrencia.Api.Tests/HinovaMapeamentosTests.cs`:

```csharp
using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class HinovaMapeamentosTests(ApiFixture api)
{
    public sealed record VoluntarioDto(string Codigo, string Nome, string Cpf, bool JaVinculado, string? VinculadoA);
    public sealed record MapeamentoDto(Guid UserId, string UserName, string CodigoVoluntario, string NomeHinova, string CpfHinova, DateTimeOffset MappedAt);

    private async Task<ApiClient> LoginAsAdminAsync(SeededTenant s)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    private static async Task ConfigureCredentialsAsync(ApiClient admin) =>
        await ApiClient.ExpectAsync(
            await admin.PutAsync("/integracoes/hinova/credenciais", new { usuario = "usuario", senha = "senha", tokenSga = "token" }),
            HttpStatusCode.NoContent);

    [Fact]
    public async Task Listing_voluntarios_without_credentials_configured_is_a_bad_request()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var response = await admin.GetAsync("/integracoes/hinova/voluntarios");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("hinova.nao_configurado", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Listing_voluntarios_filters_by_name_and_marks_mapped_ones()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);

        var all = await admin.GetJsonAsync<List<VoluntarioDto>>("/integracoes/hinova/voluntarios");
        Assert.Equal(2, all.Count);
        var ana = all.Single(v => v.Codigo == "101");
        Assert.True(ana.JaVinculado);
        Assert.Equal("João Silva", ana.VinculadoA);

        var filtered = await admin.GetJsonAsync<List<VoluntarioDto>>("/integracoes/hinova/voluntarios?query=bruno");
        Assert.Equal("Bruno Costa Lima", Assert.Single(filtered).Nome);
    }

    [Fact]
    public async Task Mapping_the_same_user_twice_is_a_conflict()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);

        var response = await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "102", nomeHinova = "Bruno Costa Lima", cpfHinova = "22233344455" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hinova.vinculo_duplicado", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Listing_and_removing_mapeamentos()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);

        var list = await admin.GetJsonAsync<List<MapeamentoDto>>("/integracoes/hinova/mapeamentos");
        Assert.Equal("101", Assert.Single(list).CodigoVoluntario);

        await ApiClient.ExpectAsync(await admin.DeleteAsync($"/integracoes/hinova/mapeamentos/{s.Joao}"), HttpStatusCode.NoContent);

        var listAfter = await admin.GetJsonAsync<List<MapeamentoDto>>("/integracoes/hinova/mapeamentos");
        Assert.Empty(listAfter);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter HinovaMapeamentosTests`
Expected: FAIL — the four routes do not exist yet.

- [ ] **Step 3: Extend `HinovaEndpoints.cs`**

In `src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs`, add these records next to the existing ones:

```csharp
    public sealed record VoluntarioResponse(string Codigo, string Nome, string Cpf, bool JaVinculado, string? VinculadoA);
    public sealed record MapeamentoResponse(Guid UserId, string UserName, string CodigoVoluntario, string NomeHinova, string CpfHinova, DateTimeOffset MappedAt);
    public sealed record CriarMapeamentoRequest(Guid UserId, string CodigoVoluntario, string NomeHinova, string CpfHinova);
```

Add these row classes next to the existing ones:

```csharp
    private sealed class MappingRow
    {
        public string CodigoVoluntario { get; set; } = "";
        public string VinculadoA { get; set; } = "";
    }

    private sealed class MapeamentoRow
    {
        public Guid UserId { get; set; }
        public string UserName { get; set; } = "";
        public string CodigoVoluntario { get; set; } = "";
        public string NomeHinova { get; set; } = "";
        public string CpfHinova { get; set; } = "";
        public DateTimeOffset MappedAt { get; set; }
    }
```

Extend `MapHinovaEndpoints` to:

```csharp
    public static void MapHinovaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/integracoes/hinova/credenciais", ObterCredenciaisStatusAsync).RequirePermission("integracoes.gerenciar");
        app.MapPut("/integracoes/hinova/credenciais", SalvarCredenciaisAsync).RequirePermission("integracoes.gerenciar");
        app.MapGet("/integracoes/hinova/voluntarios", ListarVoluntariosAsync).RequirePermission("integracoes.gerenciar");
        app.MapGet("/integracoes/hinova/mapeamentos", ListarMapeamentosAsync).RequirePermission("integracoes.gerenciar");
        app.MapPost("/integracoes/hinova/mapeamentos", CriarMapeamentoAsync).RequirePermission("integracoes.gerenciar");
        app.MapDelete("/integracoes/hinova/mapeamentos/{userId:guid}", RemoverMapeamentoAsync).RequirePermission("integracoes.gerenciar");
    }
```

Add the four new handlers at the end of the class, before the final `}`:

```csharp
    private static async Task<IResult> ListarVoluntariosAsync(string? query, RequestContext request, Database db, IHinovaClient hinova,
        AesGcmCipher cipher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();

        var credenciais = await db.InTenantAsync(tenant, user, tx => tx.QuerySingleOrDefaultAsync<CredenciaisRow>(
            "select usuario_enc, senha_enc, token_sga_enc from hinova_credenciais where tenant_id = @tenant",
            new { tenant }), ct);
        if (credenciais is null)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.nao_configurado");

        var usuario = cipher.Decrypt(credenciais.UsuarioEnc);
        var senha = cipher.Decrypt(credenciais.SenhaEnc);
        var tokenSga = cipher.Decrypt(credenciais.TokenSgaEnc);

        string tokenUsuario;
        try
        {
            tokenUsuario = await hinova.AutenticarAsync(usuario, senha, tokenSga, ct);
        }
        catch (HinovaAuthException)
        {
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.credenciais_invalidas");
        }
        var voluntarios = await hinova.ListarVoluntariosAsync(tokenUsuario, ct);

        var filtered = string.IsNullOrWhiteSpace(query)
            ? voluntarios
            : voluntarios.Where(v => v.Nome.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        var mapped = (await db.InTenantAsync(tenant, user, tx => tx.QueryAsync<MappingRow>(
            """
            select m.codigo_voluntario, u.name as vinculado_a
              from hinova_voluntario_mapping m join users u on u.id = m.user_id
             where m.tenant_id = @tenant
            """,
            new { tenant }), ct)).ToDictionary(m => m.CodigoVoluntario, m => m.VinculadoA);

        return Results.Ok(filtered.Select(v => new VoluntarioResponse(
            v.Codigo, v.Nome, v.Cpf, mapped.ContainsKey(v.Codigo), mapped.GetValueOrDefault(v.Codigo))).ToList());
    }

    private static async Task<IResult> ListarMapeamentosAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var rows = await db.InTenantAsync(tenant, user, tx => tx.QueryAsync<MapeamentoRow>(
            """
            select m.user_id, u.name as user_name, m.codigo_voluntario, m.nome_hinova, m.cpf_hinova, m.mapped_at
              from hinova_voluntario_mapping m join users u on u.id = m.user_id
             where m.tenant_id = @tenant
             order by u.name
            """,
            new { tenant }), ct);

        return Results.Ok(rows.Select(r => new MapeamentoResponse(
            r.UserId, r.UserName, r.CodigoVoluntario, r.NomeHinova, r.CpfHinova, r.MappedAt)).ToList());
    }

    private static async Task<IResult> CriarMapeamentoAsync(CriarMapeamentoRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            try
            {
                await tx.ExecuteAsync(
                    """
                    insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
                    values (@tenant, @userId, @codigo, @nome, @cpf, @actor)
                    """,
                    new { tenant, actor, userId = body.UserId, codigo = body.CodigoVoluntario, nome = body.NomeHinova, cpf = body.CpfHinova });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "hinova.vinculo_duplicado");
            }
            await WriteAsync(tx, tenant, actor, "hinova.vincular", "hinova_voluntario_mapping", body.UserId, null, body);
            return 0;
        }, ct);

        return Results.Created($"/integracoes/hinova/mapeamentos/{body.UserId}", body);
    }

    private static async Task<IResult> RemoverMapeamentoAsync(Guid userId, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var deleted = await tx.ExecuteAsync(
                "delete from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @userId", new { tenant, userId });
            if (deleted == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "hinova.vinculo_not_found");
            await WriteAsync(tx, tenant, actor, "hinova.desvincular", "hinova_voluntario_mapping", userId, null, null);
            return 0;
        }, ct);

        return Results.NoContent();
    }
```

Add these `using`s at the top of the file if not already present:

```csharp
using Npgsql;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "HinovaMapeamentosTests|HinovaCredenciaisTests"`
Expected: PASS (all).

- [ ] **Step 5: Run the full API test suite for regressions**

Run: `dotnet test tests/Recorrencia.Api.Tests`

Expect two pre-existing failures surfaced here for the first time (no earlier task's test
run covered them — Task 5 only ran a filtered subset), both caused by the same catalog
change as the `RbacTests`/`ProvisioningTests`/`RoleTests` fixes already made in Task 1:

- `tests/Recorrencia.Api.Tests/PlatformTests.cs`, `New_tenant_admin_receives_an_invite_and_gets_full_permissions`: `Assert.Equal(14, me.Permissions.Count);` → change to `15`.
- `tests/Recorrencia.Api.Tests/AuthTests.cs`, `Login_returns_a_session_and_me_describes_the_user`: `Assert.Equal(6, me.Modules.Count);` → change to `7`.

Fix both, then re-run `dotnet test tests/Recorrencia.Api.Tests` and confirm zero failures.

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs tests/Recorrencia.Api.Tests/HinovaMapeamentosTests.cs tests/Recorrencia.Api.Tests/PlatformTests.cs tests/Recorrencia.Api.Tests/AuthTests.cs
git commit -m "feat(api): add voluntarios listing and mapeamentos CRUD endpoints"
```

---

## Task 7: Frontend — icons, types, and the sidebar nav item

**Files:**
- Modify: `web/src/app/components/app-icon.tsx`
- Modify: `web/src/lib/types.ts`
- Modify: `web/src/app/app-shell.tsx`

**Interfaces:**
- Produces: `IconName` gains `'settings' | 'link' | 'trash'`; `types.ts` gains `HinovaCredenciaisStatus`, `HinovaVoluntario`, `HinovaMapeamento`, `UserSummary`; `AppShell` renders a "Configurações" nav item only when the viewer's permissions include `integracoes.gerenciar`.
- Consumes: `Me.permissions` (already defined).

- [ ] **Step 1: Add the new icons**

In `web/src/app/components/app-icon.tsx`, change the `IconName` union:

```tsx
export type IconName = 'grid' | 'wallet' | 'calendar' | 'logout' | 'search' | 'back' | 'chevron' | 'info' | 'settings' | 'link' | 'trash';
```

Add three new cases inside the `switch`, before the closing `}` of the function body (after the `'info'` case):

```tsx
    case 'settings':
      return (
        <svg className={cls} {...svgProps}>
          <circle cx={12} cy={12} r={3} />
          <path d="M12 2v3M12 19v3M4.2 4.2l2.1 2.1M17.7 17.7l2.1 2.1M2 12h3M19 12h3M4.2 19.8l2.1-2.1M17.7 6.3l2.1-2.1" />
        </svg>
      );
    case 'link':
      return (
        <svg className={cls} {...svgProps}>
          <path d="M9 15 15 9" />
          <path d="M11 6l1.5-1.5a4 4 0 1 1 5.5 5.5L16 11" />
          <path d="M13 18l-1.5 1.5a4 4 0 1 1-5.5-5.5L8 13" />
        </svg>
      );
    case 'trash':
      return (
        <svg className={cls} {...svgProps}>
          <path d="M4 7h16" />
          <path d="M10 11v6M14 11v6" />
          <path d="M6 7l1 13a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2l1-13" />
          <path d="M9 7V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v3" />
        </svg>
      );
```

- [ ] **Step 2: Add the frontend types**

In `web/src/lib/types.ts`, append:

```ts
export type UserSummary = { id: string; name: string; email: string; status: string };

export type HinovaCredenciaisStatus = { configurado: boolean; atualizadoEm: string | null; atualizadoPor: string | null };

export type HinovaVoluntario = { codigo: string; nome: string; cpf: string; jaVinculado: boolean; vinculadoA: string | null };

export type HinovaMapeamento = {
  userId: string;
  userName: string;
  codigoVoluntario: string;
  nomeHinova: string;
  cpfHinova: string;
  mappedAt: string;
};
```

- [ ] **Step 3: Add the permission-gated nav item**

In `web/src/app/app-shell.tsx`, change `ActivePage` and `NAV_ITEMS`:

```tsx
export type ActivePage = 'overview' | 'wallet' | 'closing' | 'settings';

const NAV_ITEMS: { id: ActivePage; icon: IconName; label: string; href: string | null; permission?: string }[] = [
  { id: 'overview', icon: 'grid', label: 'Minha recorrência', href: '/' },
  { id: 'wallet', icon: 'wallet', label: 'Minha carteira', href: '/carteira' },
  { id: 'closing', icon: 'calendar', label: 'Fechamento', href: null },
  { id: 'settings', icon: 'settings', label: 'Configurações', href: '/configuracoes/integracoes', permission: 'integracoes.gerenciar' },
];
```

Change the `nav` rendering to skip items whose `permission` the viewer lacks:

```tsx
        <nav aria-label="Navegação principal">
          {NAV_ITEMS.filter((item) => !item.permission || me.permissions.some((p) => p.key === item.permission)).map((item) =>
            item.href ? (
```

(The rest of the `.map` body is unchanged — only the `NAV_ITEMS.map(...)` call becomes `NAV_ITEMS.filter(...).map(...)`.)

- [ ] **Step 4: Typecheck**

Run: `cd web && npm run typecheck`
Expected: no errors (Task 8 creates `/configuracoes/integracoes`, so this route will 404 at runtime until then, but typechecking the string literal href does not require the route to exist).

- [ ] **Step 5: Commit**

```bash
git add web/src/app/components/app-icon.tsx web/src/lib/types.ts web/src/app/app-shell.tsx
git commit -m "feat(web): add settings/link/trash icons and permission-gated Configurações nav item"
```

---

## Task 8: Frontend — `Configurações > Integrações` page

**Files:**
- Create: `web/src/app/configuracoes/integracoes/page.tsx`
- Create: `web/src/app/configuracoes/integracoes/actions.ts`
- Create: `web/src/app/configuracoes/integracoes/credenciais-form.tsx`
- Create: `web/src/app/configuracoes/integracoes/mapeamento-form.tsx`
- Modify: `web/src/lib/errors.ts`

**Interfaces:**
- Consumes: `GET/PUT /integracoes/hinova/credenciais`, `GET /integracoes/hinova/voluntarios`, `GET/POST/DELETE /integracoes/hinova/mapeamentos`, `GET /users` (Task 6, and the existing `UserEndpoints`).
- Produces: the page at `/configuracoes/integracoes`, reachable from the sidebar item added in Task 7.

- [ ] **Step 1: Add the new error messages**

In `web/src/lib/errors.ts`, add to the `messages` map:

```ts
  'hinova.credenciais_invalidas': 'A Hinova não aceitou essas credenciais. Confira usuário, senha e token.',
  'hinova.nao_configurado': 'Configure as credenciais da Hinova antes de buscar voluntários.',
  'hinova.vinculo_duplicado': 'Esse usuário ou esse voluntário já está vinculado a outro registro.',
```

- [ ] **Step 2: Write the Server Actions**

Create `web/src/app/configuracoes/integracoes/actions.ts`:

```ts
'use server';

import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function salvarCredenciaisHinova(_: FormState, formData: FormData): Promise<FormState> {
  try {
    await apiFetch('/integracoes/hinova/credenciais', {
      method: 'PUT',
      body: {
        usuario: String(formData.get('usuario') ?? ''),
        senha: String(formData.get('senha') ?? ''),
        tokenSga: String(formData.get('tokenSga') ?? ''),
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  return { message: 'Credenciais salvas.' };
}

export async function vincularVoluntario(_: FormState, formData: FormData): Promise<FormState> {
  try {
    await apiFetch('/integracoes/hinova/mapeamentos', {
      method: 'POST',
      body: {
        userId: String(formData.get('userId') ?? ''),
        codigoVoluntario: String(formData.get('codigoVoluntario') ?? ''),
        nomeHinova: String(formData.get('nomeHinova') ?? ''),
        cpfHinova: String(formData.get('cpfHinova') ?? ''),
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  return { message: 'Voluntário vinculado.' };
}

export async function desvincularVoluntario(_: FormState, formData: FormData): Promise<FormState> {
  try {
    await apiFetch(`/integracoes/hinova/mapeamentos/${String(formData.get('userId') ?? '')}`, { method: 'DELETE' });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  return { message: 'Vínculo removido.' };
}
```

- [ ] **Step 3: Write the credentials form (client component)**

Create `web/src/app/configuracoes/integracoes/credenciais-form.tsx`:

```tsx
'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { salvarCredenciaisHinova } from './actions';

export function CredenciaisForm() {
  const [state, formAction, pending] = useActionState<FormState, FormData>(salvarCredenciaisHinova, {});
  return (
    <form action={formAction} className="form">
      <label>
        Usuário
        <input name="usuario" type="text" autoComplete="off" required />
      </label>
      <label>
        Senha
        <input name="senha" type="password" autoComplete="off" required />
      </label>
      <label>
        Token da SGA
        <input name="tokenSga" type="password" autoComplete="off" required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      {state.message && <p className="success">{state.message}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Salvando…' : 'Salvar credenciais'}</button>
    </form>
  );
}
```

- [ ] **Step 4: Write the mapping form (client component)**

Create `web/src/app/configuracoes/integracoes/mapeamento-form.tsx`:

```tsx
'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import type { HinovaVoluntario, UserSummary } from '@/lib/types';
import { desvincularVoluntario, vincularVoluntario } from './actions';

export function VincularForm({ voluntario, usuarios }: { voluntario: HinovaVoluntario; usuarios: UserSummary[] }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(vincularVoluntario, {});
  return (
    <form action={formAction} className="inline">
      <input type="hidden" name="codigoVoluntario" value={voluntario.codigo} />
      <input type="hidden" name="nomeHinova" value={voluntario.nome} />
      <input type="hidden" name="cpfHinova" value={voluntario.cpf} />
      <label>
        Usuário APROVEC
        <select name="userId" required defaultValue="">
          <option value="" disabled>Selecione</option>
          {usuarios.map((u) => (
            <option key={u.id} value={u.id}>{u.name}</option>
          ))}
        </select>
      </label>
      <button type="submit" disabled={pending}>{pending ? 'Vinculando…' : 'Vincular'}</button>
      {state.error && <p role="alert" className="error">{state.error}</p>}
    </form>
  );
}

export function DesvincularForm({ userId }: { userId: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(desvincularVoluntario, {});
  return (
    <form action={formAction}>
      <input type="hidden" name="userId" value={userId} />
      <button type="submit" className="secondary" disabled={pending}>{pending ? 'Removendo…' : 'Desvincular'}</button>
      {state.error && <p role="alert" className="error">{state.error}</p>}
    </form>
  );
}
```

- [ ] **Step 5: Write the page (Server Component)**

Create `web/src/app/configuracoes/integracoes/page.tsx`:

```tsx
import { notFound } from 'next/navigation';
import { AppShell } from '../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import { Icon } from '../../components/app-icon';
import { formatDate } from '@/lib/format';
import type { HinovaCredenciaisStatus, HinovaMapeamento, HinovaVoluntario, Me, UserSummary } from '@/lib/types';
import { CredenciaisForm } from './credenciais-form';
import { DesvincularForm, VincularForm } from './mapeamento-form';

export default async function IntegracoesPage({
  searchParams,
}: {
  searchParams: Promise<{ query?: string }>;
}) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const canManage = me.permissions.some((p) => p.key === 'integracoes.gerenciar');
  if (!canManage) {
    return (
      <AppShell me={me} active="settings">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso às integrações.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const { query } = await searchParams;
  const status = await apiFetch<HinovaCredenciaisStatus>('/integracoes/hinova/credenciais');
  const mapeamentos = await apiFetch<HinovaMapeamento[]>('/integracoes/hinova/mapeamentos');

  let voluntarios: HinovaVoluntario[] = [];
  let usuarios: UserSummary[] = [];
  let searchError: string | null = null;
  if (status.configurado) {
    try {
      const params = new URLSearchParams();
      if (query) params.set('query', query);
      voluntarios = await apiFetch<HinovaVoluntario[]>(`/integracoes/hinova/voluntarios?${params.toString()}`);
      usuarios = (await apiFetch<UserSummary[]>('/users')).filter((u) => u.status === 'ativo');
    } catch {
      searchError = 'Não foi possível buscar voluntários na Hinova agora. Tente novamente em instantes.';
    }
  }

  return (
    <AppShell me={me} active="settings">
      <div className="shell">
        <div className="page-heading">
          <p className="eyebrow">Configurações</p>
          <h1>Integrações</h1>
          <p className="muted">Conecte a Hinova e ligue voluntários aos usuários do APROVEC.</p>
        </div>

        <section className="card">
          <h2>Credenciais Hinova</h2>
          <p className="muted">
            {status.configurado
              ? `Configurado em ${formatDate(status.atualizadoEm!.slice(0, 10))} por ${status.atualizadoPor}.`
              : 'Ainda não configurado.'}
          </p>
          <CredenciaisForm />
        </section>

        {status.configurado && (
          <section className="card">
            <h2>Mapeamento de voluntários</h2>
            <form method="get" className="search-field">
              <Icon name="search" />
              <input type="search" name="query" placeholder="Buscar por nome" defaultValue={query ?? ''} aria-label="Buscar voluntário por nome" />
            </form>

            {searchError && <p className="error">{searchError}</p>}

            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Voluntário</th>
                    <th>CPF</th>
                    <th>Vínculo</th>
                  </tr>
                </thead>
                <tbody>
                  {voluntarios.map((v) => (
                    <tr key={v.codigo}>
                      <td>{v.nome}</td>
                      <td>{v.cpf}</td>
                      <td>
                        {v.jaVinculado ? (
                          <span className="badge neutral">Vinculado a {v.vinculadoA}</span>
                        ) : (
                          <VincularForm voluntario={v} usuarios={usuarios} />
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        )}

        <section className="card">
          <h2>Vínculos atuais</h2>
          {mapeamentos.length === 0 ? (
            <p className="muted">Nenhum voluntário vinculado ainda.</p>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Usuário APROVEC</th>
                    <th>Voluntário Hinova</th>
                    <th>Vinculado em</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {mapeamentos.map((m) => (
                    <tr key={m.userId}>
                      <td>{m.userName}</td>
                      <td>{m.nomeHinova}</td>
                      <td>{formatDate(m.mappedAt.slice(0, 10))}</td>
                      <td><DesvincularForm userId={m.userId} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      </div>
    </AppShell>
  );
}
```

- [ ] **Step 6: Typecheck, lint, build**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: clean.

- [ ] **Step 7: Commit**

```bash
git add web/src/app/configuracoes web/src/lib/errors.ts
git commit -m "feat(web): add Configurações > Integrações page for Hinova credentials and voluntário mapping"
```

---

## Task 9: E2E scenario

**Files:**
- Modify: `web/e2e/fundacao.spec.ts`

**Interfaces:**
- Consumes: the running docker-compose stack with `Hinova:UseFake=true` (Task 4), so `IHinovaClient` resolves to `DevFakeHinovaClient` with its fixed 3-voluntário dataset.

- [ ] **Step 1: Add the scenario**

In `web/e2e/fundacao.spec.ts`, add a new `test(...)` following the existing style (reuse whatever login helper the file already uses for the admin user of a seeded tenant):

```ts
test('admin configura a Hinova e vincula um voluntário', async ({ page }) => {
  // ...login as the seeded tenant's admin, matching the file's existing helper...

  await page.getByRole('link', { name: 'Configurações' }).click();
  await expect(page).toHaveURL(/\/configuracoes\/integracoes$/);

  await page.getByLabel('Usuário').fill('usuario-dev');
  await page.getByLabel('Senha').fill('senha-dev');
  await page.getByLabel('Token da SGA').fill('token-dev');
  await page.getByRole('button', { name: 'Salvar credenciais' }).click();
  await expect(page.getByText('Credenciais salvas.')).toBeVisible();

  await page.getByLabel('Buscar voluntário por nome').fill('Ana Paula');
  await page.getByLabel('Buscar voluntário por nome').press('Enter');
  await expect(page.getByRole('cell', { name: 'Ana Paula Ferreira' })).toBeVisible();

  await page.getByLabel('Usuário APROVEC').selectOption({ label: 'João Silva' });
  await page.getByRole('button', { name: 'Vincular' }).click();
  await expect(page.getByText('Voluntário vinculado.')).toBeVisible();
  await expect(page.getByRole('row', { name: /João Silva.*Ana Paula Ferreira/ })).toBeVisible();
});
```

Adjust the exact login helper call and selector details to match whatever the rest of `fundacao.spec.ts` already uses (read the file first — do not guess the helper's name).

- [ ] **Step 2: Ensure the local stack runs with the fake Hinova client**

Before running E2E, confirm `.env` (not `.env.example`) has `HINOVA_USE_FAKE=true` (the default from Task 4's `docker-compose.yml` change already falls back to `true` if unset, so no action is needed unless `.env` explicitly overrides it).

- [ ] **Step 3: Run the E2E suite**

Run: `cd web && npm run e2e`
Expected: PASS, including the new scenario, with no regressions in the existing 6 scenarios.

- [ ] **Step 4: Commit**

```bash
git add web/e2e/fundacao.spec.ts
git commit -m "test(e2e): cover configuring Hinova credentials and vincular a voluntário"
```

---

## Final Verification

- [ ] `dotnet test` (whole solution) passes.
- [ ] `cd web && npm run typecheck && npm run lint && npm run build && npm run e2e` all pass.
- [ ] Manually verify against the local docker-compose stack: log in as `admin@<tenant>.local`, open Configurações > Integrações from the sidebar, save credentials, search "Bruno", vincular to a user, confirm it appears under "Vínculos atuais", desvincular, confirm it disappears. Log in as a non-admin (e.g. `joao@<tenant>.local`) and confirm "Configurações" does not appear in the sidebar and `/configuracoes/integracoes` shows the "sem acesso" card.
