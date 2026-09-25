# Planos de Carreira — Cadastro e Estrutura — Design

## Contexto

Hoje o sistema tem dois eixos totalmente desacoplados por consultor: o papel RBAC
(`roles`/`role_templates` — o que ele pode acessar) e o plano de comissão único do
tenant (`commission_plans`/`commission_rules` — a fórmula de rateio: % própria,
upline por nível, global). Nenhum dos dois sabe se o consultor é CLT Interno, CLT
Externo ou Só Externo, nem existe qualquer noção de bônus por faixa de vendas, meta
mínima de contratos, ou um valor extra fixo condicionado ao regime de trabalho.

Este spec cria um terceiro eixo, **plano de carreira**, atribuído individualmente a
cada consultor, que carrega essa classificação e essas regras de bonificação. Os
valores do plano de carreira **somam** por cima do que `commission_plans` já calcula
para aquele consultor — nunca substituem. Um consultor sem plano de carreira
atribuído continua funcionando exatamente como hoje, sem qualquer diferença.

## Fora de escopo

- **Apuração real.** Este spec cadastra a estrutura e as regras; não calcula nada.
  Buscar dados reais da Hinova (contratos, boletos por data de início de contrato,
  a listagem de veículos para a meta mínima) e produzir o valor de bonificação de
  uma competência é um projeto seguinte, dedicado só a isso.
- **Integração "Listar Veículo" da Hinova.** Não existe hoje nenhum cliente/DTO para
  esse endpoint (`IHinovaClient` só tem `ListarVoluntariosAsync`, `BuscarVoluntarioAsync`,
  `CadastrarVoluntarioAsync`, `ListarBoletosPeriodoAsync`) — construir isso fica para
  o projeto de apuração.
- **Histórico de atribuição.** Trocar o plano de carreira de um consultor apenas
  atualiza o campo atual; não guarda quando cada plano esteve vigente.
- **Fluxo rascunho/ativo com imutabilidade** (como `commission_plans` tem hoje) — o
  plano de carreira é editável livremente enquanto ativo ou inativo.
- **Um motor de condições genérico para o "regime extra"** — é um valor fixo simples
  por plano (ver Decisões).

## Decisões

- **Convenção de nomenclatura**: inglês só para estrutura técnica genérica já usada
  em toda tabela existente (`id`, `tenant_id`, `name`, `status`, `created_at`,
  `updated_at`); português para todo vocabulário de domínio e para todo valor de
  enum — mesma régua que `commission_plans`/`boletos`/`fechamentos` já seguem
  (`valor`, `vencimento`, `competencia`, e enums como `'rascunho'`/`'ativo'`/
  `'confirmado'`/`'a_vencer'`, nunca em inglês).
- **Entidade nova e desacoplada**, não uma extensão de `commission_plans` nem de
  `roles`. Um consultor tem, independentemente: um papel (RBAC), um plano de
  comissão vigente do tenant (herdado automaticamente), e opcionalmente um plano de
  carreira (`users.plano_carreira_id`, nullable).
- **Atribuição simples, sem histórico** — um campo em `users`, igual `supervisor_id`
  hoje. Trocar o plano de carreira de um consultor é uma operação imediata, sem
  efeito retroativo modelado.
- **Status simples, sem fluxo rascunho/ativo** — `status text check (status in
  ('ativo', 'inativo'))`, editável livremente a qualquer momento (diferente de
  `commission_plans`, que trava depois de ativado). Um plano `inativo` continua
  atribuído a quem já o tinha, mas não deveria aparecer como opção nova ao atribuir
  (aplicado na tela, não no banco).
- **"Regime extra" é um valor fixo simples por plano** (`bonus_extra_valor`), não um
  motor de condições. Cada plano já tem uma classificação fixa; o exemplo do usuário
  ("se for CLT Externo, +R$1.500") se resolve simplesmente preenchendo esse campo só
  nos planos classificados como CLT Externo — sem precisar modelar a condição em si.
- **Recorrência com número flexível de níveis**, não fixo em própria+2 níveis — cada
  caso é particular. Modelada como tabela filha (`planos_carreira_regras_recorrencia`),
  mesma forma de `commission_rules` (tipo própria/upline + nível).
- **Meta mínima e sua fonte de data são opcionais e vêm juntas** — se
  `meta_minima_contratos` for preenchido, `meta_minima_fonte_data` também precisa
  ser (e vice-versa); nenhum dos dois preenchido significa que este plano não usa
  meta mínima.
- **Permissões**: reaproveitar `regras_comissao.editar`/`regras_comissao.visualizar`
  para o CRUD do plano de carreira, em vez de `integracoes.gerenciar` (cogitado
  inicialmente em chat) — plano de carreira é fundamentalmente uma regra de
  compensação, o mesmo domínio que já protege `commission_plans`/`commission_rules`,
  não uma configuração de integração com a Hinova. Para atribuir um plano a um
  consultor específico, reaproveitar `estrutura.editar` (mesma sensibilidade que
  trocar `supervisor_id` hoje). **Este é uma revisão da proposta inicial do chat —
  confirmar durante a revisão deste spec.**

## Arquitetura

### Banco de dados

Nova migration `0024_planos_carreira.sql` (0023 é o último número usado hoje):

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

-- app.users_column_guard() (0003_users_hierarchy.sql, já estendida por
-- 0021/0023_users_column_guard_*.sql) só sabe proteger supervisor_id/status/name/email
-- hoje -- sem esta extensão, a policy geral de users_update (que aceita
-- estrutura.editar OU usuarios.desligar OU usuarios.convidar) seria a ÚNICA barreira
-- pra mudar plano_carreira_id, mais larga do que a intenção deste spec (só
-- estrutura.editar). Mesmo padrão exato já usado pra supervisor_id.
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
    -- Mesmo padrão de commission_plans/commission_rules (0011_rls.sql:72-78): SELECT
    -- aberto a qualquer membro do tenant (a permissão regras_comissao.visualizar já é
    -- checada na camada da API, não duplicada aqui); só escrita exige regras_comissao.editar.
    execute format('create policy %I on %I for select to app_user using (true)', t || '_select', t);
    execute format(
      'create policy %I on %I for all to app_user using ((select app.has_permission(''regras_comissao.editar''))) with check ((select app.has_permission(''regras_comissao.editar'')))',
      t || '_write', t);
  end loop;
end
$$;

grant select, insert, update, delete on planos_carreira, planos_carreira_faixas_bonus, planos_carreira_regras_recorrencia to app_user;

-- users tem grants explícitos por coluna (0003_users_hierarchy.sql:146-148), não um
-- grant geral -- uma coluna nova precisa entrar nessas duas listas explicitamente,
-- senão nem para SELECT ela fica visível para app_user.
grant select (plano_carreira_id) on users to app_user;
grant update (plano_carreira_id) on users to app_user;
```

A `unique (plano_carreira_id, tipo, nivel)` em `planos_carreira_regras_recorrencia`
garante no máximo uma linha `própria` por plano (nível sempre `null` nesse caso, então
a unicidade cai sobre `(plano_carreira_id, 'propria', null)`) e no máximo uma linha por
nível de `upline`.

### Backend — `src/Recorrencia.Api/Comissao/PlanoCarreiraEndpoints.cs` (novo arquivo)

```
GET    /planos-carreira                → lista os planos do tenant (regras_comissao.visualizar)
POST   /planos-carreira                → cria (regras_comissao.editar)
PUT    /planos-carreira/{id}           → edita campos + substitui faixas/regras (regras_comissao.editar)
DELETE /planos-carreira/{id}           → remove, só se nenhum usuário estiver com esse plano atribuído (regras_comissao.editar)
```

- `PUT /users/{id}/plano-carreira` (novo, em `UserEndpoints.cs`) — atribui/remove o
  plano de carreira de um consultor (`estrutura.editar`). Corpo:
  `{ planoCarreiraId: string | null }`. Valida que o plano pertence ao mesmo tenant
  (a FK composta já garante isso a nível de banco; a 404/400 é responsabilidade do
  handler antes de deixar o banco rejeitar).
- `PlanoCarreiraRequest`: `Name`, `Classificacao`, `JanelaApuracaoDias`,
  `MetaMinimaContratos?`, `MetaMinimaFonteData?`, `BonusExtraValor?`,
  `RecorrenciaAtiva`, `Faixas: FaixaBonusInput[]`, `RegrasRecorrencia: RegraRecorrenciaInput[]?`.
  `PUT` substitui completamente as faixas/regras existentes (delete + insert dentro da
  mesma transação) — `CommissionPlanEndpoints` não tem um PUT equivalente hoje (plano
  de comissão é imutável depois de ativado, criado uma vez via `POST`), então este é
  um padrão novo neste codebase, não uma reutilização direta.
- Validação do all-or-nothing de meta mínima replicada em C# antes do banco (mesmo
  padrão do `hinova.campos_incompletos` em `InviteAsync`), com código
  `plano_carreira.meta_minima_incompleta`.
- Validação de `RecorrenciaAtiva = true` exigindo pelo menos uma linha em
  `RegrasRecorrencia`, e `RecorrenciaAtiva = false` ignorando/rejeitando linhas
  enviadas (400 `plano_carreira.recorrencia_inconsistente`) — evita salvar regras
  "fantasma" que nunca seriam usadas.

### Frontend

- Nova área `web/src/app/administracao/planos-carreira/` — listagem (nome,
  classificação, status) + formulário de criar/editar com: campos do plano, tabela
  dinâmica de faixas de bônus (adicionar/remover linha), campo de meta mínima + select
  da fonte de data (só habilitado se meta mínima preenchida), campo de bônus extra, e
  toggle de recorrência que revela uma tabela dinâmica de linhas (própria/upline + nível
  + taxa).
- Na tela de edição de participante já existente
  (`web/src/app/administracao/participantes/[id]/editar/`), um novo campo (select) para
  atribuir/remover o plano de carreira do consultor, visível só para quem tem
  `estrutura.editar` — mesmo padrão de graceful-degrade já usado ali para os campos da
  Hinova.

## Testes

- CRUD completo de `planos_carreira` (criar com/sem meta mínima, com/sem recorrência,
  editar substituindo faixas, listar, tentar deletar um plano em uso → erro).
- Validações: meta mínima incompleta (só um dos dois campos), recorrência ativa sem
  linhas, `quantidade_max < quantidade_min`, `nivel` presente com `tipo = 'propria'`
  ou ausente com `tipo = 'upline'`.
- Atribuição de plano a um consultor, troca, remoção (`planoCarreiraId: null`),
  tentativa de atribuir um plano de outro tenant (404).
- Permissão: um ator sem `regras_comissao.visualizar` recebe 403 ao chamar `GET
  /planos-carreira` (checado na camada da API, não na RLS — mesmo padrão de
  `commission_plans`); sem `regras_comissao.editar` recebe 403 ao criar/editar/deletar
  (este sim reforçado também na RLS, via a policy `_write`).
