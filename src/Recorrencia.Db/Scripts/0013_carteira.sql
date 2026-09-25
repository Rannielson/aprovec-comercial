create function app.carteira_owner_ids()
returns uuid[]
language plpgsql stable security definer set search_path = pg_catalog, public, app
as $$
declare
  v_scope text := app.scope_for('carteira.visualizar');
begin
  if v_scope is null then
    return array[]::uuid[];
  end if;
  if v_scope = 'tenant' then
    return coalesce(
      (select array_agg(u.id order by u.id) from users u where u.tenant_id = app.current_tenant()),
      array[]::uuid[]);
  end if;
  return app.visible_owner_ids(v_scope);
end
$$;

grant execute on function app.carteira_owner_ids() to app_user, app_superadmin;
