-- Commission cutoffs are tenant-scoped and opt-in per consultant. A tenant may choose
-- its own cutoff date for each selected person; everyone else keeps the active plan.
create table commission_contract_cutoffs (
  tenant_id uuid not null references tenants (id),
  user_id uuid not null,
  cutoff_date date not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  primary key (tenant_id, user_id),
  foreign key (tenant_id, user_id) references users (tenant_id, id) on delete cascade
);

create trigger commission_contract_cutoffs_touch before update on commission_contract_cutoffs
  for each row execute function app.touch_updated_at();

alter table commission_contract_cutoffs enable row level security;
alter table commission_contract_cutoffs force row level security;
create policy commission_contract_cutoffs_tenant on commission_contract_cutoffs
  as restrictive for all to app_user
  using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant());
create policy commission_contract_cutoffs_select on commission_contract_cutoffs
  for select to app_user using (app.has_permission('regras_comissao.visualizar'));
create policy commission_contract_cutoffs_write on commission_contract_cutoffs
  for all to app_user using (app.has_permission('regras_comissao.editar'))
  with check (app.has_permission('regras_comissao.editar'));
grant select, insert, update, delete on commission_contract_cutoffs to app_user;

-- A boleto can cover several vehicles, potentially with contract dates on both
-- sides of a cutoff. Keep the SGA vehicle data instead of guessing one date for
-- the entire payment.
create table boleto_veiculos (
  tenant_id uuid not null,
  boleto_id uuid not null,
  ordem int not null check (ordem >= 0),
  codigo_veiculo text,
  placa text,
  data_inicio_contrato date,
  primary key (tenant_id, boleto_id, ordem),
  foreign key (tenant_id, boleto_id) references boletos (tenant_id, id) on delete cascade
);

create index boleto_veiculos_contrato_idx on boleto_veiculos (tenant_id, data_inicio_contrato);

alter table boleto_veiculos enable row level security;
alter table boleto_veiculos force row level security;
create policy boleto_veiculos_tenant on boleto_veiculos
  as restrictive for all to app_user
  using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant());
create policy boleto_veiculos_select on boleto_veiculos for select to app_user using (
  exists (
    select 1 from boletos b
     where b.tenant_id = boleto_veiculos.tenant_id and b.id = boleto_veiculos.boleto_id
  )
);
create policy boleto_veiculos_write on boleto_veiculos
  for all to app_user using (app.has_permission('integracoes.gerenciar'))
  with check (app.has_permission('integracoes.gerenciar'));
grant select, insert, update, delete on boleto_veiculos to app_user;

-- Commission readers can see only the dates attached to boleto rows within
-- their payable tree. This function does not expose the rest of a portfolio.
create function app.commission_source_vehicle_dates(p_competencia date, p_beneficiaries uuid[], p_unpaid boolean)
returns table (boleto_id uuid, ordem int, data_inicio_contrato date)
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
begin
  perform app.assert_commission_access(p_competencia, p_beneficiaries);
  if p_unpaid then
    return query
      select v.boleto_id, v.ordem, v.data_inicio_contrato
        from app.commission_source_unpaid_boletos(p_competencia, p_beneficiaries) b
        join boleto_veiculos v on v.tenant_id = app.current_tenant() and v.boleto_id = b.boleto_id;
  else
    return query
      select v.boleto_id, v.ordem, v.data_inicio_contrato
        from app.commission_source_boletos(p_competencia, p_beneficiaries) b
        join boleto_veiculos v on v.tenant_id = app.current_tenant() and v.boleto_id = b.boleto_id;
  end if;
end
$$;

create function app.commission_source_cutoffs(p_competencia date, p_beneficiaries uuid[])
returns table (user_id uuid, cutoff_date date)
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
begin
  perform app.assert_commission_access(p_competencia, p_beneficiaries);
  return query
    select c.user_id, c.cutoff_date
      from commission_contract_cutoffs c
     where c.tenant_id = app.current_tenant() and c.user_id = any(p_beneficiaries);
end
$$;

grant execute on function app.commission_source_vehicle_dates(date, uuid[], boolean),
  app.commission_source_cutoffs(date, uuid[]) to app_user;

-- Keep the segment field for compatibility with existing confirmed snapshots;
-- new cutoffs store only the payable post-cutoff upline portion (segment 0).
alter table fechamento_detalhes add column contract_segment smallint not null default 0
  check (contract_segment in (0, 1));
do $$
declare v_constraint text;
begin
  select con.conname into v_constraint
    from pg_constraint con
   where con.conrelid = 'fechamento_detalhes'::regclass
     and con.contype = 'u'
     and (select array_agg(a.attname::text order by k.ordinality)
            from unnest(con.conkey) with ordinality k(attnum, ordinality)
            join pg_attribute a on a.attrelid = con.conrelid and a.attnum = k.attnum)
         = array['fechamento_id', 'beneficiario_id', 'boleto_id', 'rule_id'];
  if v_constraint is null then raise exception 'fechamento_detalhes unique constraint not found'; end if;
  execute format('alter table fechamento_detalhes drop constraint %I', v_constraint);
end
$$;
alter table fechamento_detalhes
  add constraint fechamento_detalhes_contract_segment_key
  unique (fechamento_id, beneficiario_id, boleto_id, rule_id, contract_segment);
