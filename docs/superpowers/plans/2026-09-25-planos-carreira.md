# Planos de Carreira — Cadastro e Estrutura — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deixar um admin cadastrar planos de carreira (classificação CLT Interno/CLT Externo/Só Externo, faixas de bônus por placa, meta mínima, regime extra, recorrência opcional) e atribuir um a cada consultor — só a estrutura/cadastro, sem apurar nada de verdade ainda.

**Architecture:** Três tabelas novas (`planos_carreira` + duas filhas), desacopladas de `roles` e `commission_plans`; `users.plano_carreira_id` como atribuição simples; um novo endpoint CRUD (`PlanoCarreiraEndpoints.cs`) e um endpoint de atribuição em `UserEndpoints.cs`; uma tela nova em `web/src/app/administracao/remuneracao/` (reaproveitando o item de navegação "Remuneração" já reservado, hoje "Em breve") + um campo na tela de edição de participante já existente.

**Tech Stack:** C#/ASP.NET Core minimal APIs, Postgres com RLS, Dapper, Next.js 16 App Router (Server Actions), xUnit.

**Spec:** [docs/superpowers/specs/2026-09-25-planos-carreira-design.md](../specs/2026-09-25-planos-carreira-design.md)

## Global Constraints

- Nomenclatura: inglês só para `id`/`tenant_id`/`name`/`status`/`created_at`/`updated_at`; português para todo o resto (colunas de domínio e valores de enum) — mesma régua de `commission_plans`/`boletos`/`fechamentos`.
- Os valores do plano de carreira somam por cima do que `commission_plans` já calcula — nunca substituem, e nada aqui calcula valor real (isso é um projeto seguinte).
- Permissões: `regras_comissao.visualizar`/`regras_comissao.editar` para o CRUD do plano; `estrutura.editar` para atribuir um plano a um consultor.
- Fora de escopo: apuração real, integração "Listar Veículo" da Hinova, histórico de atribuição, fluxo rascunho/ativo com imutabilidade.

---

### Task 1: Migration — tabelas, RLS e extensão do guard de `users`

**Files:**
- Create: `src/Recorrencia.Db/Scripts/0024_planos_carreira.sql`
- Test: `tests/Recorrencia.Db.Tests/PlanosCarreiraTests.cs`

**Interfaces:**
- Produces: tabelas `planos_carreira`, `planos_carreira_faixas_bonus`, `planos_carreira_regras_recorrencia`; coluna `users.plano_carreira_id`; `app.users_column_guard()` estendida para proteger essa coluna com `estrutura.editar`. Tasks 2 e 3 dependem de todas essas.

- [ ] **Step 1: Escrever os testes que falham**

Criar `tests/Recorrencia.Db.Tests/PlanosCarreiraTests.cs`:

```csharp
namespace Recorrencia.Db.Tests;

[Collection(DbCollection.Name)]
public class PlanosCarreiraTests(PostgresFixture db)
{
    private readonly Seed _seed = new(db);

    [Fact]
    public async Task Administrador_can_create_a_plano_with_faixas_and_regras()
    {
        var t = await _seed.TenantAsync();
        var admin = await _seed.UserAsync(t, "Admin");
        await _seed.AssignTemplateRoleAsync(t, admin, "administrador");

        var planoId = Guid.NewGuid();
        await db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into planos_carreira (id, tenant_id, name, classificacao, janela_apuracao_dias, recorrencia_ativa)
            values (@planoId, @t, 'Consultor CLT Externo', 'clt_externo', 30, true)
            """,
            new { planoId, t }, tx));
        await db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into planos_carreira_faixas_bonus (id, tenant_id, plano_carreira_id, quantidade_min, quantidade_max, valor_por_placa)
            values (@id, @t, @planoId, 1, 14, 33)
            """,
            new { id = Guid.NewGuid(), t, planoId }, tx));
        await db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            """
            insert into planos_carreira_regras_recorrencia (id, tenant_id, plano_carreira_id, tipo, nivel, taxa)
            values (@id, @t, @planoId, 'propria', null, 0.07)
            """,
            new { id = Guid.NewGuid(), t, planoId }, tx));

        var count = await db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteScalarAsync<int>(
            "select count(*) from planos_carreira_faixas_bonus where plano_carreira_id = @planoId", new { planoId }, tx));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task User_without_regras_comissao_editar_cannot_create_a_plano()
    {
        var t = await _seed.TenantAsync();
        var u = await _seed.UserAsync(t, "Sem permissão");
        var role = await _seed.RoleAsync(t, "Sem regras", ("estrutura.visualizar", "tenant"));
        await _seed.AssignRoleAsync(t, u, role);

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, u, (c, tx) => c.ExecuteAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@id, @t, 'X', 'clt_interno')",
            new { id = Guid.NewGuid(), t }, tx)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Setting_plano_carreira_without_estrutura_editar_is_rejected()
    {
        var t = await _seed.TenantAsync();
        var admin = await _seed.UserAsync(t, "Admin", status: "ativo");
        var alvo = await _seed.UserAsync(t, "Alvo");
        var planoId = Guid.NewGuid();
        await _seed.ExecAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@planoId, @t, 'Plano', 'clt_interno')",
            new { planoId, t });
        var role = await _seed.RoleAsync(t, "Sem estrutura editar", ("usuarios.convidar", null));
        await _seed.AssignRoleAsync(t, admin, role);

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            "update users set plano_carreira_id = @planoId where id = @alvo", new { planoId, alvo }, tx)));
        Assert.Equal("auth.forbidden", ex.MessageText);
    }

    [Fact]
    public async Task Setting_plano_carreira_with_estrutura_editar_succeeds()
    {
        var t = await _seed.TenantAsync();
        var admin = await _seed.UserAsync(t, "Admin", status: "ativo");
        var alvo = await _seed.UserAsync(t, "Alvo");
        var planoId = Guid.NewGuid();
        await _seed.ExecAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@planoId, @t, 'Plano', 'clt_interno')",
            new { planoId, t });
        var role = await _seed.RoleAsync(t, "Com estrutura editar", ("estrutura.editar", null));
        await _seed.AssignRoleAsync(t, admin, role);

        await db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            "update users set plano_carreira_id = @planoId where id = @alvo", new { planoId, alvo }, tx));

        var result = await _seed.ScalarAsync<Guid?>("select plano_carreira_id from users where id = @alvo", new { alvo });
        Assert.Equal(planoId, result);
    }

    [Fact]
    public async Task Meta_minima_requires_both_fields_together()
    {
        var t = await _seed.TenantAsync();
        var admin = await _seed.UserAsync(t, "Admin");
        await _seed.AssignTemplateRoleAsync(t, admin, "administrador");

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao, meta_minima_contratos) values (@id, @t, 'X', 'clt_interno', 10)",
            new { id = Guid.NewGuid(), t }, tx)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Regra_upline_requires_a_nivel()
    {
        var t = await _seed.TenantAsync();
        var admin = await _seed.UserAsync(t, "Admin");
        await _seed.AssignTemplateRoleAsync(t, admin, "administrador");
        var planoId = Guid.NewGuid();
        await _seed.ExecAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@planoId, @t, 'Plano', 'clt_interno')",
            new { planoId, t });

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            "insert into planos_carreira_regras_recorrencia (id, tenant_id, plano_carreira_id, tipo, nivel, taxa) values (@id, @t, @planoId, 'upline', null, 0.02)",
            new { id = Guid.NewGuid(), t, planoId }, tx)));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task Regra_recorrencia_cannot_duplicate_tipo_e_nivel()
    {
        var t = await _seed.TenantAsync();
        var admin = await _seed.UserAsync(t, "Admin");
        await _seed.AssignTemplateRoleAsync(t, admin, "administrador");
        var planoId = Guid.NewGuid();
        await _seed.ExecAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@planoId, @t, 'Plano', 'clt_interno')",
            new { planoId, t });
        await db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            "insert into planos_carreira_regras_recorrencia (id, tenant_id, plano_carreira_id, tipo, nivel, taxa) values (@id, @t, @planoId, 'propria', null, 0.07)",
            new { id = Guid.NewGuid(), t, planoId }, tx));

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t, admin, (c, tx) => c.ExecuteAsync(
            "insert into planos_carreira_regras_recorrencia (id, tenant_id, plano_carreira_id, tipo, nivel, taxa) values (@id, @t, @planoId, 'propria', null, 0.05)",
            new { id = Guid.NewGuid(), t, planoId }, tx)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Assigning_a_plano_from_another_tenant_is_rejected()
    {
        var t1 = await _seed.TenantAsync();
        var t2 = await _seed.TenantAsync();
        var admin = await _seed.UserAsync(t1, "Admin");
        await _seed.AssignTemplateRoleAsync(t1, admin, "administrador");
        var alvo = await _seed.UserAsync(t1, "Alvo");
        var planoOutroTenant = Guid.NewGuid();
        await _seed.ExecAsync(
            "insert into planos_carreira (id, tenant_id, name, classificacao) values (@id, @t2, 'Outro', 'clt_interno')",
            new { id = planoOutroTenant, t2 });

        var ex = await DbExtensions.ThrowsPgAsync(() => db.AsAppUserAsync(t1, admin, (c, tx) => c.ExecuteAsync(
            "update users set plano_carreira_id = @planoOutroTenant where id = @alvo", new { planoOutroTenant, alvo }, tx)));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }
}
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `cd tests/Recorrencia.Db.Tests && dotnet test --filter "FullyQualifiedName~PlanosCarreiraTests"`
Expected: FAIL — `relation "planos_carreira" does not exist` (as tabelas ainda não existem).

- [ ] **Step 3: Criar a migration**

Criar `src/Recorrencia.Db/Scripts/0024_planos_carreira.sql`:

```sql
create table planos_carreira (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  name text not null check (length(btrim(name)) > 0),
  status text not null default 'ativo' check (status in ('ativo', 'inativo')),
  classificacao text not null check (classificacao in ('clt_interno', 'clt_externo', 'so_externo')),
  janela_apuracao_dias int not null default 30 check (janela_apuracao_dias > 0),
  meta_minima_contratos int check (meta_minima_contratos is null or meta_minima_contratos > 0),
  meta_minima_fonte_data text check (meta_minima_fonte_data in ('contrato', 'cadastro')),
  bonus_extra_valor numeric(14,2) check (bonus_extra_valor is null or bonus_extra_valor > 0),
  recorrencia_ativa boolean not null default false,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, id),
  check ((meta_minima_contratos is null) = (meta_minima_fonte_data is null))
);

create trigger planos_carreira_touch before update on planos_carreira
  for each row execute function app.touch_updated_at();

create table planos_carreira_faixas_bonus (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  plano_carreira_id uuid not null,
  quantidade_min int not null check (quantidade_min > 0),
  quantidade_max int check (quantidade_max is null or quantidade_max >= quantidade_min),
  valor_por_placa numeric(14,2) not null check (valor_por_placa > 0),
  foreign key (tenant_id, plano_carreira_id) references planos_carreira (tenant_id, id) on delete cascade
);

create table planos_carreira_regras_recorrencia (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  plano_carreira_id uuid not null,
  tipo text not null check (tipo in ('propria', 'upline')),
  nivel int check (nivel is null or nivel > 0),
  taxa numeric(7,4) not null check (taxa > 0 and taxa <= 1),
  foreign key (tenant_id, plano_carreira_id) references planos_carreira (tenant_id, id) on delete cascade,
  unique (plano_carreira_id, tipo, nivel),
  check ((tipo = 'upline') = (nivel is not null))
);

alter table users add column plano_carreira_id uuid;
alter table users add foreign key (tenant_id, plano_carreira_id) references planos_carreira (tenant_id, id);

-- app.users_column_guard() (0003_users_hierarchy.sql, já estendida por 0021/0023) só protege
-- supervisor_id/status/name/email hoje -- sem esta extensão, a policy geral de users_update
-- (estrutura.editar OU usuarios.desligar OU usuarios.convidar) seria a ÚNICA barreira pra mudar
-- plano_carreira_id, mais larga do que a intenção deste plano (só estrutura.editar).
create or replace function app.users_column_guard() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  if app.current_user_id() is null then
    return new;
  end if;

  if new.supervisor_id is distinct from old.supervisor_id
     and not app.has_permission('estrutura.editar') then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  if new.plano_carreira_id is distinct from old.plano_carreira_id
     and not app.has_permission('estrutura.editar') then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  if new.status is distinct from old.status
     and not (
       app.has_permission('usuarios.desligar')
       or (old.status = 'convidado' and new.status = 'ativo' and app.has_permission('usuarios.convidar'))
     ) then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  if (new.name is distinct from old.name or new.email is distinct from old.email)
     and not app.has_permission('usuarios.convidar') then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  return new;
end
$$;

do $$
declare
  t text;
begin
  foreach t in array array['planos_carreira', 'planos_carreira_faixas_bonus', 'planos_carreira_regras_recorrencia']
  loop
    execute format('alter table %I enable row level security', t);
    execute format('alter table %I force row level security', t);
    execute format(
      'create policy %I on %I as restrictive for all to app_user using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant())',
      t || '_tenant_isolation', t);
    -- Mesmo padrão de commission_plans/commission_rules (0011_rls.sql): SELECT aberto a
    -- qualquer membro do tenant (regras_comissao.visualizar é checada na API); só escrita
    -- exige regras_comissao.editar.
    execute format('create policy %I on %I for select to app_user using (true)', t || '_select', t);
    execute format(
      'create policy %I on %I for all to app_user using ((select app.has_permission(''regras_comissao.editar''))) with check ((select app.has_permission(''regras_comissao.editar'')))',
      t || '_write', t);
  end loop;
end
$$;

grant select, insert, update, delete on planos_carreira, planos_carreira_faixas_bonus, planos_carreira_regras_recorrencia to app_user;

-- users tem grants explícitos por coluna (0003_users_hierarchy.sql), não um grant geral --
-- a coluna nova precisa entrar nessas duas listas explicitamente.
grant select (plano_carreira_id) on users to app_user;
grant update (plano_carreira_id) on users to app_user;
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `cd tests/Recorrencia.Db.Tests && dotnet test --filter "FullyQualifiedName~PlanosCarreiraTests"`
Expected: PASS — 7 testes.

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Db/Scripts/0024_planos_carreira.sql tests/Recorrencia.Db.Tests/PlanosCarreiraTests.cs
git commit -m "feat(db): add planos_carreira tables, RLS and users_column_guard extension"
```

---

### Task 2: `PlanoCarreiraEndpoints.cs` — CRUD do plano de carreira

**Files:**
- Create: `src/Recorrencia.Api/PlanosCarreira/PlanoCarreiraEndpoints.cs`
- Modify: `src/Recorrencia.Api/Program.cs` (registrar o novo endpoint)
- Modify: `web/src/lib/errors.ts`
- Test: `tests/Recorrencia.Api.Tests/PlanoCarreiraTests.cs`

**Interfaces:**
- Consumes: as tabelas da Task 1.
- Produces: `GET/POST /planos-carreira`, `PUT/DELETE /planos-carreira/{id}`. `PlanoCarreiraResponse(Guid Id, string Name, string Status, string Classificacao, int JanelaApuracaoDias, int? MetaMinimaContratos, string? MetaMinimaFonteData, decimal? BonusExtraValor, bool RecorrenciaAtiva, IReadOnlyList<FaixaBonusResponse> Faixas, IReadOnlyList<RegraRecorrenciaResponse> RegrasRecorrencia)` — Tasks 4 e 5 (frontend) consomem exatamente esse formato de resposta.

- [ ] **Step 1: Escrever os testes que falham**

Criar `tests/Recorrencia.Api.Tests/PlanoCarreiraTests.cs`:

```csharp
using Recorrencia.Api.Cli;

namespace Recorrencia.Api.Tests;

[Collection(ApiCollection.Name)]
public class PlanoCarreiraTests(ApiFixture api)
{
    public sealed record FaixaDto(Guid Id, int QuantidadeMin, int? QuantidadeMax, decimal ValorPorPlaca);
    public sealed record RegraDto(Guid Id, string Tipo, int? Nivel, decimal Taxa);
    public sealed record PlanoDto(Guid Id, string Name, string Status, string Classificacao, int JanelaApuracaoDias,
        int? MetaMinimaContratos, string? MetaMinimaFonteData, decimal? BonusExtraValor, bool RecorrenciaAtiva,
        List<FaixaDto> Faixas, List<RegraDto> RegrasRecorrencia);

    private async Task<ApiClient> LoginAsAdminAsync(SeededTenant s)
    {
        var client = api.Client(s.Slug);
        await client.LoginAsync($"admin@{s.Slug}.local", ApiFixture.Password);
        return client;
    }

    private static readonly object CorpoValido = new
    {
        name = "Consultor CLT Externo",
        classificacao = "clt_externo",
        janelaApuracaoDias = 30,
        metaMinimaContratos = (int?)null,
        metaMinimaFonteData = (string?)null,
        bonusExtraValor = 1500m,
        recorrenciaAtiva = true,
        faixas = new[]
        {
            new { quantidadeMin = 1, quantidadeMax = (int?)14, valorPorPlaca = 33m },
            new { quantidadeMin = 15, quantidadeMax = (int?)19, valorPorPlaca = 40m },
            new { quantidadeMin = 30, quantidadeMax = (int?)null, valorPorPlaca = 60m },
        },
        regrasRecorrencia = new[]
        {
            new { tipo = "propria", nivel = (int?)null, taxa = 0.07m },
            new { tipo = "upline", nivel = (int?)1, taxa = 0.02m },
        },
    };

    [Fact]
    public async Task Administrador_creates_and_lists_a_plano()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var created = await admin.PostAsync("/planos-carreira", CorpoValido);
        await ApiClient.ExpectAsync(created, HttpStatusCode.Created);
        var plano = (await created.Content.ReadFromJsonAsync<PlanoDto>(ApiClient.Json))!;
        Assert.Equal("ativo", plano.Status);
        Assert.Equal(3, plano.Faixas.Count);
        Assert.Equal(2, plano.RegrasRecorrencia.Count);

        var list = await admin.GetJsonAsync<List<PlanoDto>>("/planos-carreira");
        Assert.Contains(list, p => p.Id == plano.Id);
    }

    [Fact]
    public async Task Creating_without_name_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var response = await admin.PostAsync("/planos-carreira", new { name = "", classificacao = "clt_interno", janelaApuracaoDias = 30, recorrenciaAtiva = false });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("plano_carreira.name_required", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Meta_minima_with_only_one_field_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var response = await admin.PostAsync("/planos-carreira", new
        {
            name = "X", classificacao = "clt_interno", janelaApuracaoDias = 30, recorrenciaAtiva = false,
            metaMinimaContratos = 10,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("plano_carreira.meta_minima_incompleta", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Recorrencia_ativa_without_regras_is_rejected()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);

        var response = await admin.PostAsync("/planos-carreira", new
        {
            name = "X", classificacao = "clt_interno", janelaApuracaoDias = 30, recorrenciaAtiva = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("plano_carreira.recorrencia_inconsistente", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Consultor_cannot_create_a_plano()
    {
        var s = await api.SeedAsync();
        var joao = api.Client(s.Slug);
        await joao.LoginAsync($"joao@{s.Slug}.local", ApiFixture.Password);

        var response = await joao.PostAsync("/planos-carreira", CorpoValido);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Updating_a_plano_replaces_its_faixas_and_regras()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        var created = await admin.PostAsync("/planos-carreira", CorpoValido);
        var plano = (await created.Content.ReadFromJsonAsync<PlanoDto>(ApiClient.Json))!;

        var response = await admin.PutAsync($"/planos-carreira/{plano.Id}", new
        {
            name = "Renomeado", status = "inativo", classificacao = "clt_externo", janelaApuracaoDias = 45,
            metaMinimaContratos = (int?)null, metaMinimaFonteData = (string?)null, bonusExtraValor = (decimal?)null,
            recorrenciaAtiva = false,
            faixas = new[] { new { quantidadeMin = 1, quantidadeMax = (int?)null, valorPorPlaca = 50m } },
            regrasRecorrencia = Array.Empty<object>(),
        });

        await ApiClient.ExpectAsync(response, HttpStatusCode.OK);
        var atualizado = (await response.Content.ReadFromJsonAsync<PlanoDto>(ApiClient.Json))!;
        Assert.Equal("Renomeado", atualizado.Name);
        Assert.Equal("inativo", atualizado.Status);
        Assert.Single(atualizado.Faixas);
        Assert.Empty(atualizado.RegrasRecorrencia);
    }

    [Fact]
    public async Task Deleting_a_plano_in_use_is_a_conflict()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        var created = await admin.PostAsync("/planos-carreira", CorpoValido);
        var plano = (await created.Content.ReadFromJsonAsync<PlanoDto>(ApiClient.Json))!;
        await ApiClient.ExpectAsync(
            await admin.PutAsync($"/users/{s.Joao}/plano-carreira", new { planoCarreiraId = plano.Id }),
            HttpStatusCode.NoContent);

        var response = await admin.DeleteAsync($"/planos-carreira/{plano.Id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("plano_carreira.em_uso", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Deleting_an_unused_plano_succeeds()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsAdminAsync(s);
        var created = await admin.PostAsync("/planos-carreira", CorpoValido);
        var plano = (await created.Content.ReadFromJsonAsync<PlanoDto>(ApiClient.Json))!;

        await ApiClient.ExpectAsync(await admin.DeleteAsync($"/planos-carreira/{plano.Id}"), HttpStatusCode.NoContent);

        var list = await admin.GetJsonAsync<List<PlanoDto>>("/planos-carreira");
        Assert.DoesNotContain(list, p => p.Id == plano.Id);
    }
}
```

Este teste (`Deleting_a_plano_in_use_is_a_conflict`) usa `PUT /users/{id}/plano-carreira`, que só existe depois da Task 3 — normal, ele fica **pendente (falhando por 404 de rota)** até a Task 3 estar feita; os outros 7 testes desta task já devem passar com o que é implementado aqui.

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "FullyQualifiedName~PlanoCarreiraTests"`
Expected: FAIL — 404 em todos (a rota não existe ainda).

- [ ] **Step 3: Implementar**

Criar `src/Recorrencia.Api/PlanosCarreira/PlanoCarreiraEndpoints.cs`:

```csharp
using Npgsql;
using Recorrencia.Api.Authorization;
using Recorrencia.Api.Infrastructure;
using Recorrencia.Api.Tenancy;
using static Recorrencia.Api.Audit.Audit;

namespace Recorrencia.Api.PlanosCarreira;

public static class PlanoCarreiraEndpoints
{
    public sealed record FaixaBonusInput(int QuantidadeMin, int? QuantidadeMax, decimal ValorPorPlaca);
    public sealed record RegraRecorrenciaInput(string? Tipo, int? Nivel, decimal Taxa);
    public sealed record PlanoCarreiraRequest(string? Name, string? Status, string? Classificacao, int JanelaApuracaoDias,
        int? MetaMinimaContratos, string? MetaMinimaFonteData, decimal? BonusExtraValor, bool RecorrenciaAtiva,
        FaixaBonusInput[]? Faixas, RegraRecorrenciaInput[]? RegrasRecorrencia);

    public sealed record FaixaBonusResponse(Guid Id, int QuantidadeMin, int? QuantidadeMax, decimal ValorPorPlaca);
    public sealed record RegraRecorrenciaResponse(Guid Id, string Tipo, int? Nivel, decimal Taxa);
    public sealed record PlanoCarreiraResponse(Guid Id, string Name, string Status, string Classificacao,
        int JanelaApuracaoDias, int? MetaMinimaContratos, string? MetaMinimaFonteData, decimal? BonusExtraValor,
        bool RecorrenciaAtiva, IReadOnlyList<FaixaBonusResponse> Faixas, IReadOnlyList<RegraRecorrenciaResponse> RegrasRecorrencia);

    private sealed class PlanoRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Classificacao { get; set; } = "";
        public int JanelaApuracaoDias { get; set; }
        public int? MetaMinimaContratos { get; set; }
        public string? MetaMinimaFonteData { get; set; }
        public decimal? BonusExtraValor { get; set; }
        public bool RecorrenciaAtiva { get; set; }
    }

    private static readonly string[] Classificacoes = ["clt_interno", "clt_externo", "so_externo"];
    private static readonly string[] MetaMinimaFontes = ["contrato", "cadastro"];
    private static readonly string[] StatusValidos = ["ativo", "inativo"];
    private static readonly string[] TiposRecorrencia = ["propria", "upline"];

    public static void MapPlanoCarreiraEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/planos-carreira", ListarAsync).RequirePermission("regras_comissao.visualizar");
        app.MapPost("/planos-carreira", CriarAsync).RequirePermission("regras_comissao.editar");
        app.MapPut("/planos-carreira/{id:guid}", AtualizarAsync).RequirePermission("regras_comissao.editar");
        app.MapDelete("/planos-carreira/{id:guid}", RemoverAsync).RequirePermission("regras_comissao.editar");
    }

    private static async Task<PlanoCarreiraResponse> LoadPlanoAsync(Tx tx, Guid id)
    {
        var plano = await tx.QuerySingleAsync<PlanoRow>(
            """
            select id, name, status, classificacao, janela_apuracao_dias, meta_minima_contratos,
                   meta_minima_fonte_data, bonus_extra_valor, recorrencia_ativa
              from planos_carreira where id = @id
            """,
            new { id });
        var faixas = (await tx.QueryAsync<FaixaBonusResponse>(
            "select id, quantidade_min, quantidade_max, valor_por_placa from planos_carreira_faixas_bonus where plano_carreira_id = @id order by quantidade_min",
            new { id })).ToList();
        var regras = (await tx.QueryAsync<RegraRecorrenciaResponse>(
            "select id, tipo, nivel, taxa from planos_carreira_regras_recorrencia where plano_carreira_id = @id order by tipo, nivel",
            new { id })).ToList();
        return new PlanoCarreiraResponse(plano.Id, plano.Name, plano.Status, plano.Classificacao, plano.JanelaApuracaoDias,
            plano.MetaMinimaContratos, plano.MetaMinimaFonteData, plano.BonusExtraValor, plano.RecorrenciaAtiva, faixas, regras);
    }

    private static async Task<IResult> ListarAsync(RequestContext request, Database db, CancellationToken ct)
    {
        var planos = await db.InTenantAsync(request.RequireTenant(), request.RequireUser(), async tx =>
        {
            var ids = await tx.QueryAsync<Guid>("select id from planos_carreira order by name");
            var list = new List<PlanoCarreiraResponse>();
            foreach (var id in ids)
                list.Add(await LoadPlanoAsync(tx, id));
            return list;
        }, ct);
        return Results.Ok(planos);
    }

    private static (string Name, string Classificacao, FaixaBonusInput[] Faixas, RegraRecorrenciaInput[] Regras) Validate(PlanoCarreiraRequest body)
    {
        var name = (body.Name ?? "").Trim();
        if (name.Length == 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.name_required");

        if (body.Classificacao is null || !Classificacoes.Contains(body.Classificacao))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");
        if (body.JanelaApuracaoDias <= 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");
        if (body.BonusExtraValor is <= 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");
        if (body.Status is not null && !StatusValidos.Contains(body.Status))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");

        if ((body.MetaMinimaContratos is null) != (body.MetaMinimaFonteData is null))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.meta_minima_incompleta");
        if (body.MetaMinimaContratos is <= 0)
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.meta_minima_incompleta");
        if (body.MetaMinimaFonteData is not null && !MetaMinimaFontes.Contains(body.MetaMinimaFonteData))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.meta_minima_incompleta");

        var faixas = body.Faixas ?? [];
        if (faixas.Any(f => f.QuantidadeMin <= 0 || (f.QuantidadeMax is not null && f.QuantidadeMax < f.QuantidadeMin) || f.ValorPorPlaca <= 0))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");

        var regras = body.RegrasRecorrencia ?? [];
        if (body.RecorrenciaAtiva != (regras.Length > 0))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.recorrencia_inconsistente");
        if (regras.Any(r => r.Tipo is null || !TiposRecorrencia.Contains(r.Tipo)
                || (r.Tipo == "upline") != (r.Nivel is >= 1)
                || r.Taxa <= 0 || r.Taxa > 1))
            throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.dados_invalidos");

        return (name, body.Classificacao, faixas, regras);
    }

    private static async Task InsertFaixasAsync(Tx tx, Guid tenant, Guid planoId, FaixaBonusInput[] faixas)
    {
        foreach (var faixa in faixas)
        {
            await tx.ExecuteAsync(
                """
                insert into planos_carreira_faixas_bonus (id, tenant_id, plano_carreira_id, quantidade_min, quantidade_max, valor_por_placa)
                values (@id, @tenant, @planoId, @quantidadeMin, @quantidadeMax, @valorPorPlaca)
                """,
                new { id = Guid.CreateVersion7(), tenant, planoId, quantidadeMin = faixa.QuantidadeMin, quantidadeMax = faixa.QuantidadeMax, valorPorPlaca = faixa.ValorPorPlaca });
        }
    }

    private static async Task InsertRegrasAsync(Tx tx, Guid tenant, Guid planoId, RegraRecorrenciaInput[] regras)
    {
        try
        {
            foreach (var regra in regras)
            {
                await tx.ExecuteAsync(
                    """
                    insert into planos_carreira_regras_recorrencia (id, tenant_id, plano_carreira_id, tipo, nivel, taxa)
                    values (@id, @tenant, @planoId, @tipo, @nivel, @taxa)
                    """,
                    new { id = Guid.CreateVersion7(), tenant, planoId, tipo = regra.Tipo, nivel = regra.Nivel, taxa = regra.Taxa });
            }
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ApiProblem(StatusCodes.Status409Conflict, "plano_carreira.regra_duplicada");
        }
    }

    private static async Task<IResult> CriarAsync(PlanoCarreiraRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var (name, classificacao, faixas, regras) = Validate(body);

        var id = Guid.CreateVersion7();
        var plano = await db.InTenantAsync(tenant, actor, async tx =>
        {
            await tx.ExecuteAsync(
                """
                insert into planos_carreira (id, tenant_id, name, classificacao, janela_apuracao_dias, meta_minima_contratos,
                       meta_minima_fonte_data, bonus_extra_valor, recorrencia_ativa)
                values (@id, @tenant, @name, @classificacao, @janelaApuracaoDias, @metaMinimaContratos,
                       @metaMinimaFonteData, @bonusExtraValor, @recorrenciaAtiva)
                """,
                new { id, tenant, name, classificacao, janelaApuracaoDias = body.JanelaApuracaoDias,
                    metaMinimaContratos = body.MetaMinimaContratos, metaMinimaFonteData = body.MetaMinimaFonteData,
                    bonusExtraValor = body.BonusExtraValor, recorrenciaAtiva = body.RecorrenciaAtiva });
            await InsertFaixasAsync(tx, tenant, id, faixas);
            await InsertRegrasAsync(tx, tenant, id, regras);
            await WriteAsync(tx, tenant, actor, "planos_carreira.criar", "planos_carreira", id, null, body);
            return await LoadPlanoAsync(tx, id);
        }, ct);
        return Results.Created($"/planos-carreira/{id}", plano);
    }

    private static async Task<IResult> AtualizarAsync(Guid id, PlanoCarreiraRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        var (name, classificacao, faixas, regras) = Validate(body);
        var status = body.Status ?? "ativo";

        var plano = await db.InTenantAsync(tenant, actor, async tx =>
        {
            var affected = await tx.ExecuteAsync(
                """
                update planos_carreira set name = @name, status = @status, classificacao = @classificacao,
                       janela_apuracao_dias = @janelaApuracaoDias, meta_minima_contratos = @metaMinimaContratos,
                       meta_minima_fonte_data = @metaMinimaFonteData, bonus_extra_valor = @bonusExtraValor,
                       recorrencia_ativa = @recorrenciaAtiva
                 where id = @id
                """,
                new { id, name, status, classificacao, janelaApuracaoDias = body.JanelaApuracaoDias,
                    metaMinimaContratos = body.MetaMinimaContratos, metaMinimaFonteData = body.MetaMinimaFonteData,
                    bonusExtraValor = body.BonusExtraValor, recorrenciaAtiva = body.RecorrenciaAtiva });
            if (affected == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "plano_carreira.not_found");
            await tx.ExecuteAsync("delete from planos_carreira_faixas_bonus where plano_carreira_id = @id", new { id });
            await tx.ExecuteAsync("delete from planos_carreira_regras_recorrencia where plano_carreira_id = @id", new { id });
            await InsertFaixasAsync(tx, tenant, id, faixas);
            await InsertRegrasAsync(tx, tenant, id, regras);
            await WriteAsync(tx, tenant, actor, "planos_carreira.atualizar", "planos_carreira", id, null, body);
            return await LoadPlanoAsync(tx, id);
        }, ct);
        return Results.Ok(plano);
    }

    private static async Task<IResult> RemoverAsync(Guid id, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var emUso = await tx.ExecuteScalarAsync<bool>("select exists(select 1 from users where plano_carreira_id = @id)", new { id });
            if (emUso)
                throw new ApiProblem(StatusCodes.Status409Conflict, "plano_carreira.em_uso");
            var affected = await tx.ExecuteAsync("delete from planos_carreira where id = @id", new { id });
            if (affected == 0)
                throw new ApiProblem(StatusCodes.Status404NotFound, "plano_carreira.not_found");
            await WriteAsync(tx, tenant, actor, "planos_carreira.remover", "planos_carreira", id, null, null);
            return 0;
        }, ct);
        return Results.NoContent();
    }
}
```

Em `src/Recorrencia.Api/Program.cs`, junto das outras chamadas `app.Map*Endpoints()`, adicionar:

```csharp
app.MapPlanoCarreiraEndpoints();
```

(qualquer posição entre as demais — não há ordem de dependência entre elas).

Em `web/src/lib/errors.ts`, adicionar (novo bloco, antes do fechamento do objeto):

```ts
  'plano_carreira.name_required': 'Informe o nome do plano.',
  'plano_carreira.dados_invalidos': 'Confira os dados do plano — algum campo está inválido.',
  'plano_carreira.meta_minima_incompleta': 'Preencha a quantidade mínima e a fonte de data juntas, ou deixe as duas em branco.',
  'plano_carreira.recorrencia_inconsistente': 'Ative a recorrência para informar as taxas, ou remova as taxas se ela estiver desativada.',
  'plano_carreira.regra_duplicada': 'Já existe uma regra de recorrência para esse tipo e nível.',
  'plano_carreira.not_found': 'Plano de carreira não encontrado.',
  'plano_carreira.em_uso': 'Este plano está atribuído a participantes — remova a atribuição antes de excluir.',
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "FullyQualifiedName~PlanoCarreiraTests"`
Expected: 7 de 8 PASS (`Deleting_a_plano_in_use_is_a_conflict` continua falhando até a Task 3 — esperado).

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Api/PlanosCarreira/PlanoCarreiraEndpoints.cs src/Recorrencia.Api/Program.cs \
  web/src/lib/errors.ts tests/Recorrencia.Api.Tests/PlanoCarreiraTests.cs
git commit -m "feat(api): add CRUD endpoints for planos de carreira"
```

---

### Task 3: `PUT /users/{id}/plano-carreira` — atribuir plano ao consultor

**Files:**
- Modify: `src/Recorrencia.Api/Users/UserEndpoints.cs`
- Modify: `web/src/lib/errors.ts`
- Test: `tests/Recorrencia.Api.Tests/UserTests.cs`, `tests/Recorrencia.Api.Tests/PlanoCarreiraTests.cs`

**Interfaces:**
- Produces: `PUT /users/{id}/plano-carreira`; `UserRow`/`GET /users` ganham `PlanoCarreiraId`. Tasks 5 e 6 (frontend) consomem esse campo pra saber qual plano já está atribuído.

- [ ] **Step 1: Escrever os testes que falham**

Adicionar ao final da classe `UserTests` em `tests/Recorrencia.Api.Tests/UserTests.cs`:

```csharp
    [Fact]
    public async Task Admin_assigns_and_removes_a_plano_carreira()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var created = await admin.PostAsync("/planos-carreira", new
        {
            name = "Plano X", classificacao = "clt_interno", janelaApuracaoDias = 30, recorrenciaAtiva = false,
        });
        var planoId = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        await ApiClient.ExpectAsync(
            await admin.PutAsync($"/users/{s.Maria}/plano-carreira", new { planoCarreiraId = planoId }),
            HttpStatusCode.NoContent);
        var sees = await admin.GetJsonAsync<List<UserDto>>("/users");
        Assert.Equal(planoId, sees.Single(u => u.Id == s.Maria).PlanoCarreiraId);

        await ApiClient.ExpectAsync(
            await admin.PutAsync($"/users/{s.Maria}/plano-carreira", new { planoCarreiraId = (Guid?)null }),
            HttpStatusCode.NoContent);
        var depois = await admin.GetJsonAsync<List<UserDto>>("/users");
        Assert.Null(depois.Single(u => u.Id == s.Maria).PlanoCarreiraId);
    }

    [Fact]
    public async Task Assigning_a_plano_from_another_tenant_is_rejected()
    {
        var s = await api.SeedAsync();
        var other = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var otherAdmin = await LoginAsync(other, "admin");
        var created = await otherAdmin.PostAsync("/planos-carreira", new
        {
            name = "Plano de outro tenant", classificacao = "clt_interno", janelaApuracaoDias = 30, recorrenciaAtiva = false,
        });
        var planoDeOutroTenant = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

        var response = await admin.PutAsync($"/users/{s.Maria}/plano-carreira", new { planoCarreiraId = planoDeOutroTenant });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("plano_carreira.invalido", await ApiClient.CodeAsync(response));
    }

    [Fact]
    public async Task Assigning_a_plano_without_estrutura_editar_is_forbidden()
    {
        var s = await api.SeedAsync();
        var admin = await LoginAsync(s, "admin");
        var created = await admin.PostAsync("/planos-carreira", new
        {
            name = "Plano Y", classificacao = "clt_interno", janelaApuracaoDias = 30, recorrenciaAtiva = false,
        });
        var planoId = (await created.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;
        var maria = await LoginAsync(s, "maria");

        var response = await maria.PutAsync($"/users/{s.Pedro}/plano-carreira", new { planoCarreiraId = planoId });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
```

`UserDto` (já existente na classe) precisa do campo novo — mudar sua declaração de:

```csharp
    public sealed record UserDto(Guid Id, string Name, string Email, string Status, Guid? SupervisorId, Guid[] RoleIds);
```

para:

```csharp
    public sealed record UserDto(Guid Id, string Name, string Email, string Status, Guid? SupervisorId, Guid[] RoleIds, Guid? PlanoCarreiraId);
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "FullyQualifiedName~UserTests|FullyQualifiedName~PlanoCarreiraTests"`
Expected: FAIL — os 3 novos testes em `UserTests.cs` com 404 (rota não existe), e `Deleting_a_plano_in_use_is_a_conflict` (da Task 2) também ainda falhando pelo mesmo motivo.

- [ ] **Step 3: Implementar**

Em `src/Recorrencia.Api/Users/UserEndpoints.cs`, adicionar `using Recorrencia.Api.PlanosCarreira;`? **Não** — não é necessário, o novo handler só faz SQL direto contra `planos_carreira`, sem referenciar tipos daquele namespace.

Mudar `UserRow` (topo do arquivo) de:

```csharp
    public sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Status { get; set; } = "";
        public Guid? SupervisorId { get; set; }
        public Guid[] RoleIds { get; set; } = [];
    }
```

para:

```csharp
    public sealed class UserRow
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Status { get; set; } = "";
        public Guid? SupervisorId { get; set; }
        public Guid[] RoleIds { get; set; } = [];
        public Guid? PlanoCarreiraId { get; set; }
    }
```

Mudar `ListAsync`'s SQL de:

```csharp
            select u.id, u.name, u.email, u.status, u.supervisor_id,
                   coalesce(array_agg(ur.role_id) filter (where ur.role_id is not null), '{}')::uuid[] as role_ids
              from users u
              left join user_roles ur on ur.user_id = u.id
             group by u.id, u.name, u.email, u.status, u.supervisor_id
             order by u.name
```

para (só adiciona a coluna nova ao `select` e ao `group by`):

```csharp
            select u.id, u.name, u.email, u.status, u.supervisor_id, u.plano_carreira_id,
                   coalesce(array_agg(ur.role_id) filter (where ur.role_id is not null), '{}')::uuid[] as role_ids
              from users u
              left join user_roles ur on ur.user_id = u.id
             group by u.id, u.name, u.email, u.status, u.supervisor_id, u.plano_carreira_id
             order by u.name
```

Adicionar o record (ao lado de `UpdateUserRequest`):

```csharp
    public sealed record PlanoCarreiraAssignRequest(Guid? PlanoCarreiraId);
```

Em `MapUserEndpoints`, adicionar a rota:

```csharp
        app.MapPut("/users/{id:guid}/plano-carreira", SetPlanoCarreiraAsync).RequirePermission("estrutura.editar");
```

Adicionar o handler (por exemplo depois de `SetPasswordAsync`, antes de `SetRolesAsync`):

```csharp
    private static async Task<IResult> SetPlanoCarreiraAsync(Guid id, PlanoCarreiraAssignRequest body, RequestContext request, Database db, CancellationToken ct)
    {
        var tenant = request.RequireTenant();
        var actor = request.RequireUser();
        await db.InTenantAsync(tenant, actor, async tx =>
        {
            var exists = await tx.QuerySingleOrDefaultAsync<bool?>("select true from users where id = @id for update", new { id });
            if (exists is null)
                throw new ApiProblem(StatusCodes.Status404NotFound, "users.not_found");
            try
            {
                await tx.ExecuteAsync("update users set plano_carreira_id = @planoCarreiraId where id = @id",
                    new { planoCarreiraId = body.PlanoCarreiraId, id });
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
            {
                throw new ApiProblem(StatusCodes.Status400BadRequest, "plano_carreira.invalido");
            }
            await WriteAsync(tx, tenant, actor, "users.set_plano_carreira", "users", id, null, new { body.PlanoCarreiraId });
            return 0;
        }, ct);
        return Results.NoContent();
    }
```

Em `web/src/lib/errors.ts`, adicionar (junto das outras entradas `plano_carreira.*` da Task 2):

```ts
  'plano_carreira.invalido': 'Selecione um plano de carreira válido.',
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `cd tests/Recorrencia.Api.Tests && dotnet test --filter "FullyQualifiedName~UserTests|FullyQualifiedName~PlanoCarreiraTests"`
Expected: PASS — todos os testes de `UserTests.cs` e todos os 8 de `PlanoCarreiraTests.cs`, incluindo `Deleting_a_plano_in_use_is_a_conflict` (que já usa esta rota).

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Api/Users/UserEndpoints.cs web/src/lib/errors.ts \
  tests/Recorrencia.Api.Tests/UserTests.cs
git commit -m "feat(api): add PUT /users/{id}/plano-carreira to assign a career plan"
```

---

### Task 4: Frontend — tipos, ativar o item de navegação "Remuneração" e a listagem

**Files:**
- Modify: `web/src/lib/types.ts`
- Modify: `web/src/app/app-shell.tsx`
- Create: `web/src/app/administracao/remuneracao/page.tsx`

**Interfaces:**
- Consumes: `GET /planos-carreira` (Task 2).
- Produces: `PlanoCarreira`, `FaixaBonus`, `RegraRecorrencia` (`web/src/lib/types.ts`) — Task 5 consome esses tipos.

Sem teste automatizado próprio (o projeto não tem testes de componente React). Verificação manual no Step 4.

- [ ] **Step 1: Adicionar os tipos**

Em `web/src/lib/types.ts`, ao final do arquivo:

```ts
export type FaixaBonus = { id: string; quantidadeMin: number; quantidadeMax: number | null; valorPorPlaca: number };

export type RegraRecorrencia = { id: string; tipo: 'propria' | 'upline'; nivel: number | null; taxa: number };

export type PlanoCarreira = {
  id: string;
  name: string;
  status: string;
  classificacao: 'clt_interno' | 'clt_externo' | 'so_externo';
  janelaApuracaoDias: number;
  metaMinimaContratos: number | null;
  metaMinimaFonteData: string | null;
  bonusExtraValor: number | null;
  recorrenciaAtiva: boolean;
  faixas: FaixaBonus[];
  regrasRecorrencia: RegraRecorrencia[];
};
```

E mudar `UserNode` de:

```ts
export type UserNode = { id: string; name: string; email: string; status: string; supervisorId: string | null; roleIds: string[] };
```

para:

```ts
export type UserNode = {
  id: string;
  name: string;
  email: string;
  status: string;
  supervisorId: string | null;
  roleIds: string[];
  planoCarreiraId: string | null;
};
```

- [ ] **Step 2: Ativar o item de navegação**

Em `web/src/app/app-shell.tsx`, mudar:

```tsx
  { id: 'remuneracao', icon: 'shield', label: 'Remuneração', href: null },
```

para:

```tsx
  { id: 'remuneracao', icon: 'shield', label: 'Remuneração', href: '/administracao/remuneracao', permission: 'regras_comissao.visualizar' },
```

- [ ] **Step 3: Criar a listagem**

Criar `web/src/app/administracao/remuneracao/page.tsx`:

```tsx
import { notFound } from 'next/navigation';
import { AppShell } from '../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import type { Me, PlanoCarreira } from '@/lib/types';

const CLASSIFICACAO_LABELS: Record<string, string> = {
  clt_interno: 'CLT Interno',
  clt_externo: 'CLT Externo',
  so_externo: 'Só Externo',
};

export default async function RemuneracaoPage() {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const has = (key: string) => me.permissions.some((p) => p.key === key);
  if (!has('regras_comissao.visualizar')) {
    return (
      <AppShell me={me} active="remuneracao">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso a planos de carreira.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const planos = await apiFetch<PlanoCarreira[]>('/planos-carreira');
  const canEdit = has('regras_comissao.editar');

  return (
    <AppShell me={me} active="remuneracao">
      <div className="shell">
        <div className="page-heading with-action">
          <div>
            <p className="eyebrow">Administração comercial</p>
            <h1>Planos de carreira</h1>
            <p className="muted">Classificação e regras de bonificação por consultor.</p>
          </div>
          {canEdit && (
            <a className="action-link" href="/administracao/remuneracao/novo">
              Novo plano
            </a>
          )}
        </div>
        <section className="card">
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Nome</th>
                  <th>Classificação</th>
                  <th>Status</th>
                  {canEdit && <th>Ações</th>}
                </tr>
              </thead>
              <tbody>
                {planos.length === 0 ? (
                  <tr>
                    <td colSpan={canEdit ? 4 : 3} className="empty-state">
                      <strong>Nenhum plano de carreira cadastrado</strong>
                    </td>
                  </tr>
                ) : (
                  planos.map((p) => (
                    <tr key={p.id}>
                      <td>{p.name}</td>
                      <td>{CLASSIFICACAO_LABELS[p.classificacao] ?? p.classificacao}</td>
                      <td>
                        <span className={p.status === 'ativo' ? 'badge progress' : 'badge neutral'}>{p.status}</span>
                      </td>
                      {canEdit && (
                        <td>
                          <a className="action-link" href={`/administracao/remuneracao/${p.id}/editar`}>
                            Editar
                          </a>
                        </td>
                      )}
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </section>
      </div>
    </AppShell>
  );
}
```

- [ ] **Step 4: Verificar manualmente**

Run: `cd web && npm run build`
Expected: build sem erros de tipo. `UserNode` ganhando um campo novo obrigatório pode acusar erro de tipo em qualquer lugar que constrói um `UserNode` manualmente (não deveria haver nenhum — é sempre lido da API) — se o build falhar por isso, é sinal de um local esquecido, corrigir antes de seguir.

Com a API e o web rodando localmente: abrir a sidebar, confirmar que "Remuneração" agora é um link clicável (não mais "Em breve"), e que `/administracao/remuneracao` mostra "Nenhum plano de carreira cadastrado".

- [ ] **Step 5: Commit**

```bash
git add web/src/lib/types.ts web/src/app/app-shell.tsx web/src/app/administracao/remuneracao/page.tsx
git commit -m "feat(web): activate Remuneração nav item and list planos de carreira"
```

---

### Task 5: Frontend — formulário de criar/editar plano de carreira

**Files:**
- Create: `web/src/app/administracao/remuneracao/plano-carreira-form.tsx`
- Create: `web/src/app/administracao/remuneracao/actions.ts`
- Create: `web/src/app/administracao/remuneracao/novo/page.tsx`
- Create: `web/src/app/administracao/remuneracao/[id]/editar/page.tsx`

**Interfaces:**
- Consumes: `PlanoCarreira`/`FaixaBonus`/`RegraRecorrencia` (Task 4), `POST/PUT /planos-carreira` (Task 2).

Sem teste automatizado próprio. Verificação manual no Step 5.

- [ ] **Step 1: Criar o server action**

Criar `web/src/app/administracao/remuneracao/actions.ts`:

```ts
'use server';

import { revalidatePath } from 'next/cache';
import { redirect } from 'next/navigation';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

type FaixaInput = { quantidadeMin: string; quantidadeMax: string; valorPorPlaca: string };
type RegraInput = { tipo: string; nivel: string; taxa: string };

export async function salvarPlanoCarreira(_: FormState, formData: FormData): Promise<FormState> {
  const id = formData.get('id') ? String(formData.get('id')) : null;
  const recorrenciaAtiva = formData.get('recorrenciaAtiva') === '1';
  const metaMinimaContratosRaw = String(formData.get('metaMinimaContratos') ?? '');
  const bonusExtraValorRaw = String(formData.get('bonusExtraValor') ?? '');

  let faixas: FaixaInput[];
  let regras: RegraInput[];
  try {
    faixas = JSON.parse(String(formData.get('faixasJson') ?? '[]'));
    regras = JSON.parse(String(formData.get('regrasJson') ?? '[]'));
  } catch {
    return { error: messageFor('plano_carreira.dados_invalidos') };
  }

  const body = {
    name: String(formData.get('name') ?? ''),
    status: formData.get('status') ? String(formData.get('status')) : undefined,
    classificacao: String(formData.get('classificacao') ?? ''),
    janelaApuracaoDias: Number(formData.get('janelaApuracaoDias') ?? 0),
    metaMinimaContratos: metaMinimaContratosRaw ? Number(metaMinimaContratosRaw) : null,
    metaMinimaFonteData: metaMinimaContratosRaw ? String(formData.get('metaMinimaFonteData') ?? '') : null,
    bonusExtraValor: bonusExtraValorRaw ? Number(bonusExtraValorRaw) : null,
    recorrenciaAtiva,
    faixas: faixas.map((f) => ({
      quantidadeMin: Number(f.quantidadeMin),
      quantidadeMax: f.quantidadeMax ? Number(f.quantidadeMax) : null,
      valorPorPlaca: Number(f.valorPorPlaca),
    })),
    regrasRecorrencia: regras.map((r) => ({
      tipo: r.tipo,
      nivel: r.nivel ? Number(r.nivel) : null,
      taxa: Number(r.taxa),
    })),
  };

  try {
    if (id) {
      await apiFetch(`/planos-carreira/${id}`, { method: 'PUT', body });
    } else {
      await apiFetch('/planos-carreira', { method: 'POST', body });
    }
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/remuneracao');
  redirect('/administracao/remuneracao');
}
```

- [ ] **Step 2: Criar o formulário**

Criar `web/src/app/administracao/remuneracao/plano-carreira-form.tsx`:

```tsx
'use client';

import { useActionState, useState } from 'react';
import type { FormState } from '@/lib/form-state';
import type { PlanoCarreira } from '@/lib/types';
import { salvarPlanoCarreira } from './actions';

type FaixaRow = { quantidadeMin: string; quantidadeMax: string; valorPorPlaca: string };
type RegraRow = { tipo: 'propria' | 'upline'; nivel: string; taxa: string };

export function PlanoCarreiraForm({ plano }: { plano?: PlanoCarreira }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(salvarPlanoCarreira, {});
  const [metaMinimaAtiva, setMetaMinimaAtiva] = useState(plano?.metaMinimaContratos != null);
  const [recorrenciaAtiva, setRecorrenciaAtiva] = useState(plano?.recorrenciaAtiva ?? false);
  const [faixas, setFaixas] = useState<FaixaRow[]>(
    plano?.faixas.map((f) => ({
      quantidadeMin: String(f.quantidadeMin),
      quantidadeMax: f.quantidadeMax === null ? '' : String(f.quantidadeMax),
      valorPorPlaca: String(f.valorPorPlaca),
    })) ?? [],
  );
  const [regras, setRegras] = useState<RegraRow[]>(
    plano?.regrasRecorrencia.map((r) => ({ tipo: r.tipo, nivel: r.nivel === null ? '' : String(r.nivel), taxa: String(r.taxa) })) ?? [],
  );

  return (
    <form action={formAction} className="form">
      {plano && <input type="hidden" name="id" value={plano.id} />}
      <label>
        Nome
        <input type="text" name="name" defaultValue={plano?.name} required />
      </label>
      <label>
        Classificação
        <select name="classificacao" defaultValue={plano?.classificacao ?? 'clt_interno'} required>
          <option value="clt_interno">CLT Interno</option>
          <option value="clt_externo">CLT Externo</option>
          <option value="so_externo">Só Externo</option>
        </select>
      </label>
      {plano && (
        <label>
          Status
          <select name="status" defaultValue={plano.status}>
            <option value="ativo">Ativo</option>
            <option value="inativo">Inativo</option>
          </select>
        </label>
      )}
      <label>
        Janela de apuração (dias)
        <input type="number" name="janelaApuracaoDias" defaultValue={plano?.janelaApuracaoDias ?? 30} min={1} required />
      </label>

      <fieldset>
        <legend>Faixas de bônus por placa</legend>
        {faixas.map((faixa, i) => (
          <div key={i} className="inline">
            <input
              type="number"
              placeholder="Mínimo"
              value={faixa.quantidadeMin}
              min={1}
              onChange={(e) => setFaixas(faixas.map((f, j) => (j === i ? { ...f, quantidadeMin: e.target.value } : f)))}
              required
            />
            <input
              type="number"
              placeholder="Máximo (vazio = sem limite)"
              value={faixa.quantidadeMax}
              onChange={(e) => setFaixas(faixas.map((f, j) => (j === i ? { ...f, quantidadeMax: e.target.value } : f)))}
            />
            <input
              type="number"
              placeholder="Valor por placa"
              step="0.01"
              value={faixa.valorPorPlaca}
              min={0.01}
              onChange={(e) => setFaixas(faixas.map((f, j) => (j === i ? { ...f, valorPorPlaca: e.target.value } : f)))}
              required
            />
            <button type="button" className="secondary" onClick={() => setFaixas(faixas.filter((_, j) => j !== i))}>
              Remover
            </button>
          </div>
        ))}
        <button type="button" className="secondary" onClick={() => setFaixas([...faixas, { quantidadeMin: '', quantidadeMax: '', valorPorPlaca: '' }])}>
          Adicionar faixa
        </button>
        <input type="hidden" name="faixasJson" value={JSON.stringify(faixas)} />
      </fieldset>

      <label className="inline">
        <input type="checkbox" checked={metaMinimaAtiva} onChange={(e) => setMetaMinimaAtiva(e.target.checked)} />
        Exigir meta mínima de contratos novos
      </label>
      {metaMinimaAtiva && (
        <>
          <label>
            Meta mínima de contratos
            <input type="number" name="metaMinimaContratos" defaultValue={plano?.metaMinimaContratos ?? ''} min={1} required />
          </label>
          <label>
            Fonte da data
            <select name="metaMinimaFonteData" defaultValue={plano?.metaMinimaFonteData ?? 'contrato'} required>
              <option value="contrato">Data do contrato</option>
              <option value="cadastro">Data de cadastro</option>
            </select>
          </label>
        </>
      )}

      <label>
        Bônus extra (regime especial)
        <input type="number" name="bonusExtraValor" step="0.01" min={0.01} defaultValue={plano?.bonusExtraValor ?? ''} />
      </label>
      <p className="muted">Deixe em branco se este plano não tem valor extra fixo.</p>

      <label className="inline">
        <input type="checkbox" checked={recorrenciaAtiva} onChange={(e) => setRecorrenciaAtiva(e.target.checked)} />
        Ativar recorrência adicional (própria/upline)
      </label>
      {recorrenciaAtiva && (
        <fieldset>
          <legend>Taxas de recorrência</legend>
          {regras.map((regra, i) => (
            <div key={i} className="inline">
              <select
                value={regra.tipo}
                onChange={(e) =>
                  setRegras(
                    regras.map((r, j) =>
                      j === i ? { ...r, tipo: e.target.value as 'propria' | 'upline', nivel: e.target.value === 'propria' ? '' : r.nivel } : r,
                    ),
                  )
                }
              >
                <option value="propria">Própria</option>
                <option value="upline">Upline</option>
              </select>
              {regra.tipo === 'upline' && (
                <input
                  type="number"
                  placeholder="Nível"
                  value={regra.nivel}
                  min={1}
                  onChange={(e) => setRegras(regras.map((r, j) => (j === i ? { ...r, nivel: e.target.value } : r)))}
                  required
                />
              )}
              <input
                type="number"
                placeholder="Taxa (ex: 0.07 = 7%)"
                step="0.0001"
                value={regra.taxa}
                min={0.0001}
                max={1}
                onChange={(e) => setRegras(regras.map((r, j) => (j === i ? { ...r, taxa: e.target.value } : r)))}
                required
              />
              <button type="button" className="secondary" onClick={() => setRegras(regras.filter((_, j) => j !== i))}>
                Remover
              </button>
            </div>
          ))}
          <button type="button" className="secondary" onClick={() => setRegras([...regras, { tipo: 'propria', nivel: '', taxa: '' }])}>
            Adicionar taxa
          </button>
        </fieldset>
      )}
      <input type="hidden" name="recorrenciaAtiva" value={recorrenciaAtiva ? '1' : ''} />
      <input type="hidden" name="regrasJson" value={JSON.stringify(regras)} />

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

- [ ] **Step 3: Criar a página de criação**

Criar `web/src/app/administracao/remuneracao/novo/page.tsx`:

```tsx
import { notFound, redirect } from 'next/navigation';
import { AppShell } from '../../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import type { Me } from '@/lib/types';
import { PlanoCarreiraForm } from '../plano-carreira-form';

export default async function NovoPlanoCarreiraPage() {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const has = (key: string) => me.permissions.some((p) => p.key === key);
  if (!has('regras_comissao.editar')) redirect('/administracao/remuneracao');

  return (
    <AppShell me={me} active="remuneracao">
      <div className="shell">
        <div className="page-heading">
          <div>
            <p className="eyebrow">Administração comercial</p>
            <h1>Novo plano de carreira</h1>
          </div>
          <a className="action-link" href="/administracao/remuneracao">
            Voltar
          </a>
        </div>
        <section className="card">
          <PlanoCarreiraForm />
        </section>
      </div>
    </AppShell>
  );
}
```

- [ ] **Step 4: Criar a página de edição**

Criar `web/src/app/administracao/remuneracao/[id]/editar/page.tsx`:

```tsx
import { notFound, redirect } from 'next/navigation';
import { AppShell } from '../../../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import type { Me, PlanoCarreira } from '@/lib/types';
import { PlanoCarreiraForm } from '../../plano-carreira-form';

export default async function EditarPlanoCarreiraPage({ params }: { params: Promise<{ id: string }> }) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const has = (key: string) => me.permissions.some((p) => p.key === key);
  if (!has('regras_comissao.editar')) redirect('/administracao/remuneracao');

  const { id } = await params;
  const planos = await apiFetch<PlanoCarreira[]>('/planos-carreira');
  const plano = planos.find((p) => p.id === id);
  if (!plano) notFound();

  return (
    <AppShell me={me} active="remuneracao">
      <div className="shell">
        <div className="page-heading">
          <div>
            <p className="eyebrow">Administração comercial</p>
            <h1>Editar plano de carreira</h1>
          </div>
          <a className="action-link" href="/administracao/remuneracao">
            Voltar
          </a>
        </div>
        <section className="card">
          <PlanoCarreiraForm plano={plano} />
        </section>
      </div>
    </AppShell>
  );
}
```

- [ ] **Step 5: Verificar manualmente**

Run: `cd web && npm run build`
Expected: build sem erros de tipo.

Com a API e o web rodando localmente: criar um plano com 3 faixas de bônus e recorrência ativada com 2 regras (própria + upline nível 1) — igual ao exemplo original da conversa (Adesão/faixas de R$33/40/50/60, 7%/2%/1%). Confirmar que salva, aparece na listagem, e que reabrir pra editar mostra as faixas/regras certas. Editar removendo uma faixa e desativando a recorrência, confirmar que salva só o que ficou. Tentar criar um plano com meta mínima só com um dos dois campos preenchidos, confirmar que a mensagem de erro aparece.

- [ ] **Step 6: Commit**

```bash
git add web/src/app/administracao/remuneracao/plano-carreira-form.tsx web/src/app/administracao/remuneracao/actions.ts \
  web/src/app/administracao/remuneracao/novo/page.tsx "web/src/app/administracao/remuneracao/[id]/editar/page.tsx"
git commit -m "feat(web): add the plano de carreira create/edit form"
```

---

### Task 6: Frontend — atribuir plano de carreira na tela de edição de participante

**Files:**
- Modify: `web/src/app/administracao/participantes/[id]/editar/page.tsx`
- Modify: `web/src/app/administracao/participantes/[id]/editar/participante-edit-form.tsx`
- Modify: `web/src/app/administracao/actions.ts`

**Interfaces:**
- Consumes: `GET /planos-carreira` (Task 2), `PUT /users/{id}/plano-carreira` (Task 3), `PlanoCarreira` (Task 4).

Sem teste automatizado próprio. Verificação manual no Step 4.

- [ ] **Step 1: Buscar os planos na página**

Em `web/src/app/administracao/participantes/[id]/editar/page.tsx`, mudar o import de tipos de:

```tsx
import type { HinovaMapeamento, Me, UserNode } from '@/lib/types';
```

para:

```tsx
import type { HinovaMapeamento, Me, PlanoCarreira, UserNode } from '@/lib/types';
```

Depois do bloco que busca `mapeamento` (a variável `canSeeHinova`/`mapeamentos`/`mapeamento`), adicionar:

```tsx
  const canSeePlanos = has('estrutura.editar');
  const planos = canSeePlanos ? await apiFetch<PlanoCarreira[]>('/planos-carreira') : [];
```

E passar `planos` pro formulário, mudando:

```tsx
          <ParticipanteEditForm user={user} mapeamento={mapeamento} />
```

para:

```tsx
          <ParticipanteEditForm user={user} mapeamento={mapeamento} planos={planos} />
```

- [ ] **Step 2: Adicionar o campo no formulário**

Em `web/src/app/administracao/participantes/[id]/editar/participante-edit-form.tsx`, mudar o import de tipos de:

```tsx
import type { HinovaMapeamento, UserNode } from '@/lib/types';
```

para:

```tsx
import type { HinovaMapeamento, PlanoCarreira, UserNode } from '@/lib/types';
```

Mudar a assinatura do componente de:

```tsx
export function ParticipanteEditForm({ user, mapeamento }: { user: UserNode; mapeamento: HinovaMapeamento | null }) {
```

para:

```tsx
export function ParticipanteEditForm({
  user,
  mapeamento,
  planos,
}: {
  user: UserNode;
  mapeamento: HinovaMapeamento | null;
  planos: PlanoCarreira[];
}) {
```

Depois do bloco de campos da Hinova (o `{mapeamento && (...)}`), antes do campo "Nova senha", adicionar:

```tsx
      {planos.length > 0 && (
        <label>
          Plano de carreira
          <select name="planoCarreiraId" defaultValue={user.planoCarreiraId ?? ''}>
            <option value="">Sem plano de carreira</option>
            {planos
              .filter((p) => p.status === 'ativo' || p.id === user.planoCarreiraId)
              .map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
          </select>
        </label>
      )}
```

(o filtro deixa planos `inativo` fora das opções novas, mas mantém visível o que já está atribuído, mesmo se foi desativado depois — evita "esconder" o valor atual do select.)

- [ ] **Step 3: Ler o campo no server action**

Em `web/src/app/administracao/actions.ts`, dentro de `editarParticipante`, depois do bloco que já chama `PUT /users/{id}/password`, adicionar:

```ts
    const planoCarreiraIdRaw = String(formData.get('planoCarreiraId') ?? '');
    await apiFetch(`/users/${id}/plano-carreira`, {
      method: 'PUT',
      body: { planoCarreiraId: planoCarreiraIdRaw || null },
    });
```

(sempre chama, mesmo pra "remover" — igual `PUT /users/{id}` já faz hoje pra nome/e-mail; enviar vazio limpa a atribuição.)

- [ ] **Step 4: Verificar manualmente**

Run: `cd web && npm run build`
Expected: build sem erros de tipo.

Com a API e o web rodando localmente: criar um plano de carreira, abrir a edição de um participante, atribuir o plano, salvar, reabrir e confirmar que o select já vem com o plano certo selecionado. Trocar pra "Sem plano de carreira", salvar, confirmar que zera. Desativar o plano (status inativo) com ele ainda atribuído a alguém, reabrir a edição dessa pessoa e confirmar que o plano desativado ainda aparece no select (não desaparece silenciosamente).

- [ ] **Step 5: Commit**

```bash
git add "web/src/app/administracao/participantes/[id]/editar/page.tsx" \
  "web/src/app/administracao/participantes/[id]/editar/participante-edit-form.tsx" \
  web/src/app/administracao/actions.ts
git commit -m "feat(web): assign a career plan from the participant edit screen"
```
