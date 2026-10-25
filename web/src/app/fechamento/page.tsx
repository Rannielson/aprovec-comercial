import { notFound, redirect } from 'next/navigation';
import { cookies } from 'next/headers';
import { AppShell } from '../app-shell';
import { ApiError, apiFetch, currentHost } from '@/lib/api';
import { Icon } from '../components/app-icon';
import { ConfirmarButton, ProvisionarButton } from './fechamento-actions';
import { PayoutStatement } from './payout-statement';
import { ConsultantDashboard } from './consultant-dashboard';
import { AutoRefresh } from '../components/auto-refresh';
import { LiveFilterInput } from '../components/live-filter-input';
import { messageFor } from '@/lib/errors';
import {
  currentCompetencia,
  formatCompetencia,
  formatMoney,
  isCompetencia,
  previousCompetencia,
  statusBadgeClass,
  statusLabels,
} from '@/lib/format';
import type { Commissions, Fechamento, LostCommission, Me, Pagamento } from '@/lib/types';
import { SESSION_COOKIE } from '@/lib/session';

const STEP_LABELS = ['Apuração concluída', 'Confirmação', 'Provisionamento'];

export default async function FechamentoPage({ searchParams }: {
  searchParams: Promise<{ competencia?: string }>;
}) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  if (!(await cookies()).has(SESSION_COOKIE)) redirect('/login');

  let me: Me;
  try {
    me = await apiFetch<Me>('/me');
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) redirect('/login');
    throw error;
  }
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
  const lastClosedMonth = previousCompetencia(currentMonth);
  const latestMonth = canConfirm ? lastClosedMonth : currentMonth;
  const requested = (await searchParams).competencia;
  const month = isCompetencia(requested) && requested <= latestMonth ? requested : latestMonth;

  const [fechamentos, currentCommissions] = await Promise.all([
    apiFetch<Fechamento[]>('/fechamentos'),
    canConfirm ? apiFetch<Commissions>(`/commissions/${currentMonth}`) : Promise.resolve(null),
  ]);
  const fechamento = fechamentos.find((f) => f.competencia === month) ?? null;
  const closed = fechamento?.status === 'confirmado' || fechamento?.status === 'provisionado';
  let previewError: string | null = null;
  let payout: Pagamento;
  try {
    payout = await apiFetch<Pagamento>(`/payouts/${month}${canConfirm && !closed ? '/preview' : ''}`,
      { timeoutMs: 180_000 });
  } catch (error) {
    if (!(error instanceof ApiError) || !canConfirm || closed) throw error;
    previewError = messageFor(error.code);
    payout = await apiFetch<Pagamento>(`/payouts/${month}`);
  }
  const monthTotal = isFullVisibility ? payout.total : payout.beneficiarios.find((b) => b.userId === me.id)?.total ?? 0;
  const currentTotal = currentCommissions
    ? isFullVisibility ? currentCommissions.total : currentCommissions.beneficiaries.find((b) => b.userId === me.id)?.total ?? 0
    : 0;

  if (!canConfirm) {
    const person = payout.beneficiarios.find((b) => b.userId === me.id) ?? null;
    const lost = await apiFetch<LostCommission>(`/commissions/${month}/lost`);
    return (
      <AppShell me={me} active="closing">
        <AutoRefresh />
        <div className="shell consultant-dashboard-shell">
          <div className="page-heading dashboard-heading">
            <div>
              <p className="eyebrow">Painel do consultor</p>
              <h1>Dashboard</h1>
              <p className="muted">Veja quanto você vai receber e de onde vem cada valor.</p>
            </div>
            <form method="get" className="closing-period-picker">
              <label>Competência do pagamento
                <LiveFilterInput key={month} type="month" name="competencia" initialValue={month} max={currentMonth} />
              </label>
              <button type="submit" className="secondary">Ver mês</button>
            </form>
          </div>
          <ConsultantDashboard payout={payout} person={person} lost={lost} />
        </div>
      </AppShell>
    );
  }

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
        {previewError ? (
          <p role="alert" className="error">A bonificação por placas precisa ser conferida antes da confirmação: {previewError}</p>
        ) : canConfirm ? (
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

        <form method="get" className="closing-period-picker">
          <label>Competência do pagamento
            <input type="month" name="competencia" defaultValue={month} max={lastClosedMonth} />
          </label>
          <button type="submit" className="secondary">Ver demonstrativo</button>
        </form>

        <div className="closing-banner">
          <Icon name="info" />
          Você está conferindo {formatCompetencia(month)}. A comissão da árvore de {formatCompetencia(currentMonth)} continua em apuração, com{' '}
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
          <PayoutStatement payout={payout} isFullVisibility={isFullVisibility} viewerId={me.id} canApproveExtra={canConfirm && !closed} />
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
