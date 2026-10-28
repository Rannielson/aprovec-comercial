alter table fechamento_detalhes add column gross_base numeric(14,2), add column bank_fee numeric(14,2);
alter table fechamento_detalhes add constraint detalhe_liquido_composition check
 ((gross_base is null and bank_fee is null) or
  (gross_base is not null and bank_fee is not null and gross_base >= base and bank_fee = gross_base-base));

create function app.commission_source_financials(p_competencia date,p_beneficiaries uuid[])
returns table(boleto_id uuid,participante_id uuid,valor numeric,valor_bruto numeric,tarifa_bancaria numeric)
language sql stable security definer set search_path=pg_catalog,public,app as $$
 select s.boleto_id,s.participante_id,s.valor,b.valor,coalesce(b.tarifa_bancaria,0)
 from app.commission_source_boletos(p_competencia,p_beneficiaries) s
 join boletos b on b.id=s.boleto_id and b.tenant_id=app.current_tenant()
$$;
grant execute on function app.commission_source_financials(date,uuid[]) to app_user;

create table fechamento_revisoes(
 id uuid primary key default gen_random_uuid(),tenant_id uuid not null references tenants(id),
 fechamento_id uuid not null,competencia date not null,actor_id uuid not null,motivo text not null,
 fechamento jsonb not null,detalhes jsonb not null,remuneracao jsonb not null,
 created_at timestamptz not null default now(),transaction_id bigint not null default txid_current(),
 foreign key(tenant_id,fechamento_id) references fechamentos(tenant_id,id),
 foreign key(tenant_id,actor_id) references users(tenant_id,id)
);
alter table fechamento_revisoes enable row level security;
alter table fechamento_revisoes force row level security;
create policy fechamento_revisao_tenant on fechamento_revisoes as restrictive for select to app_user
 using(tenant_id=app.current_tenant());
create policy fechamento_revisao_admin on fechamento_revisoes for select to app_user
 using(app.has_permission('fechamento.confirmar') and app.scope_for('comissoes.visualizar')='tenant');
grant select on fechamento_revisoes to app_user;

create or replace function app.fechamentos_guard() returns trigger
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

  -- A reopen must have an immutable archive written by the privileged revision
  -- function in this exact transaction. Direct status edits remain forbidden.
  if old.status='confirmado' and new.status='apuracao'
     and app.has_permission('fechamento.confirmar') and app.scope_for('comissoes.visualizar')='tenant'
     and exists(select 1 from fechamento_revisoes r where r.fechamento_id=old.id
       and r.tenant_id=old.tenant_id and r.actor_id=app.current_user_id()
       and r.transaction_id=txid_current() and r.fechamento=to_jsonb(old)) then
    return new;
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


create function app.reopen_fechamento(p_competencia date,p_motivo text) returns uuid
language plpgsql security definer set search_path=pg_catalog,public,app as $$
declare f fechamentos%rowtype; revision uuid;
begin
 if not app.has_permission('fechamento.confirmar') or app.scope_for('comissoes.visualizar') is distinct from 'tenant' then
  raise exception 'auth.forbidden' using errcode='42501';
 end if;
 if length(trim(coalesce(p_motivo,'')))<10 then raise exception 'fechamento.reason_required' using errcode='P0001'; end if;
 select * into f from fechamentos where tenant_id=app.current_tenant() and competencia=p_competencia for update;
 if f.id is null then raise exception 'fechamento.not_found' using errcode='P0001'; end if;
 if f.status<>'confirmado' then raise exception 'fechamento.invalid_transition' using errcode='P0001'; end if;
 insert into fechamento_revisoes(tenant_id,fechamento_id,competencia,actor_id,motivo,fechamento,detalhes,remuneracao)
 values(f.tenant_id,f.id,f.competencia,app.current_user_id(),trim(p_motivo),to_jsonb(f),
   coalesce((select jsonb_agg(to_jsonb(d)) from fechamento_detalhes d where d.fechamento_id=f.id),'[]'),
   coalesce((select jsonb_agg(to_jsonb(r)) from fechamento_remuneracao r where r.fechamento_id=f.id),'[]'))
 returning id into revision;
 update fechamentos set status='apuracao',confirmado_em=null,confirmado_por=null where id=f.id;
 delete from fechamento_detalhes where fechamento_id=f.id;
 delete from fechamento_remuneracao where fechamento_id=f.id and tipo<>'extra';
 return revision;
end
$$;
revoke all on function app.reopen_fechamento(date,text) from public;
grant execute on function app.reopen_fechamento(date,text) to app_user;

-- The lost-commission estimate also uses only a known net base.
create or replace function app.commission_source_unpaid_boletos(p_competencia date, p_beneficiaries uuid[])
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
    select b.id, b.participante_id, b.comissao_base,
           case when b.status = 'a_vencer' then 'atraso' else b.status end
      from boletos b
     where b.tenant_id = app.current_tenant()
       and b.comissao_base > 0
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
