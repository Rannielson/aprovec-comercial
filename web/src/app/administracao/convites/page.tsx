import { notFound } from 'next/navigation';
import { AppShell } from '../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import { formatDate } from '@/lib/format';
import type { Me, SolicitacaoCadastro } from '@/lib/types';
import { SolicitacaoActions } from './solicitacao-actions';

export default async function ConvitesPage() {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const canApprove = me.permissions.some((p) => p.key === 'usuarios.convidar') && me.permissions.some((p) => p.key === 'integracoes.gerenciar');
  if (!canApprove) {
    return (
      <AppShell me={me} active="convites">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso à fila de convites.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const pendentes = await apiFetch<SolicitacaoCadastro[]>('/solicitacoes-cadastro');

  return (
    <AppShell me={me} active="convites" convitesPendentes={pendentes.length}>
      <div className="shell">
        <div className="page-heading">
          <p className="eyebrow">Administração</p>
          <h1>Convites pendentes</h1>
          <p className="muted">Analise quem foi indicado, por quem, e aprove para cadastrar de verdade na Hinova.</p>
        </div>

        <section className="card">
          {pendentes.length === 0 ? (
            <p className="muted" style={{ padding: '24px' }}>Nenhuma solicitação pendente.</p>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Nome</th>
                    <th>CPF</th>
                    <th>Contato</th>
                    <th>Indicado por</th>
                    <th>Recebido em</th>
                    <th>Ações</th>
                  </tr>
                </thead>
                <tbody>
                  {pendentes.map((s) => (
                    <tr key={s.id}>
                      <td><strong>{s.nome}</strong></td>
                      <td>{s.cpf}</td>
                      <td>{s.celular}<br /><span className="muted">{s.email}</span></td>
                      <td>{s.indicadorNome}</td>
                      <td>{formatDate(s.criadoEm.slice(0, 10))}</td>
                      <td><SolicitacaoActions id={s.id} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      </div>
    </AppShell>
  );
}
