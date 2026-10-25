import { Icon } from '../components/app-icon';
import { formatCompetencia, formatMoney, formatPercent } from '@/lib/format';
import { dashboardBreakdown } from '@/lib/payout-dashboard';
import type { LostCommission, Pagamento, PagamentoBeneficiario } from '@/lib/types';
import { PayoutStatement } from './payout-statement';

function ValueLine({ label, amount }: { label: string; amount: number }) {
  return <div className="dashboard-value-line"><span>{label}</span><strong>{formatMoney(amount)}</strong></div>;
}

export function ConsultantDashboard({ payout, person, lost }: {
  payout: Pagamento;
  person: PagamentoBeneficiario | null;
  lost: LostCommission;
}) {
  const breakdown = person ? dashboardBreakdown(person) : null;
  const isFinal = payout.status === 'confirmado' || payout.status === 'provisionado';
  const direct = breakdown?.direct ?? 0;
  const team = breakdown?.team ?? 0;
  const own = breakdown?.directRules.reduce((sum, rule) => sum + rule.amount, 0) ?? 0;
  const teamRules = breakdown?.teamRules ?? [];
  const standardRates = [0.02, 0.01];
  const otherRates = [...new Set(teamRules.map((rule) => rule.rate))]
    .filter((rate) => !standardRates.some((standard) => Math.round(standard * 10000) === Math.round(rate * 10000)))
    .sort((a, b) => b - a);
  const rateCards = [...standardRates, ...otherRates].map((rate) => {
    const rules = teamRules.filter((rule) => Math.round(rule.rate * 10000) === Math.round(rate * 10000));
    return {
      rate,
      base: rules.reduce((sum, rule) => sum + rule.base, 0),
      amount: rules.reduce((sum, rule) => sum + rule.amount, 0),
      label: rules[0]?.level ? `${rules[0].level}º nível da equipe` : 'Equipe',
    };
  });

  return (
    <>
      <section className="dashboard-hero" aria-label="Resumo do pagamento">
        <div>
          <p className="dashboard-hero-label">{isFinal ? 'Total confirmado a receber' : 'Prévia do total a receber'}</p>
          <strong className="dashboard-hero-total">{formatMoney(person?.total ?? 0)}</strong>
          <p>{formatCompetencia(payout.competencia)} · ganhos diretos + ganhos da equipe</p>
        </div>
        <span className={`badge ${isFinal ? 'recebido' : 'warning'}`}>{isFinal ? 'Confirmado' : 'Provisório'}</span>
      </section>

      <div className="dashboard-metrics">
        <section className="dashboard-metric dashboard-metric-direct" aria-labelledby="dashboard-direct-label">
          <div className="dashboard-metric-head"><Icon name="wallet" /><span>01 / Receita</span></div>
          <h2 id="dashboard-direct-label">Ganhos diretos</h2>
          <strong className="dashboard-metric-total">{formatMoney(direct)}</strong>
          <p>Sua carteira e os bônus aprovados neste fechamento.</p>
          <div className="dashboard-metric-lines">
            <ValueLine label="Carteira própria" amount={own} />
            <ValueLine label="Bônus de produção" amount={person?.placas ?? 0} />
            <ValueLine label="Bônus extra aprovado" amount={person?.extra ?? 0} />
          </div>
        </section>

        <section className="dashboard-metric dashboard-metric-team" aria-labelledby="dashboard-team-label">
          <div className="dashboard-metric-head"><Icon name="people" /><span>02 / Rede</span></div>
          <h2 id="dashboard-team-label">Ganhos da equipe</h2>
          <strong className="dashboard-metric-total">{formatMoney(team)}</strong>
          <p>Total das comissões geradas pelos níveis da sua árvore.</p>
          <div className="dashboard-metric-lines">
            <span className="dashboard-empty">Veja abaixo o valor separado por percentual.</span>
          </div>
        </section>

        <section className="dashboard-metric dashboard-metric-lost" aria-labelledby="dashboard-lost-label">
          <div className="dashboard-metric-head"><Icon name="info" /><span>03 / Atenção</span></div>
          <h2 id="dashboard-lost-label">Perdidos</h2>
          <strong className="dashboard-metric-total">{lost.available ? formatMoney(lost.total) : 'Ainda não apurado'}</strong>
          <p>Comissão potencial de boletos vencidos ou cancelados. Não entra no total a receber.</p>
          <div className="dashboard-metric-lines">
            {lost.available ? (
              <>
                <ValueLine label="Em atraso" amount={lost.emAtraso} />
                <ValueLine label="Cancelados" amount={lost.cancelados} />
                <span className="dashboard-empty">{lost.boletos} {lost.boletos === 1 ? 'boleto' : 'boletos'} sem comissão recebida.</span>
              </>
            ) : <span className="dashboard-empty">Os boletos por vencimento deste mês ainda precisam ser importados.</span>}
          </div>
        </section>
      </div>

      <section className="dashboard-rates" aria-labelledby="dashboard-rates-title">
        <div className="dashboard-rates-heading">
          <div><p className="eyebrow">Árvore comissionada</p><h2 id="dashboard-rates-title">Ganhos por percentual</h2></div>
          <span>Total da equipe <strong>{formatMoney(team)}</strong></span>
        </div>
        <div className="dashboard-rate-cards">
          {rateCards.map((card) => (
            <article className="dashboard-rate-card" key={card.rate}>
              <div className="dashboard-rate-top"><span>{card.label}</span><strong>{formatPercent(card.rate)}</strong></div>
              <div className="dashboard-rate-amount">{formatMoney(card.amount)}</div>
              <p>Base paga {formatMoney(card.base)} × {formatPercent(card.rate)}</p>
            </article>
          ))}
        </div>
      </section>

      <details className="dashboard-statement" id="demonstrativo">
        <summary><span><Icon name="receipt" /> Ver demonstrativo completo</span><Icon name="chevron" /></summary>
        <PayoutStatement payout={payout} isFullVisibility={false} viewerId={person?.userId ?? ''} canApproveExtra={false} />
      </details>
    </>
  );
}
