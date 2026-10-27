alter table hinova_boleto_sync_jobs
  add column somente_pagamentos boolean not null default false,
  add constraint hinova_sync_modo check (not (somente_pagamentos and somente_vencimentos));
