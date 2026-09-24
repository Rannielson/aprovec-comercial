import { SetPasswordForm } from './set-password-form';

export default async function SetPasswordPage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  return (
    <main className="auth">
      <section className="card">
        <p className="eyebrow">Acesso</p>
        <h1>Defina sua senha</h1>
        <p className="muted">Use de 10 a 128 caracteres.</p>
        <SetPasswordForm token={token} />
      </section>
    </main>
  );
}
