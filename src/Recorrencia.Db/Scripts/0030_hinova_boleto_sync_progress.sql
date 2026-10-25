alter table hinova_boleto_sync_jobs
  add column fase text not null default 'na_fila',
  add column pagamentos_consultados int not null default 0,
  add column pagamentos_processados int not null default 0,
  add column vencimentos_consultados int not null default 0,
  add column vencimentos_processados int not null default 0;
