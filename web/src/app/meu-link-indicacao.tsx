'use client';

import { useState } from 'react';

export function MeuLinkIndicacao({ url }: { url: string | null }) {
  const [copiado, setCopiado] = useState(false);

  if (!url) {
    return <p className="muted">Vincule-se a um código de voluntário na Hinova para gerar seu link de indicação.</p>;
  }

  async function copiar() {
    if (url) {
      await navigator.clipboard.writeText(url);
      setCopiado(true);
      setTimeout(() => setCopiado(false), 2000);
    }
  }

  return (
    <div className="referral-link">
      <input type="text" value={url || ''} readOnly aria-label="Seu link de indicação" />
      <button type="button" className="secondary" onClick={copiar}>{copiado ? 'Copiado!' : 'Copiar meu link'}</button>
    </div>
  );
}
