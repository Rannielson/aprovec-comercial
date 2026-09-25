create table tenants (
  id uuid primary key default gen_random_uuid(),
  slug text not null unique
    check (slug ~ '^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$' and slug not in ('admin', 'www', 'api')),
  name text not null check (length(btrim(name)) > 0),
  status text not null default 'ativo' check (status in ('ativo', 'suspenso')),
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create trigger tenants_touch before update on tenants
  for each row execute function app.touch_updated_at();

create table platform_admins (
  id uuid primary key default gen_random_uuid(),
  email citext not null unique,
  password_hash text not null,
  created_at timestamptz not null default now(),
  updated_at timestamptz not null default now()
);

create trigger platform_admins_touch before update on platform_admins
  for each row execute function app.touch_updated_at();
