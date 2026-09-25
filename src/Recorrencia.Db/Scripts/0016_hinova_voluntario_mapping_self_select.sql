-- hinova_voluntario_mapping's única policy (0014_hinova_integracao.sql) restringe QUALQUER
-- acesso a integracoes.gerenciar (Administrador) -- um consultor não consegue ver nem a própria
-- linha, o que a Fase 5 (indicação) precisa: saber se o próprio usuário já está vinculado à
-- Hinova, para decidir se mostra o link de indicação. Policy nova, só de SELECT, para a própria
-- linha -- insert/update/delete continuam exclusivos do Administrador via a policy já existente.
create policy hinova_voluntario_mapping_self_select on hinova_voluntario_mapping for select to app_user
  using (user_id = app.current_user_id());
