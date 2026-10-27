import { AutoRefresh } from '../components/auto-refresh';
import { notFound } from 'next/navigation';
import { AppShell } from '../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import { Icon } from '../components/app-icon';
import { currentCompetencia, formatCompetencia, formatDate, formatMoney, formatPercent, ruleLabel, statusBadgeClass, statusLabels } from '@/lib/format';
import type { Commissions, CommissionDownline, CommissionEntry, Me } from '@/lib/types';

const SOURCE_TABS: { value: 'proprias' | 'indicacoes'; label: string; icon: 'wallet' | 'people' }[] = [
  { value: 'proprias', label: 'Vendas próprias', icon: 'wallet' },
  { value: 'indicacoes', label: 'Minha árvore', icon: 'people' },
];

export default async function ComissoesPage({
  searchParams,
}: {
  searchParams: Promise<{ source?: string }>;
}) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const canSeeCommissions = me.permissions.some((p) => p.key === 'comissoes.visualizar');
  if (!canSeeCommissions) {
    return (
      <AppShell me={me} active="commissions">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso às comissões.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const { source } = await searchParams;
  const activeSource = source === 'indicacoes' ? 'indicacoes' : 'proprias';
  const month = currentCompetencia();

  const [commissions, entries, downline] = await Promise.all([
    apiFetch<Commissions>(`/commissions/${month}`),
    apiFetch<CommissionEntry[]>(`/commissions/${month}/entries?beneficiaryId=${me.id}`),
    apiFetch<CommissionDownline[]>(`/commissions/${month}/downline?beneficiaryId=${me.id}`),
  ]);
  const own = commissions.beneficiaries.find((b) => b.userId === me.id) ?? null;

  const ownRule = own?.byRule.find((r) => r.ruleType === 'own') ?? null;
  const downlineBase = downline.reduce((sum, person) => sum + person.base, 0);
  const downlineAmount = downline.reduce((sum, person) => sum + person.amount, 0);
  const downlineRates = [...new Set(downline.map((person) => person.rate))]
    .filter((rate) => rate > 0).sort((a, b) => b - a);

  const ownEntries = entries
    .filter((e) => e.ruleType === 'own')
    .sort((a, b) => (b.pagoEm ?? '').localeCompare(a.pagoEm ?? ''));

  function hrefFor(sourceValue: string): string {
    return sourceValue === 'proprias' ? '/comissoes' : `/comissoes?source=${sourceValue}`;
  }

  return (
    <AppShell me={me} active="commissions">
      <AutoRefresh />
      <div className="shell">
        <div className="page-heading">
          <p className="eyebrow">Painel do vendedor</p>
          <h1>Minhas comissões</h1>
          <p className="muted">Veja como cada recebimento contribui para o seu resultado.</p>
        </div>

        <section className="commission-band">
          <div className="commission-total">
            <span className="overline">Comissão apurada · {formatCompetencia(month)}</span>
            <div className="hero-number">{formatMoney(own?.total ?? 0)}</div>
            <span className="subtle">Sobre pagamentos confirmados</span>
          </div>
          {own?.byRule.map((rule, index) => (
            <div className="commission-part" key={`${rule.ruleType}-${rule.level}-${rule.groupId}-${rule.rate}`}>
              <div className={index === 0 ? 'rate-badge' : 'rate-badge secondary'}>
                {new Intl.NumberFormat('pt-BR', { maximumFractionDigits: 2 }).format(rule.rate * 100)}
                <span>%</span>
              </div>
              <div>
                <span className="subtle">{ruleLabel(rule)}</span>
                <strong>{formatMoney(rule.amount)}</strong>
                <span className="formula">{formatMoney(rule.base)} · {formatPercent(rule.rate)}</span>
              </div>
            </div>
          ))}
          <span className={statusBadgeClass(commissions.status)}>{statusLabels[commissions.status] ?? commissions.status}</span>
        </section>

        <section className="card commission-panel">
          <div className="panel-header">
            <div>
              <h2>Origem dos ganhos</h2>
              <p>Uma base de cálculo clara para cada comissão</p>
            </div>
          </div>
          <div className="commission-tabs">
            {SOURCE_TABS.map((tab) => (
              <a key={tab.value} href={hrefFor(tab.value)} className={tab.value === activeSource ? 'selected' : ''}>
                <Icon name={tab.icon} />
                {tab.label} <span>{tab.value === 'proprias'
                  ? formatPercent(ownRule?.rate ?? 0)
                  : downlineRates.map(formatPercent).join(' · ') || '—'}</span>
              </a>
            ))}
          </div>

          {activeSource === 'proprias' ? (
            <>
              <div className="source-summary">
                <div>
                  <span>Base líquida recebida</span>
                  <strong>{formatMoney(ownRule?.base ?? 0)}</strong>
                </div>
                <span className="operator">×</span>
                <div>
                  <span>Sua taxa</span>
                  <strong>{formatPercent(ownRule?.rate ?? 0)}</strong>
                </div>
                <span className="operator">=</span>
                <div className="source-result">
                  <span>Comissão própria</span>
                  <strong>{formatMoney(ownRule?.amount ?? 0)}</strong>
                </div>
              </div>
              {ownEntries.length === 0 ? (
                <p className="muted" style={{ padding: '0 24px 24px' }}>Nenhum pagamento próprio nesta competência.</p>
              ) : (
                <div className="table-wrap">
                  <table>
                    <thead>
                      <tr>
                        <th>Associado</th>
                        <th>Pagamento</th>
                        <th className="number">Base líquida</th>
                        <th className="number">Comissão</th>
                      </tr>
                    </thead>
                    <tbody>
                      {ownEntries.slice(0, 8).map((entry) => (
                        <tr key={entry.boletoId}>
                          <td>
                            <strong>{entry.associadoNome}</strong>
                            {entry.placa && <span className="plate">{entry.placa}</span>}
                          </td>
                          <td>{entry.pagoEm ? formatDate(entry.pagoEm) : '—'}</td>
                          <td className="number">{formatMoney(entry.base)}</td>
                          <td className="number">{formatMoney(entry.amount)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
              <div className="panel-footer">
                <span>Exibindo {Math.min(8, ownEntries.length)} de {ownEntries.length} pagamentos que compõem a comissão.</span>
                <a href="/carteira?status=recebido" className="text-button">Ver todos os pagamentos</a>
              </div>
            </>
          ) : (
            <>
              <div className="source-summary">
                <div>
                  <span>Recebidos nas carteiras abaixo</span>
                  <strong>{formatMoney(downlineBase)}</strong>
                </div>
                <span className="operator">→</span>
                <div className="source-result">
                  <span>Sua comissão da árvore</span>
                  <strong>{formatMoney(downlineAmount)}</strong>
                </div>
              </div>
              {downline.length === 0 ? (
                <p className="muted" style={{ padding: '0 24px 24px' }}>Nenhuma pessoa nos níveis remunerados da sua árvore.</p>
              ) : (
                downline.map((person) => (
                  <div className="referral-detail-row" key={`${person.userId}-${person.level}`}>
                    <span className="row-avatar">{person.name.split(' ').slice(0, 2).map((p) => p[0]).join('')}</span>
                    <div>
                      <strong>{person.name}</strong>
                      <p>{person.level}º nível · {formatMoney(person.base)} recebidos · {formatPercent(person.rate)}
                        {person.pagamentos === 0 ? ' · Sem pagamentos nesta competência' : ` · ${person.pagamentos} pagamentos`}</p>
                    </div>
                    <strong>{formatMoney(person.amount)}</strong>
                  </div>
                ))
              )}
              <div className="referral-rule">
                <div>
                  <Icon name="people" />
                  <strong>Todos os níveis remunerados, inclusive sem pagamentos.</strong>
                </div>
                <p>
                  Cada carteira abaixo aparece com a taxa do seu nível. Quando não houve recebimentos no mês,
                  a base e a comissão ficam em {formatMoney(0)}.
                </p>
              </div>
            </>
          )}
        </section>
      </div>
    </AppShell>
  );
}
