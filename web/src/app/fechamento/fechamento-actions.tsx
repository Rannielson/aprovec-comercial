'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { confirmarFechamento, provisionarFechamento } from './actions';

export function ConfirmarButton({ competencia }: { competencia: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(confirmarFechamento, {});
  return (
    <form action={formAction}>
      <input type="hidden" name="competencia" value={competencia} />
      {state.error && <p role="alert" className="error">{state.error}</p>}
      <button type="submit" className="full" disabled={pending}>{pending ? 'Confirmando…' : 'Confirmar demonstrativo'}</button>
    </form>
  );
}

export function ProvisionarButton({ competencia }: { competencia: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(provisionarFechamento, {});
  return (
    <form action={formAction}>
      <input type="hidden" name="competencia" value={competencia} />
      {state.error && <p role="alert" className="error">{state.error}</p>}
      <button type="submit" className="full" disabled={pending}>{pending ? 'Provisionando…' : 'Marcar como provisionado'}</button>
    </form>
  );
}
