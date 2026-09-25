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
