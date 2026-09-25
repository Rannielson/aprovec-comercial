import { notFound } from 'next/navigation';
import { ApiError, apiFetch } from '@/lib/api';
import type { TenantInfo } from '@/lib/types';
import { AuthBrandPanel } from '../components/auth-brand-panel';
import { LoginForm } from './login-form';

export default async function LoginPage() {
  let tenant: TenantInfo;
  try {
    tenant = await apiFetch<TenantInfo>('/tenant', { token: null });
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) notFound();
    throw error;
  }

  return (
    <main className="login-shell">
      <section className="login-panel">
        <div className="login-card">
          <p className="eyebrow">{tenant.platform ? 'Plataforma' : 'Acesso'}</p>
          <h1>{tenant.name}</h1>
          <p className="muted">Entre com seu e-mail e senha para acessar o painel.</p>
          <LoginForm platform={tenant.platform} />
        </div>
      </section>
      <AuthBrandPanel />
    </main>
  );
}
