create function app.provision_tenant(
  p_slug text, p_name text, p_admin_name text, p_admin_email text,
  p_plan_template text, p_plan_effective_from date)
returns table (tenant_id uuid, admin_user_id uuid)
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
#variable_conflict use_column
declare
  v_tenant uuid;
  v_admin uuid;
  v_admin_role uuid;
  v_plan uuid;
begin
  insert into tenants (slug, name) values (lower(p_slug), p_name) returning id into v_tenant;

  insert into tenant_modules (tenant_id, module_key) select v_tenant, key from modules;

  insert into roles (tenant_id, name, source_template_key)
  select v_tenant, name, key from role_templates;

  insert into role_permissions (tenant_id, role_id, permission_key, scope)
  select v_tenant, r.id, tp.permission_key, tp.scope
    from roles r
    join role_template_permissions tp on tp.template_key = r.source_template_key
   where r.tenant_id = v_tenant;

  insert into users (tenant_id, name, email) values (v_tenant, p_admin_name, p_admin_email::citext)
  returning id into v_admin;

  select id into v_admin_role from roles where tenant_id = v_tenant and source_template_key = 'administrador';
  insert into user_roles (tenant_id, user_id, role_id) values (v_tenant, v_admin, v_admin_role);

  if p_plan_template is not null then
    if not exists (select 1 from plan_templates where key = p_plan_template) then
      raise exception 'plan.template_not_found' using errcode = 'P0001';
    end if;

    insert into commission_groups (tenant_id, name)
    select distinct v_tenant, group_name
      from plan_template_rules
     where template_key = p_plan_template and group_name is not null;

    insert into commission_plans (tenant_id, name, effective_from, source_template_key)
    select v_tenant, name, p_plan_effective_from, key from plan_templates where key = p_plan_template
    returning id into v_plan;

    insert into commission_rules (tenant_id, plan_id, type, rate, level, group_id)
    select v_tenant, v_plan, tr.type, tr.rate, tr.level, g.id
      from plan_template_rules tr
      left join commission_groups g on g.tenant_id = v_tenant and g.name = tr.group_name
     where tr.template_key = p_plan_template
     order by tr.position;

    update commission_plans set status = 'ativo', activated_at = now() where id = v_plan;
  end if;

  return query select v_tenant, v_admin;
end
$$;

grant execute on function app.provision_tenant(text, text, text, text, text, date) to app_superadmin;
