create table plan_templates (
  key text primary key,
  name text not null
);

create table plan_template_rules (
  template_key text not null references plan_templates (key) on delete cascade,
  position int not null,
  type text not null check (type in ('own', 'upline', 'global')),
  rate numeric(7,4) not null check (rate > 0 and rate <= 1),
  level int,
  group_name text,
  primary key (template_key, position),
  check (
    (type = 'own' and level is null and group_name is null)
    or (type = 'upline' and level is not null and level >= 1 and group_name is null)
    or (type = 'global' and level is null and group_name is not null)
  )
);

insert into plan_templates (key, name) values ('aprovec', 'Modelo APROVEC');
insert into plan_template_rules (template_key, position, type, rate, level, group_name) values
  ('aprovec', 1, 'own', 0.0700, null, null),
  ('aprovec', 2, 'upline', 0.0200, 1, null),
  ('aprovec', 3, 'global', 0.0100, null, 'Coordenação');

create table commission_groups (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  name text not null check (length(btrim(name)) > 0),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, name),
  unique (tenant_id, id)
);

create trigger commission_groups_touch before update on commission_groups
  for each row execute function app.touch_updated_at();

create table commission_group_members (
  tenant_id uuid not null,
  group_id uuid not null,
  user_id uuid not null,
  created_at timestamptz not null default now(),
  primary key (group_id, user_id),
  foreign key (tenant_id, group_id) references commission_groups (tenant_id, id) on delete cascade,
  foreign key (tenant_id, user_id) references users (tenant_id, id) on delete cascade
);

create table commission_plans (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  name text not null check (length(btrim(name)) > 0),
  effective_from date not null check (extract(day from effective_from) = 1),
  status text not null default 'rascunho' check (status in ('rascunho', 'ativo')),
  source_template_key text references plan_templates (key),
  activated_at timestamptz,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  unique (tenant_id, id),
  check ((status = 'ativo') = (activated_at is not null))
);

create unique index commission_plans_active_effective
  on commission_plans (tenant_id, effective_from) where status = 'ativo';

create trigger commission_plans_touch before update on commission_plans
  for each row execute function app.touch_updated_at();

create table commission_rules (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null,
  plan_id uuid not null,
  type text not null check (type in ('own', 'upline', 'global')),
  rate numeric(7,4) not null check (rate > 0 and rate <= 1),
  level int,
  group_id uuid,
  foreign key (tenant_id, plan_id) references commission_plans (tenant_id, id) on delete cascade,
  foreign key (tenant_id, group_id) references commission_groups (tenant_id, id),
  check (
    (type = 'own' and level is null and group_id is null)
    or (type = 'upline' and level is not null and level >= 1 and group_id is null)
    or (type = 'global' and level is null and group_id is not null)
  )
);

create unique index commission_rules_one_own on commission_rules (plan_id) where type = 'own';
create unique index commission_rules_unique_level on commission_rules (plan_id, level) where type = 'upline';
create unique index commission_rules_unique_group on commission_rules (plan_id, group_id) where type = 'global';

create function app.commission_plans_guard() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  if old.status = 'ativo' then
    raise exception 'plan.active_immutable' using errcode = 'P0001';
  end if;
  if tg_op = 'DELETE' then
    return old;
  end if;
  if new.status = 'ativo' and not exists (select 1 from commission_rules where plan_id = new.id) then
    raise exception 'plan.empty' using errcode = 'P0001';
  end if;
  return new;
end
$$;

create trigger commission_plans_guard before update or delete on commission_plans
  for each row execute function app.commission_plans_guard();

create function app.commission_rules_guard() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
declare
  v_plan uuid := case when tg_op = 'DELETE' then old.plan_id else new.plan_id end;
begin
  if exists (select 1 from commission_plans where id = v_plan and status = 'ativo') then
    raise exception 'plan.active_immutable' using errcode = 'P0001';
  end if;
  if tg_op = 'DELETE' then
    return old;
  end if;
  return new;
end
$$;

create trigger commission_rules_guard before insert or update or delete on commission_rules
  for each row execute function app.commission_rules_guard();

grant select on plan_templates, plan_template_rules to app_user;
grant select, insert, update, delete on commission_groups, commission_group_members, commission_plans, commission_rules to app_user;
