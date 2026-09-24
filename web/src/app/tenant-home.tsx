import { AppShell } from './app-shell';
import { apiFetch } from '@/lib/api';
import {
  currentCompetencia,
  formatCompetencia,
  formatMoney,
  formatPercent,
  isCompetencia,
  previousCompetencia,
  ruleLabel,
  statusBadgeClass,
  statusLabels,
} from '@/lib/format';
import type { Commissions, Me } from '@/lib/types';

export async function TenantHome({ competencia }: { competencia?: string }) {
  const me = await apiFetch<Me>('/me');
  const month = isCompetencia(competencia) ? competencia : currentCompetencia();
  const canSeeCommissions = me.permissions.some((p) => p.key === 'comissoes.visualizar');
  const [commissions, previous] = await Promise.all([
    canSeeCommissions ? apiFetch<Commissions>(`/commissions/${month}`) : Promise.resolve(null),
    canSeeCommissions
      ? apiFetch<Commissions>(`/commissions/${previousCompetencia(month)}`).catch(() => null)
      : Promise.resolve(null),
  ]);
  const own = commissions?.beneficiaries.find((b) => b.userId === me.id) ?? null;
  const previousOwn = previous?.beneficiaries.find((b) => b.userId === me.id) ?? null;

  return (
    <AppShell me={me} active="overview">
      <div className="shell">
        <div className="page-heading">
          <p className="eyebrow">Painel do vendedor</p>
          <h1>Minha recorrência</h1>
          <p className="muted">Acompanhe sua carteira e a origem de cada comissão.</p>
        </div>

        {commissions ? (
          <>
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

            <form className="inline" method="get">
              <label>
                Competência
                <input type="month" name="competencia" defaultValue={month} />
              </label>
              <button type="submit" className="secondary">Ver</button>
            </form>

            {previous && previousOwn && previousOwn.total > 0 && (
              <section className="closing-card">
                <p className="closing-eyebrow">Fechamento anterior</p>
                <h2>{formatCompetencia(previousCompetencia(month))}</h2>
                <p className="closing-amount">{formatMoney(previousOwn.total)}</p>
                <span className={statusBadgeClass(previous.status)}>{statusLabels[previous.status] ?? previous.status}</span>
              </section>
            )}

            {commissions.beneficiaries.length === 0 ? (
              <p className="muted">Nenhuma comissão nesta competência.</p>
            ) : (
              <div className="table-wrap">
                <table>
                  <thead>
                    <tr>
                      <th>Pessoa</th>
                      <th>Origem</th>
                      <th className="number">Taxa</th>
                      <th className="number">Base paga</th>
                      <th className="number">Comissão</th>
                    </tr>
                  </thead>
                  {commissions.beneficiaries.map((person) => (
                    <tbody key={person.userId}>
                      {person.byRule.map((rule, index) => (
                        <tr key={`${rule.ruleType}-${rule.level}-${rule.groupId}-${rule.rate}`}>
                          <td>{index === 0 ? (person.name ?? 'Participante') : ''}</td>
                          <td>{ruleLabel(rule)}</td>
                          <td className="number">{formatPercent(rule.rate)}</td>
                          <td className="number">{formatMoney(rule.base)}</td>
                          <td className="number">{formatMoney(rule.amount)}</td>
                        </tr>
                      ))}
                      {person.byRule.length > 1 && (
                        <tr className="subtotal">
                          <td />
                          <td colSpan={3}>Total</td>
                          <td className="number">{formatMoney(person.total)}</td>
                        </tr>
                      )}
                    </tbody>
                  ))}
                </table>
              </div>
            )}
          </>
        ) : (
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso às comissões.</p>
          </section>
        )}
      </div>
    </AppShell>
  );
}
