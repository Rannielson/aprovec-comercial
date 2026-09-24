import { notFound } from 'next/navigation';
import { currentHost } from '@/lib/api';
import { ResetForm } from './reset-form';

export default async function ResetPasswordPage() {
  // Password-reset is a tenant-user flow, not a platform-admin one: 404 on an unknown host
  // (same as every other page) and also on the platform host, matching the plan's
  // host-restriction constraint.
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  return (
    <main className="auth">
      <section className="card">
        <p className="eyebrow">Acesso</p>
        <h1>Redefinir senha</h1>
        <p className="muted">Informe o e-mail usado no acesso.</p>
        <ResetForm />
      </section>
    </main>
  );
}
