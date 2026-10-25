alter table hinova_boleto_sync_jobs
  add column pagamento_inicio date,
  add column pagamento_fim date,
  add column agendado_para date,
  add constraint hinova_sync_intervalo check (
    (pagamento_inicio is null and pagamento_fim is null and agendado_para is null)
    or (pagamento_inicio is not null and pagamento_fim is not null
        and pagamento_inicio <= pagamento_fim and agendado_para is not null)
  );

create unique index hinova_boleto_sync_diario_idx
  on hinova_boleto_sync_jobs (tenant_id, agendado_para)
  where agendado_para is not null;
