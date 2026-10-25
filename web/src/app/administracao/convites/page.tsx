import { notFound } from 'next/navigation';
import { AppShell } from '../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import type { Me, SolicitacaoCadastro } from '@/lib/types';
import { SolicitacoesTable } from './solicitacoes-table';

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
            <p className="muted">Seu perfil não inclui acesso ao histórico de cadastros.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const cadastros = await apiFetch<SolicitacaoCadastro[]>('/solicitacoes-cadastro');
  const exigemAtencao = cadastros.filter((s) => s.status === 'pendente' && (s.erroCodigo || !s.processarAutomaticamente)).length;

  return (
    <AppShell me={me} active="convites" convitesPendentes={exigemAtencao}>
      <div className="shell">
        <div className="page-heading">
          <p className="eyebrow">Administração</p>
          <h1>Últimos cadastros realizados</h1>
          <p className="muted">Os novos cadastros seguem automaticamente para a Hinova. Acompanhe o resultado e revise apenas os casos com divergência.</p>
        </div>

        <section className="card">
          {cadastros.length === 0 ? (
            <p className="muted" style={{ padding: '24px' }}>Nenhum cadastro recebido ainda.</p>
          ) : (
            <SolicitacoesTable pendentes={cadastros} />
          )}
        </section>
      </div>
    </AppShell>
  );
}
