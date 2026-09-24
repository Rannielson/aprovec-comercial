'use client';

import Link from 'next/link';

export default function Error({ retry }: { error: Error & { digest?: string }; retry: () => void }) {
  return (
    <main className="auth">
      <section className="card">
        <p className="eyebrow">Erro</p>
        <h1>Algo deu errado.</h1>
        <p className="muted">Não foi possível concluir esta ação. Tente novamente.</p>
        <button type="button" onClick={() => retry()}>
          Tentar de novo
        </button>
        <Link href="/">Voltar para o início</Link>
      </section>
    </main>
  );
}
