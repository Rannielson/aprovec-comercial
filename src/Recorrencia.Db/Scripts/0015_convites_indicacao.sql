create table solicitacoes_cadastro (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  indicador_user_id uuid not null,
  nome text not null,
  cpf text not null,
  celular text not null,
  email text not null,
  cep text not null,
  logradouro text not null,
  numero text not null,
  complemento text,
  bairro text not null,
  cidade text not null,
  estado text not null,
  status text not null default 'pendente' check (status in ('pendente', 'aprovado', 'rejeitado')),
  criado_em timestamptz not null default now(),
  resolvido_em timestamptz,
  resolvido_por uuid,
  user_id_resultante uuid,
  foreign key (tenant_id, indicador_user_id) references users (tenant_id, id),
  foreign key (tenant_id, resolvido_por) references users (tenant_id, id),
  foreign key (tenant_id, user_id_resultante) references users (tenant_id, id),
  check (status = 'pendente' or (resolvido_em is not null and resolvido_por is not null)),
  check (status <> 'aprovado' or user_id_resultante is not null)
);

create table convite_links (
  tenant_id uuid not null references tenants (id),
  user_id uuid not null,
  token text not null,
  criado_em timestamptz not null default now(),
  primary key (tenant_id, user_id),
  unique (token),
  foreign key (tenant_id, user_id) references users (tenant_id, id)
);

do $$
declare
  t text;
begin
  foreach t in array array['solicitacoes_cadastro', 'convite_links']
  loop
    execute format('alter table %I enable row level security', t);
    execute format('alter table %I force row level security', t);
    execute format(
      'create policy %I on %I as restrictive for all to app_user using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant())',
      t || '_tenant_isolation', t);
  end loop;
end
$$;

-- Qualquer visitante anônimo (dentro do tenant resolvido pelo host, sem usuário autenticado)
-- pode criar uma solicitação -- é exatamente isso que o formulário público faz. A leitura/edição
-- (fila de aprovação) exige as mesmas duas permissões que já protegem a criação manual de usuário
-- com vínculo Hinova.
create policy solicitacoes_cadastro_insert_publico on solicitacoes_cadastro for insert to app_user
  with check (true);
-- Postgres's CREATE POLICY takes exactly one command per FOR clause (no "for select, update"
-- comma list) -- select and update need their own policies, even though the condition is identical.
create policy solicitacoes_cadastro_select_admin on solicitacoes_cadastro for select to app_user
  using ((select app.has_permission('usuarios.convidar')) and (select app.has_permission('integracoes.gerenciar')));
create policy solicitacoes_cadastro_update_admin on solicitacoes_cadastro for update to app_user
  using ((select app.has_permission('usuarios.convidar')) and (select app.has_permission('integracoes.gerenciar')))
  with check ((select app.has_permission('usuarios.convidar')) and (select app.has_permission('integracoes.gerenciar')));

-- Qualquer visitante anônimo com o token em mãos pode ler o link (para mostrar "você foi
-- indicado por X" na página pública) -- o token em si (32 bytes aleatórios) é o segredo, não a
-- permissão de quem pergunta. Cada usuário gerencia sua própria linha.
create policy convite_links_self on convite_links for all to app_user
  using (user_id = app.current_user_id())
  with check (user_id = app.current_user_id());
create policy convite_links_select_publico on convite_links for select to app_user using (true);

grant select, insert, update on solicitacoes_cadastro to app_user;
grant select, insert on convite_links to app_user;
