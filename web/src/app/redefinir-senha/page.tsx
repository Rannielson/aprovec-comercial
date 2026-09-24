import { ResetForm } from './reset-form';

export default function ResetPasswordPage() {
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
