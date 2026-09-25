# Convite por indicação — cadastro auto-serviço com aprovação (Fase 5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let any Hinova-linked consultant share a personal, permanent invite link; whoever opens it fills a short public form (no login); the submission lands in an admin approval queue; on approval the backend registers the person as a real Hinova voluntário (`Cadastrar Voluntário`, checked first for CPF collisions via `Buscar Voluntário`) and creates their APROVEC user under the referrer, automatically.

**Architecture:** Two new Postgres tables (`solicitacoes_cadastro`, `convite_links`) hold pending applications and permanent per-user link tokens — nobody becomes a real `users` row until approved. Two new Hinova client methods (`BuscarVoluntarioAsync`, `CadastrarVoluntarioAsync`) extend the existing `IHinovaClient`. The approval endpoint reuses (via extraction) the exact same user-creation logic `POST /users` already uses. Two new public, unauthenticated endpoints (resolve a token, submit a form) sit alongside the existing authenticated ones, following the same pattern already used by `/auth/login` and `/auth/set-password`.

**Tech Stack:** C# / ASP.NET Core minimal APIs, Dapper, PostgreSQL (RLS), Next.js 16 (App Router, Server Components + Server Actions), Playwright.

**Spec:** `docs/superpowers/specs/2026-09-25-convites-indicacao-design.md`

## Global Constraints

- `formato_pagamento`, `valor_pagamento` e `codigo_classificacao` da Hinova nunca são coletados nem enviados (spec, seção "Descobertas").
- CPF já cadastrado na Hinova **bloqueia** a aprovação — nunca sobrescreve silenciosamente (spec, decisão 1).
- Rejeição não envia e-mail nem qualquer notificação ao candidato (spec, decisão 2).
- O indicador só pode gerar/usar seu link se já tiver uma linha em `hinova_voluntario_mapping` (spec, decisão 3).
- `convite_links.token` é guardado em texto puro (não hash) — é um identificador público reutilizável, não uma credencial de uso único (spec corrigida, commit `e969cc3`).
- Nenhuma permissão nova: aprovar/rejeitar/listar a fila exige exatamente `usuarios.convidar` + `integracoes.gerenciar` (hoje só o Administrador tem as duas).
- Nenhuma linha em `users` é criada antes da aprovação.
- A criação do usuário durante a aprovação reaproveita as mesmas rotinas internas que `POST /users` já usa (extraídas de `UserEndpoints.cs`), não uma chamada HTTP interna.

---

### Task 1: Migração de banco — `solicitacoes_cadastro` e `convite_links`

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0015_convites_indicacao.sql`
- Test: `tests/Recorrencia.Db.Tests/ConvitesIndicacaoTests.cs`

**Interfaces:**
- Consumes: nada de tasks anteriores.
- Produces: tabelas `solicitacoes_cadastro (id, tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, complemento, bairro, cidade, estado, status, criado_em, resolvido_em, resolvido_por, user_id_resultante)` e `convite_links (tenant_id, user_id, token, criado_em)`. Tasks 4/5/6 fazem `select`/`insert`/`update` diretamente nessas tabelas via Dapper (sem funções PL/pgSQL novas).

- [ ] **Step 1: Escrever a migração**

```sql
create table solicitacoes_cadastro (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  indicador_user_id uuid not null,
  nome text not null,
  cpf text not null,
  celular text not null,
  email text not null,
  cep text not null,
  logradouro text not null,
  numero text not null,
  complemento text,
  bairro text not null,
  cidade text not null,
  estado text not null,
  status text not null default 'pendente' check (status in ('pendente', 'aprovado', 'rejeitado')),
  criado_em timestamptz not null default now(),
  resolvido_em timestamptz,
  resolvido_por uuid,
  user_id_resultante uuid,
  foreign key (tenant_id, indicador_user_id) references users (tenant_id, id),
  foreign key (tenant_id, resolvido_por) references users (tenant_id, id),
  foreign key (tenant_id, user_id_resultante) references users (tenant_id, id),
  check (status = 'pendente' or (resolvido_em is not null and resolvido_por is not null)),
  check (status <> 'aprovado' or user_id_resultante is not null)
);

create table convite_links (
  tenant_id uuid not null references tenants (id),
  user_id uuid not null,
  token text not null,
  criado_em timestamptz not null default now(),
  primary key (tenant_id, user_id),
  unique (token),
  foreign key (tenant_id, user_id) references users (tenant_id, id)
);

do $$
declare
  t text;
begin
  foreach t in array array['solicitacoes_cadastro', 'convite_links']
  loop
    execute format('alter table %I enable row level security', t);
    execute format('alter table %I force row level security', t);
    execute format(
      'create policy %I on %I as restrictive for all to app_user using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant())',
      t || '_tenant_isolation', t);
  end loop;
end
$$;

-- Qualquer visitante anônimo (dentro do tenant resolvido pelo host, sem usuário autenticado)
-- pode criar uma solicitação -- é exatamente isso que o formulário público faz. A leitura/edição
-- (fila de aprovação) exige as mesmas duas permissões que já protegem a criação manual de usuário
-- com vínculo Hinova.
create policy solicitacoes_cadastro_insert_publico on solicitacoes_cadastro for insert to app_user
  with check (true);
-- Postgres's CREATE POLICY takes exactly one command per FOR clause (no "for select, update"
-- comma list) -- select and update need their own policies, even though the condition is identical.
create policy solicitacoes_cadastro_select_admin on solicitacoes_cadastro for select to app_user
  using ((select app.has_permission('usuarios.convidar')) and (select app.has_permission('integracoes.gerenciar')));
create policy solicitacoes_cadastro_update_admin on solicitacoes_cadastro for update to app_user
  using ((select app.has_permission('usuarios.convidar')) and (select app.has_permission('integracoes.gerenciar')))
  with check ((select app.has_permission('usuarios.convidar')) and (select app.has_permission('integracoes.gerenciar')));

-- Qualquer visitante anônimo com o token em mãos pode ler o link (para mostrar "você foi
-- indicado por X" na página pública) -- o token em si (32 bytes aleatórios) é o segredo, não a
-- permissão de quem pergunta. Cada usuário gerencia sua própria linha.
create policy convite_links_self on convite_links for all to app_user
  using (user_id = app.current_user_id())
  with check (user_id = app.current_user_id());
create policy convite_links_select_publico on convite_links for select to app_user using (true);

grant select, insert, update on solicitacoes_cadastro to app_user;
grant select, insert on convite_links to app_user;
```

- [ ] **Step 2: Rodar a suíte de banco para confirmar que a migração aplica sem erro**

Run: `dotnet test tests/Recorrencia.Db.Tests/Recorrencia.Db.Tests.csproj`
Expected: PASS (a suíte roda todas as migrações do zero contra um Postgres efêmero; qualquer erro de sintaxe na migração falha aqui).

- [ ] **Step 3: Escrever os testes de RLS/constraints da migração**

`tests/Recorrencia.Db.Tests/ConvitesIndicacaoTests.cs` — siga exatamente o padrão de `tests/Recorrencia.Db.Tests/HinovaIntegracaoTests.cs` (mesma classe `Seed`, mesmo `db.AsAppUserAsync`, mesmo `[Collection(DbCollection.Name)]`).

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class ConvitesIndicacaoTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    private Task ActivateAsync(Guid userId) =>
        _seed.ExecAsync("update users set status = 'ativo', password_hash = 'hash-de-teste' where id = @userId", new { userId });

    [Fact]
    public async Task Anyone_can_insert_a_solicitacao_without_being_a_real_user()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var indicador = await _seed.UserAsync(tenant, "Indicador");
        var id = Guid.NewGuid();

        // "Sem usuário" = a mesma chamada que o backend faz via db.InTenantAsync(tenant, null, ...)
        // para o fluxo público: tenant setado, app.current_user_id() nulo. O id é gerado aqui em
        // C# e inserido explicitamente -- nunca via "returning", que sob RLS é filtrado pelas
        // policies de SELECT da tabela: uma sessão anônima sem nenhuma policy de select anônimo
        // não veria a linha de volta (RETURNING de zero linhas visíveis, não um erro).
        await db.AsAppUserAsync(tenant, null, (c, tx) => c.ExecuteAsync(
            """
            insert into solicitacoes_cadastro
              (id, tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, bairro, cidade, estado)
            values (@id, @tenant, @indicador, 'Fulano', '11111111111', '11999999999', 'fulano@teste.local',
                    '30000000', 'Rua X', '1', 'Bairro', 'Cidade', 'UF')
            """,
            new { id, tenant, indicador }, tx));

        // Confirma que a linha existe de verdade lendo como admin (que já tem select legítimo
        // via solicitacoes_cadastro_select_admin) -- não como o próprio anônimo: o fluxo público
        // nunca precisa ler solicitacoes_cadastro de volta, então a tabela não tem (nem deveria
        // ter) uma policy de select anônimo, que exporia CPF/endereço/e-mail de todo mundo no tenant.
        var count = await db.AsAppUserAsync(tenant, admin, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from solicitacoes_cadastro where id = @id", new { id }, tx));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task User_without_usuarios_convidar_cannot_see_pending_solicitacoes()
    {
        var tenant = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(tenant);
        var indicador = await _seed.UserAsync(tenant, "Indicador");
        await db.AsAppUserAsync(tenant, null, (c, tx) => c.ExecuteAsync(
            """
            insert into solicitacoes_cadastro
              (tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, bairro, cidade, estado)
            values (@tenant, @indicador, 'Fulano', '11111111111', '11999999999', 'fulano@teste.local',
                    '30000000', 'Rua X', '1', 'Bairro', 'Cidade', 'UF')
            """,
            new { tenant, indicador }, tx));

        var semPermissao = await _seed.UserAsync(tenant, "Sem convidar");
        await _seed.AssignRoleAsync(tenant, semPermissao, await _seed.RoleAsync(tenant, "Só estrutura", ("estrutura.visualizar", "tenant")));

        var count = await db.AsAppUserAsync(tenant, semPermissao, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from solicitacoes_cadastro where tenant_id = @tenant", new { tenant }, tx));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Admin_with_both_permissions_can_see_and_update_solicitacoes()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var indicador = await _seed.UserAsync(tenant, "Indicador");
        var id = Guid.NewGuid();
        await db.AsAppUserAsync(tenant, null, (c, tx) => c.ExecuteAsync(
            """
            insert into solicitacoes_cadastro
              (id, tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, bairro, cidade, estado)
            values (@id, @tenant, @indicador, 'Fulano', '11111111111', '11999999999', 'fulano@teste.local',
                    '30000000', 'Rua X', '1', 'Bairro', 'Cidade', 'UF')
            """,
            new { id, tenant, indicador }, tx));

        var count = await db.AsAppUserAsync(tenant, admin, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from solicitacoes_cadastro where tenant_id = @tenant", new { tenant }, tx));
        Assert.Equal(1, count);

        await db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            "update solicitacoes_cadastro set status = 'rejeitado', resolvido_em = now(), resolvido_por = @admin where id = @id",
            new { admin, id }, tx));
    }

    [Fact]
    public async Task Approving_without_a_resultado_user_id_is_rejected_by_the_check_constraint()
    {
        var (tenant, admin) = await _seed.ProvisionAsync();
        await ActivateAsync(admin);
        var indicador = await _seed.UserAsync(tenant, "Indicador");
        var id = Guid.NewGuid();
        await db.AsAppUserAsync(tenant, null, (c, tx) => c.ExecuteAsync(
            """
            insert into solicitacoes_cadastro
              (id, tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, bairro, cidade, estado)
            values (@id, @tenant, @indicador, 'Fulano', '11111111111', '11999999999', 'fulano@teste.local',
                    '30000000', 'Rua X', '1', 'Bairro', 'Cidade', 'UF')
            """,
            new { id, tenant, indicador }, tx));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, admin, (c, tx) => c.ExecuteAsync(
            "update solicitacoes_cadastro set status = 'aprovado', resolvido_em = now(), resolvido_por = @admin where id = @id",
            new { admin, id }, tx)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task A_user_can_create_and_read_their_own_convite_link()
    {
        var tenant = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(tenant);
        var user = await _seed.UserAsync(tenant, "Consultor");

        await db.AsAppUserAsync(tenant, user, (c, tx) => c.ExecuteAsync(
            "insert into convite_links (tenant_id, user_id, token) values (@tenant, @user, 'token-de-teste')",
            new { tenant, user }, tx));

        var token = await db.AsAppUserAsync(tenant, user, (c, tx) =>
            c.ExecuteScalarAsync<string>("select token from convite_links where tenant_id = @tenant and user_id = @user", new { tenant, user }, tx));
        Assert.Equal("token-de-teste", token);
    }

    [Fact]
    public async Task Anyone_can_resolve_a_convite_link_by_token_without_being_a_real_user()
    {
        var tenant = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(tenant);
        var user = await _seed.UserAsync(tenant, "Consultor");
        await db.AsAppUserAsync(tenant, user, (c, tx) => c.ExecuteAsync(
            "insert into convite_links (tenant_id, user_id, token) values (@tenant, @user, 'token-publico')",
            new { tenant, user }, tx));

        var found = await db.AsAppUserAsync(tenant, null, (c, tx) =>
            c.ExecuteScalarAsync<Guid?>("select user_id from convite_links where tenant_id = @tenant and token = 'token-publico'", new { tenant }, tx));
        Assert.Equal(user, found);
    }

    [Fact]
    public async Task A_user_cannot_insert_a_convite_link_for_someone_else()
    {
        var tenant = await _seed.TenantAsync();
        await _seed.EnableModulesAsync(tenant);
        var user = await _seed.UserAsync(tenant, "Consultor");
        var outro = await _seed.UserAsync(tenant, "Outro");

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(tenant, user, (c, tx) => c.ExecuteAsync(
            "insert into convite_links (tenant_id, user_id, token) values (@tenant, @outro, 'token-indevido')",
            new { tenant, outro }, tx)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Tenant_isolation_hides_another_tenants_solicitacoes()
    {
        var (tenantA, adminA) = await _seed.ProvisionAsync();
        await ActivateAsync(adminA);
        var (tenantB, adminB) = await _seed.ProvisionAsync();
        await ActivateAsync(adminB);
        var indicadorB = await _seed.UserAsync(tenantB, "Indicador B");
        await db.AsAppUserAsync(tenantB, null, (c, tx) => c.ExecuteAsync(
            """
            insert into solicitacoes_cadastro
              (tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, bairro, cidade, estado)
            values (@tenantB, @indicadorB, 'Fulano', '11111111111', '11999999999', 'fulano@teste.local',
                    '30000000', 'Rua X', '1', 'Bairro', 'Cidade', 'UF')
            """,
            new { tenantB, indicadorB }, tx));

        var count = await db.AsAppUserAsync(tenantA, adminA, (c, tx) =>
            c.ExecuteScalarAsync<int>("select count(*) from solicitacoes_cadastro where tenant_id = @tenantB", new { tenantB }, tx));
        Assert.Equal(0, count);
    }
}
```

- [ ] **Step 4: Rodar os testes**

Run: `dotnet test tests/Recorrencia.Db.Tests/Recorrencia.Db.Tests.csproj`
Expected: PASS (todos os testes acima, incluindo os pré-existentes).

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Db/Scripts/0015_convites_indicacao.sql tests/Recorrencia.Db.Tests/ConvitesIndicacaoTests.cs
git commit -m "feat(db): add solicitacoes_cadastro and convite_links tables"
```

---

### Task 2: Cliente Hinova — `BuscarVoluntarioAsync` e `CadastrarVoluntarioAsync`

**Files:**
- Modify: `src/Recorrencia.Api/Integracoes/IHinovaClient.cs`
- Modify: `src/Recorrencia.Api/Integracoes/HinovaClient.cs`
- Modify: `src/Recorrencia.Api/Integracoes/DevFakeHinovaClient.cs`
- Modify: `src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs`
- Create: `src/Recorrencia.Api/Integracoes/HinovaAuth.cs`
- Modify: `tests/Recorrencia.Api.Tests/HinovaClientTests.cs`
- Modify: `tests/Recorrencia.Api.Tests/FakeHinovaClient.cs`

**Interfaces:**
- Consumes: nada de tasks anteriores.
- Produces: `IHinovaClient.BuscarVoluntarioAsync(tokenUsuario, cpfOuCodigo, ct) : Task<HinovaVoluntarioDetalhe?>` (retorna `null` quando a Hinova responde 404 -- "não encontrado" é um resultado esperado, não uma exceção); `IHinovaClient.CadastrarVoluntarioAsync(tokenUsuario, CadastrarVoluntarioRequest, ct) : Task<string>` (retorna o `codigo_voluntario`); `HinovaVoluntarioDetalhe(string Codigo, string Nome, string Cpf, IReadOnlyList<string> CooperativaCodigos)`; `CadastrarVoluntarioRequest(string Nome, string Cpf, string? Celular, string? Email, string Logradouro, string Numero, string? Complemento, string Bairro, string Cidade, string Estado, string Cep, IReadOnlyList<string> CooperativaCodigos, string? CodigoVoluntarioVinculado, string? Obs)`; `HinovaAuth.GetTokenUsuarioAsync(Database db, Guid tenant, Guid user, AesGcmCipher cipher, IHinovaClient hinova, CancellationToken ct) : Task<string>` (lança `ApiProblem(400, "hinova.nao_configurado")` ou `ApiProblem(400, "hinova.credenciais_invalidas")`, iguais aos já usados em `HinovaEndpoints.ListarVoluntariosAsync`). Tasks 5 e 6 consomem esses quatro membros.

- [ ] **Step 1: Escrever os testes que falham para os dois métodos novos**

Adicione ao final de `tests/Recorrencia.Api.Tests/HinovaClientTests.cs` (mesma classe, mesmo `NewClient` helper já existente no arquivo):

```csharp
    [Fact]
    public async Task BuscarVoluntario_returns_null_on_a_404()
    {
        var (client, handler) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var found = await client.BuscarVoluntarioAsync("token-usuario", "99999999999", CancellationToken.None);

        Assert.Null(found);
        Assert.Equal("https://api.hinova.com.br/api/sga/v2/buscar/voluntario/99999999999", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task BuscarVoluntario_parses_codigo_and_cooperativa_codes_on_success()
    {
        const string json = """
            {
                "codigo_voluntario": "99",
                "nome": "HINOVA SOLUÇÕES DIGITAIS",
                "cpf": "99999999999",
                "cooperativas": [{ "codigo_cooperativa": "9", "nome_cooperativa": "Central" }]
            }
            """;
        var (client, handler) = NewClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        var found = await client.BuscarVoluntarioAsync("token-usuario", "99", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(("99", "HINOVA SOLUÇÕES DIGITAIS", "99999999999"), (found!.Codigo, found.Nome, found.Cpf));
        Assert.Equal(["9"], found.CooperativaCodigos);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("token-usuario", handler.LastRequest.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task CadastrarVoluntario_sends_every_field_and_returns_codigo_voluntario()
    {
        HttpRequestMessage? captured = null;
        var (client, handler) = NewClient(req =>
        {
            captured = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { mensagem = "OK", codigo_voluntario = "131" }),
            };
        });

        var request = new CadastrarVoluntarioRequest(
            "Fulano de Tal", "11122233344", "11999998888", "fulano@teste.local",
            "Rua X", "10", "Ap 1", "Bairro", "Cidade", "UF", "30000-000",
            ["9", "10"], "101", "Cadastrado via indicação de João Silva em 25/09/2026.");

        var codigo = await client.CadastrarVoluntarioAsync("token-usuario", request, CancellationToken.None);

        Assert.Equal("131", codigo);
        Assert.Equal("https://api.hinova.com.br/api/sga/v2/voluntario/cadastrar", handler.LastRequest!.RequestUri!.ToString());
        var body = await captured!.Content!.ReadAsStringAsync();
        Assert.Contains("\"codigo_voluntario_vinculado\":\"101\"", body);
        Assert.Contains("\"codigo_cooperativa\":\"9\"", body);
    }
```

Adicione `using System.Net;` no topo do arquivo, se ainda não houver (já é necessário para `HttpStatusCode` nos testes existentes -- confirme antes de adicionar de novo).

- [ ] **Step 2: Rodar os testes para confirmar que falham (métodos ainda não existem)**

Run: `dotnet test tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj --filter "FullyQualifiedName~HinovaClientTests"`
Expected: FAIL to compile — `IHinovaClient` não tem `BuscarVoluntarioAsync`/`CadastrarVoluntarioAsync`.

- [ ] **Step 3: Implementar `IHinovaClient.cs`**

```csharp
namespace Recorrencia.Api.Integracoes;

public interface IHinovaClient
{
    Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct);
    Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct);
    Task<HinovaVoluntarioDetalhe?> BuscarVoluntarioAsync(string tokenUsuario, string cpfOuCodigo, CancellationToken ct);
    Task<string> CadastrarVoluntarioAsync(string tokenUsuario, CadastrarVoluntarioRequest request, CancellationToken ct);
}

public sealed record HinovaVoluntario(string Codigo, string Nome, string Cpf, string? Telefone, IReadOnlyList<string> Cooperativas);

/// <summary>Result of "Buscar Voluntário" -- unlike <see cref="HinovaVoluntario"/>, carries cooperativa
/// CODES (needed by "Cadastrar Voluntário"), not cooperativa names.</summary>
public sealed record HinovaVoluntarioDetalhe(string Codigo, string Nome, string Cpf, IReadOnlyList<string> CooperativaCodigos);

public sealed record CadastrarVoluntarioRequest(
    string Nome, string Cpf, string? Celular, string? Email,
    string Logradouro, string Numero, string? Complemento, string Bairro, string Cidade, string Estado, string Cep,
    IReadOnlyList<string> CooperativaCodigos, string? CodigoVoluntarioVinculado, string? Obs);

public sealed class HinovaAuthException() : Exception("Falha ao autenticar na Hinova.");
```

- [ ] **Step 4: Implementar em `HinovaClient.cs`**

Adicione ao final da classe `HinovaClient` (antes do `}` final), e adicione `using System.Net;` ao topo do arquivo:

```csharp
    public async Task<HinovaVoluntarioDetalhe?> BuscarVoluntarioAsync(string tokenUsuario, string cpfOuCodigo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"buscar/voluntario/{Uri.EscapeDataString(cpfOuCodigo)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsuario);

        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<BuscarVoluntarioDto>(cancellationToken: ct);
        return body is null ? null : new HinovaVoluntarioDetalhe(
            body.CodigoVoluntario, body.Nome, body.Cpf,
            (body.Cooperativas ?? []).Select(c => c.CodigoCooperativa).ToList());
    }

    public async Task<string> CadastrarVoluntarioAsync(string tokenUsuario, CadastrarVoluntarioRequest request, CancellationToken ct)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "voluntario/cadastrar")
        {
            Content = JsonContent.Create(new
            {
                nome = request.Nome,
                cpf = request.Cpf,
                celular = request.Celular,
                email = request.Email,
                logradouro = request.Logradouro,
                numero = request.Numero,
                complemento = request.Complemento,
                bairro = request.Bairro,
                cidade = request.Cidade,
                estado = request.Estado,
                cep = request.Cep,
                obs = request.Obs,
                codigo_voluntario_vinculado = request.CodigoVoluntarioVinculado,
                cooperativas = request.CooperativaCodigos.Select(c => new { codigo_cooperativa = c }).ToList(),
            }),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenUsuario);

        using var response = await http.SendAsync(httpRequest, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<CadastrarResponseDto>(cancellationToken: ct);
        return result?.CodigoVoluntario ?? throw new InvalidOperationException("Hinova não retornou codigo_voluntario ao cadastrar.");
    }

    private sealed record BuscarVoluntarioDto(
        [property: JsonPropertyName("codigo_voluntario")] string CodigoVoluntario,
        [property: JsonPropertyName("nome")] string Nome,
        [property: JsonPropertyName("cpf")] string Cpf,
        [property: JsonPropertyName("cooperativas")] List<CooperativaCodigoDto>? Cooperativas);

    private sealed record CooperativaCodigoDto([property: JsonPropertyName("codigo_cooperativa")] string CodigoCooperativa);

    private sealed record CadastrarResponseDto([property: JsonPropertyName("codigo_voluntario")] string CodigoVoluntario);
```

- [ ] **Step 5: Implementar o fake de dev em `DevFakeHinovaClient.cs`**

Adicione dentro da classe `DevFakeHinovaClient` (mantendo os 3 voluntários fixos já existentes):

```csharp
    private static int _proximoCodigo = 900;

    public Task<HinovaVoluntarioDetalhe?> BuscarVoluntarioAsync(string tokenUsuario, string cpfOuCodigo, CancellationToken ct)
    {
        var match = Voluntarios.FirstOrDefault(v => v.Codigo == cpfOuCodigo || v.Cpf == cpfOuCodigo);
        // Cooperativa fixa "1" para todo mundo -- suficiente para o fluxo de dev/E2E, que só
        // precisa de ALGUM código de cooperativa para completar o Cadastrar, não de um valor real.
        return Task.FromResult(match is null ? null : new HinovaVoluntarioDetalhe(match.Codigo, match.Nome, match.Cpf, ["1"]));
    }

    public Task<string> CadastrarVoluntarioAsync(string tokenUsuario, CadastrarVoluntarioRequest request, CancellationToken ct) =>
        Task.FromResult((_proximoCodigo++).ToString());
```

- [ ] **Step 6: Extrair o helper `HinovaAuth` e refatorar `HinovaEndpoints.ListarVoluntariosAsync`**

Crie `src/Recorrencia.Api/Integracoes/HinovaAuth.cs`:

```csharp
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;

namespace Recorrencia.Api.Integracoes;

/// <summary>
/// Decrypt-then-authenticate against the real Hinova API, shared by every endpoint that needs a
/// tokenUsuario. Re-authenticates every call (no caching) -- same decision as the original
/// ListarVoluntariosAsync call site this was extracted from.
/// </summary>
public static class HinovaAuth
{
    public static async Task<string> GetTokenUsuarioAsync(Database db, Guid tenant, Guid user, AesGcmCipher cipher, IHinovaClient hinova, CancellationToken ct)
    {
        var credenciais = await db.InTenantAsync(tenant, user, tx => tx.QuerySingleOrDefaultAsync<CredenciaisRow>(
            "select usuario_enc, senha_enc, token_sga_enc from hinova_credenciais where tenant_id = @tenant",
            new { tenant }), ct);
        if (credenciais is null)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.nao_configurado");

        var usuario = cipher.Decrypt(credenciais.UsuarioEnc);
        var senha = cipher.Decrypt(credenciais.SenhaEnc);
        var tokenSga = cipher.Decrypt(credenciais.TokenSgaEnc);

        try
        {
            return await hinova.AutenticarAsync(usuario, senha, tokenSga, ct);
        }
        catch (HinovaAuthException)
        {
            throw new ApiProblem(StatusCodes.Status400BadRequest, "hinova.credenciais_invalidas");
        }
    }

    private sealed class CredenciaisRow
    {
        public byte[] UsuarioEnc { get; set; } = [];
        public byte[] SenhaEnc { get; set; } = [];
        public byte[] TokenSgaEnc { get; set; } = [];
    }
}
```

Em `HinovaEndpoints.cs`, substitua o corpo de `ListarVoluntariosAsync` (que hoje decripta as credenciais e chama `AutenticarAsync` inline) para usar o helper. A assinatura do método continua igual; troque só o miolo:

```csharp
    private static async Task<IResult> ListarVoluntariosAsync(string? query, RequestContext request, Database db, IHinovaClient hinova,
        AesGcmCipher cipher, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();

        var tokenUsuario = await HinovaAuth.GetTokenUsuarioAsync(db, tenant, user, cipher, hinova, ct);
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
            v.Codigo, v.Nome, v.Cpf, v.Telefone, v.Cooperativas, mapped.ContainsKey(v.Codigo), mapped.GetValueOrDefault(v.Codigo))).ToList());
    }
```

Remova o campo `CredenciaisRow` de `HinovaEndpoints.cs` **apenas se** mais nenhum outro método da classe o usa (confira com um `grep -n "CredenciaisRow" src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs` antes de remover -- `SalvarCredenciaisAsync`/`ObterCredenciaisStatusAsync` não o usam, então deve estar seguro de remover, mas confirme).

- [ ] **Step 7: Implementar o fake de teste em `FakeHinovaClient.cs`**

```csharp
using Recorrencia.Api.Integracoes;

namespace Recorrencia.Api.Tests;

public sealed class FakeHinovaClient : IHinovaClient
{
    public bool RejectAuth { get; set; }
    public List<HinovaVoluntario> Voluntarios { get; } =
    [
        new HinovaVoluntario("101", "Ana Paula Ferreira", "11122233344", "(31)99111-2233", ["Cooperativa Central"]),
        new HinovaVoluntario("102", "Bruno Costa Lima", "22233344455", "(31)99222-3344", ["Cooperativa Central"]),
    ];

    /// <summary>Set up before a test to make BuscarVoluntarioAsync report an existing match for that
    /// key (CPF or código) -- simulates the CPF-already-in-Hinova collision. Leave empty for the
    /// happy path (not found = null).</summary>
    public Dictionary<string, HinovaVoluntarioDetalhe> BuscarPorChave { get; } = new();

    public string ProximoCodigoCadastrado { get; set; } = "999";
    public CadastrarVoluntarioRequest? UltimoCadastro { get; private set; }

    public Task<string> AutenticarAsync(string usuario, string senha, string tokenSga, CancellationToken ct) =>
        RejectAuth ? throw new HinovaAuthException() : Task.FromResult("token-usuario-fake");

    public Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntariosAsync(string tokenUsuario, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<HinovaVoluntario>>(Voluntarios);

    public Task<HinovaVoluntarioDetalhe?> BuscarVoluntarioAsync(string tokenUsuario, string cpfOuCodigo, CancellationToken ct) =>
        Task.FromResult(BuscarPorChave.GetValueOrDefault(cpfOuCodigo));

    public Task<string> CadastrarVoluntarioAsync(string tokenUsuario, CadastrarVoluntarioRequest request, CancellationToken ct)
    {
        UltimoCadastro = request;
        return Task.FromResult(ProximoCodigoCadastrado);
    }
}
```

- [ ] **Step 8: Rodar toda a suíte de API para confirmar que nada quebrou e os testes novos passam**

Run: `dotnet test tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj`
Expected: PASS (inclui `HinovaClientTests`, `HinovaMapeamentosTests` e tudo o mais que já existia).

- [ ] **Step 9: Commit**

```bash
git add src/Recorrencia.Api/Integracoes/ tests/Recorrencia.Api.Tests/HinovaClientTests.cs tests/Recorrencia.Api.Tests/FakeHinovaClient.cs
git commit -m "feat(api): add Hinova Buscar/Cadastrar Voluntário client methods"
```

---

### Task 3: Extrair criação de usuário reaproveitável em `UserEndpoints.cs`

Refatoração pura — nenhum comportamento observável muda. `InviteAsync` (`POST /users`) passa a chamar três métodos internos novos em vez de ter a lógica inline; a Task 6 vai reaproveitar os mesmos três métodos para a aprovação de solicitações.

**Files:**
- Modify: `src/Recorrencia.Api/Users/UserEndpoints.cs`

**Interfaces:**
- Consumes: nada de tasks anteriores.
- Produces: três métodos `internal static` em `UserEndpoints`, chamáveis de qualquer arquivo do projeto (ex.: `UserEndpoints.CreateUserWithRolesAsync(...)`) --
  - `Task CreateUserWithRolesAsync(Tx tx, Guid tenant, Guid id, string name, string email, Guid? supervisorId, IReadOnlyCollection<Guid> roleIds)`
  - `Task LinkHinovaAsync(Tx tx, Guid tenant, Guid userId, Guid actor, string codigoVoluntario, string nomeHinova, string cpfHinova)`
  - `Task<string> CreateInviteTokenAsync(Tx tx, Guid tenant, Guid actor, Guid userId, string name, string email, Guid? supervisorId, Guid[] roleIds, int ttlSeconds)`

  A Task 6 chama os três dentro da própria transação da aprovação.

- [ ] **Step 1: Confirmar a linha de base (testes existentes passam antes de tocar no arquivo)**

Run: `dotnet test tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj --filter "FullyQualifiedName~UserTests|FullyQualifiedName~AuthTests"`
Expected: PASS.

- [ ] **Step 2: Extrair os três métodos e refatorar `InviteAsync`**

Em `src/Recorrencia.Api/Users/UserEndpoints.cs`, adicione os três métodos abaixo dentro da classe `UserEndpoints` (por exemplo, logo depois de `InviteAsync`):

```csharp
    /// <summary>
    /// Insere o usuário e seus papéis. Reaproveitado por InviteAsync (POST /users) e pela
    /// aprovação de solicitação de cadastro (Fase 5) -- a mesma operação, dois pontos de entrada.
    /// </summary>
    internal static async Task CreateUserWithRolesAsync(Tx tx, Guid tenant, Guid id, string name, string email,
        Guid? supervisorId, IReadOnlyCollection<Guid> roleIds)
    {
        try
        {
            await tx.ExecuteAsync(
                "insert into users (id, tenant_id, name, email, supervisor_id) values (@id, @tenant, @name, @email, @supervisor)",
                new { id, tenant, name, email, supervisor = supervisorId });
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
    }

    internal static async Task LinkHinovaAsync(Tx tx, Guid tenant, Guid userId, Guid actor, string codigoVoluntario, string nomeHinova, string cpfHinova)
    {
        try
        {
            await tx.ExecuteAsync(
                """
                insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
                values (@tenant, @userId, @codigoVoluntario, @nomeHinova, @cpfHinova, @actor)
                """,
                new { tenant, userId, codigoVoluntario, nomeHinova, cpfHinova, actor });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ApiProblem(StatusCodes.Status409Conflict, "hinova.vinculo_duplicado");
        }
        await WriteAsync(tx, tenant, actor, "hinova.vincular", "hinova_voluntario_mapping", userId, null, new { codigoVoluntario, nomeHinova, cpfHinova });
    }

    internal static async Task<string> CreateInviteTokenAsync(Tx tx, Guid tenant, Guid actor, Guid userId, string name, string email,
        Guid? supervisorId, Guid[] roleIds, int ttlSeconds)
    {
        var token = Tokens.New();
        await tx.ExecuteAsync("select app.create_invite(@userId, @hash, 'convite', @ttl)",
            new { userId, hash = Tokens.Hash(token), ttl = ttlSeconds });
        await WriteAsync(tx, tenant, actor, "users.invite", "users", userId, null, new { name, email, supervisorId, roleIds });
        return token;
    }
```

Agora troque o corpo de `InviteAsync` para usar os três métodos, mantendo tudo antes e depois igual (validação, checagem de permissão, e o envio de e-mail depois do `db.InTenantAsync`):

```csharp
        var id = Guid.CreateVersion7();
        var token = "";
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            await RoleGuards.EnsureCanGrantRolesAsync(tx, mine, roleIds);
            await CreateUserWithRolesAsync(tx, tenant, id, name, address, body.SupervisorId, roleIds);
            if (hasHinovaFields)
                await LinkHinovaAsync(tx, tenant, id, actor, codigoVoluntario, nomeHinova, cpfHinova);
            token = await CreateInviteTokenAsync(tx, tenant, actor, id, name, address, body.SupervisorId, roleIds, options.Value.InviteTtlSeconds);
            return 0;
        }, ct);

        await email.SendAsync(address, "Convite de acesso",
            EmailTemplates.AccessLink(
                "Convite de acesso",
                $"Você foi convidado para acessar a plataforma de {request.TenantName}.",
                "Definir minha senha",
                links.SetPassword(request.TenantSlug!, token),
                "O link vale por 72 horas.",
                links.Logo(request.TenantSlug!)), ct);
        return Results.Created($"/users/{id}", new { id });
```

Remova o código antigo que essa nova versão substitui (o `try/catch` de insert em `users`, o loop de `user_roles`, o bloco `if (hasHinovaFields)` com o insert em `hinova_voluntario_mapping`, e a linha `select app.create_invite(...)` -- tudo isso já está dentro dos três métodos extraídos).

- [ ] **Step 3: Rodar a suíte para confirmar que nada quebrou**

Run: `dotnet test tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj --filter "FullyQualifiedName~UserTests|FullyQualifiedName~AuthTests"`
Expected: PASS -- mesmo resultado do Step 1, sem nenhuma mudança de comportamento.

- [ ] **Step 4: Commit**

```bash
git add src/Recorrencia.Api/Users/UserEndpoints.cs
git commit -m "refactor(api): extract reusable user-creation helpers from POST /users"
```

---

### Task 4: Endpoints de link de indicação (`/convite-links/*`)

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0016_hinova_voluntario_mapping_self_select.sql`
- Create: `src/Recorrencia.Db/Scripts/0017_resolve_convite_link.sql`
- Create: `src/Recorrencia.Api/Convites/ConviteLinkEndpoints.cs`
- Modify: `src/Recorrencia.Api/Email/LinkBuilder.cs`
- Modify: `src/Recorrencia.Api/Program.cs`
- Test: `tests/Recorrencia.Api.Tests/ConviteLinkTests.cs`

**Interfaces:**
- Consumes: nada de tasks anteriores (independente das Tasks 2/3).
- Produces: `GET /convite-links/me` (autenticado, sem permissão extra) devolve `{ url }` ou `409 convite.indicador_sem_hinova`; `GET /convite-links/{token}` (público) devolve `{ indicadorNome, tenantNome }` ou `404 convite.link_invalido`. A função SQL `app.resolve_convite_link(p_tenant uuid, p_token text) returns table (user_id uuid, nome text)` (Step 0b) é o único jeito seguro de resolver um token pra um usuário sob uma sessão anônima -- a Task 5 reaproveita essa MESMA função (não a chamada HTTP) pra descobrir o `indicador_user_id` na hora de submeter o formulário.

- [ ] **Step 0a: Migração — consultor precisa ver o próprio vínculo Hinova**

**Descoberto durante a execução deste plano, não estava no design original:** a única policy de `hinova_voluntario_mapping` (`0014_hinova_integracao.sql`) restringe QUALQUER acesso a quem tem `integracoes.gerenciar` -- só o Administrador. Um consultor comum não consegue ver nem a própria linha, o que quebra `MeAsync` (Step 3 abaixo): a checagem "esse usuário já tem vínculo Hinova?" sempre voltaria vazia para um consultor real, mesmo com o vínculo existindo. Corrija com uma migração nova (não edite `0014`, que já está em produção):

```sql
-- hinova_voluntario_mapping's única policy (0014_hinova_integracao.sql) restringe QUALQUER
-- acesso a integracoes.gerenciar (Administrador) -- um consultor não consegue ver nem a própria
-- linha, o que a Fase 5 (indicação) precisa: saber se o próprio usuário já está vinculado à
-- Hinova, para decidir se mostra o link de indicação. Policy nova, só de SELECT, para a própria
-- linha -- insert/update/delete continuam exclusivos do Administrador via a policy já existente.
create policy hinova_voluntario_mapping_self_select on hinova_voluntario_mapping for select to app_user
  using (user_id = app.current_user_id());
```

Crie `src/Recorrencia.Db/Scripts/0016_hinova_voluntario_mapping_self_select.sql` com exatamente esse conteúdo.

- [ ] **Step 0b: Migração — resolver um link de indicação sob sessão anônima**

**Segunda descoberta, mesma causa raiz:** `users_select` (`0011_rls.sql`) não tem NENHUM ramo para sessão anônima (`app.current_user_id() is null`) -- só `id = próprio usuário`, ou visibilidade por escopo de `estrutura.visualizar`, ambos exigindo um usuário autenticado. Isso quebra qualquer `join` com `users` feito sob `db.InTenantAsync(tenant, null, ...)`: silenciosamente devolve zero linhas (não um erro), token válido ou não. **Não corrija isso com uma policy de SELECT anônimo em `users`** -- essa tabela tem e-mail, status e `supervisor_id` de todo mundo; qualquer policy que exponha linhas por RLS exporia a linha inteira, não só o nome. O padrão já usado neste mesmo projeto pra exatamente esse problema (`app.find_login`, em `0005_auth_functions.sql`, usado por `PasswordEndpoints` do mesmo jeito) é uma função `security definer`: roda com privilégio elevado internamente, mas só devolve os dois campos que o chamador realmente precisa.

```sql
-- Mesmo padrão de app.find_login (0005_auth_functions.sql) para o mesmo problema: uma sessão
-- anônima não vê nenhuma linha de `users` via RLS (users_select não tem ramo anônimo), então um
-- join direto sob db.InTenantAsync(tenant, null, ...) devolveria zero linhas sempre, token válido
-- ou não. security definer resolve isso internamente sem abrir SELECT anônimo em `users` (que
-- exporia e-mail/status/supervisor_id de todo mundo, não só o nome de quem tem link).
create function app.resolve_convite_link(p_tenant uuid, p_token text)
returns table (user_id uuid, nome text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select u.id, u.name
    from convite_links cl
    join users u on u.id = cl.user_id
   where cl.tenant_id = p_tenant and cl.token = p_token and u.status = 'ativo'
$$;

grant execute on function app.resolve_convite_link(uuid, text) to app_user, app_superadmin;
```

Crie `src/Recorrencia.Db/Scripts/0017_resolve_convite_link.sql` com exatamente esse conteúdo. Depois de criar os dois arquivos deste Step, rode `dotnet test tests/Recorrencia.Db.Tests/Recorrencia.Db.Tests.csproj` para confirmar que ambas as migrações aplicam sem erro antes de seguir para os próximos steps -- os testes de `ConviteLinkTests.cs` (Step 2) dependem das duas para passar.

- [ ] **Step 1: Adicionar `LinkBuilder.Indicar`**

Em `src/Recorrencia.Api/Email/LinkBuilder.cs`, adicione mais um método (mesmo padrão de `SetPassword`):

```csharp
    public string Indicar(string slug, string token) =>
        $"{options.Value.Scheme}://{slug}.{options.Value.RootDomain}/indicar/{Uri.EscapeDataString(token)}";
```

- [ ] **Step 2: Escrever os testes que falham**

Crie `tests/Recorrencia.Api.Tests/ConviteLinkTests.cs`:

```csharp
namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class ConviteLinkTests(ApiFixture api)
{
    public sealed record ConviteLinkDto(string Url);
    public sealed record ConviteLinkPublicoDto(string IndicadorNome, string TenantNome);

    private async Task VincularHinovaAsync(SeededTenant s, Guid userId, string codigo)
    {
        await api.SqlAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @userId, @codigo, 'Nome Hinova', '11111111111', @userId)
            """,
            new { tenant = s.TenantId, userId, codigo });
    }

    [Fact]
    public async Task Me_returns_409_when_the_user_has_no_hinova_mapping()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var response = await joao.GetAsync("/convite-links/me");
        await ApiClient.ExpectAsync(response, HttpStatusCode.Conflict);
        Assert.Equal("convite.indicador_sem_hinova", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Me_creates_and_then_reuses_the_same_link()
    {
        var s = await api.SeedAsync();
        await VincularHinovaAsync(s, s.Joao, "201");
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var first = await joao.GetJsonAsync<ConviteLinkDto>("/convite-links/me");
        var second = await joao.GetJsonAsync<ConviteLinkDto>("/convite-links/me");

        Assert.Equal(first.Url, second.Url);
        Assert.Contains("/indicar/", first.Url);
    }

    [Fact]
    public async Task Public_lookup_resolves_the_indicador_name_from_the_token()
    {
        var s = await api.SeedAsync();
        await VincularHinovaAsync(s, s.Joao, "202");
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);
        var link = await joao.GetJsonAsync<ConviteLinkDto>("/convite-links/me");
        var token = link.Url.Split('/').Last();

        var anonimo = api.Client(s.Slug);
        var found = await anonimo.GetJsonAsync<ConviteLinkPublicoDto>($"/convite-links/{token}");
        Assert.Equal("João Silva", found.IndicadorNome);
    }

    [Fact]
    public async Task Public_lookup_returns_404_for_an_unknown_token()
    {
        var s = await api.SeedAsync();
        var anonimo = api.Client(s.Slug);
        var response = await anonimo.GetAsync("/convite-links/token-que-nao-existe");
        await ApiClient.ExpectAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("convite.link_invalido", await ApiClient.CodeAsync(response));
    }
}
```

- [ ] **Step 3: Rodar para confirmar que falham**

Run: `dotnet test tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj --filter "FullyQualifiedName~ConviteLinkTests"`
Expected: FAIL (404 em tudo -- endpoint não existe ainda).

- [ ] **Step 4: Implementar `ConviteLinkEndpoints.cs`**

```csharp
using Recorrencia.Api.Email;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Convites;

public static class ConviteLinkEndpoints
{
    public sealed record ConviteLinkResponse(string Url);
    public sealed record ConviteLinkPublicoResponse(string IndicadorNome, string TenantNome);

    private sealed class LinkRow
    {
        public string Token { get; set; } = "";
    }

    private sealed class ResolvedLinkRow
    {
        public Guid UserId { get; set; }
        public string Nome { get; set; } = "";
    }

    public static void MapConviteLinkEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/convite-links/me", MeAsync).RequireUser();
        app.MapGet("/convite-links/{token}", PublicoAsync);
    }

    private static async Task<IResult> MeAsync(RequestContext request, Database db, LinkBuilder links, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();

        var token = await db.InTenantAsync(tenant, user, async tx =>
        {
            var existing = await tx.QuerySingleOrDefaultAsync<LinkRow>(
                "select token from convite_links where tenant_id = @tenant and user_id = @user", new { tenant, user });
            if (existing is not null)
                return existing.Token;

            var hasHinova = await tx.ExecuteScalarAsync<bool>(
                "select exists(select 1 from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @user)",
                new { tenant, user });
            if (!hasHinova)
                throw new ApiProblem(StatusCodes.Status409Conflict, "convite.indicador_sem_hinova");

            var novo = Tokens.New();
            await tx.ExecuteAsync("insert into convite_links (tenant_id, user_id, token) values (@tenant, @user, @novo)",
                new { tenant, user, novo });
            return novo;
        }, ct);

        return Results.Ok(new ConviteLinkResponse(links.Indicar(request.TenantSlug!, token)));
    }

    private static async Task<IResult> PublicoAsync(string token, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        // app.resolve_convite_link (Step 0) does this join as SECURITY DEFINER, bypassing RLS
        // internally -- an anonymous session can't see any row of `users` directly (0011_rls.sql's
        // users_select has no anonymous branch), so a plain join here would silently return zero
        // rows every time, token valid or not.
        var resolved = await db.InTenantAsync(tenant, null, tx => tx.QuerySingleOrDefaultAsync<ResolvedLinkRow>(
            "select * from app.resolve_convite_link(@tenant, @token)", new { tenant, token }), ct);
        if (resolved is null)
            throw new ApiProblem(StatusCodes.Status404NotFound, "convite.link_invalido");

        return Results.Ok(new ConviteLinkPublicoResponse(resolved.Nome, request.TenantName!));
    }
}
```

Note: `db.InTenantAsync(tenant, null, ...)` é o mesmo padrão já usado por `PasswordEndpoints.RequestResetAsync` para "tenant conhecido, usuário anônimo" -- tenant vem do host, não exige sessão.

- [ ] **Step 5: Registrar em `Program.cs`**

Adicione, junto das outras chamadas `app.Map*Endpoints()` (depois de `app.MapHinovaEndpoints();`):

```csharp
app.MapConviteLinkEndpoints();
```

E adicione `using Recorrencia.Api.Convites;` ao topo de `Program.cs` se ainda não houver um `using` que cubra esse namespace.

- [ ] **Step 6: Rodar os testes**

Run: `dotnet test tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj --filter "FullyQualifiedName~ConviteLinkTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Recorrencia.Api/Convites/ConviteLinkEndpoints.cs src/Recorrencia.Api/Email/LinkBuilder.cs src/Recorrencia.Api/Program.cs tests/Recorrencia.Api.Tests/ConviteLinkTests.cs
git commit -m "feat(api): add GET /convite-links/me and the public token lookup"
```

---

### Task 5: Submissão pública do formulário

**Files:**
- Create: `src/Recorrencia.Api/Convites/SolicitacaoCadastroEndpoints.cs` (só o endpoint público nesta task -- a Task 6 adiciona listagem/aprovação/rejeição no mesmo arquivo)
- Modify: `src/Recorrencia.Api/Program.cs`
- Test: `tests/Recorrencia.Api.Tests/SolicitacaoCadastroTests.cs`

**Interfaces:**
- Consumes: `app.resolve_convite_link(p_tenant, p_token)` (Task 4, Step 0b) -- a mesma função SQL, chamada aqui em vez de duplicada, já que resolver `indicador_user_id` sob uma sessão anônima tem exatamente o mesmo problema de RLS que `ConviteLinkEndpoints.PublicoAsync` já resolve.
- Produces: `POST /convite-links/{token}/solicitacoes` (público) devolve `201 { id }` ou `404 convite.link_invalido` / `429 auth.too_many_attempts` / `400 request.invalid`. A Task 6 insere `ListarAsync`/`AprovarAsync`/`RejeitarAsync` no mesmo arquivo, no mesmo `MapSolicitacaoCadastroEndpoints`.

- [ ] **Step 1: Escrever os testes que falham**

Crie `tests/Recorrencia.Api.Tests/SolicitacaoCadastroTests.cs`:

```csharp
namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class SolicitacaoCadastroTests(ApiFixture api)
{
    private static readonly object CorpoValido = new
    {
        nome = "Fulano de Tal",
        cpf = "11122233344",
        celular = "11999998888",
        email = "fulano@teste.local",
        cep = "30000-000",
        logradouro = "Rua X",
        numero = "10",
        bairro = "Bairro",
        cidade = "Cidade",
        estado = "UF",
    };

    private async Task<string> LinkTokenAsync(SeededTenant s, Guid userId, string codigo)
    {
        await api.SqlAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @userId, @codigo, 'Nome Hinova', '11111111111', @userId)
            """,
            new { tenant = s.TenantId, userId, codigo });
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);
        var link = await joao.GetJsonAsync<ConviteLinkTests.ConviteLinkDto>("/convite-links/me");
        return link.Url.Split('/').Last();
    }

    [Fact]
    public async Task Submitting_creates_a_pending_solicitacao()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "301");
        var anonimo = api.Client(s.Slug);

        var response = await anonimo.PostAsync($"/convite-links/{token}/solicitacoes", CorpoValido);
        await ApiClient.ExpectAsync(response, HttpStatusCode.Created);

        var count = await api.SqlScalarAsync<int>(
            "select count(*) from solicitacoes_cadastro where tenant_id = @tenant and status = 'pendente'", new { tenant = s.TenantId });
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Submitting_with_an_unknown_token_returns_404()
    {
        var s = await api.SeedAsync();
        var anonimo = api.Client(s.Slug);

        var response = await anonimo.PostAsync("/convite-links/token-invalido/solicitacoes", CorpoValido);
        await ApiClient.ExpectAsync(response, HttpStatusCode.NotFound);
        Assert.Equal("convite.link_invalido", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Submitting_without_a_required_field_returns_400()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "302");
        var anonimo = api.Client(s.Slug);

        var response = await anonimo.PostAsync($"/convite-links/{token}/solicitacoes", new { nome = "Fulano" });
        await ApiClient.ExpectAsync(response, HttpStatusCode.BadRequest);
        Assert.Equal("request.invalid", await ApiClient.CodeAsync(response));
    }
}
```

- [ ] **Step 2: Rodar para confirmar que falham**

Run: `dotnet test tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj --filter "FullyQualifiedName~SolicitacaoCadastroTests"`
Expected: FAIL (404 em tudo -- endpoint não existe ainda).

- [ ] **Step 3: Implementar `SolicitacaoCadastroEndpoints.cs` (só o endpoint público por agora)**

```csharp
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Security;
using Recorrencia.Api.Tenancy;

namespace Recorrencia.Api.Convites;

public static class SolicitacaoCadastroEndpoints
{
    public sealed record SolicitarRequest(string? Nome, string? Cpf, string? Celular, string? Email,
        string? Cep, string? Logradouro, string? Numero, string? Complemento, string? Bairro, string? Cidade, string? Estado);

    private sealed class IndicadorRow
    {
        public Guid UserId { get; set; }
        public string Nome { get; set; } = "";
    }

    public static void MapSolicitacaoCadastroEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/convite-links/{token}/solicitacoes", SolicitarAsync);
    }

    private static async Task<IResult> SolicitarAsync(string token, SolicitarRequest body, RequestContext request, Database db,
        LoginThrottle throttle, CancellationToken ct)
    {
        var tenant = request.RequireTenant();

        var ipKey = request.ClientIp is { } ip ? $"convite-submit-ip:{ip}" : null;
        var keys = ipKey is not null ? new[] { ipKey } : Array.Empty<string>();
        if (!throttle.TryReserve(keys))
            throw new ApiProblem(StatusCodes.Status429TooManyRequests, "auth.too_many_attempts");

        try
        {
            var nome = (body.Nome ?? "").Trim();
            var cpf = (body.Cpf ?? "").Trim();
            var celular = (body.Celular ?? "").Trim();
            var email = (body.Email ?? "").Trim();
            var cep = (body.Cep ?? "").Trim();
            var logradouro = (body.Logradouro ?? "").Trim();
            var numero = (body.Numero ?? "").Trim();
            var complemento = body.Complemento?.Trim();
            var bairro = (body.Bairro ?? "").Trim();
            var cidade = (body.Cidade ?? "").Trim();
            var estado = (body.Estado ?? "").Trim();
            if (nome.Length == 0 || cpf.Length == 0 || celular.Length == 0 || email.Length == 0 || cep.Length == 0
                || logradouro.Length == 0 || numero.Length == 0 || bairro.Length == 0 || cidade.Length == 0 || estado.Length == 0)
                throw new ApiProblem(StatusCodes.Status400BadRequest, "request.invalid");

            // app.resolve_convite_link (Task 4, Step 0b) -- não um join direto com `users`: uma
            // sessão anônima não vê nenhuma linha de `users` via RLS, então um join aqui devolveria
            // zero linhas sempre, token válido ou não. Mesma função que ConviteLinkEndpoints.PublicoAsync usa.
            var indicador = await db.InTenantAsync(tenant, null, tx => tx.QuerySingleOrDefaultAsync<IndicadorRow>(
                "select * from app.resolve_convite_link(@tenant, @token)",
                new { tenant, token }), ct) ?? throw new ApiProblem(StatusCodes.Status404NotFound, "convite.link_invalido");

            // O id é gerado aqui, não via "returning" -- sob RLS, RETURNING é filtrado pelas
            // policies de SELECT da tabela, e essa sessão é anônima (db.InTenantAsync(tenant,
            // null, ...)): sem uma policy de select anônimo (que não existe, e não deveria
            // existir -- exporia CPF/endereço/e-mail de todo mundo no tenant), um insert com
            // "returning id" simplesmente devolveria zero linhas e QuerySingleAsync lançaria.
            // Gerar o id em C# (mesmo padrão de UserEndpoints.InviteAsync) evita o problema
            // completamente -- não é preciso ler a linha de volta pra saber o id que ela tem.
            var id = Guid.CreateVersion7();
            await db.InTenantAsync(tenant, null, tx => tx.ExecuteAsync(
                """
                insert into solicitacoes_cadastro
                  (id, tenant_id, indicador_user_id, nome, cpf, celular, email, cep, logradouro, numero, complemento, bairro, cidade, estado)
                values (@id, @tenant, @indicadorId, @nome, @cpf, @celular, @email, @cep, @logradouro, @numero, @complemento, @bairro, @cidade, @estado)
                """,
                new { id, tenant, indicadorId = indicador.UserId, nome, cpf, celular, email, cep, logradouro, numero, complemento, bairro, cidade, estado }), ct);

            throttle.Complete(keys, false);
            return Results.Created($"/solicitacoes-cadastro/{id}", new { id });
        }
        catch
        {
            throttle.Complete(keys, true);
            throw;
        }
    }
}
```

- [ ] **Step 4: Registrar em `Program.cs`**

Adicione junto de `app.MapConviteLinkEndpoints();`:

```csharp
app.MapSolicitacaoCadastroEndpoints();
```

- [ ] **Step 5: Rodar os testes**

Run: `dotnet test tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj --filter "FullyQualifiedName~SolicitacaoCadastroTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Api/Convites/SolicitacaoCadastroEndpoints.cs src/Recorrencia.Api/Program.cs tests/Recorrencia.Api.Tests/SolicitacaoCadastroTests.cs
git commit -m "feat(api): add the public convite submission endpoint"
```

---

### Task 6: Fila de aprovação — listar, aprovar (com Hinova real), rejeitar

A task mais delicada do plano: junta as Tasks 2, 3 e 5. Implemente com cuidado e rode a suíte inteira ao final, não só o filtro desta classe.

**Files:**
- Modify: `src/Recorrencia.Api/Convites/SolicitacaoCadastroEndpoints.cs`
- Modify: `tests/Recorrencia.Api.Tests/SolicitacaoCadastroTests.cs`

**Interfaces:**
- Consumes: `HinovaAuth.GetTokenUsuarioAsync` (Task 2), `IHinovaClient.BuscarVoluntarioAsync`/`CadastrarVoluntarioAsync` (Task 2), `UserEndpoints.CreateUserWithRolesAsync`/`LinkHinovaAsync`/`CreateInviteTokenAsync` (Task 3), `api.Hinova` (o `FakeHinovaClient` já registrado em `ApiFixture`, Task 2).
- Produces: `GET /solicitacoes-cadastro` (lista pendentes), `POST /solicitacoes-cadastro/{id}/aprovar`, `POST /solicitacoes-cadastro/{id}/rejeitar` -- todos atrás de `usuarios.convidar` + `integracoes.gerenciar`.

- [ ] **Step 1: Escrever os testes que falham**

Adicione à mesma classe `SolicitacaoCadastroTests` (Task 5):

```csharp
    public sealed record SolicitacaoDto(Guid Id, string Nome, string Cpf, string Celular, string Email, string Cep,
        string Logradouro, string Numero, string? Complemento, string Bairro, string Cidade, string Estado,
        Guid IndicadorUserId, string IndicadorNome, DateTimeOffset CriadoEm);

    private async Task<(Guid Id, string Email)> SubmitPendingAsync(SeededTenant s, string token, string cpf = "11122233344")
    {
        var email = $"fulano-{Guid.NewGuid():N}@teste.local";
        var anonimo = api.Client(s.Slug);
        var corpo = new
        {
            nome = "Fulano de Tal", cpf, celular = "11999998888", email,
            cep = "30000-000", logradouro = "Rua X", numero = "10", bairro = "Bairro", cidade = "Cidade", estado = "UF",
        };
        var response = await anonimo.PostAsync($"/convite-links/{token}/solicitacoes", corpo);
        await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<IdDto>(ApiClient.Json);
        return (created!.Id, email);
    }

    public sealed record IdDto(Guid Id);

    [Fact]
    public async Task Admin_can_list_pending_solicitacoes()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "310");
        await SubmitPendingAsync(s, token);

        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        var pendentes = await admin.GetJsonAsync<List<SolicitacaoDto>>("/solicitacoes-cadastro");

        Assert.Contains(pendentes, p => p.IndicadorNome == "João Silva");
    }

    [Fact]
    public async Task Consultor_without_the_admin_permissions_cannot_list()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var response = await joao.GetAsync("/solicitacoes-cadastro");
        await ApiClient.ExpectAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Approving_creates_the_user_linked_to_the_indicador_and_sends_the_invite_email()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "311");
        var (id, email) = await SubmitPendingAsync(s, token, "99988877766");

        api.Hinova.ProximoCodigoCadastrado = "555";
        // AprovarAsync also looks up the INDICADOR (código "311", from LinkTokenAsync above) via
        // BuscarVoluntarioAsync, to read their cooperativa codes for CadastrarVoluntarioRequest --
        // without this, the fake returns null for that lookup and approval 409s with
        // solicitacao.indicador_sem_hinova before ever reaching CadastrarVoluntarioAsync.
        api.Hinova.BuscarPorChave["311"] = new HinovaVoluntarioDetalhe("311", "João Silva", "11111111111", ["1"]);
        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        // HinovaAuth.GetTokenUsuarioAsync (Task 2) requires a saved hinova_credenciais row before
        // it will call AutenticarAsync -- DevSeed/SeedAsync never seeds one, so approval would
        // otherwise fail with 400 hinova.nao_configurado before ever reaching FakeHinovaClient.
        // PUT validates against api.Hinova.AutenticarAsync first (accepts anything unless
        // RejectAuth is set), same as HinovaCredenciaisTests already does.
        await admin.PutAsync("/integracoes/hinova/credenciais", new { usuario = "usuario", senha = "senha", tokenSga = "token" });
        var response = await admin.PostAsync($"/solicitacoes-cadastro/{id}/aprovar");
        await ApiClient.ExpectAsync(response, HttpStatusCode.OK);

        var mapeamento = await api.SqlScalarAsync<string>(
            "select codigo_voluntario from hinova_voluntario_mapping where tenant_id = @tenant and codigo_voluntario = '555'",
            new { tenant = s.TenantId });
        Assert.Equal("555", mapeamento);

        var supervisor = await api.SqlScalarAsync<Guid>(
            "select supervisor_id from users where tenant_id = @tenant and email = @email", new { tenant = s.TenantId, email });
        Assert.Equal(s.Joao, supervisor);

        Assert.NotNull(api.Hinova.UltimoCadastro);
        Assert.Equal("311", api.Hinova.UltimoCadastro!.CodigoVoluntarioVinculado);

        Assert.Equal("Convite de acesso", api.Emails.LastTo(email)?.Subject);
    }

    [Fact]
    public async Task Approving_blocks_when_the_cpf_already_exists_in_hinova()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "312");
        var (id, _) = await SubmitPendingAsync(s, token, "88877766655");
        api.Hinova.BuscarPorChave["88877766655"] = new HinovaVoluntarioDetalhe("777", "Outra Pessoa", "88877766655", ["1"]);

        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        await admin.PutAsync("/integracoes/hinova/credenciais", new { usuario = "usuario", senha = "senha", tokenSga = "token" });
        var response = await admin.PostAsync($"/solicitacoes-cadastro/{id}/aprovar");

        await ApiClient.ExpectAsync(response, HttpStatusCode.Conflict);
        Assert.Equal("solicitacao.cpf_ja_cadastrado", await ApiClient.CodeAsync(response));

        var status = await api.SqlScalarAsync<string>("select status from solicitacoes_cadastro where id = @id", new { id });
        Assert.Equal("pendente", status);
    }

    [Fact]
    public async Task Rejecting_marks_the_solicitacao_and_creates_no_user()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "313");
        var (id, email) = await SubmitPendingAsync(s, token, "77766655544");

        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        var response = await admin.PostAsync($"/solicitacoes-cadastro/{id}/rejeitar");
        await ApiClient.ExpectAsync(response, HttpStatusCode.NoContent);

        var status = await api.SqlScalarAsync<string>("select status from solicitacoes_cadastro where id = @id", new { id });
        Assert.Equal("rejeitado", status);
        var count = await api.SqlScalarAsync<int>("select count(*) from users where tenant_id = @tenant and email = @email", new { tenant = s.TenantId, email });
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Approving_an_already_resolved_solicitacao_returns_409()
    {
        var s = await api.SeedAsync();
        var token = await LinkTokenAsync(s, s.Joao, "314");
        var (id, _) = await SubmitPendingAsync(s, token, "66655544433");
        var admin = api.Client(s.Slug);
        await admin.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        await admin.PostAsync($"/solicitacoes-cadastro/{id}/rejeitar");

        var response = await admin.PostAsync($"/solicitacoes-cadastro/{id}/aprovar");
        await ApiClient.ExpectAsync(response, HttpStatusCode.Conflict);
        Assert.Equal("solicitacao.ja_resolvida", await ApiClient.CodeAsync(response));
    }
```

Verifique se `ApiFixture.Emails` (`CapturingEmailSender`) já expõe uma lista `Sent` com um campo `Subject` -- se o nome real for diferente, ajuste a asserção `Assert.Contains(api.Emails.Sent, ...)` para bater com a API real de `CapturingEmailSender` (dê um `grep -n "class CapturingEmailSender" -A 15 tests/Recorrencia.Api.Tests/*.cs` antes de escrever essa linha, caso o nome do campo não seja exatamente esse).

- [ ] **Step 2: Rodar para confirmar que falham**

Run: `dotnet test tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj --filter "FullyQualifiedName~SolicitacaoCadastroTests"`
Expected: FAIL (os endpoints de listar/aprovar/rejeitar ainda não existem).

- [ ] **Step 3: Implementar listar/aprovar/rejeitar**

Adicione ao `SolicitacaoCadastroEndpoints.cs` (mesma classe da Task 5):

```csharp
    public sealed record SolicitacaoResponse(Guid Id, string Nome, string Cpf, string Celular, string Email, string Cep,
        string Logradouro, string Numero, string? Complemento, string Bairro, string Cidade, string Estado,
        Guid IndicadorUserId, string IndicadorNome, DateTimeOffset CriadoEm);

    private sealed class SolicitacaoRow
    {
        public Guid Id { get; set; }
        public Guid TenantId { get; set; }
        public Guid IndicadorUserId { get; set; }
        // Populated only by ListarAsync's join (it aliases users.name as indicador_nome); the
        // plain "select * from solicitacoes_cadastro" that AprovarAsync/RejeitarAsync run has no
        // such column, so it stays "" there -- harmless, since neither of those handlers reads it.
        public string IndicadorNome { get; set; } = "";
        public string Nome { get; set; } = "";
        public string Cpf { get; set; } = "";
        public string Celular { get; set; } = "";
        public string Email { get; set; } = "";
        public string Cep { get; set; } = "";
        public string Logradouro { get; set; } = "";
        public string Numero { get; set; } = "";
        public string? Complemento { get; set; }
        public string Bairro { get; set; } = "";
        public string Cidade { get; set; } = "";
        public string Estado { get; set; } = "";
        public string Status { get; set; } = "";
        public DateTimeOffset CriadoEm { get; set; }
    }
```

Troque a assinatura de `MapSolicitacaoCadastroEndpoints` para:

```csharp
    public static void MapSolicitacaoCadastroEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/convite-links/{token}/solicitacoes", SolicitarAsync);
        app.MapGet("/solicitacoes-cadastro", ListarAsync).RequirePermission("usuarios.convidar").RequirePermission("integracoes.gerenciar");
        app.MapPost("/solicitacoes-cadastro/{id:guid}/aprovar", AprovarAsync).RequirePermission("usuarios.convidar").RequirePermission("integracoes.gerenciar");
        app.MapPost("/solicitacoes-cadastro/{id:guid}/rejeitar", RejeitarAsync).RequirePermission("usuarios.convidar").RequirePermission("integracoes.gerenciar");
    }
```

Adicione os três handlers novos (e os `using` correspondentes: `Recorrencia.Api.Authorization`, `Recorrencia.Api.Email`, `Recorrencia.Api.Integracoes`, `Recorrencia.Api.Security`, `Recorrencia.Api.Users`, `Microsoft.Extensions.Options`, `static Recorrencia.Api.Audit.Audit`):

```csharp
    private static async Task<IResult> ListarAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var user = request.RequireUser();
        var rows = await db.InTenantAsync(tenant, user, tx => tx.QueryAsync<SolicitacaoRow>(
            """
            select s.id, s.tenant_id, s.indicador_user_id, u.name as indicador_nome, s.nome, s.cpf, s.celular, s.email,
                   s.cep, s.logradouro, s.numero, s.complemento, s.bairro, s.cidade, s.estado, s.status, s.criado_em
              from solicitacoes_cadastro s join users u on u.id = s.indicador_user_id
             where s.status = 'pendente'
             order by s.criado_em
            """), ct);
        return Results.Ok(rows.Select(r => new SolicitacaoResponse(r.Id, r.Nome, r.Cpf, r.Celular, r.Email, r.Cep,
            r.Logradouro, r.Numero, r.Complemento, r.Bairro, r.Cidade, r.Estado, r.IndicadorUserId, r.IndicadorNome, r.CriadoEm)).ToList());
    }

    private static async Task<IResult> AprovarAsync(Guid id, RequestContext request, Database db, CurrentPermissions permissions,
        IHinovaClient hinova, AesGcmCipher cipher, IEmailSender email, LinkBuilder links, IOptions<AuthOptions> options, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var mine = await permissions.GetAsync(ct);

        // A transação fica aberta durante as chamadas HTTP à Hinova (2-3 round-trips) -- aceitável
        // aqui: é uma ação de admin, de baixo volume (poucas aprovações por dia), travando uma
        // única linha de solicitacoes_cadastro, não uma tabela quente do sistema.
        var (newUserId, enderecoEmail, inviteToken) = await db.InTenantAsync(tenant, actor, async tx =>
        {
            var solicitacao = await tx.QuerySingleOrDefaultAsync<SolicitacaoRow>(
                "select * from solicitacoes_cadastro where tenant_id = @tenant and id = @id for update",
                new { tenant, id }) ?? throw new ApiProblem(StatusCodes.Status404NotFound, "solicitacao.not_found");
            if (solicitacao.Status != "pendente")
                throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.ja_resolvida");

            var indicadorCodigo = await tx.QuerySingleOrDefaultAsync<string>(
                "select codigo_voluntario from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @indicadorId",
                new { tenant, indicadorId = solicitacao.IndicadorUserId })
                ?? throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.indicador_sem_hinova");
            var indicadorNome = await tx.QuerySingleAsync<string>(
                "select name from users where id = @id", new { id = solicitacao.IndicadorUserId });

            var tokenUsuario = await HinovaAuth.GetTokenUsuarioAsync(db, tenant, actor, cipher, hinova, ct);

            var existente = await hinova.BuscarVoluntarioAsync(tokenUsuario, solicitacao.Cpf, ct);
            if (existente is not null)
                throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.cpf_ja_cadastrado");

            var indicador = await hinova.BuscarVoluntarioAsync(tokenUsuario, indicadorCodigo, ct)
                ?? throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.indicador_sem_hinova");

            var obs = $"Cadastrado via indicação de {indicadorNome} em {DateOnly.FromDateTime(DateTime.UtcNow):dd/MM/yyyy}.";
            var codigoVoluntario = await hinova.CadastrarVoluntarioAsync(tokenUsuario, new CadastrarVoluntarioRequest(
                solicitacao.Nome, solicitacao.Cpf, solicitacao.Celular, solicitacao.Email,
                solicitacao.Logradouro, solicitacao.Numero, solicitacao.Complemento, solicitacao.Bairro,
                solicitacao.Cidade, solicitacao.Estado, solicitacao.Cep, indicador.CooperativaCodigos, indicadorCodigo, obs), ct);

            var roleIds = (await tx.QueryAsync<Guid>(
                "select id from roles where tenant_id = @tenant and source_template_key = 'consultor'", new { tenant })).ToArray();

            var userId = Guid.CreateVersion7();
            await RoleGuards.EnsureCanGrantRolesAsync(tx, mine, roleIds);
            await UserEndpoints.CreateUserWithRolesAsync(tx, tenant, userId, solicitacao.Nome, solicitacao.Email, solicitacao.IndicadorUserId, roleIds);
            await UserEndpoints.LinkHinovaAsync(tx, tenant, userId, actor, codigoVoluntario, solicitacao.Nome, solicitacao.Cpf);
            var inviteToken = await UserEndpoints.CreateInviteTokenAsync(tx, tenant, actor, userId, solicitacao.Nome, solicitacao.Email,
                solicitacao.IndicadorUserId, roleIds, options.Value.InviteTtlSeconds);

            await tx.ExecuteAsync(
                "update solicitacoes_cadastro set status = 'aprovado', resolvido_em = now(), resolvido_por = @actor, user_id_resultante = @userId where id = @id",
                new { actor, userId, id });
            await WriteAsync(tx, tenant, actor, "solicitacao.aprovar", "solicitacoes_cadastro", id, null, new { userId, codigoVoluntario });

            return (userId, solicitacao.Email, inviteToken);
        }, ct);

        await email.SendAsync(enderecoEmail, "Convite de acesso",
            EmailTemplates.AccessLink(
                "Convite de acesso",
                $"Você foi convidado para acessar a plataforma de {request.TenantName}.",
                "Definir minha senha",
                links.SetPassword(request.TenantSlug!, inviteToken),
                "O link vale por 72 horas.",
                links.Logo(request.TenantSlug!)), ct);

        return Results.Ok(new { userId = newUserId });
    }

    private static async Task<IResult> RejeitarAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var status = await tx.QuerySingleOrDefaultAsync<string>(
                "select status from solicitacoes_cadastro where tenant_id = @tenant and id = @id for update", new { tenant, id })
                ?? throw new ApiProblem(StatusCodes.Status404NotFound, "solicitacao.not_found");
            if (status != "pendente")
                throw new ApiProblem(StatusCodes.Status409Conflict, "solicitacao.ja_resolvida");

            await tx.ExecuteAsync(
                "update solicitacoes_cadastro set status = 'rejeitado', resolvido_em = now(), resolvido_por = @actor where id = @id",
                new { actor, id });
            await WriteAsync(tx, tenant, actor, "solicitacao.rejeitar", "solicitacoes_cadastro", id, null, null);
            return 0;
        }, ct);
        return Results.NoContent();
    }
```

- [ ] **Step 4: Rodar a suíte inteira de API (não só este filtro)**

Run: `dotnet test tests/Recorrencia.Api.Tests/Recorrencia.Api.Tests.csproj`
Expected: PASS -- inclui `SolicitacaoCadastroTests`, `ConviteLinkTests`, `UserTests`, `AuthTests`, `HinovaClientTests`, `HinovaMapeamentosTests` e todo o resto.

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Api/Convites/SolicitacaoCadastroEndpoints.cs tests/Recorrencia.Api.Tests/SolicitacaoCadastroTests.cs
git commit -m "feat(api): add the approval queue -- list, approve (real Hinova Cadastrar), reject"
```

---

### Task 7: Página pública `/indicar/[token]`

**Files:**
- Modify: `web/src/lib/types.ts`
- Modify: `web/src/lib/errors.ts`
- Create: `web/src/app/indicar/[token]/page.tsx`
- Create: `web/src/app/indicar/[token]/indicacao-form.tsx`
- Create: `web/src/app/indicar/[token]/actions.ts`

**Interfaces:**
- Consumes: `apiFetch`/`ApiError`/`currentHost` (`@/lib/api`), `AuthBrandPanel` (`web/src/app/components/auth-brand-panel.tsx`, já existe), `FormState` (`@/lib/form-state`), `messageFor` (`@/lib/errors`).
- Produces: nada consumido por outras tasks (página final, sem dependentes).

- [ ] **Step 1: Tipos e mensagens de erro**

Em `web/src/lib/types.ts`, adicione:

```ts
export type ConviteLinkPublico = { indicadorNome: string; tenantNome: string };
```

Em `web/src/lib/errors.ts`, adicione ao objeto `messages`:

```ts
  'convite.link_invalido': 'Esse link não é válido ou expirou.',
  'convite.indicador_sem_hinova': 'Você precisa estar vinculado a um código de voluntário na Hinova antes de gerar seu link.',
  'request.invalid': 'Preencha todos os campos obrigatórios.',
  'solicitacao.not_found': 'Solicitação não encontrada.',
  'solicitacao.ja_resolvida': 'Essa solicitação já foi resolvida.',
  'solicitacao.cpf_ja_cadastrado': 'Esse CPF já está cadastrado na Hinova. Resolva manualmente antes de aprovar.',
  'solicitacao.indicador_sem_hinova': 'O indicador não está mais vinculado a um código de voluntário na Hinova.',
```

(Confira se `request.invalid` já existe no arquivo antes de duplicar a chave -- se já existir, pule essa linha.)

- [ ] **Step 2: Página pública**

`web/src/app/indicar/[token]/page.tsx`:

```tsx
import { notFound } from 'next/navigation';
import { ApiError, apiFetch } from '@/lib/api';
import type { ConviteLinkPublico } from '@/lib/types';
import { AuthBrandPanel } from '../../components/auth-brand-panel';
import { IndicacaoForm } from './indicacao-form';

export default async function IndicarPage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;

  let convite: ConviteLinkPublico;
  try {
    convite = await apiFetch<ConviteLinkPublico>(`/convite-links/${token}`, { token: null });
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) notFound();
    throw error;
  }

  return (
    <main className="login-shell">
      <section className="login-panel">
        <div className="login-card">
          <p className="eyebrow">Indicação</p>
          <h1>Você foi indicado por {convite.indicadorNome}</h1>
          <p className="muted">Preencha seus dados para solicitar seu cadastro em {convite.tenantNome}.</p>
          <IndicacaoForm token={token} />
        </div>
      </section>
      <AuthBrandPanel />
    </main>
  );
}
```

- [ ] **Step 3: Server Action**

`web/src/app/indicar/[token]/actions.ts`:

```ts
'use server';

import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function solicitarCadastro(_: FormState, formData: FormData): Promise<FormState> {
  const token = String(formData.get('token') ?? '');
  try {
    await apiFetch(`/convite-links/${token}/solicitacoes`, {
      method: 'POST',
      token: null,
      body: {
        nome: String(formData.get('nome') ?? ''),
        cpf: String(formData.get('cpf') ?? ''),
        celular: String(formData.get('celular') ?? ''),
        email: String(formData.get('email') ?? ''),
        cep: String(formData.get('cep') ?? ''),
        logradouro: String(formData.get('logradouro') ?? ''),
        numero: String(formData.get('numero') ?? ''),
        complemento: String(formData.get('complemento') ?? ''),
        bairro: String(formData.get('bairro') ?? ''),
        cidade: String(formData.get('cidade') ?? ''),
        estado: String(formData.get('estado') ?? ''),
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  return { message: 'Solicitação enviada! Você vai receber um e-mail quando ela for aprovada.' };
}
```

- [ ] **Step 4: Formulário cliente com autopreenchimento de CEP**

`web/src/app/indicar/[token]/indicacao-form.tsx`:

```tsx
'use client';

import { useActionState, useState } from 'react';
import type { FormState } from '@/lib/form-state';
import { solicitarCadastro } from './actions';

type ViaCepResponse = { logradouro?: string; bairro?: string; localidade?: string; uf?: string; erro?: boolean };

export function IndicacaoForm({ token }: { token: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(solicitarCadastro, {});
  const [endereco, setEndereco] = useState({ logradouro: '', bairro: '', cidade: '', estado: '' });

  async function onCepBlur(event: React.FocusEvent<HTMLInputElement>) {
    const cep = event.target.value.replace(/\D/g, '');
    if (cep.length !== 8) return;
    try {
      const response = await fetch(`https://viacep.com.br/ws/${cep}/json/`);
      const data: ViaCepResponse = await response.json();
      if (data.erro) return;
      setEndereco({ logradouro: data.logradouro ?? '', bairro: data.bairro ?? '', cidade: data.localidade ?? '', estado: data.uf ?? '' });
    } catch {
      // CEP autopreenchimento é uma conveniência -- se a ViaCEP estiver fora do ar, a pessoa
      // ainda pode digitar o endereço manualmente nos campos abaixo.
    }
  }

  if (state.message) {
    return <p className="success">{state.message}</p>;
  }

  return (
    <form action={formAction} className="form">
      <input type="hidden" name="token" value={token} />
      <label>
        Nome completo
        <input name="nome" type="text" autoComplete="name" required />
      </label>
      <label>
        CPF
        <input name="cpf" type="text" autoComplete="off" required />
      </label>
      <label>
        Celular
        <input name="celular" type="tel" autoComplete="tel" required />
      </label>
      <label>
        E-mail
        <input name="email" type="email" autoComplete="email" required />
      </label>
      <label>
        CEP
        <input name="cep" type="text" autoComplete="postal-code" onBlur={onCepBlur} required />
      </label>
      <label>
        Endereço
        <input name="logradouro" type="text" autoComplete="address-line1" defaultValue={endereco.logradouro} key={endereco.logradouro} required />
      </label>
      <label>
        Número
        <input name="numero" type="text" required />
      </label>
      <label>
        Complemento (opcional)
        <input name="complemento" type="text" autoComplete="address-line2" />
      </label>
      <label>
        Bairro
        <input name="bairro" type="text" defaultValue={endereco.bairro} key={`bairro-${endereco.bairro}`} required />
      </label>
      <label>
        Cidade
        <input name="cidade" type="text" defaultValue={endereco.cidade} key={`cidade-${endereco.cidade}`} required />
      </label>
      <label>
        Estado
        <input name="estado" type="text" defaultValue={endereco.estado} key={`estado-${endereco.estado}`} required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Enviando…' : 'Solicitar cadastro'}</button>
    </form>
  );
}
```

Nota: os campos de endereço usam `key={endereco.campo}` para forçar o React a trocar de `defaultValue` quando o ViaCEP responde depois da digitação inicial (um `<input>` não controlado só relê `defaultValue` quando remontado) -- e continuam editáveis livremente pela pessoa depois disso, já que não são `value` controlado.

- [ ] **Step 5: Verificação**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: limpo (sem erros; o warning pré-existente de `<img>` em `app-shell.tsx`/`auth-brand-panel.tsx`, se aparecer, não é desta task).

- [ ] **Step 6: Commit**

```bash
git add web/src/lib/types.ts web/src/lib/errors.ts web/src/app/indicar/
git commit -m "feat(web): add the public self-service invite form"
```

---

### Task 8: Cartão "meu link de indicação" em Minha recorrência

**Files:**
- Modify: `web/src/app/tenant-home.tsx`
- Create: `web/src/app/meu-link-indicacao.tsx`

**Interfaces:**
- Consumes: `ConviteLink` (tipo novo, adicionado nesta task em `web/src/lib/types.ts`), `apiFetch`/`ApiError`.
- Produces: nada consumido por outras tasks.

- [ ] **Step 1: Tipo**

Em `web/src/lib/types.ts`, adicione:

```ts
export type ConviteLink = { url: string };
```

- [ ] **Step 2: Componente cliente do botão de copiar**

`web/src/app/meu-link-indicacao.tsx`:

```tsx
'use client';

import { useState } from 'react';

export function MeuLinkIndicacao({ url }: { url: string | null }) {
  const [copiado, setCopiado] = useState(false);

  if (!url) {
    return <p className="muted">Vincule-se a um código de voluntário na Hinova para gerar seu link de indicação.</p>;
  }

  async function copiar() {
    await navigator.clipboard.writeText(url);
    setCopiado(true);
    setTimeout(() => setCopiado(false), 2000);
  }

  return (
    <div className="referral-link">
      <input type="text" value={url} readOnly aria-label="Seu link de indicação" />
      <button type="button" className="secondary" onClick={copiar}>{copiado ? 'Copiado!' : 'Copiar meu link'}</button>
    </div>
  );
}
```

- [ ] **Step 3: CSS mínimo pro layout do cartão**

Em `web/src/app/globals.css`, adicione (mesmo bloco de adições da feature, junto de onde `.card`/`.form` já estão definidos, ou ao final do arquivo antes do `@media` de mobile):

```css
.referral-link { display: flex; gap: 8px; align-items: center; }
.referral-link input { flex: 1; min-width: 0; padding: 9px 12px; border: 1px solid var(--line); border-radius: 8px; background: var(--paper); color: var(--muted); font-size: 0.75rem; }
```

- [ ] **Step 4: Ligar em `tenant-home.tsx`**

Em `web/src/app/tenant-home.tsx`, importe o tipo e o componente:

```ts
import { MeuLinkIndicacao } from './meu-link-indicacao';
import type { Commissions, ConviteLink, Me } from '@/lib/types';
```

Busque o link junto das outras chamadas já existentes no topo da função (perto de `const [commissions, previous] = await Promise.all([...])`):

```ts
  const conviteLink = await apiFetch<ConviteLink>('/convite-links/me').catch(() => null);
```

E adicione um novo cartão no JSX, por exemplo logo depois da seção `commission-band` já existente:

```tsx
        <section className="card">
          <h2>Meu link de indicação</h2>
          <p className="muted">Compartilhe para que novos consultores entrem direto na sua equipe.</p>
          <MeuLinkIndicacao url={conviteLink?.url ?? null} />
        </section>
```

- [ ] **Step 5: Verificação**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: limpo.

- [ ] **Step 6: Commit**

```bash
git add web/src/lib/types.ts web/src/app/meu-link-indicacao.tsx web/src/app/tenant-home.tsx web/src/app/globals.css
git commit -m "feat(web): show the consultant's own referral link on Minha recorrência"
```

---

### Task 9: Fila de aprovação do administrador + item de menu

**Files:**
- Create: `web/src/app/administracao/convites/page.tsx`
- Create: `web/src/app/administracao/convites/actions.ts`
- Modify: `web/src/app/app-shell.tsx`
- Modify: `web/src/lib/types.ts`

**Interfaces:**
- Consumes: `SolicitacaoCadastro` (tipo novo desta task), `FormState`, o padrão de `useActionState` já usado em `credenciais-form.tsx`.
- Produces: nada consumido por outras tasks.

- [ ] **Step 1: Tipo**

Em `web/src/lib/types.ts`, adicione:

```ts
export type SolicitacaoCadastro = {
  id: string;
  nome: string;
  cpf: string;
  celular: string;
  email: string;
  cep: string;
  logradouro: string;
  numero: string;
  complemento: string | null;
  bairro: string;
  cidade: string;
  estado: string;
  indicadorUserId: string;
  indicadorNome: string;
  criadoEm: string;
};
```

- [ ] **Step 2: Server Actions**

`web/src/app/administracao/convites/actions.ts`:

```ts
'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function aprovarSolicitacao(_: FormState, formData: FormData): Promise<FormState> {
  const id = String(formData.get('id') ?? '');
  try {
    await apiFetch(`/solicitacoes-cadastro/${id}/aprovar`, { method: 'POST' });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/convites');
  return { message: 'Solicitação aprovada. O novo participante já recebeu o convite por e-mail.' };
}

export async function rejeitarSolicitacao(_: FormState, formData: FormData): Promise<FormState> {
  const id = String(formData.get('id') ?? '');
  try {
    await apiFetch(`/solicitacoes-cadastro/${id}/rejeitar`, { method: 'POST' });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/convites');
  return { message: 'Solicitação rejeitada.' };
}
```

- [ ] **Step 3: Página**

`web/src/app/administracao/convites/page.tsx` -- siga a estrutura de `web/src/app/administracao/participantes/page.tsx` (checagem de permissão, `AppShell`, `.card`/`.table-wrap`), mas com um pequeno componente cliente por linha para os dois botões de ação (aprovar/rejeitar precisam de `useActionState` cada, então não podem ser um simples `<form method="get">` como as buscas em outras páginas):

```tsx
import { notFound } from 'next/navigation';
import { AppShell } from '../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import { formatDate } from '@/lib/format';
import type { Me, SolicitacaoCadastro } from '@/lib/types';
import { SolicitacaoActions } from './solicitacao-actions';

export default async function ConvitesPage() {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const canApprove = me.permissions.some((p) => p.key === 'usuarios.convidar') && me.permissions.some((p) => p.key === 'integracoes.gerenciar');
  if (!canApprove) {
    return (
      <AppShell me={me} active="convites">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso à fila de convites.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const pendentes = await apiFetch<SolicitacaoCadastro[]>('/solicitacoes-cadastro');

  return (
    <AppShell me={me} active="convites">
      <div className="shell">
        <div className="page-heading">
          <p className="eyebrow">Administração</p>
          <h1>Convites pendentes</h1>
          <p className="muted">Analise quem foi indicado, por quem, e aprove para cadastrar de verdade na Hinova.</p>
        </div>

        <section className="card">
          {pendentes.length === 0 ? (
            <p className="muted" style={{ padding: '24px' }}>Nenhuma solicitação pendente.</p>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Nome</th>
                    <th>CPF</th>
                    <th>Contato</th>
                    <th>Indicado por</th>
                    <th>Recebido em</th>
                    <th>Ações</th>
                  </tr>
                </thead>
                <tbody>
                  {pendentes.map((s) => (
                    <tr key={s.id}>
                      <td><strong>{s.nome}</strong></td>
                      <td>{s.cpf}</td>
                      <td>{s.celular}<br /><span className="muted">{s.email}</span></td>
                      <td>{s.indicadorNome}</td>
                      <td>{formatDate(s.criadoEm.slice(0, 10))}</td>
                      <td><SolicitacaoActions id={s.id} /></td>
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

- [ ] **Step 4: Componente cliente das ações**

`web/src/app/administracao/convites/solicitacao-actions.tsx`:

```tsx
'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { aprovarSolicitacao, rejeitarSolicitacao } from './actions';

export function SolicitacaoActions({ id }: { id: string }) {
  const [approveState, approveAction, approvePending] = useActionState<FormState, FormData>(aprovarSolicitacao, {});
  const [rejectState, rejectAction, rejectPending] = useActionState<FormState, FormData>(rejeitarSolicitacao, {});

  return (
    <div className="inline">
      <form action={approveAction}>
        <input type="hidden" name="id" value={id} />
        <button type="submit" disabled={approvePending || rejectPending}>{approvePending ? 'Aprovando…' : 'Aprovar'}</button>
      </form>
      <form action={rejectAction}>
        <input type="hidden" name="id" value={id} />
        <button type="submit" className="secondary" disabled={approvePending || rejectPending}>{rejectPending ? 'Rejeitando…' : 'Rejeitar'}</button>
      </form>
      {approveState.error && <p role="alert" className="error">{approveState.error}</p>}
      {rejectState.error && <p role="alert" className="error">{rejectState.error}</p>}
    </div>
  );
}
```

- [ ] **Step 5: Item de menu com contador**

Em `web/src/app/app-shell.tsx`:

1. Adicione `'convites'` a `ActivePage`.
2. Adicione um item ao `NAV_ITEMS`, com `permission: 'usuarios.convidar'` (a mesma checagem de "pode ver a fila" -- a segunda permissão, `integracoes.gerenciar`, já é implícita para quem tem a primeira nesta versão do produto, já que só Administrador tem qualquer uma das duas):

```ts
  { id: 'convites', icon: 'people', label: 'Convites', href: '/administracao/convites', permission: 'usuarios.convidar' },
```

3. Adicione `'convites'` a `ADMIN_ONLY_NAV_IDS` (Administrador precisa continuar vendo este item no menu exclusivo dele).
4. Para o contador de pendências (mesmo efeito visual do `nav-count` que "Fechamento" tinha no mockup antes de virar uma tela real), busque a contagem em `AppShell` e mostre um badge no item -- adicione ao componente `AppShell`:

```tsx
export function AppShell({ me, active, children, convitesPendentes }: { me: Me; active: ActivePage; children: React.ReactNode; convitesPendentes?: number }) {
```

E no JSX do item de navegação, ao lado do `<span>{item.label}</span>`:

```tsx
                {item.id === 'convites' && !!convitesPendentes && <span className="nav-count">{convitesPendentes}</span>}
```

`AppShell` hoje não é `async` nem recebe essa prop -- como ela é usada por toda página existente (`carteira`, `comissoes`, `fechamento`, `administracao/*`, `tenant-home`), **não** torne a busca da contagem obrigatória: mantenha `convitesPendentes` opcional (undefined = sem badge) e só passe esse valor a partir de `web/src/app/administracao/convites/page.tsx` (que já tem `pendentes.length` em mãos) e, opcionalmente, de `tenant-home.tsx` se quiser o badge visível ali também. Não é necessário buscar a contagem em toda página só para alimentar o badge.

- [ ] **Step 6: Verificação**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: limpo.

- [ ] **Step 7: Commit**

```bash
git add web/src/app/administracao/convites/ web/src/app/app-shell.tsx web/src/lib/types.ts
git commit -m "feat(web): add the admin approval queue page and nav item"
```

---

### Task 10: E2E do caminho feliz completo

**Files:**
- Modify: `web/e2e/fundacao.spec.ts`

**Interfaces:**
- Consumes: `login` helper, `latestLinkFor` (`./emails`) -- ambos já existem no arquivo.
- Produces: nada (última task).

- [ ] **Step 1: Escrever o teste**

Adicione ao final de `web/e2e/fundacao.spec.ts`:

```ts
test('indicação: gerar link, preencher formulário, aprovar e logar', async ({ page }) => {
  const cpf = `1112223${Date.now() % 10000}`;
  const email = `indicado-${Date.now()}@e2e.aprovec.local`;

  // Vincula Pedro Santos ao voluntário 103 da Hinova (fake de dev) para ele poder gerar um link.
  await login(page, tenant, 'admin@aprovec.local');
  await page.getByRole('link', { name: 'Configurações' }).click();
  await page.getByLabel('Usuário', { exact: true }).fill('usuario-dev');
  await page.getByLabel('Senha').fill('senha-dev');
  await page.getByLabel('Token da SGA').fill('token-dev');
  await page.getByRole('button', { name: 'Salvar credenciais' }).click();
  await expect(page.getByText('Credenciais salvas.')).toBeVisible();
  await page.getByLabel('Buscar voluntário por nome').fill('Carla');
  await page.getByLabel('Buscar voluntário por nome').press('Enter');
  await page.getByLabel('Usuário APROVEC').selectOption({ label: 'Pedro Santos' });
  await page.getByRole('button', { name: 'Vincular' }).click();
  await expect(page.getByText('Voluntário vinculado.')).toBeVisible();

  // Pedro pega o próprio link de indicação.
  await login(page, tenant, 'pedro@aprovec.local');
  await expect(page.getByLabel('Seu link de indicação')).toBeVisible();
  const link = await page.getByLabel('Seu link de indicação').inputValue();

  // Um visitante (sem sessão) abre o link e preenche o formulário.
  await page.goto(link);
  await expect(page.getByRole('heading', { name: 'Você foi indicado por Pedro Santos' })).toBeVisible();
  await page.getByLabel('Nome completo').fill('Indicado E2E');
  await page.getByLabel('CPF').fill(cpf);
  await page.getByLabel('Celular').fill('11999990000');
  await page.getByLabel('E-mail').fill(email);
  await page.getByLabel('CEP').fill('01310-100');
  await page.getByLabel('Número').fill('100');
  await page.getByLabel('Bairro').fill('Bela Vista');
  await page.getByLabel('Cidade').fill('São Paulo');
  await page.getByLabel('Estado').fill('SP');
  await page.getByRole('button', { name: 'Solicitar cadastro' }).click();
  await expect(page.getByText('Solicitação enviada!')).toBeVisible();

  // O administrador aprova.
  await login(page, tenant, 'admin@aprovec.local');
  await page.getByRole('link', { name: 'Convites' }).click();
  await expect(page).toHaveURL(`${tenant}/administracao/convites`);
  const linha = page.getByRole('row', { name: new RegExp(cpf) });
  await expect(linha).toBeVisible();
  await expect(linha).toContainText('Pedro Santos');
  await linha.getByRole('button', { name: 'Aprovar' }).click();
  await expect(page.getByText('Solicitação aprovada.')).toBeVisible();

  // O novo participante recebe o e-mail e consegue definir a senha e logar.
  await page.goto(await latestLinkFor(email));
  await page.getByLabel('Nova senha').fill('senha-do-indicado-1');
  await page.getByLabel('Confirme a senha').fill('senha-do-indicado-1');
  await page.getByRole('button', { name: 'Salvar senha' }).click();
  await expect(page.getByRole('complementary').getByText('Indicado E2E')).toBeVisible();

  // Limpeza: desvincula Pedro da Hinova para deixar o voluntário 103 livre de novo (mesmo padrão
  // de limpeza dos outros testes deste arquivo que usam o banco de dev persistente).
  await page.goto(`${tenant}/configuracoes/integracoes`);
  const vinculosAtuais = page.locator('section', { has: page.getByRole('heading', { name: 'Vínculos atuais' }) });
  const vinculo = vinculosAtuais.getByRole('row', { name: /Pedro Santos/ });
  await vinculo.getByRole('button', { name: 'Desvincular' }).click();
  await expect(vinculo).not.toBeVisible();
});
```

Confira os textos exatos dos botões/labels da tela de definir senha (`Nova senha`, `Confirme a senha`, `Salvar senha`) contra `web/src/app/definir-senha/[token]/set-password-form.tsx` antes de rodar -- ajuste se os rótulos reais forem outros.

- [ ] **Step 2: Rodar o E2E**

Run: `cd web && npm run e2e -- --grep "indicação"`
Expected: PASS. Se falhar por um seletor/rótulo, ajuste o teste para bater com o texto real renderizado (não o código das páginas) -- as páginas das Tasks 7-9 são a fonte da verdade.

- [ ] **Step 3: Rodar o arquivo inteiro para confirmar que nada mais quebrou**

Run: `cd web && npm run e2e`
Expected: PASS em todos os testes do arquivo.

- [ ] **Step 4: Commit**

```bash
git add web/e2e/fundacao.spec.ts
git commit -m "test(e2e): cover the full self-service invite + approval happy path"
```
