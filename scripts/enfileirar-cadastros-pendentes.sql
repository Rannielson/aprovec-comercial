-- Executar uma única vez por tenant com:
-- psql -X -v ON_ERROR_STOP=1 -v tenant_slug=sales -f scripts/enfileirar-cadastros-pendentes.sql
--
-- Apenas cadastros antigos feitos pelos links de indicação entram na fila automática.
-- O indicador_user_id não é alterado: o worker usa esse vínculo para criar a árvore.
-- Cadastros já processados, em processamento ou marcados para revisão ficam intocados.

\set ON_ERROR_STOP on

begin;

with enfileiradas as (
  update solicitacoes_cadastro as s
     set processar_automaticamente = true
   where s.tenant_id = (select id from tenants where slug = :'tenant_slug')
     and s.status = 'pendente'
     and not s.processar_automaticamente
     and s.processando_em is null
     and s.erro_codigo is null
     and exists (
       select 1
         from users as indicador
         join hinova_voluntario_mapping as vinculo
           on vinculo.tenant_id = indicador.tenant_id
          and vinculo.user_id = indicador.id
        where indicador.tenant_id = s.tenant_id
          and indicador.id = s.indicador_user_id
     )
  returning s.id, s.indicador_user_id
)
select count(*) as cadastros_enfileirados from enfileiradas;

commit;
