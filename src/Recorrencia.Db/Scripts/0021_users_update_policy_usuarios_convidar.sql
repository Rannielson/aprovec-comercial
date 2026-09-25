alter policy users_update on users
  using ((select app.has_permission('estrutura.editar')) or (select app.has_permission('usuarios.desligar')) or (select app.has_permission('usuarios.convidar')))
  with check ((select app.has_permission('estrutura.editar')) or (select app.has_permission('usuarios.desligar')) or (select app.has_permission('usuarios.convidar')));
