-- app.users_column_guard() unconditionally forbade changing `name`/`email` on the `users`
-- table, since before this plan nobody could edit those columns at all. PUT /users/{id} now
-- needs to change name/email and is already gated at the route level by `usuarios.convidar`;
-- this migration closes the same gap at the trigger level so the DB-layer guard matches the
-- route-level permission instead of rejecting every caller unconditionally.
create or replace function app.users_column_guard() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
begin
  if app.current_user_id() is null then
    return new;
  end if;

  if new.supervisor_id is distinct from old.supervisor_id
     and not app.has_permission('estrutura.editar') then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  if new.status is distinct from old.status
     and not app.has_permission('usuarios.desligar') then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  if (new.name is distinct from old.name or new.email is distinct from old.email)
     and not app.has_permission('usuarios.convidar') then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  return new;
end
$$;
