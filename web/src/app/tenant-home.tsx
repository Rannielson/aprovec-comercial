import { apiFetch } from '@/lib/api';
import {
  currentCompetencia,
  formatCompetencia,
  formatMoney,
  formatPercent,
  isCompetencia,
  ruleLabel,
  statusLabels,
} from '@/lib/format';
import type { Commissions, Me } from '@/lib/types';

export async function TenantHome({ competencia }: { competencia?: string }) {
  const me = await apiFetch<Me>('/me');
  const month = isCompetencia(competencia) ? competencia : currentCompetencia();
  const canSeeCommissions = me.permissions.some((p) => p.key === 'comissoes.visualizar');
  const commissions = canSeeCommissions ? await apiFetch<Commissions>(`/commissions/${month}`) : null;

  return (
    <main className="shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">{me.tenant.name}</p>
          <h1>Olá, {me.name}</h1>
        </div>
        <form action="/logout" method="post">
          <button type="submit" className="secondary">Sair</button>
        </form>
      </header>

      {commissions ? (
        <section className="card">
          <div className="section-head">
            <div>
              <p className="eyebrow">Comissões · {formatCompetencia(month)}</p>
              <p className="total">{formatMoney(commissions.total)}</p>
            </div>
            <span className="badge">{statusLabels[commissions.status] ?? commissions.status}</span>
          </div>

          <form className="inline" method="get">
            <label>
              Competência
              <input type="month" name="competencia" defaultValue={month} />
            </label>
            <button type="submit" className="secondary">Ver</button>
          </form>

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
        </section>
      ) : (
        <section className="card">
          <p className="muted">Seu perfil não inclui acesso às comissões.</p>
        </section>
      )}
    </main>
  );
}
