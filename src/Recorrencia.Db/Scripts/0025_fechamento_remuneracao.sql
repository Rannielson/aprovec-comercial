-- Valores de remuneração fora da árvore são fotografados no fechamento. A linha
-- 'placas' é gerada pela apuração Hinova; a linha 'extra' exige aprovação humana.
create table fechamento_remuneracao (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null,
  fechamento_id uuid not null,
  beneficiario_id uuid not null,
  tipo text not null check (tipo in ('placas', 'extra')),
  plano_carreira_id uuid not null,
  plano_nome text not null,
  competencia_producao date not null check (extract(day from competencia_producao) = 1),
  data_limite_pagamento date,
  quantidade int not null default 0 check (quantidade >= 0),
  valor_unitario numeric(14,2),
  valor numeric(14,2) not null check (valor >= 0),
  detalhes jsonb not null default '[]'::jsonb,
  aprovado_por uuid,
  aprovado_em timestamptz,
  created_at timestamptz not null default now(),
  unique (fechamento_id, beneficiario_id, tipo),
  foreign key (tenant_id, fechamento_id) references fechamentos (tenant_id, id),
  foreign key (tenant_id, beneficiario_id) references users (tenant_id, id),
  foreign key (tenant_id, aprovado_por) references users (tenant_id, id),
  check ((tipo = 'extra') = (aprovado_por is not null and aprovado_em is not null)),
  check (tipo = 'placas' or (quantidade = 0 and valor_unitario is null))
);

create index fechamento_remuneracao_beneficiario_idx
  on fechamento_remuneracao (fechamento_id, beneficiario_id);

create function app.fechamento_remuneracao_guard() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
declare
  v_fechamento uuid := case when tg_op = 'DELETE' then old.fechamento_id else new.fechamento_id end;
  v_status text;
begin
  select status into v_status from fechamentos where id = v_fechamento for share;
  if v_status is null or v_status in ('confirmado', 'provisionado') then
    raise exception 'fechamento.immutable' using errcode = 'P0001';
  end if;
  if tg_op = 'UPDATE' and new.fechamento_id <> old.fechamento_id then
    raise exception 'fechamento.immutable' using errcode = 'P0001';
  end if;
  return case when tg_op = 'DELETE' then old else new end;
end
$$;

create trigger fechamento_remuneracao_guard
  before insert or update or delete on fechamento_remuneracao
  for each row execute function app.fechamento_remuneracao_guard();

alter table fechamento_remuneracao enable row level security;
alter table fechamento_remuneracao force row level security;
create policy fechamento_remuneracao_tenant_isolation on fechamento_remuneracao
  as restrictive for all to app_user
  using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant());
create policy fechamento_remuneracao_select on fechamento_remuneracao for select to app_user using (
  (select app.scope_for('comissoes.visualizar')) = 'tenant'
  or beneficiario_id = any ((select app.visible_owner_ids((select app.scope_for('comissoes.visualizar'))))::uuid[])
);
create policy fechamento_remuneracao_write on fechamento_remuneracao for all to app_user
  using ((select app.has_permission('fechamento.confirmar')))
  with check ((select app.has_permission('fechamento.confirmar')));

grant select, insert, update, delete on fechamento_remuneracao to app_user;
