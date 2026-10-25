'use client';

import { useState } from 'react';
import { Icon } from '../../../../components/app-icon';
import { formatCompetencia, formatDate, formatMoney } from '@/lib/format';
import type { ApuracaoBonusPlacas, PlanoCarreira } from '@/lib/types';

export function ApuracaoBonusCard({
  apuracao,
  plano,
}: {
  apuracao: ApuracaoBonusPlacas;
  plano: PlanoCarreira;
}) {
  const [detalhesAbertos, setDetalhesAbertos] = useState(false);
  const faixa = plano.faixas.find((item) =>
    apuracao.placasPagas >= item.quantidadeMin &&
    (item.quantidadeMax === null || apuracao.placasPagas <= item.quantidadeMax));
  const faixaLabel = faixa
    ? `${faixa.quantidadeMin}${faixa.quantidadeMax === null ? '+' : `–${faixa.quantidadeMax}`}`
    : 'Sem faixa';

  return (
    <div className="apuracao-result">
      <div className="apuracao-result-heading">
        <div>
          <p className="apuracao-overline">Resultado de {formatCompetencia(apuracao.competencia)}</p>
          <h3>{apuracao.participanteNome}</h3>
        </div>
        <div className="apuracao-statuses">
          {!apuracao.planoAtribuido && <span className="apuracao-status simulation">Simulação</span>}
          <span className={`apuracao-status ${apuracao.final ? 'final' : 'provisional'}`}>
            {apuracao.final ? 'Janela encerrada' : 'Provisório'}
          </span>
        </div>
      </div>

      <div className="apuracao-metrics">
        <div className="apuracao-metric">
          <span>Veículos únicos</span>
          <strong>{apuracao.placasPagas}</strong>
          <small>1 cadastro por veículo</small>
        </div>
        <div className="apuracao-metric">
          <span>Faixa {faixaLabel}</span>
          <strong>{formatMoney(apuracao.valorPorPlaca ?? 0)}</strong>
          <small>por veículo qualificado</small>
        </div>
        <div className="apuracao-metric apuracao-metric-total">
          <span>Bônus calculado</span>
          <strong>{formatMoney(apuracao.valorBonus)}</strong>
          <small>{apuracao.placasPagas} × {formatMoney(apuracao.valorPorPlaca ?? 0)}</small>
        </div>
      </div>

      <div className="apuracao-dates">
        <span><Icon name="calendar" /> Pagamentos até <strong>{formatDate(apuracao.apuradoAte)}</strong></span>
        <span>Prazo final <strong>{formatDate(apuracao.dataLimitePagamento)}</strong></span>
      </div>
      {!apuracao.planoAtribuido && (
        <p className="apuracao-notice">Este consultor não está vinculado a este plano. O valor acima é uma simulação.</p>
      )}

      {apuracao.placas.length > 0 ? (
        <div className="apuracao-details">
          <button
            type="button"
            className="apuracao-details-toggle"
            aria-expanded={detalhesAbertos}
            onClick={() => setDetalhesAbertos((abertos) => !abertos)}
          >
            <span className="apuracao-details-title">
              {detalhesAbertos ? 'Ocultar detalhamento' : 'Ver detalhamento'}
              <small>{apuracao.placasPagas} veículos</small>
            </span>
            <Icon name="chevron" />
          </button>
          {detalhesAbertos && (
            <div className="apuracao-details-body">
              <p>Um veículo entra uma vez na contagem, mesmo com mais de um boleto FECHAMENTO pago. Baixas com pendência entram quando há pagamento.</p>
              <div className="table-wrap">
                <table>
                  <thead><tr><th>Código veículo</th><th>Placa</th><th>Contrato</th><th>Primeiro pagamento</th><th>Nosso número</th></tr></thead>
                  <tbody>
                    {apuracao.placas.map((item) => (
                      <tr key={item.codigoVeiculo || item.placa}>
                        <td>{item.codigoVeiculo || '—'}</td>
                        <td>{item.placa || '—'}</td>
                        <td>{formatDate(item.dataContrato)}</td>
                        <td>{formatDate(item.dataPagamento)}</td>
                        <td>{item.nossoNumero}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      ) : (
        <p className="apuracao-empty">Nenhum veículo qualificado nesta competência.</p>
      )}
      <p className="apuracao-footnote">Esta consulta não lança nem confirma uma comissão.</p>
    </div>
  );
}
