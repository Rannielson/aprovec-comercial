-- Expose only the named descendants within the active commission plan's payable levels.
-- The existing path function validates that the requested beneficiary is in the caller's
-- commission scope before this security-definer function reads names through users RLS.
create function app.commission_source_people(p_competencia date, p_beneficiario uuid)
returns table (user_id uuid, name text, level int)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select u.id, u.name, paths.depth
    from app.commission_source_paths(p_competencia, array[p_beneficiario]) paths
    join users u on u.id = paths.descendant_id and u.tenant_id = app.current_tenant()
   where paths.ancestor_id = p_beneficiario
   order by paths.depth, u.name, u.id
$$;

grant execute on function app.commission_source_people(date, uuid) to app_user;
