create table users (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  name text not null check (length(btrim(name)) > 0),
  email citext not null,
  password_hash text,
  status text not null default 'convidado' check (status in ('convidado', 'ativo', 'desligado')),
  supervisor_id uuid,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, email),
  unique (tenant_id, id),
  foreign key (tenant_id, supervisor_id) references users (tenant_id, id),
  check (supervisor_id is null or supervisor_id <> id),
  check (status <> 'ativo' or password_hash is not null)
);

create index users_supervisor_idx on users (supervisor_id);

create trigger users_touch before update on users
  for each row execute function app.touch_updated_at();

create table hierarchy_paths (
  tenant_id uuid not null,
  ancestor_id uuid not null,
  descendant_id uuid not null,
  depth int not null check (depth >= 0),
  primary key (ancestor_id, descendant_id),
  foreign key (tenant_id, ancestor_id) references users (tenant_id, id) on delete cascade,
  foreign key (tenant_id, descendant_id) references users (tenant_id, id) on delete cascade
);

create index hierarchy_paths_descendant_idx on hierarchy_paths (descendant_id, depth);

create function app.users_guard() returns trigger
language plpgsql
as $$
begin
  if new.tenant_id <> old.tenant_id then
    raise exception 'users.tenant_immutable' using errcode = 'P0001';
  end if;
  return new;
end
$$;

create trigger users_guard before update of tenant_id on users
  for each row execute function app.users_guard();

create function app.hierarchy_after_insert() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  insert into hierarchy_paths (tenant_id, ancestor_id, descendant_id, depth)
  values (new.tenant_id, new.id, new.id, 0);

  if new.supervisor_id is not null then
    insert into hierarchy_paths (tenant_id, ancestor_id, descendant_id, depth)
    select new.tenant_id, p.ancestor_id, new.id, p.depth + 1
      from hierarchy_paths p
     where p.descendant_id = new.supervisor_id;
  end if;
  return null;
end
$$;

create function app.hierarchy_before_update() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  perform pg_advisory_xact_lock(hashtext('hierarchy:' || new.tenant_id::text));
  if new.supervisor_id is not null and exists (
       select 1 from hierarchy_paths
        where ancestor_id = new.id and descendant_id = new.supervisor_id) then
    raise exception 'hierarchy.cycle' using errcode = 'P0001';
  end if;
  return new;
end
$$;

create function app.hierarchy_after_update() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  delete from hierarchy_paths d
   where d.descendant_id in (select descendant_id from hierarchy_paths where ancestor_id = new.id)
     and d.ancestor_id not in (select descendant_id from hierarchy_paths where ancestor_id = new.id);

  if new.supervisor_id is not null then
    insert into hierarchy_paths (tenant_id, ancestor_id, descendant_id, depth)
    select new.tenant_id, sup.ancestor_id, sub.descendant_id, sup.depth + sub.depth + 1
      from hierarchy_paths sup
      cross join hierarchy_paths sub
     where sup.descendant_id = new.supervisor_id
       and sub.ancestor_id = new.id;
  end if;
  return null;
end
$$;

create trigger users_hierarchy_insert after insert on users
  for each row execute function app.hierarchy_after_insert();

create trigger users_hierarchy_check before update of supervisor_id on users
  for each row when (new.supervisor_id is distinct from old.supervisor_id)
  execute function app.hierarchy_before_update();

create trigger users_hierarchy_move after update of supervisor_id on users
  for each row when (new.supervisor_id is distinct from old.supervisor_id)
  execute function app.hierarchy_after_update();

grant select (id, tenant_id, name, email, status, supervisor_id, created_at, updated_at) on users to app_user;
grant insert (id, tenant_id, name, email, supervisor_id) on users to app_user;
grant update (name, email, status, supervisor_id) on users to app_user;
grant select on hierarchy_paths to app_user;
