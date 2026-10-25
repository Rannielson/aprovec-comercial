-- A zero-loss result is meaningful only after the full due-date window was
-- imported. Paid-only imports do not prove that no unpaid boletos exist.
create table boleto_vencimento_importacoes (
  tenant_id uuid not null references tenants (id),
  competencia date not null check (extract(day from competencia) = 1),
  importado_em timestamptz not null default now(),
  total_retornado int not null check (total_retornado >= 0),
  total_vinculado int not null check (total_vinculado >= 0),
  primary key (tenant_id, competencia)
);

alter table boleto_vencimento_importacoes enable row level security;
alter table boleto_vencimento_importacoes force row level security;
create policy boleto_vencimento_importacoes_isolation on boleto_vencimento_importacoes
  as restrictive for all to app_user
  using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant());
create policy boleto_vencimento_importacoes_select on boleto_vencimento_importacoes
  for select to app_user using (app.has_permission('comissoes.visualizar') or app.has_permission('integracoes.gerenciar'));
create policy boleto_vencimento_importacoes_write on boleto_vencimento_importacoes
  for all to app_user using (app.has_permission('integracoes.gerenciar'))
  with check (app.has_permission('integracoes.gerenciar'));
grant select, insert, update on boleto_vencimento_importacoes to app_user;

-- The same payable-tree scope as paid commissions, but with boletos whose due
-- month is selected and which remain unpaid. It must not expose another
-- participant's portfolio through the caller's ordinary boletos SELECT RLS.
create function app.commission_source_unpaid_boletos(p_competencia date, p_beneficiaries uuid[])
returns table (boleto_id uuid, participante_id uuid, valor numeric, status text)
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
    select 1 from commission_rules r
    join commission_group_members m on m.group_id = r.group_id
    where r.plan_id = v_plan and r.type = 'global' and m.user_id = any(p_beneficiaries)
  ) into v_has_global;

  return query
    select b.id, b.participante_id, b.valor,
           case when b.status = 'a_vencer' then 'atraso' else b.status end
      from boletos b
     where b.tenant_id = app.current_tenant()
       and (b.status in ('atraso', 'cancelado')
         or (b.status = 'a_vencer' and b.vencimento < (now() at time zone 'America/Sao_Paulo')::date))
       and b.vencimento >= p_competencia
       and b.vencimento < (p_competencia + interval '1 month')::date
       and (
         v_has_global
         or b.participante_id in (
           select hp.descendant_id from hierarchy_paths hp
            where hp.tenant_id = app.current_tenant()
              and hp.ancestor_id = any(p_beneficiaries)
              and hp.depth <= v_max_level));
end
$$;

grant execute on function app.commission_source_unpaid_boletos(date, uuid[]) to app_user;
