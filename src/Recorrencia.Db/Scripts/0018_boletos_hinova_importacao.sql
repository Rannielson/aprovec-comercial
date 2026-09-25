-- Lets a real Hinova boleto import be re-run safely: each imported row remembers the Hinova
-- "nosso_numero" it came from, so re-importing the same period updates instead of duplicating.
-- Null for the dev-seed rows (synthetic associado_ref, no real Hinova boleto behind them).
alter table boletos add column hinova_nosso_numero text;

create unique index boletos_hinova_nosso_numero_idx
  on boletos (tenant_id, hinova_nosso_numero)
  where hinova_nosso_numero is not null;

-- boletos had only a select grant/policy -- every row so far came from DevSeed/the superuser
-- connection, bypassing both entirely. The Hinova boleto import runs as app_user, through the
-- same permission that already gates every other write this integration makes.
grant insert, update on boletos to app_user;

create policy boletos_write on boletos for insert to app_user
  with check ((select app.has_permission('integracoes.gerenciar')));
create policy boletos_update on boletos for update to app_user
  using ((select app.has_permission('integracoes.gerenciar')))
  with check ((select app.has_permission('integracoes.gerenciar')));
