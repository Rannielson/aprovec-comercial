'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { confirmarFechamento, definirBonusExtra, provisionarFechamento } from './actions';

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

export function BonusExtraButton({ competencia, beneficiarioId, aprovado }: {
  competencia: string;
  beneficiarioId: string;
  aprovado: boolean;
}) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(definirBonusExtra, {});
  return (
    <form action={formAction} className="payout-extra-action">
      <input type="hidden" name="competencia" value={competencia} />
      <input type="hidden" name="beneficiarioId" value={beneficiarioId} />
      <input type="hidden" name="aprovado" value={aprovado ? '0' : '1'} />
      <button type="submit" className="secondary" disabled={pending}>
        {pending ? 'Salvando…' : aprovado ? 'Revogar aprovação' : 'Aprovar bônus extra'}
      </button>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      {state.message && <p className="success">{state.message}</p>}
    </form>
  );
}
