import { notFound } from 'next/navigation';
import { currentHost } from '@/lib/api';
import { AuthBrandPanel } from '../components/auth-brand-panel';
import { ResetForm } from './reset-form';

export default async function ResetPasswordPage() {
  // Password-reset is a tenant-user flow, not a platform-admin one: 404 on an unknown host
  // (same as every other page) and also on the platform host, matching the plan's
  // host-restriction constraint.
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  return (
    <main className="login-shell">
      <section className="login-panel">
        <div className="login-card">
          <p className="eyebrow">Acesso</p>
          <h1>Redefinir senha</h1>
          <p className="muted">Informe o e-mail usado no acesso.</p>
          <ResetForm />
        </div>
      </section>
      <AuthBrandPanel />
    </main>
  );
}
