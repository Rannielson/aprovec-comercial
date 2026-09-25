import Link from 'next/link';

export default function NotFound() {
  return (
    <main className="auth">
      <section className="card">
        <p className="eyebrow">Erro</p>
        <h1>Página não encontrada.</h1>
        <p className="muted">O endereço acessado não existe ou não está mais disponível.</p>
        <Link href="/">Voltar para o início</Link>
      </section>
    </main>
  );
}
