create function app.set_password_admin(p_user uuid, p_password_hash text)
returns void
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
declare
  v_status text;
begin
  if app.current_user_id() is not null and not app.has_permission('usuarios.convidar') then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  select status into v_status from users
   where id = p_user and tenant_id = app.current_tenant()
   for update;

  if not found or v_status = 'desligado' then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  update users set password_hash = p_password_hash, status = 'ativo' where id = p_user;
  delete from sessions where user_id = p_user and tenant_id = app.current_tenant();
end
$$;

grant execute on function app.set_password_admin(uuid, text) to app_user, app_superadmin;

-- PUT /users/{id} and PUT /users/{id}/password must refuse to act on a user whose roles
-- outrank the actor (RoleGuards grant ceiling), which needs the TARGET's role ids. But
-- user_roles_select only shows another user's role rows to usuarios.gerenciar_perfis
-- holders, so for a usuarios.convidar-only actor a plain select comes back empty and the
-- ceiling check silently passes. This reads them past RLS, scoped to the current tenant,
-- and only for an actor who holds usuarios.convidar (the permission both callers require).
create function app.user_role_ids(p_user uuid)
returns setof uuid
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
begin
  if not app.has_permission('usuarios.convidar') then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  return query
    select ur.role_id from user_roles ur
     where ur.user_id = p_user and ur.tenant_id = app.current_tenant();
end
$$;

grant execute on function app.user_role_ids(uuid) to app_user;
