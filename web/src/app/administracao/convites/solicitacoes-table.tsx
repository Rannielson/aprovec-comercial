'use client';

import { useEffect, useRef, useState } from 'react';
import { useRouter } from 'next/navigation';
import type { SolicitacaoCadastro } from '@/lib/types';
import { SolicitacaoRow } from './solicitacao-row';

export function SolicitacoesTable({ pendentes }: { pendentes: SolicitacaoCadastro[] }) {
  const router = useRouter();
  const activeRef = useRef<string | null>(null);
  const [activeId, setActiveId] = useState<string | null>(null);

  useEffect(() => {
    if (!pendentes.some((s) =>
      (s.status === 'pendente' && s.processarAutomaticamente && !s.erroCodigo)
      || (s.status === 'aprovado' && s.processarAutomaticamente && !s.conviteEnviadoEm && !s.conviteErro))) return;
    const interval = setInterval(() => router.refresh(), 5000);
    return () => clearInterval(interval);
  }, [pendentes, router]);

  function start(id: string) {
    if (activeRef.current !== null) return false;
    activeRef.current = id;
    setActiveId(id);
    return true;
  }

  function finish(id: string) {
    if (activeRef.current !== id) return;
    activeRef.current = null;
    setActiveId(null);
  }

  return (
    <>
      <div style={{ padding: '12px 16px' }}>
        <button type="button" className="secondary" onClick={() => router.refresh()}>Atualizar cadastros</button>
      </div>
      {activeId && <p className="muted" role="status" style={{ padding: '12px 16px' }}>
        Processando uma solicitação. Aguarde o resultado antes de analisar a próxima.
      </p>}
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Nome</th>
              <th>CPF</th>
              <th>Contato</th>
              <th>Indicado por</th>
              <th>Recebido em</th>
              <th>Situação</th>
              <th>Ações</th>
            </tr>
          </thead>
          <tbody>
            {pendentes.map((s) => (
              <SolicitacaoRow key={s.id} solicitacao={s} activeId={activeId} onStart={start} onFinish={finish} />
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
