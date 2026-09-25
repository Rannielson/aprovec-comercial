import { notFound } from 'next/navigation';
import { AppShell } from '../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import type { Me, PlanoCarreira } from '@/lib/types';

const CLASSIFICACAO_LABELS: Record<string, string> = {
  clt_interno: 'CLT Interno',
  clt_externo: 'CLT Externo',
  so_externo: 'Só Externo',
};

export default async function RemuneracaoPage() {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const has = (key: string) => me.permissions.some((p) => p.key === key);
  if (!has('regras_comissao.visualizar')) {
    return (
      <AppShell me={me} active="remuneracao">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso a planos de carreira.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const planos = await apiFetch<PlanoCarreira[]>('/planos-carreira');
  const canEdit = has('regras_comissao.editar');

  return (
    <AppShell me={me} active="remuneracao">
      <div className="shell">
        <div className="page-heading with-action">
          <div>
            <p className="eyebrow">Administração comercial</p>
            <h1>Planos de carreira</h1>
            <p className="muted">Classificação e regras de bonificação por consultor.</p>
          </div>
          {canEdit && (
            <a className="action-link" href="/administracao/remuneracao/novo">
              Novo plano
            </a>
          )}
        </div>
        <section className="card">
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Nome</th>
                  <th>Classificação</th>
                  <th>Status</th>
                  {canEdit && <th>Ações</th>}
                </tr>
              </thead>
              <tbody>
                {planos.length === 0 ? (
                  <tr>
                    <td colSpan={canEdit ? 4 : 3} className="empty-state">
                      <strong>Nenhum plano de carreira cadastrado</strong>
                    </td>
                  </tr>
                ) : (
                  planos.map((p) => (
                    <tr key={p.id}>
                      <td>{p.name}</td>
                      <td>{CLASSIFICACAO_LABELS[p.classificacao] ?? p.classificacao}</td>
                      <td>
                        <span className={p.status === 'ativo' ? 'badge progress' : 'badge neutral'}>{p.status}</span>
                      </td>
                      {canEdit && (
                        <td>
                          <a className="action-link" href={`/administracao/remuneracao/${p.id}/editar`}>
                            Editar
                          </a>
                        </td>
                      )}
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </section>
      </div>
    </AppShell>
  );
}
