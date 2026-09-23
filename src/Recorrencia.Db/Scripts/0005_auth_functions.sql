create function app.resolve_tenant(p_slug text)
returns table (id uuid, name text, status text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select t.id, t.name, t.status from tenants t where t.slug = lower(p_slug)
$$;

create function app.find_login(p_tenant uuid, p_email text)
returns table (user_id uuid, password_hash text, status text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select u.id, u.password_hash, u.status
    from users u
   where u.tenant_id = p_tenant and u.email = p_email::citext
$$;

create function app.create_session(
  p_token_hash bytea, p_tenant uuid, p_user uuid,
  p_idle_seconds int, p_absolute_seconds int, p_ip text, p_user_agent text)
returns uuid
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v_id uuid;
begin
  if not exists (
       select 1 from users u join tenants t on t.id = u.tenant_id
        where u.id = p_user and u.tenant_id = p_tenant and u.status = 'ativo' and t.status = 'ativo') then
    raise exception 'auth.invalid_user' using errcode = 'P0001';
  end if;

  insert into sessions (token_hash, kind, tenant_id, user_id, expires_at, absolute_expires_at, ip, user_agent)
  values (
    p_token_hash, 'tenant', p_tenant, p_user,
    now() + make_interval(secs => least(p_idle_seconds, p_absolute_seconds)),
    now() + make_interval(secs => p_absolute_seconds),
    p_ip::inet, p_user_agent)
  returning id into v_id;
  return v_id;
end
$$;

create function app.find_platform_login(p_email text)
returns table (admin_id uuid, password_hash text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select a.id, a.password_hash from platform_admins a where a.email = p_email::citext
$$;

create function app.create_platform_session(
  p_token_hash bytea, p_admin uuid, p_idle_seconds int, p_absolute_seconds int, p_ip text, p_user_agent text)
returns uuid
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v_id uuid;
begin
  if not exists (select 1 from platform_admins where id = p_admin) then
    raise exception 'auth.invalid_user' using errcode = 'P0001';
  end if;

  insert into sessions (token_hash, kind, platform_admin_id, expires_at, absolute_expires_at, ip, user_agent)
  values (
    p_token_hash, 'platform', p_admin,
    now() + make_interval(secs => least(p_idle_seconds, p_absolute_seconds)),
    now() + make_interval(secs => p_absolute_seconds),
    p_ip::inet, p_user_agent)
  returning id into v_id;
  return v_id;
end
$$;

create function app.resolve_session(p_token_hash bytea, p_idle_seconds int)
returns table (session_id uuid, kind text, tenant_id uuid, user_id uuid, platform_admin_id uuid)
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
#variable_conflict use_column
declare
  s sessions%rowtype;
begin
  select * into s from sessions where token_hash = p_token_hash for update;
  if not found then
    return;
  end if;

  if s.expires_at <= now() or s.absolute_expires_at <= now() then
    delete from sessions where id = s.id;
    return;
  end if;

  if s.kind = 'tenant' and not exists (
       select 1 from users u join tenants t on t.id = u.tenant_id
        where u.id = s.user_id and u.status = 'ativo' and t.status = 'ativo') then
    delete from sessions where id = s.id;
    return;
  end if;

  update sessions
     set last_seen_at = now(),
         expires_at = least(now() + make_interval(secs => p_idle_seconds), absolute_expires_at)
   where id = s.id;

  return query select s.id, s.kind, s.tenant_id, s.user_id, s.platform_admin_id;
end
$$;

create function app.revoke_session(p_token_hash bytea)
returns void
language sql volatile security definer set search_path = pg_catalog, public, app
as $$
  delete from sessions where token_hash = p_token_hash
$$;

create function app.revoke_user_sessions(p_user uuid)
returns int
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v_count int;
begin
  delete from sessions where user_id = p_user and tenant_id = app.current_tenant();
  get diagnostics v_count = row_count;
  return v_count;
end
$$;

create function app.create_invite(p_user uuid, p_token_hash bytea, p_purpose text, p_ttl_seconds int)
returns void
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v_status text;
begin
  select status into v_status from users where id = p_user and tenant_id = app.current_tenant();
  if not found then
    raise exception 'invite.user_not_found' using errcode = 'P0001';
  end if;

  if (p_purpose = 'convite' and v_status <> 'convidado')
     or (p_purpose = 'redefinicao' and v_status <> 'ativo') then
    raise exception 'invite.invalid_status' using errcode = 'P0001';
  end if;

  delete from invite_tokens where user_id = p_user and purpose = p_purpose and used_at is null;

  insert into invite_tokens (token_hash, tenant_id, user_id, purpose, expires_at)
  values (p_token_hash, app.current_tenant(), p_user, p_purpose, now() + make_interval(secs => p_ttl_seconds));
end
$$;

create function app.consume_invite(p_tenant uuid, p_token_hash bytea, p_password_hash text)
returns uuid
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v invite_tokens%rowtype;
  v_status text;
begin
  select i.* into v
    from invite_tokens i
    join tenants t on t.id = i.tenant_id
   where i.token_hash = p_token_hash and i.tenant_id = p_tenant and t.status = 'ativo'
   for update of i;

  if not found or v.used_at is not null or v.expires_at <= now() then
    raise exception 'invite.invalid' using errcode = 'P0001';
  end if;

  select status into v_status from users where id = v.user_id for update;
  if (v.purpose = 'convite' and v_status <> 'convidado')
     or (v.purpose = 'redefinicao' and v_status <> 'ativo') then
    raise exception 'invite.invalid' using errcode = 'P0001';
  end if;

  update users set password_hash = p_password_hash, status = 'ativo' where id = v.user_id;
  update invite_tokens set used_at = now() where id = v.id;
  delete from sessions where user_id = v.user_id;
  return v.user_id;
end
$$;

create function app.rehash_own_password(p_hash text)
returns void
language sql volatile security definer set search_path = pg_catalog, public, app
as $$
  update users set password_hash = p_hash
   where id = app.current_user_id() and tenant_id = app.current_tenant() and status = 'ativo'
$$;

grant execute on function
  app.resolve_tenant(text),
  app.find_login(uuid, text),
  app.create_session(bytea, uuid, uuid, int, int, text, text),
  app.find_platform_login(text),
  app.create_platform_session(bytea, uuid, int, int, text, text),
  app.resolve_session(bytea, int),
  app.revoke_session(bytea),
  app.revoke_user_sessions(uuid),
  app.create_invite(uuid, bytea, text, int),
  app.consume_invite(uuid, bytea, text),
  app.rehash_own_password(text)
to app_user, app_superadmin;
