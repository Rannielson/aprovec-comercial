-- Keep the bank's gross receipt intact. Unknown fees never become zero implicitly.
alter table boletos add column tarifa_bancaria numeric(14,2),
  add column tarifa_confirmada_em timestamptz,
  add column tarifa_referencia date,
  add column comissao_base numeric generated always as (
    case when hinova_nosso_numero is null then valor
         when tarifa_bancaria is not null and tarifa_bancaria >= 0 and tarifa_bancaria <= valor
              and (tarifa_bancaria > 0 or tarifa_referencia >= pago_em)
           then valor - tarifa_bancaria end
  ) stored;
alter table boletos add constraint boleto_tarifa_nonnegative check (tarifa_bancaria >= 0);

create table hinova_boleto_tarifas (
  tenant_id uuid not null references tenants(id),
  nosso_numero text not null,
  codigo_alteracao bigint not null,
  data_alteracao timestamptz not null,
  valor numeric(14,2),
  consultado_em timestamptz not null default now(),
  primary key(tenant_id, nosso_numero),
  check (valor >= 0)
);
create table hinova_tarifa_sync_dias (
  tenant_id uuid not null references tenants(id), dia date not null,
  consultado_em timestamptz not null default now(), primary key(tenant_id, dia)
);
alter table hinova_boleto_tarifas enable row level security;
alter table hinova_boleto_tarifas force row level security;
alter table hinova_tarifa_sync_dias enable row level security;
alter table hinova_tarifa_sync_dias force row level security;
create policy tarifa_tenant on hinova_boleto_tarifas as restrictive for all to app_user
 using(tenant_id=app.current_tenant()) with check(tenant_id=app.current_tenant());
create policy tarifa_admin on hinova_boleto_tarifas for all to app_user
 using(app.has_permission('integracoes.gerenciar')) with check(app.has_permission('integracoes.gerenciar'));
create policy tarifa_dia_tenant on hinova_tarifa_sync_dias as restrictive for all to app_user
 using(tenant_id=app.current_tenant()) with check(tenant_id=app.current_tenant());
create policy tarifa_dia_admin on hinova_tarifa_sync_dias for all to app_user
 using(app.has_permission('integracoes.gerenciar')) with check(app.has_permission('integracoes.gerenciar'));
grant select,insert,update on hinova_boleto_tarifas,hinova_tarifa_sync_dias to app_user;

create or replace function app.commission_source_boletos(p_competencia date, p_beneficiaries uuid[])
returns table (boleto_id uuid, participante_id uuid, valor numeric, pago_em date)
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
#variable_conflict use_column
declare
  v_plan uuid;
  v_max_level int;
  v_has_global boolean;
begin
  perform app.assert_commission_access(p_competencia, p_beneficiaries);
  v_plan := app.commission_plan_for(p_competencia);
  if v_plan is null then return; end if;

  v_max_level := app.commission_max_level(v_plan);
  select exists (
    select 1
      from commission_rules r
      join commission_group_members m on m.group_id = r.group_id
     where r.plan_id = v_plan and r.type = 'global' and m.user_id = any (p_beneficiaries)
  ) into v_has_global;

  return query
    select b.id, b.participante_id, b.comissao_base, b.pago_em
      from boletos b
     where b.tenant_id = app.current_tenant()
       and b.status = 'recebido'
       and b.commission_hold_reason is null
       and b.comissao_base > 0
       and b.pago_em >= p_competencia
       and b.pago_em < (p_competencia + interval '1 month')::date
       and (
         v_has_global
         or b.participante_id in (
           select hp.descendant_id
             from hierarchy_paths hp
            where hp.tenant_id = app.current_tenant()
              and hp.ancestor_id = any (p_beneficiaries)
              and hp.depth <= v_max_level));
end
$$;

create or replace function app.commission_pending_fees(p_competencia date, p_beneficiaries uuid[])
returns table (boleto_id uuid, participante_id uuid, valor numeric, pago_em date)
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
#variable_conflict use_column
declare
  v_plan uuid;
  v_max_level int;
  v_has_global boolean;
begin
  perform app.assert_commission_access(p_competencia, p_beneficiaries);
  v_plan := app.commission_plan_for(p_competencia);
  if v_plan is null then return; end if;

  v_max_level := app.commission_max_level(v_plan);
  select exists (
    select 1
      from commission_rules r
      join commission_group_members m on m.group_id = r.group_id
     where r.plan_id = v_plan and r.type = 'global' and m.user_id = any (p_beneficiaries)
  ) into v_has_global;

  return query
    select b.id, b.participante_id, b.valor, b.pago_em
      from boletos b
     where b.tenant_id = app.current_tenant()
       and b.status = 'recebido'
       and b.commission_hold_reason is null
       and b.comissao_base is null
       and b.pago_em >= p_competencia
       and b.pago_em < (p_competencia + interval '1 month')::date
       and (
         v_has_global
         or b.participante_id in (
           select hp.descendant_id
             from hierarchy_paths hp
            where hp.tenant_id = app.current_tenant()
              and hp.ancestor_id = any (p_beneficiaries)
              and hp.depth <= v_max_level));
end
$$;

grant execute on function app.commission_pending_fees(date, uuid[]) to app_user;

-- Match the .NET engine's rounding (including exact half-cent ties).
create function app.round_commission(v numeric) returns numeric
language sql immutable strict as $$
 select case when abs(v*100-trunc(v*100))=0.5 and mod(trunc(v*100),2)=0
             then trunc(v*100)/100 else round(v,2) end
$$;
grant execute on function app.round_commission(numeric) to app_user;
