import { notFound } from 'next/navigation';
import { currentHost } from '@/lib/api';
import { AuthBrandPanel } from '../../components/auth-brand-panel';
import { SetPasswordForm } from './set-password-form';

export default async function SetPasswordPage({ params }: { params: Promise<{ token: string }> }) {
  // Password-set is a tenant-user flow, not a platform-admin one: 404 on an unknown host
  // (same as every other page) and also on the platform host, matching the plan's
  // host-restriction constraint.
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const { token } = await params;
  return (
    <main className="login-shell">
      <section className="login-panel">
        <div className="login-card">
          <p className="eyebrow">Acesso</p>
          <h1>Defina sua senha</h1>
          <p className="muted">Use de 10 a 128 caracteres.</p>
          <SetPasswordForm token={token} />
        </div>
      </section>
      <AuthBrandPanel />
    </main>
  );
}
