'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { aprovarSolicitacao, rejeitarSolicitacao } from './actions';

export function SolicitacaoActions({ id }: { id: string }) {
  const [approveState, approveAction, approvePending] = useActionState<FormState, FormData>(aprovarSolicitacao, {});
  const [rejectState, rejectAction, rejectPending] = useActionState<FormState, FormData>(rejeitarSolicitacao, {});

  return (
    <div className="inline">
      <form action={approveAction}>
        <input type="hidden" name="id" value={id} />
        <button type="submit" disabled={approvePending || rejectPending}>{approvePending ? 'Aprovando…' : 'Aprovar'}</button>
      </form>
      <form action={rejectAction}>
        <input type="hidden" name="id" value={id} />
        <button type="submit" className="secondary" disabled={approvePending || rejectPending}>{rejectPending ? 'Rejeitando…' : 'Rejeitar'}</button>
      </form>
      {approveState.error && <p role="alert" className="error">{approveState.error}</p>}
      {rejectState.error && <p role="alert" className="error">{rejectState.error}</p>}
    </div>
  );
}
