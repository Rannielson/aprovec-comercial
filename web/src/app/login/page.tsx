import { notFound } from 'next/navigation';
import { ApiError, apiFetch } from '@/lib/api';
import type { TenantInfo } from '@/lib/types';
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
    <main className="auth">
      <section className="card">
        <p className="eyebrow">{tenant.platform ? 'Plataforma' : 'Recorrência comercial'}</p>
        <h1>{tenant.name}</h1>
        <LoginForm platform={tenant.platform} />
      </section>
    </main>
  );
}
