create table modules (
  key text primary key,
  name text not null,
  sort_order int not null
);

create table permissions (
  key text primary key,
  module_key text not null references modules (key),
  name text not null,
  scoped boolean not null,
  check (key like module_key || '.%')
);

create table role_templates (
  key text primary key,
  name text not null,
  description text not null
);

create table role_template_permissions (
  template_key text not null references role_templates (key) on delete cascade,
  permission_key text not null references permissions (key),
  scope text check (scope in ('own', 'direct', 'subtree', 'tenant')),
  primary key (template_key, permission_key)
);

create function app.check_permission_scope() returns trigger
language plpgsql security definer set search_path = pg_catalog, public, app
as $$
declare
  v_scoped boolean;
begin
  select scoped into v_scoped from permissions where key = new.permission_key;
  if v_scoped and new.scope is null then
    raise exception 'role.scope_required' using errcode = 'P0001';
  end if;
  if not v_scoped and new.scope is not null then
    raise exception 'role.scope_not_allowed' using errcode = 'P0001';
  end if;
  return new;
end
$$;

create trigger role_template_permissions_scope before insert or update on role_template_permissions
  for each row execute function app.check_permission_scope();

insert into modules (key, name, sort_order) values
  ('carteira', 'Carteira', 1),
  ('comissoes', 'Comissões', 2),
  ('fechamento', 'Fechamento', 3),
  ('estrutura', 'Estrutura', 4),
  ('regras_comissao', 'Regras de comissão', 5),
  ('usuarios', 'Usuários e perfis', 6);

insert into permissions (key, module_key, name, scoped) values
  ('carteira.visualizar', 'carteira', 'Ver boletos', true),
  ('carteira.exportar', 'carteira', 'Exportar boletos', false),
  ('comissoes.visualizar', 'comissoes', 'Ver comissões', true),
  ('comissoes.exportar', 'comissoes', 'Exportar comissões', false),
  ('fechamento.visualizar', 'fechamento', 'Ver fechamentos', false),
  ('fechamento.confirmar', 'fechamento', 'Confirmar fechamento', false),
  ('fechamento.provisionar', 'fechamento', 'Marcar como provisionado', false),
  ('estrutura.visualizar', 'estrutura', 'Ver estrutura', true),
  ('estrutura.editar', 'estrutura', 'Alterar supervisores', false),
  ('regras_comissao.visualizar', 'regras_comissao', 'Ver regras de comissão', false),
  ('regras_comissao.editar', 'regras_comissao', 'Editar regras de comissão', false),
  ('usuarios.convidar', 'usuarios', 'Convidar usuários', false),
  ('usuarios.desligar', 'usuarios', 'Desligar usuários', false),
  ('usuarios.gerenciar_perfis', 'usuarios', 'Gerenciar perfis', false);

insert into role_templates (key, name, description) values
  ('consultor', 'Consultor', 'Vê a própria carteira e as próprias comissões.'),
  ('coordenador', 'Coordenador', 'Acompanha toda a operação da empresa.'),
  ('administrador', 'Administrador', 'Configura a empresa, perfis, estrutura e regras.');

insert into role_template_permissions (template_key, permission_key, scope) values
  ('consultor', 'carteira.visualizar', 'own'),
  ('consultor', 'comissoes.visualizar', 'own'),
  ('consultor', 'fechamento.visualizar', null),
  ('consultor', 'estrutura.visualizar', 'direct'),
  ('coordenador', 'carteira.visualizar', 'tenant'),
  ('coordenador', 'carteira.exportar', null),
  ('coordenador', 'comissoes.visualizar', 'tenant'),
  ('coordenador', 'comissoes.exportar', null),
  ('coordenador', 'fechamento.visualizar', null),
  ('coordenador', 'estrutura.visualizar', 'tenant'),
  ('coordenador', 'regras_comissao.visualizar', null);

insert into role_template_permissions (template_key, permission_key, scope)
select 'administrador', key, case when scoped then 'tenant' end from permissions;

grant select on modules, permissions, role_templates, role_template_permissions to app_user;
