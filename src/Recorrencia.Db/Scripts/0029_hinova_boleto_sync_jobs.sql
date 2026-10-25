create table hinova_boleto_sync_jobs (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  competencia date not null check (extract(day from competencia) = 1),
  requested_by uuid not null,
  status text not null default 'queued' check (status in ('queued', 'running', 'completed', 'failed')),
  requested_at timestamptz not null default now(),
  started_at timestamptz,
  finished_at timestamptz,
  total_boletos int,
  importados int,
  sem_vinculo int,
  conflito_multiplo_vendedor int,
  nao_pago int,
  vencimento_retornados int,
  vencimento_vinculados int,
  error_code text,
  foreign key (tenant_id, requested_by) references users (tenant_id, id),
  unique (tenant_id, id)
);

create unique index hinova_boleto_sync_active_idx on hinova_boleto_sync_jobs (tenant_id, competencia)
  where status in ('queued', 'running');
create index hinova_boleto_sync_recent_idx on hinova_boleto_sync_jobs (tenant_id, requested_at desc);

alter table hinova_boleto_sync_jobs enable row level security;
alter table hinova_boleto_sync_jobs force row level security;
create policy hinova_boleto_sync_tenant on hinova_boleto_sync_jobs
  as restrictive for all to app_user
  using (tenant_id = app.current_tenant()) with check (tenant_id = app.current_tenant());
create policy hinova_boleto_sync_select on hinova_boleto_sync_jobs
  for select to app_user using (app.has_permission('integracoes.gerenciar'));
create policy hinova_boleto_sync_insert on hinova_boleto_sync_jobs
  for insert to app_user with check (app.has_permission('integracoes.gerenciar') and requested_by = app.current_user_id());
grant select, insert on hinova_boleto_sync_jobs to app_user;
