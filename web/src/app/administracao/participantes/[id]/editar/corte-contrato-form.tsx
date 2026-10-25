'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { salvarCorteContrato } from '../../../actions';

export function CorteContratoForm({ userId, cutoffDate }: { userId: string; cutoffDate: string | null }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(salvarCorteContrato, {});
  return (
    <form action={formAction} className="form">
      <input type="hidden" name="id" value={userId} />
      <label>
        Última data sem comissão da equipe
        <input type="date" name="cutoffDate" defaultValue={cutoffDate ?? ''} />
      </label>
      <p className="muted">Exemplo: 30/09/2026. Contratos de veículos a partir de 01/10/2026 geram a comissão dos níveis acima quando o boleto for pago. A carteira própria continua conforme o plano. Sem uma data, valem as regras normais para todos os contratos.</p>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      {state.message && <p role="status" className="success">{state.message}</p>}
      <div className="inline">
        <button type="submit" disabled={pending}>{pending ? 'Salvando…' : 'Salvar data de corte'}</button>
      </div>
    </form>
  );
}
