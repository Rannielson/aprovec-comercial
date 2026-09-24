insert into modules (key, name, sort_order) values
  ('integracoes', 'Integrações', 7);

insert into permissions (key, module_key, name, scoped) values
  ('integracoes.gerenciar', 'integracoes', 'Gerenciar integrações', false);

insert into role_template_permissions (template_key, permission_key, scope) values
  ('administrador', 'integracoes.gerenciar', null);

create table hinova_credenciais (
  tenant_id uuid primary key references tenants (id),
  usuario_enc bytea not null,
  senha_enc bytea not null,
  token_sga_enc bytea not null,
  updated_at timestamptz not null default now(),
  updated_by uuid not null,
  foreign key (tenant_id, updated_by) references users (tenant_id, id)
);

create table hinova_voluntario_mapping (
  tenant_id uuid not null references tenants (id),
  user_id uuid not null,
  codigo_voluntario text not null,
  nome_hinova text not null,
  cpf_hinova text not null,
  mapped_at timestamptz not null default now(),
  mapped_by uuid not null,
  primary key (tenant_id, user_id),
  unique (tenant_id, codigo_voluntario),
  foreign key (tenant_id, user_id) references users (tenant_id, id),
  foreign key (tenant_id, mapped_by) references users (tenant_id, id)
);

do $$
declare
  t text;
begin
  foreach t in array array['hinova_credenciais', 'hinova_voluntario_mapping']
  loop
    execute format('alter table %I enable row level security', t);
    execute format('alter table %I force row level security', t);
    execute format(
      'create policy %I on %I as restrictive for all to app_user using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant())',
      t || '_tenant_isolation', t);
  end loop;
end
$$;

create policy hinova_credenciais_access on hinova_credenciais for all to app_user
  using ((select app.has_permission('integracoes.gerenciar')))
  with check ((select app.has_permission('integracoes.gerenciar')));

create policy hinova_voluntario_mapping_access on hinova_voluntario_mapping for all to app_user
  using ((select app.has_permission('integracoes.gerenciar')))
  with check ((select app.has_permission('integracoes.gerenciar')));

grant select, insert, update on hinova_credenciais to app_user;
grant select, insert, delete on hinova_voluntario_mapping to app_user;

-- provision_tenant only copies modules/role_template_permissions into tenant_modules/
-- role_permissions once, at tenant creation. Backfill both for every tenant that already
-- existed before this migration, so existing tenants' administradores get the feature too.
insert into tenant_modules (tenant_id, module_key)
select id, 'integracoes' from tenants
on conflict do nothing;

insert into role_permissions (role_id, tenant_id, permission_key, scope)
select r.id, r.tenant_id, 'integracoes.gerenciar', null
  from roles r
 where r.source_template_key = 'administrador'
on conflict do nothing;
