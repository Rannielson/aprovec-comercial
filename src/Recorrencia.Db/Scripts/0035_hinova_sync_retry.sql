alter table hinova_boleto_sync_jobs
  add column tentativas integer not null default 0,
  add column retry_after timestamptz;
