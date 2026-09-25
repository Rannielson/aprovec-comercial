-- app.users_column_guard() gated every `status` change on `usuarios.desligar`. But
-- app.set_password_admin (behind POST /users with a password and PUT /users/{id}/password,
-- both gated by `usuarios.convidar`) activates a pending user by flipping status
-- 'convidado' -> 'ativo', so any actor without `usuarios.desligar` got auth.forbidden.
-- This allows exactly that one transition for a `usuarios.convidar` holder; every other
-- status transition (anything touching 'desligado') still requires `usuarios.desligar`.
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
     and not (
       app.has_permission('usuarios.desligar')
       or (old.status = 'convidado' and new.status = 'ativo' and app.has_permission('usuarios.convidar'))
     ) then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  if (new.name is distinct from old.name or new.email is distinct from old.email)
     and not app.has_permission('usuarios.convidar') then
    raise exception 'auth.forbidden' using errcode = 'P0001';
  end if;

  return new;
end
$$;
