import { notFound } from 'next/navigation';
import { AppShell } from '../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import { Icon } from '../components/app-icon';
import { ConfirmarButton, ProvisionarButton } from './fechamento-actions';
import {
  currentCompetencia,
  formatCompetencia,
  formatMoney,
  formatPercent,
  previousCompetencia,
  ruleLabel,
  statusBadgeClass,
  statusLabels,
} from '@/lib/format';
import type { Commissions, Fechamento, Me } from '@/lib/types';

const STEP_LABELS = ['Apuração concluída', 'Confirmação', 'Provisionamento'];

export default async function FechamentoPage() {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const canSeeFechamento = me.permissions.some((p) => p.key === 'fechamento.visualizar');
  if (!canSeeFechamento) {
    return (
      <AppShell me={me} active="closing">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso ao fechamento.</p>
          </section>
        </div>
      </AppShell>
    );
  }
  const canConfirm = me.permissions.some((p) => p.key === 'fechamento.confirmar');
  const canProvision = me.permissions.some((p) => p.key === 'fechamento.provisionar');
  // Whoever confirms/provisiona a competência (only Administrador, in practice) sees the whole
  // operation's total here -- their own total is meaningless for that decision, since they're
  // closing everyone's books, not just their own.
  const isFullVisibility = me.permissions.find((p) => p.key === 'comissoes.visualizar')?.scope === 'tenant';

  const currentMonth = currentCompetencia();
  const month = previousCompetencia(currentMonth);

  const [fechamentos, monthCommissions, currentCommissions] = await Promise.all([
    apiFetch<Fechamento[]>('/fechamentos'),
    apiFetch<Commissions>(`/commissions/${month}`),
    apiFetch<Commissions>(`/commissions/${currentMonth}`),
  ]);
  const fechamento = fechamentos.find((f) => f.competencia === month) ?? null;
  const monthOwn = monthCommissions.beneficiaries.find((b) => b.userId === me.id) ?? null;
  const monthTotal = isFullVisibility ? monthCommissions.total : monthOwn?.total ?? 0;
  const currentTotal = isFullVisibility ? currentCommissions.total : currentCommissions.beneficiaries.find((b) => b.userId === me.id)?.total ?? 0;

  const status = fechamento?.status ?? null;
  const complete = [true, status === 'confirmado' || status === 'provisionado', status === 'provisionado'];
  const currentStepIndex = complete.findIndex((c) => !c);

  const monthBadgeClass = status ? statusBadgeClass(status) : 'badge warning';
  const monthBadgeLabel = status ? statusLabels[status] ?? status : 'Aguardando confirmação';

  function nextStep() {
    if (complete[2]) {
      return (
        <>
          <div className="next-icon success"><Icon name="check" /></div>
          <h2>Comissão provisionada</h2>
          <p>O provisionamento de <strong>{formatMoney(monthTotal)}</strong> foi registrado para {formatCompetencia(month)}.</p>
        </>
      );
    }
    if (complete[1]) {
      return (
        <>
          <div className="next-icon"><Icon name="receipt" /></div>
          <h2>Marque como provisionado</h2>
          <p>O demonstrativo de {formatCompetencia(month)} foi confirmado. Marque como provisionado quando o pagamento for encaminhado.</p>
          {canProvision ? (
            <ProvisionarButton competencia={month} />
          ) : (
            <p className="muted">Aguardando o administrador marcar como provisionado.</p>
          )}
        </>
      );
    }
    return (
      <>
        <div className="next-icon"><Icon name="receipt" /></div>
        <h2>Confira os valores</h2>
        <p>
          {isFullVisibility
            ? `Revise o total apurado de toda a operação antes de confirmar o demonstrativo de ${formatCompetencia(month)}.`
            : `Revise a comissão própria e a indicação direta antes de confirmar o demonstrativo de ${formatCompetencia(month)}.`}
        </p>
        {canConfirm ? (
          <ConfirmarButton competencia={month} />
        ) : (
          <p className="muted">Aguardando confirmação do administrador.</p>
        )}
      </>
    );
  }

  return (
    <AppShell me={me} active="closing">
      <div className="shell">
        <div className="page-heading">
          <p className="eyebrow">Painel do vendedor</p>
          <h1>Fechamento mensal</h1>
          <p className="muted">Confira seu demonstrativo e acompanhe as próximas etapas.</p>
        </div>

        <div className="closing-banner">
          <Icon name="info" />
          Você está conferindo {formatCompetencia(month)}. A competência de {formatCompetencia(currentMonth)} continua em apuração, com{' '}
          <strong>{formatMoney(currentTotal)}</strong> até o corte atual.
        </div>

        <div className="stepper">
          {STEP_LABELS.map((label, index) => (
            <div className={`step ${complete[index] ? 'complete' : ''} ${index === currentStepIndex ? 'current' : ''}`} key={label}>
              <span>{complete[index] ? <Icon name="check" /> : index + 1}</span>
              <div>
                <strong>{label}</strong>
                <small>
                  {index === 0
                    ? 'Valores consolidados'
                    : index === 1
                      ? complete[1] ? 'Confirmado' : 'Confira os valores'
                      : complete[2] ? 'Provisionado' : complete[1] ? 'Aguardando provisionamento' : 'Encaminhamento financeiro'}
                </small>
              </div>
            </div>
          ))}
        </div>

        <div className="closing-layout">
          <section className="card statement">
            <div className="panel-header">
              <div>
                <h2>Demonstrativo de comissão</h2>
                <p>{isFullVisibility ? 'Toda a operação' : me.name} · Competência {month}</p>
              </div>
              <span className={monthBadgeClass}>{monthBadgeLabel}</span>
            </div>
            {isFullVisibility ? (
              <div className="statement-row">
                <span><strong>Comissão apurada de todos os participantes</strong></span>
                <span>{monthCommissions.beneficiaries.length} {monthCommissions.beneficiaries.length === 1 ? 'beneficiário' : 'beneficiários'}</span>
                <strong>{formatMoney(monthCommissions.total)}</strong>
              </div>
            ) : monthOwn && monthOwn.byRule.length > 0 ? (
              monthOwn.byRule.map((rule) => (
                <div className="statement-row" key={`${rule.ruleType}-${rule.level}-${rule.groupId}-${rule.rate}`}>
                  <span><strong>{ruleLabel(rule)}</strong></span>
                  <span>{formatMoney(rule.base)} <small>× {formatPercent(rule.rate)}</small></span>
                  <strong>{formatMoney(rule.amount)}</strong>
                </div>
              ))
            ) : (
              <p className="muted" style={{ padding: '24px' }}>Nenhuma comissão apurada nesta competência.</p>
            )}
            <div className="statement-total">
              <div>
                <span>{isFullVisibility ? 'Total da operação no período' : 'Seu total no período'}</span>
                <small>Comissão apurada de {formatCompetencia(month)}</small>
              </div>
              <strong>{formatMoney(monthTotal)}</strong>
            </div>
            <div className="statement-foot">
              <Icon name="shield" />
              O demonstrativo mantém separados o valor recebido, a taxa e a comissão.
            </div>
          </section>
          <section className="card next-step">{nextStep()}</section>
        </div>

        <section className="card period-list">
          <div className="panel-header">
            <h2>Competências</h2>
            <span className="section-meta">Acompanhamento mensal</span>
          </div>
          <div className="period-row">
            <span><Icon name="calendar" /><strong>{formatCompetencia(currentMonth)}</strong></span>
            <span className="badge progress">Em apuração</span>
            <strong>{formatMoney(currentTotal)}</strong>
            <span className="muted">Disponível após o fechamento</span>
          </div>
          <div className="period-row">
            <span><Icon name="calendar" /><strong>{formatCompetencia(month)}</strong></span>
            <span className={monthBadgeClass}>{monthBadgeLabel}</span>
            <strong>{formatMoney(monthTotal)}</strong>
            <span className="muted">{complete[2] ? 'Provisionado' : 'Competência em exibição'}</span>
          </div>
        </section>
      </div>
    </AppShell>
  );
}
