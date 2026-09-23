do $$
declare
  t text;
begin
  foreach t in array array[
    'users', 'hierarchy_paths', 'tenant_modules', 'roles', 'role_permissions', 'user_roles',
    'commission_groups', 'commission_group_members', 'commission_plans', 'commission_rules',
    'boletos', 'fechamentos', 'fechamento_detalhes', 'audit_log']
  loop
    execute format('alter table %I enable row level security', t);
    execute format('alter table %I force row level security', t);
    execute format(
      'create policy %I on %I as restrictive for all to app_user using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant())',
      t || '_tenant_isolation', t);
  end loop;
end
$$;

-- users
create policy users_select on users for select to app_user using (
  id = app.current_user_id()
  or (select app.scope_for('estrutura.visualizar')) = 'tenant'
  or id = any (app.visible_owner_ids((select app.scope_for('estrutura.visualizar'))))
);
create policy users_insert on users for insert to app_user
  with check ((select app.has_permission('usuarios.convidar')));
create policy users_update on users for update to app_user
  using ((select app.has_permission('estrutura.editar')) or (select app.has_permission('usuarios.desligar')))
  with check ((select app.has_permission('estrutura.editar')) or (select app.has_permission('usuarios.desligar')));

-- hierarchy_paths
create policy hierarchy_paths_select on hierarchy_paths for select to app_user using (
  (select app.scope_for('estrutura.visualizar')) = 'tenant'
  or (
    ancestor_id = any (app.visible_owner_ids((select app.scope_for('estrutura.visualizar'))))
    and descendant_id = any (app.visible_owner_ids((select app.scope_for('estrutura.visualizar'))))
  )
);

-- tenant_modules
create policy tenant_modules_select on tenant_modules for select to app_user using (true);

-- roles / role_permissions / user_roles
create policy roles_select on roles for select to app_user using (true);
create policy roles_write on roles for all to app_user
  using ((select app.has_permission('usuarios.gerenciar_perfis')))
  with check ((select app.has_permission('usuarios.gerenciar_perfis')));

create policy role_permissions_select on role_permissions for select to app_user using (true);
create policy role_permissions_write on role_permissions for all to app_user
  using ((select app.has_permission('usuarios.gerenciar_perfis')))
  with check ((select app.has_permission('usuarios.gerenciar_perfis')));

create policy user_roles_select on user_roles for select to app_user using (
  user_id = app.current_user_id() or (select app.has_permission('usuarios.gerenciar_perfis'))
);
create policy user_roles_write on user_roles for all to app_user
  using ((select app.has_permission('usuarios.gerenciar_perfis')))
  with check ((select app.has_permission('usuarios.gerenciar_perfis')));

-- commission tables
create policy commission_groups_select on commission_groups for select to app_user using (true);
create policy commission_groups_write on commission_groups for all to app_user
  using ((select app.has_permission('regras_comissao.editar')))
  with check ((select app.has_permission('regras_comissao.editar')));

create policy commission_group_members_select on commission_group_members for select to app_user using (true);
create policy commission_group_members_write on commission_group_members for all to app_user
  using ((select app.has_permission('regras_comissao.editar')))
  with check ((select app.has_permission('regras_comissao.editar')));

create policy commission_plans_select on commission_plans for select to app_user using (true);
create policy commission_plans_write on commission_plans for all to app_user
  using ((select app.has_permission('regras_comissao.editar')))
  with check ((select app.has_permission('regras_comissao.editar')));

create policy commission_rules_select on commission_rules for select to app_user using (true);
create policy commission_rules_write on commission_rules for all to app_user
  using ((select app.has_permission('regras_comissao.editar')))
  with check ((select app.has_permission('regras_comissao.editar')));

-- boletos
create policy boletos_select on boletos for select to app_user using (
  (select app.scope_for('carteira.visualizar')) = 'tenant'
  or participante_id = any (app.visible_owner_ids((select app.scope_for('carteira.visualizar'))))
);

-- fechamentos
create policy fechamentos_select on fechamentos for select to app_user
  using ((select app.has_permission('fechamento.visualizar')));
create policy fechamentos_insert on fechamentos for insert to app_user
  with check ((select app.has_permission('fechamento.confirmar')));
create policy fechamentos_update on fechamentos for update to app_user
  using ((select app.has_permission('fechamento.confirmar')) or (select app.has_permission('fechamento.provisionar')))
  with check ((select app.has_permission('fechamento.confirmar')) or (select app.has_permission('fechamento.provisionar')));

-- fechamento_detalhes
create policy fechamento_detalhes_select on fechamento_detalhes for select to app_user using (
  (select app.scope_for('comissoes.visualizar')) = 'tenant'
  or beneficiario_id = any (app.visible_owner_ids((select app.scope_for('comissoes.visualizar'))))
);
create policy fechamento_detalhes_insert on fechamento_detalhes for insert to app_user
  with check ((select app.has_permission('fechamento.confirmar')));

-- audit_log
create policy audit_log_insert on audit_log for insert to app_user
  with check (user_id = app.current_user_id());
