# Gestão de Acessos — Cadastro Direto, Senha em Tela e Edição — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let an admin cadastrar diretamente um consultor já existente na Hinova (com senha definida em tela, sem depender de e-mail), trocar essa senha depois, e editar nome/e-mail/vínculo Hinova de qualquer participante.

**Architecture:** Estende `POST /users` com um campo `Password` opcional; um único endpoint novo `PUT /users/{id}/password` serve tanto o cadastro direto quanto trocas posteriores, apoiado numa nova função SQL `SECURITY DEFINER` (o `password_hash` da tabela `users` só é gravável por essas funções, nunca por `UPDATE`/`INSERT` direto do `app_user` — ver Task 1). Dois outros endpoints novos (`PUT /users/{id}`, `PUT /integracoes/hinova/mapeamentos/{userId}`) cobrem a edição de dados cadastrais. No frontend, a tela "Participantes" ganha o campo de senha no cadastro e uma nova rota de edição.

**Tech Stack:** C#/ASP.NET Core minimal APIs, Postgres com RLS, Dapper, Next.js 16 App Router (Server Actions), xUnit.

**Spec:** [docs/superpowers/specs/2026-09-25-gestao-acessos-design.md](../specs/2026-09-25-gestao-acessos-design.md)

## Global Constraints

- Permissão de todos os endpoints novos de usuário: `usuarios.convidar` (a mesma que já existe para convidar). O de vínculo Hinova usa `integracoes.gerenciar` (a mesma da tela de integrações hoje).
- Senha mínima de 10 e máxima de 128 caracteres (`Recorrencia.Api.Security.PasswordPolicy`, já existente — reaproveitar, não duplicar a regra).
- Nenhuma mudança de comportamento em `POST /users` quando `Password` não é enviado — os testes existentes de `UserTests.cs` continuam passando sem alteração.
- Fora de escopo: importação em lote, novos campos cadastrais (celular/endereço) — não implementar nada disso aqui.

## Nota sobre uma correção em relação ao spec

O spec (`docs/superpowers/specs/2026-09-25-gestao-acessos-design.md`) descreve `POST /users`/`PUT /users/{id}/password` como um `UPDATE`/`INSERT` direto na coluna `password_hash`. Isso não funciona: `0003_users_hierarchy.sql:148` só concede `grant update (name, email, status, supervisor_id) on users to app_user` — **sem `password_hash`** — e o `insert` (linha 147) também não inclui essa coluna. Essa restrição é deliberada (toda escrita de senha hoje passa por uma função `security definer`, nunca por SQL direto do `app_user` — ver `app.consume_invite`/`app.rehash_own_password` em `0005_auth_functions.sql`). Este plano segue essa mesma convenção: Task 1 cria `app.set_password_admin`, uma função `security definer` que Tasks 3 e 4 chamam — nenhuma delas grava `password_hash` diretamente. O comportamento final é idêntico ao do spec; só o mecanismo muda.

---

### Task 1: Função SQL `app.set_password_admin`

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0018_set_password_admin.sql`
- Test: `tests/Recorrencia.Db.Tests/AuthFunctionTests.cs`

**Interfaces:**
- Produces: `app.set_password_admin(p_user uuid, p_password_hash text) returns void` — grava `password_hash`/`status = 'ativo'` no usuário e revoga todas as sessões dele. Não valida status nem existência (o chamador faz isso antes, com `for update`, na mesma transação) — Tasks 3 e 4 consomem esta função.

- [ ] **Step 1: Escrever os testes que falham**

Adicionar ao final da classe `AuthFunctionTests` em `tests/Recorrencia.Db.Tests/AuthFunctionTests.cs`:

```csharp
    [Fact]
    public async Task Set_password_admin_activates_a_pending_user()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Convidado", status: "convidado");

        await db.AsAppUserAsync(t, null, (c, tx) => c.ExecuteAsync(
            "select app.set_password_admin(@user, @hash)", new { user = u, hash = "novo-hash" }, tx));

        Assert.Equal("novo-hash", await _seed.ScalarAsync<string>("select password_hash from users where id = @u", new { u }));
        Assert.Equal("ativo", await _seed.ScalarAsync<string>("select status from users where id = @u", new { u }));
    }

    [Fact]
    public async Task Set_password_admin_revokes_existing_sessions()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Ativa", status: "ativo");
        await db.AsAppUserAsync(t, u, (c, tx) => c.ExecuteScalarAsync<Guid>(
            "select app.create_session(@hash, @tenant, @user, 3600, 86400, @ip, @ua)",
            new { hash = H("tok-set-password-admin"), tenant = t, user = u, ip = "203.0.113.7", ua = "teste" }, tx));

        await db.AsAppUserAsync(t, null, (c, tx) => c.ExecuteAsync(
            "select app.set_password_admin(@user, @hash)", new { user = u, hash = "outro-hash" }, tx));

        Assert.Equal(0, await _seed.ScalarAsync<int>("select count(*) from sessions where user_id = @u", new { u }));
    }
```

(A classe já tem `using System.Security.Cryptography;`/`using System.Text;` e o helper privado `H(string token)` no topo — reaproveitar, não duplicar.)

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `cd tests/Recorrencia.Db.Tests && dotnet test --filter "Set_password_admin"`
Expected: FAIL — `function app.set_password_admin(uuid, text) does not exist`

- [ ] **Step 3: Criar a migration**

Criar `src/Recorrencia.Db/Scripts/0018_set_password_admin.sql`:

```sql
create function app.set_password_admin(p_user uuid, p_password_hash text)
returns void
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
begin
  update users set password_hash = p_password_hash, status = 'ativo' where id = p_user;
  delete from sessions where user_id = p_user;
end
$$;

grant execute on function app.set_password_admin(uuid, text) to app_user, app_superadmin;
```

- [ ] **Step 4: Aplicar a migration e rodar os testes**

Run: `dotnet run --project src/Recorrencia.Db -- migrate` (a base de testes roda migrations automaticamente via Testcontainers; este comando é só para conferir que a migration em si não tem erro de sintaxe antes de rodar os testes)
Run: `cd tests/Recorrencia.Db.Tests && dotnet test --filter "Set_password_admin"`
Expected: PASS — 2 testes.

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Db/Scripts/0018_set_password_admin.sql tests/Recorrencia.Db.Tests/AuthFunctionTests.cs
git commit -m "feat(db): add app.set_password_admin for admin-driven password writes"
```

---

### Task 2: Permitir editar o vínculo Hinova (grant de UPDATE)

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0019_hinova_mapeamento_update_grant.sql`
- Test: `tests/Recorrencia.Db.Tests/HinovaIntegracaoTests.cs`

**Interfaces:**
- Produces: `app_user` passa a poder `UPDATE` as colunas `codigo_voluntario`, `nome_hinova`, `cpf_hinova` de `hinova_voluntario_mapping` (hoje só há `select, insert, delete` — ver `0014_hinova_integracao.sql:58`). Task 6 depende deste grant.

- [ ] **Step 1: Escrever o teste que falha**

Adicionar ao final da classe `HinovaIntegracaoTests` em `tests/Recorrencia.Db.Tests/HinovaIntegracaoTests.cs`:

```csharp
    [Fact]
    public async Task Administrador_can_update_a_mapeamento()
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

        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            """
            update hinova_voluntario_mapping set codigo_voluntario = '102', nome_hinova = 'Outro Nome', cpf_hinova = '22222222222'
             where tenant_id = @tenant and user_id = @vendedor
            """,
            new { tenant, vendedor }, tx));

        var codigo = await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteScalarAsync<string>(
            "select codigo_voluntario from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @vendedor",
            new { tenant, vendedor }, tx));
        Assert.Equal("102", codigo);
    }
```

- [ ] **Step 2: Rodar o teste e confirmar que falha**

Run: `cd tests/Recorrencia.Db.Tests && dotnet test --filter "Administrador_can_update_a_mapeamento"`
Expected: FAIL — `PostgresException` com `SqlState == PostgresErrorCodes.InsufficientPrivilege` (sem o grant, o `UPDATE` nem é permitido na tabela)

- [ ] **Step 3: Criar a migration**

Criar `src/Recorrencia.Db/Scripts/0019_hinova_mapeamento_update_grant.sql`:

```sql
grant update (codigo_voluntario, nome_hinova, cpf_hinova) on hinova_voluntario_mapping to app_user;
```

- [ ] **Step 4: Rodar o teste e confirmar que passa**

Run: `cd tests/Recorrencia.Db.Tests && dotnet test --filter "Administrador_can_update_a_mapeamento"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Db/Scripts/0019_hinova_mapeamento_update_grant.sql tests/Recorrencia.Db.Tests/HinovaIntegracaoTests.cs
git commit -m "feat(db): grant UPDATE on hinova_voluntario_mapping's editable columns"
```

---

### Task 3: `POST /users` ganha `Password` opcional (cadastro direto)

**Files:**
- Modify: `src/Recorrencia.Api/Users/UserEndpoints.cs:15` (record `InviteUserRequest`), `:58-114` (`InviteAsync`)
- Test: `tests/Recorrencia.Api.Tests/UserTests.cs`

**Interfaces:**
- Consumes: `app.set_password_admin(uuid, text)` (Task 1).
- Produces: `POST /users` aceita `password` no corpo; quando presente, o usuário criado já nasce `status = "ativo"`, sem e-mail de convite. `InviteUserRequest`/`CreateUserWithRolesAsync`/`CreateInviteTokenAsync` continuam com as mesmas assinaturas de hoje — nenhuma outra task ou arquivo (incluindo `SolicitacaoCadastroEndpoints.AprovarAsync`) precisa mudar por causa desta.

- [ ] **Step 1: Escrever os testes que falham**

Adicionar ao final da classe `UserTests` em `tests/Recorrencia.Api.Tests/UserTests.cs`:

```csharp
    [Fact]
    public async Task Creating_with_a_password_activates_immediately_without_email()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var email = $"direta@{s.Slug}.local";

        var created = await admin.PostAsync("/users", new { name = "Cadastro Direto", email, password = "senha-inicial-123" });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        Assert.Null(api.Emails.LastTo(email));

        var direta = api.Client(s.Slug);
        await direta.LoginAsync(email, "senha-inicial-123");

        var sees = await admin.GetJsonAsync<List<UserDto>>("/users");
        Assert.Contains(sees, u => u.Id == id && u.Status == "ativo");
    }

    [Fact]
    public async Task Creating_with_a_weak_password_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var email = $"fraca@{s.Slug}.local";

        var response = await admin.PostAsync("/users", new { name = "X", email, password = "curta" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.weak_password", await ApiClient.CodeAsync(response));
        Assert.Equal(0, await api.SqlScalarAsync<int>("select count(*) from users where email = @email", new { email }));
    }

    [Fact]
    public async Task Creating_with_a_password_and_hinova_fields_links_the_mapping_too()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var email = $"direta-hinova@{s.Slug}.local";

        var created = await admin.PostAsync("/users", new
        {
            name = "Direta Hinova", email, password = "senha-inicial-123",
            codigoVoluntario = "301", nomeHinova = "Direta Hinova", cpfHinova = "99988877766",
        });
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        Assert.Equal("301", await api.SqlScalarAsync<string>(
            "select codigo_voluntario from hinova_voluntario_mapping where user_id = @id", new { id }));
    }
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "Creating_with_a_password"`
Expected: FAIL — os três testes recebem `Created` mas o primeiro falha no login (usuário continua `convidado`, sem senha) e o segundo falha porque nenhuma senha é validada hoje (o usuário É criado com `email` "fraca", 400 nunca acontece).

- [ ] **Step 3: Implementar**

Em `src/Recorrencia.Api/Users/UserEndpoints.cs:15`, mudar:

```csharp
    public sealed record InviteUserRequest(string? Name, string? Email, Guid? SupervisorId, Guid[]? RoleIds,
        string? CodigoVoluntario, string? NomeHinova, string? CpfHinova);
```

para:

```csharp
    public sealed record InviteUserRequest(string? Name, string? Email, Guid? SupervisorId, Guid[]? RoleIds,
        string? CodigoVoluntario, string? NomeHinova, string? CpfHinova, string? Password);
```

Em `:58-59`, adicionar `PasswordHasher hasher` à assinatura de `InviteAsync`:

```csharp
    private static async Task<IResult> InviteAsync(InviteUserRequest body, RequestContext request, Database db,
        CurrentPermissions permissions, IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, PasswordHasher hasher, CancellationToken ct)
```

Depois do bloco de validação dos campos Hinova (`:90-91`, a checagem `hasHinovaFields && (...)`) e antes de `var id = Guid.CreateVersion7();` (`:93`), inserir:

```csharp
        string? passwordHash = null;
        if (body.Password is not null)
        {
            PasswordPolicy.Validate(body.Password);
            passwordHash = hasher.Hash(body.Password);
        }

```

Substituir o bloco de `:93-113` (do `var id = ...` até o `return Results.Created(...)`) por:

```csharp
        var id = Guid.CreateVersion7();
        var token = "";
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            await RoleGuards.EnsureCanGrantRolesAsync(tx, mine, roleIds);
            await CreateUserWithRolesAsync(tx, tenant, id, name, address, body.SupervisorId, roleIds);
            if (hasHinovaFields)
                await LinkHinovaAsync(tx, tenant, id, actor, codigoVoluntario, nomeHinova, cpfHinova);
            if (passwordHash is not null)
            {
                await tx.ExecuteAsync("select app.set_password_admin(@id, @passwordHash)", new { id, passwordHash });
                await WriteAsync(tx, tenant, actor, "users.cadastrar_com_senha", "users", id, null,
                    new { name, email = address, supervisorId = body.SupervisorId, roleIds });
            }
            else
            {
                token = await CreateInviteTokenAsync(tx, tenant, actor, id, name, address, body.SupervisorId, roleIds, options.Value.InviteTtlSeconds);
            }
            return 0;
        }, ct);

        if (passwordHash is null)
        {
            await email.SendAsync(address, "Convite de acesso",
                EmailTemplates.AccessLink(
                    "Convite de acesso",
                    $"Você foi convidado para acessar a plataforma de {request.TenantName}.",
                    "Definir minha senha",
                    links.SetPassword(request.TenantSlug!, token),
                    "O link vale por 72 horas.",
                    links.Logo(request.TenantSlug!)), ct);
        }
        return Results.Created($"/users/{id}", new { id });
```

`CreateUserWithRolesAsync` e `CreateInviteTokenAsync` (`:120-168`) **não mudam** — nenhuma edição nessas duas.

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "FullyQualifiedName~UserTests"`
Expected: PASS — todos os testes de `UserTests.cs`, incluindo os 3 novos e todos os pré-existentes sem alteração.

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Api/Users/UserEndpoints.cs tests/Recorrencia.Api.Tests/UserTests.cs
git commit -m "feat(api): let POST /users create an already-active user with an admin-set password"
```

---

### Task 4: `PUT /users/{id}/password`

**Files:**
- Modify: `src/Recorrencia.Api/Users/UserEndpoints.cs:35-42` (`MapUserEndpoints`)
- Modify: `web/src/lib/errors.ts`
- Test: `tests/Recorrencia.Api.Tests/UserTests.cs`

**Interfaces:**
- Consumes: `app.set_password_admin(uuid, text)` (Task 1).
- Produces: `PUT /users/{id}/password` — define/troca a senha de um usuário existente; usado depois pelo Task 8 (tela de edição).

- [ ] **Step 1: Escrever os testes que falham**

Adicionar ao final da classe `UserTests`:

```csharp
    [Fact]
    public async Task Admin_sets_a_new_password_and_revokes_existing_sessions()
    {
        var s = await api.SeedAsync();
        var maria = await LoginAsync(s, "maria");
        var admin = await LoginAsync(s, "admin");

        await ApiClient.ExpectAsync(
            await admin.PutAsync($"/users/{s.Maria}/password", new { password = "senha-nova-da-maria" }),
            HttpStatusCode.NoContent);

        Assert.Equal(HttpStatusCode.Unauthorized, (await maria.GetAsync("/me")).StatusCode);

        var novoLogin = api.Client(s.Slug);
        await novoLogin.LoginAsync($"maria@{s.Slug}.local", "senha-nova-da-maria");
    }

    [Fact]
    public async Task Setting_a_weak_password_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var response = await admin.PutAsync($"/users/{s.Maria}/password", new { password = "curta" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("auth.weak_password", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Setting_a_password_for_a_deactivated_user_is_a_conflict()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        await ApiClient.ExpectAsync(await admin.PostAsync($"/users/{s.Maria}/deactivate"), HttpStatusCode.NoContent);

        var response = await admin.PutAsync($"/users/{s.Maria}/password", new { password = "senha-nova-da-maria" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("users.desligado", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Setting_a_password_without_usuarios_convidar_is_forbidden()
    {
        var s = await api.SeedAsync();
        var joao = await LoginAsync(s, "joao");

        var response = await joao.PutAsync($"/users/{s.Maria}/password", new { password = "senha-nova-da-maria" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "sets_a_new_password|weak_password_is_rejected|deactivated_user_is_a_conflict|password_without_usuarios_convidar"`
Expected: FAIL — 404 em todos (a rota não existe ainda).

- [ ] **Step 3: Implementar**

Em `src/Recorrencia.Api/Users/UserEndpoints.cs`, adicionar o record (ao lado de `SupervisorRequest`/`UserRolesRequest`, perto da linha 17-18):

```csharp
    public sealed record SetUserPasswordRequest(string? Password);
```

Em `MapUserEndpoints` (`:35-42`), adicionar a rota:

```csharp
        app.MapPut("/users/{id:guid}/password", SetPasswordAsync).RequirePermission("usuarios.convidar");
```

Adicionar o handler (por exemplo, depois de `DeactivateAsync`, antes de `SetRolesAsync`):

```csharp
    private static async Task<IResult> SetPasswordAsync(Guid id, SetUserPasswordRequest body, RequestContext request,
        Database db, PasswordHasher hasher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        PasswordPolicy.Validate(body.Password);
        var hash = hasher.Hash(body.Password!);
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var status = await tx.QuerySingleOrDefaultAsync<string>("select status from users where id = @id for update", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            if (status == "desligado")
                throw new ApiProblem(StatusCodes.Status409Conflict, "users.desligado");
            await tx.ExecuteAsync("select app.set_password_admin(@id, @hash)", new { id, hash });
            await WriteAsync(tx, tenant, actor, "users.set_password_admin", "users", id, null, null);
            return 0;
        }, ct);
        return Results.NoContent();
    }
```

Em `web/src/lib/errors.ts`, adicionar (junto das outras entradas `users.*`):

```ts
  'users.not_found': 'Este participante não foi encontrado.',
  'users.desligado': 'Este participante está desligado — reative-o antes de definir uma senha.',
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "FullyQualifiedName~UserTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Api/Users/UserEndpoints.cs web/src/lib/errors.ts tests/Recorrencia.Api.Tests/UserTests.cs
git commit -m "feat(api): add PUT /users/{id}/password for admin-driven password resets"
```

---

### Task 5: `PUT /users/{id}` (editar nome/e-mail)

**Files:**
- Modify: `src/Recorrencia.Api/Users/UserEndpoints.cs:35-42` (`MapUserEndpoints`)
- Test: `tests/Recorrencia.Api.Tests/UserTests.cs`

**Interfaces:**
- Produces: `PUT /users/{id}` — atualiza `name`/`email`; usado depois pelo Task 8.

- [ ] **Step 1: Escrever os testes que falham**

Adicionar ao final da classe `UserTests`:

```csharp
    [Fact]
    public async Task Admin_updates_name_and_email()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var newEmail = $"maria-nova@{s.Slug}.local";

        await ApiClient.ExpectAsync(
            await admin.PutAsync($"/users/{s.Maria}", new { name = "Maria Nova Silva", email = newEmail }),
            HttpStatusCode.NoContent);

        var sees = await admin.GetJsonAsync<List<UserDto>>("/users");
        var maria = sees.Single(u => u.Id == s.Maria);
        Assert.Equal("Maria Nova Silva", maria.Name);
        Assert.Equal(newEmail, maria.Email);
    }

    [Fact]
    public async Task Updating_to_a_taken_email_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");

        var response = await admin.PutAsync($"/users/{s.Maria}", new { name = "Maria", email = $"joao@{s.Slug}.local" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("users.email_taken", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Updating_without_usuarios_convidar_is_forbidden()
    {
        var s = await api.SeedAsync();
        var joao = await LoginAsync(s, "joao");

        var response = await joao.PutAsync($"/users/{s.Maria}", new { name = "X", email = $"x@{s.Slug}.local" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "updates_name_and_email|taken_email_is_rejected|Updating_without_usuarios_convidar"`
Expected: FAIL — 404 (a rota não existe ainda).

- [ ] **Step 3: Implementar**

Adicionar o record (ao lado de `SetUserPasswordRequest`):

```csharp
    public sealed record UpdateUserRequest(string? Name, string? Email);
```

Em `MapUserEndpoints`, adicionar:

```csharp
        app.MapPut("/users/{id:guid}", UpdateAsync).RequirePermission("usuarios.convidar");
```

Adicionar o handler:

```csharp
    private static async Task<IResult> UpdateAsync(Guid id, UpdateUserRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var name = (body.Name ?? "").Trim();
        var address = (body.Email ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.name_required");
        if (!MailAddress.TryCreate(address, out var parsedAddress) || parsedAddress.Address != address)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "users.invalid_email");

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var current = await tx.QuerySingleOrDefaultAsync<UserRow>("select * from users where id = @id for update", new { id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            try
            {
                await tx.ExecuteAsync("update users set name = @name, email = @address where id = @id", new { name, address, id });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "users.email_taken");
            }
            await WriteAsync(tx, tenant, actor, "users.update", "users", id,
                new { current.Name, current.Email }, new { name, address });
            return 0;
        }, ct);
        return Results.NoContent();
    }
```

`UserRow` já existe no topo do arquivo (`:20-28`) — reaproveitar, não criar um novo tipo.

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "FullyQualifiedName~UserTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Api/Users/UserEndpoints.cs tests/Recorrencia.Api.Tests/UserTests.cs
git commit -m "feat(api): add PUT /users/{id} to edit name and email"
```

---

### Task 6: `PUT /integracoes/hinova/mapeamentos/{userId}`

**Files:**
- Modify: `src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs:40-48` (`MapHinovaEndpoints`)
- Modify: `web/src/lib/errors.ts`
- Test: `tests/Recorrencia.Api.Tests/HinovaMapeamentosTests.cs`

**Interfaces:**
- Consumes: o grant de `UPDATE` da Task 2.
- Produces: `PUT /integracoes/hinova/mapeamentos/{userId}` — usado depois pelo Task 8.

- [ ] **Step 1: Escrever os testes que falham**

Adicionar ao final da classe `HinovaMapeamentosTests` em `tests/Recorrencia.Api.Tests/HinovaMapeamentosTests.cs`:

```csharp
    [Fact]
    public async Task Updating_a_mapeamento_changes_its_fields()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);

        await ApiClient.ExpectAsync(
            await admin.PutAsync($"/integracoes/hinova/mapeamentos/{s.Joao}", new { codigoVoluntario = "102", nomeHinova = "Bruno Costa Lima", cpfHinova = "22233344455" }),
            HttpStatusCode.NoContent);

        var list = await admin.GetJsonAsync<List<MapeamentoDto>>("/integracoes/hinova/mapeamentos");
        var mapeamento = Assert.Single(list);
        Assert.Equal("102", mapeamento.CodigoVoluntario);
        Assert.Equal("Bruno Costa Lima", mapeamento.NomeHinova);
    }

    [Fact]
    public async Task Updating_a_mapeamento_that_does_not_exist_is_a_404()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var response = await admin.PutAsync($"/integracoes/hinova/mapeamentos/{s.Joao}",
            new { codigoVoluntario = "102", nomeHinova = "Bruno Costa Lima", cpfHinova = "22233344455" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("hinova.vinculo_not_found", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Updating_to_a_codigo_already_used_by_another_mapeamento_is_a_conflict()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        await ConfigureCredentialsAsync(admin);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Joao, codigoVoluntario = "101", nomeHinova = "Ana Paula Ferreira", cpfHinova = "11122233344" }),
            HttpStatusCode.Created);
        await ApiClient.ExpectAsync(
            await admin.PostAsync("/integracoes/hinova/mapeamentos", new { userId = s.Maria, codigoVoluntario = "102", nomeHinova = "Bruno Costa Lima", cpfHinova = "22233344455" }),
            HttpStatusCode.Created);

        var response = await admin.PutAsync($"/integracoes/hinova/mapeamentos/{s.Maria}",
            new { codigoVoluntario = "101", nomeHinova = "Bruno Costa Lima", cpfHinova = "22233344455" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("hinova.vinculo_duplicado", await ApiClient.CodeAsync(response));
    }
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "FullyQualifiedName~HinovaMapeamentosTests"`
Expected: FAIL — os 3 novos testes recebem 404 de rota inexistente (não confundir com o 404 esperado do segundo teste, que hoje seria "não achou a rota", não "não achou o vínculo").

- [ ] **Step 3: Implementar**

Em `src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs`, adicionar o record (ao lado de `CriarMapeamentoRequest`, linha 16):

```csharp
    public sealed record AtualizarMapeamentoRequest(string? CodigoVoluntario, string? NomeHinova, string? CpfHinova);
```

Em `MapHinovaEndpoints` (`:40-48`), adicionar:

```csharp
        app.MapPut("/integracoes/hinova/mapeamentos/{userId:guid}", AtualizarMapeamentoAsync).RequirePermission("integracoes.gerenciar");
```

Adicionar o handler (depois de `CriarMapeamentoAsync`, antes de `RemoverMapeamentoAsync`):

```csharp
    private static async Task<IResult> AtualizarMapeamentoAsync(Guid userId, AtualizarMapeamentoRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var codigo = (body.CodigoVoluntario ?? "").Trim();
        var nome = (body.NomeHinova ?? "").Trim();
        var cpf = (body.CpfHinova ?? "").Trim();
        if (codigo.Length == 0 || nome.Length == 0 || cpf.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "request.invalid");

        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var current = await tx.QuerySingleOrDefaultAsync<MapeamentoRow>(
                """
                select user_id, codigo_voluntario, nome_hinova, cpf_hinova
                  from hinova_voluntario_mapping
                 where tenant_id = @tenant and user_id = @userId
                 for update
                """,
                new { tenant, userId }) ?? throw new ApiProblem(StatusCodes.Status404NotFound, "hinova.vinculo_not_found");
            try
            {
                await tx.ExecuteAsync(
                    "update hinova_voluntario_mapping set codigo_voluntario = @codigo, nome_hinova = @nome, cpf_hinova = @cpf where tenant_id = @tenant and user_id = @userId",
                    new { tenant, userId, codigo, nome, cpf });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new ApiProblem(StatusCodes.Status409Conflict, "hinova.vinculo_duplicado");
            }
            await WriteAsync(tx, tenant, actor, "hinova.atualizar_vinculo", "hinova_voluntario_mapping", userId,
                new { current.CodigoVoluntario, current.NomeHinova, current.CpfHinova }, new { codigo, nome, cpf });
            return 0;
        }, ct);

        return Results.NoContent();
    }
```

`MapeamentoRow` já existe no topo do arquivo (`:30-38`) — reaproveitar. A consulta seleciona só as 4 colunas usadas (`UserName`/`MappedAt` ficam no valor default de `MapeamentoRow`, sem uso aqui).

Em `web/src/lib/errors.ts`, adicionar (junto das outras entradas `hinova.*`):

```ts
  'hinova.vinculo_not_found': 'Este participante não tem vínculo com a Hinova.',
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "FullyQualifiedName~HinovaMapeamentosTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs web/src/lib/errors.ts tests/Recorrencia.Api.Tests/HinovaMapeamentosTests.cs
git commit -m "feat(api): add PUT /integracoes/hinova/mapeamentos/{userId} to edit a mapping"
```

---

### Task 7: Frontend — senha no cadastro direto

**Files:**
- Modify: `web/src/app/administracao/participante-search.tsx`
- Modify: `web/src/app/administracao/actions.ts:10-47` (`criarParticipante`)

**Interfaces:**
- Consumes: `POST /users` com `password` (Task 3).

Este task não tem um ciclo de teste automatizado próprio (o projeto não tem testes de componente React — `web/src/**` só tem `npm run e2e`, cujo cenário completo é coberto pelo Task 8/manual abaixo). Verificação manual:

- [ ] **Step 1: Adicionar o campo de senha ao formulário**

Em `web/src/app/administracao/participante-search.tsx`, no bloco `{selected && (...)}` (linhas 60-80), depois do `<label>E-mail...</label>` e antes do `<p className="muted">A Hinova nem sempre tem e-mail...</p>`, inserir:

```tsx
          <label>
            Senha inicial
            <input type="password" name="password" required minLength={10} maxLength={128} autoComplete="new-password" />
          </label>
```

E, depois do parágrafo existente sobre e-mail, adicionar:

```tsx
          <p className="muted">A senha vale para o primeiro acesso — combine com {selected.nome} por fora (WhatsApp, telefone). Mínimo de 10 caracteres.</p>
```

- [ ] **Step 2: Passar o campo no server action**

Em `web/src/app/administracao/actions.ts`, dentro de `criarParticipante` (linhas 27-39), adicionar `password` ao corpo:

```ts
  try {
    await apiFetch('/users', {
      method: 'POST',
      body: {
        name: String(formData.get('nomeHinova') ?? ''),
        email: String(formData.get('email') ?? ''),
        password: String(formData.get('password') ?? ''),
        supervisorId: supervisorId || undefined,
        codigoVoluntario: String(formData.get('codigoVoluntario') ?? ''),
        nomeHinova: String(formData.get('nomeHinova') ?? ''),
        cpfHinova: String(formData.get('cpfHinova') ?? ''),
        roleIds,
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
```

- [ ] **Step 3: Verificar manualmente**

Run: `cd web && npm run build` (confirma que compila sem erros de tipo)
Expected: build sem erros.

Com a API e o web rodando localmente (ver `README.md`, seção Desenvolvimento): abrir `/administracao/participantes`, clicar em "Novo participante", buscar um voluntário Hinova, preencher e-mail e a nova senha, confirmar. Esperado: sem tela de "e-mail enviado", o participante aparece na lista imediatamente, e é possível logar com o e-mail e a senha definidos.

- [ ] **Step 4: Commit**

```bash
git add web/src/app/administracao/participante-search.tsx web/src/app/administracao/actions.ts
git commit -m "feat(web): collect an initial password when adding a participant directly"
```

---

### Task 8: Frontend — tela de edição de participante

**Files:**
- Modify: `web/src/app/administracao/participantes/page.tsx`
- Modify: `web/src/app/administracao/actions.ts` (adicionar `editarParticipante`)
- Create: `web/src/app/administracao/participantes/[id]/editar/page.tsx`
- Create: `web/src/app/administracao/participantes/[id]/editar/participante-edit-form.tsx`

**Interfaces:**
- Consumes: `PUT /users/{id}` (Task 5), `PUT /users/{id}/password` (Task 4), `PUT /integracoes/hinova/mapeamentos/{userId}` (Task 6), `HinovaMapeamento`/`UserNode` (`web/src/lib/types.ts`, já existentes).

Sem teste automatizado próprio (mesma razão do Task 7) — verificação manual no Step 5.

- [ ] **Step 1: Adicionar a coluna de ações na lista**

Em `web/src/app/administracao/participantes/page.tsx`, depois da linha que define `canAdd` (perto da linha 45), adicionar:

```tsx
  const canEdit = has('usuarios.convidar');
```

No `<thead>` (dentro do bloco da tabela), mudar:

```tsx
                <tr>
                  <th>Nome</th>
                  <th>E-mail</th>
                  <th>Perfil</th>
                  <th>Supervisor</th>
                </tr>
```

para:

```tsx
                <tr>
                  <th>Nome</th>
                  <th>E-mail</th>
                  <th>Perfil</th>
                  <th>Supervisor</th>
                  {canEdit && <th>Ações</th>}
                </tr>
```

No estado vazio da tabela, mudar `colSpan={4}` para `colSpan={canEdit ? 5 : 4}`.

Na linha de cada participante, depois de `<td>{u.supervisorId ? ... : 'Sem indicador'}</td>`, adicionar:

```tsx
                      {canEdit && (
                        <td>
                          <a className="action-link" href={`/administracao/participantes/${u.id}/editar`}>
                            Editar
                          </a>
                        </td>
                      )}
```

- [ ] **Step 2: Criar a página de edição**

Criar `web/src/app/administracao/participantes/[id]/editar/page.tsx`:

```tsx
import { notFound } from 'next/navigation';
import { AppShell } from '../../../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import type { HinovaMapeamento, Me, UserNode } from '@/lib/types';
import { ParticipanteEditForm } from './participante-edit-form';

export default async function EditarParticipantePage({ params }: { params: Promise<{ id: string }> }) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const has = (key: string) => me.permissions.some((p) => p.key === key);
  if (!has('usuarios.convidar')) {
    return (
      <AppShell me={me} active="participantes">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui edição de participantes.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const { id } = await params;
  const users = await apiFetch<UserNode[]>('/users');
  const user = users.find((u) => u.id === id);
  if (!user) notFound();

  const canSeeHinova = has('integracoes.gerenciar');
  const mapeamentos = canSeeHinova ? await apiFetch<HinovaMapeamento[]>('/integracoes/hinova/mapeamentos') : [];
  const mapeamento = mapeamentos.find((m) => m.userId === id) ?? null;

  return (
    <AppShell me={me} active="participantes">
      <div className="shell">
        <div className="page-heading">
          <div>
            <p className="eyebrow">Administração comercial</p>
            <h1>Editar participante</h1>
            <p className="muted">Atualize os dados de {user.name}.</p>
          </div>
          <a className="action-link" href="/administracao/participantes">
            Voltar
          </a>
        </div>
        <section className="card">
          <ParticipanteEditForm user={user} mapeamento={mapeamento} />
        </section>
      </div>
    </AppShell>
  );
}
```

- [ ] **Step 3: Criar o formulário**

Criar `web/src/app/administracao/participantes/[id]/editar/participante-edit-form.tsx`:

```tsx
'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import type { HinovaMapeamento, UserNode } from '@/lib/types';
import { editarParticipante } from '../../../actions';

export function ParticipanteEditForm({ user, mapeamento }: { user: UserNode; mapeamento: HinovaMapeamento | null }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(editarParticipante, {});

  return (
    <form action={formAction} className="form">
      <input type="hidden" name="id" value={user.id} />
      <label>
        Nome
        <input type="text" name="name" defaultValue={user.name} required />
      </label>
      <label>
        E-mail
        <input type="email" name="email" defaultValue={user.email} required />
      </label>
      {mapeamento && (
        <>
          <input type="hidden" name="hasMapeamento" value="1" />
          <label>
            Código de voluntário (Hinova)
            <input type="text" name="codigoVoluntario" defaultValue={mapeamento.codigoVoluntario} required />
          </label>
          <label>
            Nome na Hinova
            <input type="text" name="nomeHinova" defaultValue={mapeamento.nomeHinova} required />
          </label>
          <label>
            CPF na Hinova
            <input type="text" name="cpfHinova" defaultValue={mapeamento.cpfHinova} required />
          </label>
        </>
      )}
      <label>
        Nova senha
        <input type="password" name="password" minLength={10} maxLength={128} autoComplete="new-password" />
      </label>
      <p className="muted">Deixe em branco para não alterar a senha.</p>
      {state.error && (
        <p role="alert" className="error">
          {state.error}
        </p>
      )}
      {state.message && <p className="success">{state.message}</p>}
      <div className="inline">
        <button type="submit" disabled={pending}>
          {pending ? 'Salvando…' : 'Salvar'}
        </button>
      </div>
    </form>
  );
}
```

- [ ] **Step 4: Criar o server action**

Em `web/src/app/administracao/actions.ts`, adicionar ao final do arquivo:

```ts
export async function editarParticipante(_: FormState, formData: FormData): Promise<FormState> {
  const id = String(formData.get('id') ?? '');
  const name = String(formData.get('name') ?? '');
  const email = String(formData.get('email') ?? '');
  const hasMapeamento = formData.get('hasMapeamento') === '1';
  const password = String(formData.get('password') ?? '');

  try {
    await apiFetch(`/users/${id}`, { method: 'PUT', body: { name, email } });
    if (hasMapeamento) {
      await apiFetch(`/integracoes/hinova/mapeamentos/${id}`, {
        method: 'PUT',
        body: {
          codigoVoluntario: String(formData.get('codigoVoluntario') ?? ''),
          nomeHinova: String(formData.get('nomeHinova') ?? ''),
          cpfHinova: String(formData.get('cpfHinova') ?? ''),
        },
      });
    }
    if (password.length > 0) {
      await apiFetch(`/users/${id}/password`, { method: 'PUT', body: { password } });
    }
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/participantes');
  return { message: 'Participante atualizado.' };
}
```

- [ ] **Step 5: Verificar manualmente**

Run: `cd web && npm run build`
Expected: build sem erros de tipo.

Com a API e o web rodando localmente: abrir `/administracao/participantes`, clicar em "Editar" num participante com vínculo Hinova. Verificar: (a) trocar nome/e-mail e salvar funciona e reflete na lista; (b) trocar o código de voluntário Hinova funciona; (c) preencher "Nova senha" e salvar permite logar com essa senha depois; (d) deixar "Nova senha" em branco não altera a senha existente (logar com a senha antiga continua funcionando); (e) um usuário sem `integracoes.gerenciar` (ex.: um coordenador, se houver algum com `usuarios.convidar`) não vê os campos de vínculo Hinova na tela de edição.

- [ ] **Step 6: Commit**

```bash
git add web/src/app/administracao/participantes/page.tsx web/src/app/administracao/actions.ts \
  web/src/app/administracao/participantes/\[id\]/editar/page.tsx \
  web/src/app/administracao/participantes/\[id\]/editar/participante-edit-form.tsx
git commit -m "feat(web): add a participant edit screen (name, email, hinova link, password)"
```
