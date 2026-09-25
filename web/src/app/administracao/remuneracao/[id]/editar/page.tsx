import { notFound, redirect } from 'next/navigation';
import { AppShell } from '../../../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import type { Me, PlanoCarreira } from '@/lib/types';
import { PlanoCarreiraForm } from '../../plano-carreira-form';

export default async function EditarPlanoCarreiraPage({ params }: { params: Promise<{ id: string }> }) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const has = (key: string) => me.permissions.some((p) => p.key === key);
  if (!has('regras_comissao.editar')) redirect('/administracao/remuneracao');

  const { id } = await params;
  const planos = await apiFetch<PlanoCarreira[]>('/planos-carreira');
  const plano = planos.find((p) => p.id === id);
  if (!plano) notFound();

  return (
    <AppShell me={me} active="remuneracao">
      <div className="shell">
        <div className="page-heading">
          <div>
            <p className="eyebrow">Administração comercial</p>
            <h1>Editar plano de carreira</h1>
          </div>
          <a className="action-link" href="/administracao/remuneracao">
            Voltar
          </a>
        </div>
        <section className="card">
          <PlanoCarreiraForm plano={plano} />
        </section>
      </div>
    </AppShell>
  );
}
