create function app.set_password_admin(p_user uuid, p_password_hash text)
returns void
language plpgsql volatile security definer set search_path = pg_catalog, public, app
as $$
begin
  update users set password_hash = p_password_hash, status = 'ativo' where id = p_user;
  delete from sessions where user_id = p_user;
end
$$;

grant execute on function app.set_password_admin(uuid, text) to app_user, app_superadmin;
