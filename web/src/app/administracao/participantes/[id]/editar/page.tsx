import { notFound } from 'next/navigation';
import { AppShell } from '../../../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import type { HinovaMapeamento, Me, PlanoCarreira, UserNode } from '@/lib/types';
import { ParticipanteEditForm } from './participante-edit-form';

export default async function EditarParticipantePage({ params }: { params: Promise<{ id: string }> }) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const has = (key: string) => me.permissions.some((p) => p.key === key);
  if (!has('usuarios.convidar')) {
    return (
      <AppShell me={me} active="participantes">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui edição de participantes.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const { id } = await params;
  const users = await apiFetch<UserNode[]>('/users');
  const user = users.find((u) => u.id === id);
  if (!user) notFound();

  const canSeeHinova = has('integracoes.gerenciar');
  const mapeamentos = canSeeHinova ? await apiFetch<HinovaMapeamento[]>('/integracoes/hinova/mapeamentos') : [];
  const mapeamento = mapeamentos.find((m) => m.userId === id) ?? null;

  const canSeePlanos = has('estrutura.editar') && has('regras_comissao.visualizar');
  const planos = canSeePlanos ? await apiFetch<PlanoCarreira[]>('/planos-carreira') : [];

  return (
    <AppShell me={me} active="participantes">
      <div className="shell">
        <div className="page-heading">
          <div>
            <p className="eyebrow">Administração comercial</p>
            <h1>Editar participante</h1>
            <p className="muted">Atualize os dados de {user.name}.</p>
          </div>
          <a className="action-link" href="/administracao/participantes">
            Voltar
          </a>
        </div>
        <section className="card">
          <ParticipanteEditForm user={user} mapeamento={mapeamento} planos={planos} />
        </section>
      </div>
    </AppShell>
  );
}
