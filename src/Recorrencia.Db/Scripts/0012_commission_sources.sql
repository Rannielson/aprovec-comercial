create function app.commission_plan_for(p_competencia date)
returns uuid
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select p.id
    from commission_plans p
   where p.tenant_id = app.current_tenant()
     and p.status = 'ativo'
     and p.effective_from <= p_competencia
   order by p.effective_from desc
   limit 1
$$;

create function app.commission_beneficiaries()
returns uuid[]
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
declare
  v_scope text := app.scope_for('comissoes.visualizar');
begin
  if v_scope is null then
    return array[]::uuid[];
  end if;
  if v_scope = 'tenant' then
    return coalesce(
      (select array_agg(u.id order by u.id) from users u where u.tenant_id = app.current_tenant()),
      array[]::uuid[]);
  end if;
  return app.visible_owner_ids(v_scope);
end
$$;

create function app.assert_commission_access(p_competencia date, p_beneficiaries uuid[])
returns void
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
declare
  v_allowed uuid[] := app.commission_beneficiaries();
begin
  if p_competencia is null or extract(day from p_competencia) <> 1 then
    raise exception 'commission.invalid_competencia' using errcode = 'P0001';
  end if;
  if exists (
       select 1 from unnest(coalesce(p_beneficiaries, array[]::uuid[])) b(id)
        where b.id is null or not (b.id = any (v_allowed))) then
    raise exception 'forbidden' using errcode = '42501';
  end if;
end
$$;

create function app.commission_max_level(p_plan uuid)
returns int
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select coalesce(max(r.level), 0) from commission_rules r where r.plan_id = p_plan and r.type = 'upline'
$$;

create function app.commission_source_boletos(p_competencia date, p_beneficiaries uuid[])
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
  if v_plan is null then
    return;
  end if;

  v_max_level := app.commission_max_level(v_plan);
  select exists (
    select 1
      from commission_rules r
      join commission_group_members m on m.group_id = r.group_id
     where r.plan_id = v_plan and r.type = 'global' and m.user_id = any (p_beneficiaries))
  into v_has_global;

  return query
    select b.id, b.participante_id, b.valor, b.pago_em
      from boletos b
     where b.tenant_id = app.current_tenant()
       and b.status = 'recebido'
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

create function app.commission_source_paths(p_competencia date, p_beneficiaries uuid[])
returns table (ancestor_id uuid, descendant_id uuid, depth int)
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
#variable_conflict use_column
declare
  v_plan uuid;
  v_max_level int;
begin
  perform app.assert_commission_access(p_competencia, p_beneficiaries);
  v_plan := app.commission_plan_for(p_competencia);
  if v_plan is null then
    return;
  end if;

  v_max_level := app.commission_max_level(v_plan);
  return query
    select hp.ancestor_id, hp.descendant_id, hp.depth
      from hierarchy_paths hp
     where hp.tenant_id = app.current_tenant()
       and hp.ancestor_id = any (p_beneficiaries)
       and hp.depth between 1 and v_max_level;
end
$$;

create function app.fechamento_status(p_competencia date)
returns table (fechamento_id uuid, status text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select f.id, f.status
    from fechamentos f
   where f.tenant_id = app.current_tenant() and f.competencia = p_competencia
$$;

grant execute on function
  app.commission_plan_for(date),
  app.commission_beneficiaries(),
  app.commission_source_boletos(date, uuid[]),
  app.commission_source_paths(date, uuid[]),
  app.fechamento_status(date)
to app_user, app_superadmin;
