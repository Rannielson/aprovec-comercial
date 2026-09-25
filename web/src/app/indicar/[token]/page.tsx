import { notFound } from 'next/navigation';
import { ApiError, apiFetch } from '@/lib/api';
import type { ConviteLinkPublico } from '@/lib/types';
import { AuthBrandPanel } from '../../components/auth-brand-panel';
import { IndicacaoForm } from './indicacao-form';

export default async function IndicarPage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;

  let convite: ConviteLinkPublico;
  try {
    convite = await apiFetch<ConviteLinkPublico>(`/convite-links/${token}`, { token: null });
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) notFound();
    throw error;
  }

  return (
    <main className="login-shell">
      <section className="login-panel">
        <div className="login-card">
          <p className="eyebrow">Indicação</p>
          <h1>Você foi indicado por {convite.indicadorNome}</h1>
          <p className="muted">Preencha seus dados para solicitar seu cadastro em {convite.tenantNome}.</p>
          <IndicacaoForm token={token} />
        </div>
      </section>
      <AuthBrandPanel />
    </main>
  );
}
