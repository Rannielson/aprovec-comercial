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
  check ((tipo = 'upline') = (nivel is not null))
);

-- unique (plano_carreira_id, tipo, nivel) sozinho não funciona: Postgres trata cada NULL
-- como distinto pra fins de unicidade, e nivel é sempre null pra 'propria' -- duas índices
-- parciais, uma pra cada caso, fecham a lacuna corretamente.
create unique index planos_carreira_regras_recorrencia_propria_idx
  on planos_carreira_regras_recorrencia (plano_carreira_id) where tipo = 'propria';
create unique index planos_carreira_regras_recorrencia_upline_idx
  on planos_carreira_regras_recorrencia (plano_carreira_id, nivel) where tipo = 'upline';

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
