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

  -- Permission/actor checks below only apply when there is an authenticated app_user session
  -- (app.current_user_id() is not null). Trusted internal, pre-authentication flows have no
  -- acting user context and are governed by their own checks, not RBAC permissions.
  if new.status = 'confirmado' and old.status is distinct from 'confirmado'
     and app.current_user_id() is not null then
    if not app.has_permission('fechamento.confirmar') then
      raise exception 'auth.forbidden' using errcode = 'P0001';
    end if;
    if new.confirmado_por is distinct from app.current_user_id() then
      raise exception 'auth.forbidden' using errcode = 'P0001';
    end if;
  end if;

  if new.status = 'provisionado' and old.status is distinct from 'provisionado'
     and app.current_user_id() is not null then
    if not app.has_permission('fechamento.provisionar') then
      raise exception 'auth.forbidden' using errcode = 'P0001';
    end if;
    if new.provisionado_por is distinct from app.current_user_id() then
      raise exception 'auth.forbidden' using errcode = 'P0001';
    end if;
  end if;

  if old.status in ('confirmado', 'provisionado')
     and (new.confirmado_em is distinct from old.confirmado_em or new.confirmado_por is distinct from old.confirmado_por) then
    raise exception 'fechamento.immutable' using errcode = 'P0001';
  end if;

  if old.status = 'provisionado'
     and (new.provisionado_em is distinct from old.provisionado_em or new.provisionado_por is distinct from old.provisionado_por) then
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
  foreign key (tenant_id, boleto_id) references boletos (tenant_id, id),
  unique (fechamento_id, beneficiario_id, boleto_id, rule_id)
);

create index fechamento_detalhes_beneficiario_idx on fechamento_detalhes (fechamento_id, beneficiario_id);

create function app.fechamento_detalhes_guard() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
declare
  v_status text;
begin
  -- Lock the parent row itself (regardless of its current status) so a concurrent
  -- confirm/provisionar transition on the same fechamento can't interleave with this insert.
  select status into v_status from fechamentos where id = new.fechamento_id for share;
  if v_status in ('confirmado', 'provisionado') then
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
