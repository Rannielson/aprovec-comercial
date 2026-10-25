import { notFound } from 'next/navigation';
import { ApiError, platformApiFetch } from '@/lib/api';
import type { TenantInfo } from '@/lib/types';
import { AuthBrandPanel } from '../../components/auth-brand-panel';
import { LoginForm } from '../../login/login-form';
import { adminLogin } from './actions';

export default async function AdminLoginPage() {
  let platform: TenantInfo;
  try {
    platform = await platformApiFetch<TenantInfo>('/tenant', { token: null });
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) notFound();
    throw error;
  }

  return (
    <main className="login-shell">
      <section className="login-panel">
        <div className="login-card">
          <p className="eyebrow">Plataforma</p>
          <h1>{platform.name}</h1>
          <p className="muted">Entre com seu e-mail e senha para acessar o painel.</p>
          <LoginForm platform action={adminLogin} />
        </div>
      </section>
      <AuthBrandPanel />
    </main>
  );
}
