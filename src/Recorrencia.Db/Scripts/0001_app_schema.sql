alter default privileges for role app_owner revoke execute on functions from public;
alter default privileges for role app_owner in schema public
  grant select, insert, update, delete on tables to app_superadmin;

create schema app;
revoke all on schema app from public;
grant usage on schema app to app_user, app_superadmin;
grant usage on schema public to app_user, app_superadmin;

create function app.current_tenant() returns uuid
language sql stable
as $$ select nullif(current_setting('app.tenant_id', true), '')::uuid $$;

create function app.current_user_id() returns uuid
language sql stable
as $$ select nullif(current_setting('app.user_id', true), '')::uuid $$;

create function app.touch_updated_at() returns trigger
language plpgsql
as $$
begin
  new.updated_at := now();
  return new;
end
$$;

grant execute on function app.current_tenant(), app.current_user_id() to app_user, app_superadmin;
