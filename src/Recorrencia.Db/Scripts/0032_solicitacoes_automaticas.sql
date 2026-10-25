alter table solicitacoes_cadastro
  add column processar_automaticamente boolean not null default false,
  add column processando_em timestamptz,
  add column erro_codigo text,
  add column convite_enviado_em timestamptz,
  add column convite_erro text;

create index solicitacoes_cadastro_automaticas_idx
  on solicitacoes_cadastro (criado_em)
  where status = 'pendente' and processar_automaticamente;
