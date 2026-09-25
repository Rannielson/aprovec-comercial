# Extend POST /users to optionally link a Hinova voluntário Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the admin create a user and link it to a Hinova voluntário in one atomic call, so the "Administração" frontend (built in a parallel session, per `docs/superpowers/specs/2026-09-24-estrutura-arvore-comissionada-design.md`) can turn a Hinova search result directly into a positioned member of the commission tree.

**Architecture:** Backend-only. Extends the existing `POST /users` handler (`InviteAsync`) to accept three new optional fields and, inside the same DB transaction that already creates the user, insert into `hinova_voluntario_mapping` (the table Fase 3 already created) when those fields are present. No new endpoint, no migration.

**Tech Stack:** C# ASP.NET Core minimal APIs, Dapper/Npgsql, Postgres RLS.

**Spec:** `docs/superpowers/specs/2026-09-24-estrutura-arvore-comissionada-design.md` (Backend section) — this plan implements only that section; the frontend (Administração pages, pyramid visual, Participantes) is being built in a separate, parallel session and is out of scope here.

## Global Constraints

- `Name`/`Email` remain required exactly as today — unchanged validation.
- The three new fields (`codigoVoluntario`, `nomeHinova`, `cpfHinova`) are optional; when `codigoVoluntario` is blank/absent, behavior is byte-for-byte identical to today (regression, not just "mostly the same").
- Creating the user and creating the Hinova mapping happen in the same transaction: if the mapping insert fails (duplicate `codigo_voluntario`), the whole transaction rolls back — no orphaned user, no orphaned mapping.
- Sending the Hinova fields requires the caller to hold `integracoes.gerenciar`, in addition to whatever `usuarios.convidar`/`estrutura.editar` checks already apply — creating the link is exactly as sensitive as the Fase 3 credentials/mapping screen that owns the same table.
- No changes to `GET /users`, `PUT /users/{id}/supervisor`, or any Hinova endpoint from Fase 3 — this plan touches only `InviteAsync`.

---

## Task 1: Extend `InviteUserRequest` and `InviteAsync`

**Files:**
- Modify: `src/Recorrencia.Api/Users/UserEndpoints.cs`
- Modify: `tests/Recorrencia.Api.Tests/UserTests.cs`

**Interfaces:**
- Produces: `POST /users` accepts `{ name, email, supervisorId, roleIds, codigoVoluntario, nomeHinova, cpfHinova }` (last three optional). Consumed by the parallel frontend session's "Administração" pages (not built by this plan).
- Consumes: `hinova_voluntario_mapping` table (Fase 3, `src/Recorrencia.Db/Scripts/0014_hinova_integracao.sql`) — no schema change needed, this task only inserts into it.

- [ ] **Step 1: Write the failing tests**

Read the current `tests/Recorrencia.Api.Tests/UserTests.cs` first (it already has a `RoleIdAsync(tenantId, template)` helper and a `LoginAsync(s, login)` helper — reuse both, don't redefine them). Add these four tests to the file (anywhere among the existing `[Fact]` methods):

```csharp
    [Fact]
    public async Task Inviting_with_hinova_fields_creates_the_mapping_in_the_same_call()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var consultor = await RoleIdAsync(s.TenantId, "consultor");
        var email = $"nova-hinova@{s.Slug}.local";

        var created = await admin.PostAsync("/users", new
        {
            name = "Nova Vinculada",
            email,
            roleIds = new[] { consultor },
            codigoVoluntario = "555",
            nomeHinova = "Nova Vinculada Hinova",
            cpfHinova = "11122233344",
        });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        var mapeamentos = await admin.GetJsonAsync<List<MapeamentoDto>>("/integracoes/hinova/mapeamentos");
        var mapping = Assert.Single(mapeamentos, m => m.UserId == id);
        Assert.Equal("555", mapping.CodigoVoluntario);
        Assert.Equal("Nova Vinculada Hinova", mapping.NomeHinova);
    }

    [Fact]
    public async Task Inviting_with_a_codigo_voluntario_already_linked_creates_neither_user_nor_mapping()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var consultor = await RoleIdAsync(s.TenantId, "consultor");

        await ApiClient.ExpectAsync(await admin.PostAsync("/users", new
        {
            name = "Primeiro",
            email = $"primeiro@{s.Slug}.local",
            roleIds = new[] { consultor },
            codigoVoluntario = "777",
            nomeHinova = "Primeiro Hinova",
            cpfHinova = "11111111111",
        }), HttpStatusCode.Created);

        var duplicateEmail = $"segundo@{s.Slug}.local";
        var response = await admin.PostAsync("/users", new
        {
            name = "Segundo",
            email = duplicateEmail,
            roleIds = new[] { consultor },
            codigoVoluntario = "777",
            nomeHinova = "Segundo Hinova",
            cpfHinova = "22222222222",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hinova.vinculo_duplicado", await ApiClient.CodeAsync(response));

        var users = await admin.GetJsonAsync<List<UserDto>>("/users");
        Assert.DoesNotContain(users, u => u.Email == duplicateEmail);
    }

    [Fact]
    public async Task Inviting_with_hinova_fields_without_the_permission_is_forbidden()
    {
        var s = await api.SeedAsync();
        var roleId = Guid.NewGuid();
        await api.SqlAsync(
            """
            delete from user_roles where tenant_id = @tenant and user_id = @pedro;
            insert into roles (id, tenant_id, name) values (@roleId, @tenant, 'Convida sem Hinova');
            insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @roleId, 'usuarios.convidar', null);
            insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @pedro, @roleId);
            """,
            new { roleId, tenant = s.TenantId, pedro = s.Pedro });
        var pedro = await LoginAsync(s, "pedro");

        var response = await pedro.PostAsync("/users", new
        {
            name = "Sem Permissao",
            email = $"sem-permissao@{s.Slug}.local",
            codigoVoluntario = "888",
            nomeHinova = "Sem Permissao Hinova",
            cpfHinova = "33333333333",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("auth.forbidden", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Inviting_without_hinova_fields_is_unchanged()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var consultor = await RoleIdAsync(s.TenantId, "consultor");

        var response = await admin.PostAsync("/users", new
        {
            name = "Sem Hinova",
            email = $"sem-hinova@{s.Slug}.local",
            roleIds = new[] { consultor },
        });

        await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;
        var mapeamentos = await admin.GetJsonAsync<List<MapeamentoDto>>("/integracoes/hinova/mapeamentos");
        Assert.DoesNotContain(mapeamentos, m => m.UserId == id);
    }
```

Also add this record next to the file's existing `CreatedDto`/`UserDto` records (needed by the tests above):

```csharp
    public sealed record MapeamentoDto(Guid UserId, string UserName, string CodigoVoluntario, string NomeHinova, string CpfHinova, DateTimeOffset MappedAt);
```

- [ ] **Step 2: Run tests to verify the new ones fail**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "UserTests&(Inviting_with_hinova_fields_creates_the_mapping_in_the_same_call|Inviting_with_a_codigo_voluntario_already_linked_creates_neither_user_nor_mapping|Inviting_with_hinova_fields_without_the_permission_is_forbidden|Inviting_without_hinova_fields_is_unchanged)"`
Expected: the first three FAIL (feature doesn't exist yet); `Inviting_without_hinova_fields_is_unchanged` should already PASS (pure regression check against existing behavior) — if it doesn't, stop and investigate before writing any implementation code, since that would mean the plan's assumption about current behavior is wrong.

- [ ] **Step 3: Extend `InviteUserRequest`**

In `src/Recorrencia.Api/Users/UserEndpoints.cs`, change:

```csharp
    public sealed record InviteUserRequest(string? Name, string? Email, Guid? SupervisorId, Guid[]? RoleIds);
```
to:
```csharp
    public sealed record InviteUserRequest(string? Name, string? Email, Guid? SupervisorId, Guid[]? RoleIds,
        string? CodigoVoluntario, string? NomeHinova, string? CpfHinova);
```

- [ ] **Step 4: Extend `InviteAsync`**

In the same file, change the start of `InviteAsync` from:

```csharp
    private static async Task<IResult> InviteAsync(InviteUserRequest body, RequestContext request, Database db,
        CurrentPermissions permissions, IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var name = (body.Name ?? "").Trim();
        var address = (body.Email ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.name_required");
        if (!MailAddress.TryCreate(address, out var parsedAddress) || parsedAddress.Address != address)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_email");

        var roleIds = body.RoleIds ?? [];
        var mine = await permissions.GetAsync(ct);
        if (roleIds.Length > 0 && !mine.Has("usuarios.gerenciar_perfis"))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");
        // Hierarchy position drives upline commission, so placing a new hire under a given
        // supervisor is as sensitive as granting a role -- gated the same way, behind
        // estrutura.editar, rather than left open to anyone who can merely invite.
        if (body.SupervisorId is not null && !mine.Has("estrutura.editar"))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");

        var id = Guid.CreateVersion7();
```
to:
```csharp
    private static async Task<IResult> InviteAsync(InviteUserRequest body, RequestContext request, Database db,
        CurrentPermissions permissions, IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var name = (body.Name ?? "").Trim();
        var address = (body.Email ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.name_required");
        if (!MailAddress.TryCreate(address, out var parsedAddress) || parsedAddress.Address != address)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_email");

        var codigoVoluntario = (body.CodigoVoluntario ?? "").Trim();
        var nomeHinova = (body.NomeHinova ?? "").Trim();
        var cpfHinova = (body.CpfHinova ?? "").Trim();
        var linkingHinova = codigoVoluntario.Length > 0;

        var roleIds = body.RoleIds ?? [];
        var mine = await permissions.GetAsync(ct);
        if (roleIds.Length > 0 && !mine.Has("usuarios.gerenciar_perfis"))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");
        // Hierarchy position drives upline commission, so placing a new hire under a given
        // supervisor is as sensitive as granting a role -- gated the same way, behind
        // estrutura.editar, rather than left open to anyone who can merely invite.
        if (body.SupervisorId is not null && !mine.Has("estrutura.editar"))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");
        // Creating a Hinova voluntário mapping is exactly as sensitive as the Fase 3
        // credentials/mapping screen that owns hinova_voluntario_mapping -- same permission.
        if (linkingHinova && !mine.Has("integracoes.gerenciar"))
            throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");

        var id = Guid.CreateVersion7();
```

Then, inside the `db.InTenantAsync(tenant, actor, async tx => { ... })` lambda, change:

```csharp
            foreach (var roleId in roleIds.Distinct())
                await tx.ExecuteAsync("insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @id, @roleId)", new { tenant, id, roleId });

            await tx.ExecuteAsync("select app.create_invite(@id, @hash, 'convite', @ttl)",
                new { id, hash = Tokens.Hash(token), ttl = options.Value.InviteTtlSeconds });
            await WriteAsync(tx, tenant, actor, "users.invite", "users", id, null, new { name, email = address, body.SupervisorId, roleIds });
            return 0;
```
to:
```csharp
            foreach (var roleId in roleIds.Distinct())
                await tx.ExecuteAsync("insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @id, @roleId)", new { tenant, id, roleId });

            if (linkingHinova)
            {
                try
                {
                    await tx.ExecuteAsync(
                        """
                        insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
                        values (@tenant, @id, @codigoVoluntario, @nomeHinova, @cpfHinova, @actor)
                        """,
                        new { tenant, id, codigoVoluntario, nomeHinova, cpfHinova, actor });
                }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    throw new ApiProblem(StatusCodes.Status409Conflict, "hinova.vinculo_duplicado");
                }
                await WriteAsync(tx, tenant, actor, "hinova.vincular", "hinova_voluntario_mapping", id, null, new { codigoVoluntario, nomeHinova, cpfHinova });
            }

            await tx.ExecuteAsync("select app.create_invite(@id, @hash, 'convite', @ttl)",
                new { id, hash = Tokens.Hash(token), ttl = options.Value.InviteTtlSeconds });
            await WriteAsync(tx, tenant, actor, "users.invite", "users", id, null, new { name, email = address, body.SupervisorId, roleIds });
            return 0;
```

The rest of the method (role-guard call, `email.SendAsync(...)` after the transaction, `Results.Created(...)`) is unchanged — an ApiProblem thrown inside the `db.InTenantAsync` lambda propagates before that transaction's `CommitAsync` runs, so `NpgsqlTransaction`'s dispose-without-commit rolls back everything the lambda did (the user insert included) — this is the same mechanism every other endpoint in this codebase already relies on (see `RoleEndpoints.CreateAsync`), not something new to verify.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter "UserTests&(Inviting_with_hinova_fields_creates_the_mapping_in_the_same_call|Inviting_with_a_codigo_voluntario_already_linked_creates_neither_user_nor_mapping|Inviting_with_hinova_fields_without_the_permission_is_forbidden|Inviting_without_hinova_fields_is_unchanged)"`
Expected: PASS (all 4).

- [ ] **Step 6: Run the full API test suite for regressions**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS, zero regressions (this endpoint is exercised by many existing tests — `UserTests.cs`, `PlatformTests.cs`, `RoleTests.cs`, E2E's "plataforma cria empresa" scenario indirectly via `provision_tenant`, which does NOT go through this endpoint, so it's unaffected).

- [ ] **Step 7: Commit**

```bash
git add src/Recorrencia.Api/Users/UserEndpoints.cs tests/Recorrencia.Api.Tests/UserTests.cs
git commit -m "feat(api): let POST /users optionally link a Hinova voluntário in the same transaction"
```

---

## Final Verification

- [ ] `dotnet test` (whole solution) passes.
- [ ] Confirm no frontend file was touched — this plan is backend-only; the parallel session owns `web/src/app/administracao/**` and any changes to `web/src/lib/types.ts` for the new request fields.
