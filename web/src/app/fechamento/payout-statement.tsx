import { formatCompetencia, formatDate, formatMoney, formatPercent, ruleLabel } from '@/lib/format';
import type { Pagamento, PagamentoBeneficiario } from '@/lib/types';
import { BonusExtraButton } from './fechamento-actions';

function ProductionMonth({ month }: { month: string }) {
  return <>{formatCompetencia(month)}</>;
}

function PersonBreakdown({ person, competencia, canApproveExtra, finalizado }: {
  person: PagamentoBeneficiario;
  competencia: string;
  canApproveExtra: boolean;
  finalizado: boolean;
}) {
  const bonus = person.bonusPlacas;
  return (
    <div className="payout-breakdown">
      <details className="payout-detail">
        <summary><span>Árvore comissionada <small>Carteira própria e indicações</small></span><strong>{formatMoney(person.arvore)}</strong></summary>
        <div className="payout-detail-body">
          {person.regrasArvore.length > 0 ? person.regrasArvore.map((rule) => (
            <div className="payout-calculation" key={`${rule.ruleType}-${rule.level}-${rule.groupId}-${rule.rate}`}>
              <span>{ruleLabel(rule)}</span>
              <span>{formatMoney(rule.base)} × {formatPercent(rule.rate)}</span>
              <strong>{formatMoney(rule.amount)}</strong>
            </div>
          )) : <p className="muted">Nenhum recebimento com comissão da árvore nesta competência.</p>}
        </div>
      </details>

      <details className="payout-detail">
        <summary><span>Bônus de produção <small>Veículos novos pagantes, sem duplicar boletos</small></span><strong>{formatMoney(person.placas)}</strong></summary>
        <div className="payout-detail-body">
          {bonus ? (
            <>
              <p className="payout-formula">
                Produção de <ProductionMonth month={bonus.competenciaProducao} /> · {bonus.quantidade} veículos únicos ×{' '}
                {formatMoney(bonus.valorUnitario ?? 0)} = <strong>{formatMoney(bonus.valor)}</strong>
              </p>
              <p className="muted">Plano {bonus.planoNome} · pagamentos considerados até {formatDate(bonus.dataLimitePagamento)}.</p>
              {bonus.placas.length > 0 && (
                <div className="table-wrap payout-vehicle-list">
                  <table>
                    <thead><tr><th>Veículo</th><th>Placa</th><th>Contrato</th><th>Primeiro pagamento</th><th>Nosso número</th></tr></thead>
                    <tbody>{bonus.placas.map((plate) => (
                      <tr key={`${plate.codigoVeiculo}-${plate.placa}`}>
                        <td>{plate.codigoVeiculo || '—'}</td>
                        <td>{plate.placa || '—'}</td>
                        <td>{formatDate(plate.dataContrato)}</td>
                        <td>{formatDate(plate.dataPagamento)}</td>
                        <td>{plate.nossoNumero}</td>
                      </tr>
                    ))}</tbody>
                  </table>
                </div>
              )}
            </>
          ) : (
            <p className="muted">{finalizado ? 'Não houve plano de bônus por placas aplicado neste fechamento.' : 'A bonificação será definida na confirmação do fechamento.'}</p>
          )}
        </div>
      </details>

      <details className="payout-detail">
        <summary><span>Bônus extra <small>Somente após aprovação do administrador</small></span><strong>{formatMoney(person.extra)}</strong></summary>
        <div className="payout-detail-body">
          {person.bonusExtra ? (
            <p className="payout-formula">{person.bonusExtra.planoNome} · aprovado em {new Date(person.bonusExtra.aprovadoEm).toLocaleDateString('pt-BR', { timeZone: 'America/Sao_Paulo' })} · <strong>{formatMoney(person.bonusExtra.valor)}</strong></p>
          ) : <p className="muted">Nenhum bônus extra aprovado para esta competência.</p>}
          {canApproveExtra && person.extraDisponivel !== null && !finalizado && (
            <div className="payout-extra-approval">
              <span>Valor configurado no plano: <strong>{formatMoney(person.extraDisponivel)}</strong></span>
              <BonusExtraButton competencia={competencia} beneficiarioId={person.userId} aprovado={person.bonusExtra !== null} />
            </div>
          )}
        </div>
      </details>
    </div>
  );
}

export function PayoutStatement({ payout, isFullVisibility, viewerId, canApproveExtra }: {
  payout: Pagamento;
  isFullVisibility: boolean;
  viewerId: string;
  canApproveExtra: boolean;
}) {
  const finalizado = payout.status === 'confirmado' || payout.status === 'provisionado';
  const people = isFullVisibility ? payout.beneficiarios : payout.beneficiarios.filter((p) => p.userId === viewerId);
  const displayedTotal = isFullVisibility ? payout.total : people[0]?.total ?? 0;
  const displayedTree = isFullVisibility ? payout.arvore : people[0]?.arvore ?? 0;
  const displayedBonus = isFullVisibility ? payout.placas : people[0]?.placas ?? 0;
  const displayedExtra = isFullVisibility ? payout.extra : people[0]?.extra ?? 0;

  return (
    <section className="card payout-statement">
      <div className="payout-heading">
        <div>
          <p className="eyebrow">Demonstrativo de pagamento</p>
          <h2>{isFullVisibility ? 'Total da operação' : 'Seu total a receber'}</h2>
          <p className="muted">Competência {formatCompetencia(payout.competencia)} · árvore + remuneração.</p>
        </div>
        <span className={`badge ${finalizado ? 'recebido' : 'warning'}`}>{finalizado ? 'Valor confirmado' : 'Valor provisório'}</span>
      </div>
      <div className="payout-total-band">
        <span>{finalizado ? 'Total confirmado para pagamento' : 'Total em conferência'}</span>
        <strong>{formatMoney(displayedTotal)}</strong>
      </div>
      <div className="payout-component-totals">
        <div><span>Árvore comissionada</span><strong>{formatMoney(displayedTree)}</strong></div>
        <span className="payout-plus">+</span>
        <div><span>Bônus de produção</span><strong>{formatMoney(displayedBonus)}</strong></div>
        <span className="payout-plus">+</span>
        <div><span>Bônus extra aprovado</span><strong>{formatMoney(displayedExtra)}</strong></div>
      </div>
      {!finalizado && !payout.previa && (
        <p className="payout-notice">A bonificação por placas ainda não foi incorporada a este valor. O total final será congelado na confirmação.</p>
      )}
      {payout.previa && <p className="payout-notice">Prévia calculada com os pagamentos da Hinova. A confirmação registra a memória de cálculo definitiva.</p>}

      {isFullVisibility ? (
        <div className="payout-people">
          <h3>Detalhamento por participante</h3>
          {people.map((person) => (
            <details className="payout-person" key={person.userId}>
              <summary><span>{person.name}<small>Árvore {formatMoney(person.arvore)} · Placas {formatMoney(person.placas)} · Extra {formatMoney(person.extra)}</small></span><strong>{formatMoney(person.total)}</strong></summary>
              <PersonBreakdown person={person} competencia={payout.competencia} canApproveExtra={canApproveExtra} finalizado={finalizado} />
            </details>
          ))}
        </div>
      ) : people[0] ? (
        <div className="payout-people">
          <h3>De onde veio seu pagamento</h3>
          <PersonBreakdown person={people[0]} competencia={payout.competencia} canApproveExtra={false} finalizado={finalizado} />
        </div>
      ) : <p className="muted">Nenhum valor disponível para esta competência.</p>}
    </section>
  );
}
