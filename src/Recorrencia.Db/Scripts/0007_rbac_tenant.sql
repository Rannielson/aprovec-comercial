create table tenant_modules (
  tenant_id uuid not null references tenants (id) on delete cascade,
  module_key text not null references modules (key),
  enabled boolean not null default true,
  primary key (tenant_id, module_key)
);

create table roles (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  name text not null check (length(btrim(name)) > 0),
  source_template_key text references role_templates (key),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, name),
  unique (tenant_id, id)
);

create trigger roles_touch before update on roles
  for each row execute function app.touch_updated_at();

create table role_permissions (
  tenant_id uuid not null,
  role_id uuid not null,
  permission_key text not null references permissions (key),
  scope text check (scope in ('own', 'direct', 'subtree', 'tenant')),
  primary key (role_id, permission_key),
  foreign key (tenant_id, role_id) references roles (tenant_id, id) on delete cascade
);

create trigger role_permissions_scope before insert or update on role_permissions
  for each row execute function app.check_permission_scope();

create table user_roles (
  tenant_id uuid not null,
  user_id uuid not null,
  role_id uuid not null,
  primary key (user_id, role_id),
  foreign key (tenant_id, user_id) references users (tenant_id, id) on delete cascade,
  foreign key (tenant_id, role_id) references roles (tenant_id, id) on delete cascade
);

create index user_roles_role_idx on user_roles (role_id);

create function app.effective_permissions()
returns table (permission_key text, scope text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select rp.permission_key,
         (array_agg(rp.scope order by array_position(array['own', 'direct', 'subtree', 'tenant'], rp.scope) desc nulls last))[1]
    from user_roles ur
    join users u on u.id = ur.user_id and u.status = 'ativo'
    join role_permissions rp on rp.role_id = ur.role_id
    join permissions p on p.key = rp.permission_key
    join tenant_modules tm on tm.tenant_id = ur.tenant_id and tm.module_key = p.module_key and tm.enabled
   where ur.user_id = app.current_user_id()
     and ur.tenant_id = app.current_tenant()
   group by rp.permission_key
$$;

create function app.scope_for(p_permission text)
returns text
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select e.scope from app.effective_permissions() e where e.permission_key = p_permission
$$;

create function app.has_permission(p_permission text)
returns boolean
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select exists (select 1 from app.effective_permissions() e where e.permission_key = p_permission)
$$;

create function app.visible_owner_ids(p_scope text)
returns uuid[]
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select case
    when p_scope = 'own' then array[app.current_user_id()]
    when p_scope in ('direct', 'subtree') then coalesce((
      select array_agg(hp.descendant_id)
        from hierarchy_paths hp
       where hp.ancestor_id = app.current_user_id()
         and hp.tenant_id = app.current_tenant()
         and (p_scope = 'subtree' or hp.depth <= 1)), array[]::uuid[])
    else array[]::uuid[]
  end
$$;

create function app.count_active_with_permission(p_permission text)
returns int
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select count(distinct u.id)::int
    from users u
    join user_roles ur on ur.user_id = u.id
    join role_permissions rp on rp.role_id = ur.role_id and rp.permission_key = p_permission
    join permissions p on p.key = rp.permission_key
    join tenant_modules tm on tm.tenant_id = u.tenant_id and tm.module_key = p.module_key and tm.enabled
   where u.tenant_id = app.current_tenant()
     and u.status = 'ativo'
$$;

grant execute on function
  app.effective_permissions(),
  app.scope_for(text),
  app.has_permission(text),
  app.visible_owner_ids(text),
  app.count_active_with_permission(text)
to app_user, app_superadmin;

grant select on tenant_modules to app_user;
grant select, insert, update, delete on roles, role_permissions, user_roles to app_user;
