-- Mesmo padrão de app.find_login (0005_auth_functions.sql) para o mesmo problema: uma sessão
-- anônima não vê nenhuma linha de `users` via RLS (users_select não tem ramo anônimo), então um
-- join direto sob db.InTenantAsync(tenant, null, ...) devolveria zero linhas sempre, token válido
-- ou não. security definer resolve isso internamente sem abrir SELECT anônimo em `users` (que
-- exporia e-mail/status/supervisor_id de todo mundo, não só o nome de quem tem link).
create function app.resolve_convite_link(p_tenant uuid, p_token text)
returns table (user_id uuid, nome text)
language sql stable security definer set search_path = pg_catalog, public, app
as $$
  select u.id, u.name
    from convite_links cl
    join users u on u.id = cl.user_id
   where cl.tenant_id = p_tenant and cl.token = p_token and u.status = 'ativo'
$$;

grant execute on function app.resolve_convite_link(uuid, text) to app_user, app_superadmin;
