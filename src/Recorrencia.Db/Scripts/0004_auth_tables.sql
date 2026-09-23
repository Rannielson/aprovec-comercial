create table sessions (
  id uuid primary key default gen_random_uuid(),
  token_hash bytea not null unique,
  kind text not null check (kind in ('tenant', 'platform')),
  tenant_id uuid references tenants (id) on delete cascade,
  user_id uuid references users (id) on delete cascade,
  platform_admin_id uuid references platform_admins (id) on delete cascade,
  created_at timestamptz not null default now(),
  last_seen_at timestamptz not null default now(),
  expires_at timestamptz not null,
  absolute_expires_at timestamptz not null,
  ip inet,
  user_agent text,
  check (expires_at <= absolute_expires_at),
  check (
    (kind = 'tenant' and tenant_id is not null and user_id is not null and platform_admin_id is null)
    or (kind = 'platform' and tenant_id is null and user_id is null and platform_admin_id is not null)
  )
);

create index sessions_user_idx on sessions (user_id);

create table invite_tokens (
  id uuid primary key default gen_random_uuid(),
  token_hash bytea not null unique,
  tenant_id uuid not null references tenants (id) on delete cascade,
  user_id uuid not null references users (id) on delete cascade,
  purpose text not null check (purpose in ('convite', 'redefinicao')),
  expires_at timestamptz not null,
  used_at timestamptz,
  created_at timestamptz not null default now()
);

create index invite_tokens_user_idx on invite_tokens (user_id, purpose);
