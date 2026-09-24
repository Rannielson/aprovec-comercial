'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import type { HinovaVoluntario, UserSummary } from '@/lib/types';
import { desvincularVoluntario, vincularVoluntario } from './actions';

export function VincularForm({ voluntario, usuarios }: { voluntario: HinovaVoluntario; usuarios: UserSummary[] }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(vincularVoluntario, {});
  return (
    <form action={formAction} className="inline">
      <input type="hidden" name="codigoVoluntario" value={voluntario.codigo} />
      <input type="hidden" name="nomeHinova" value={voluntario.nome} />
      <input type="hidden" name="cpfHinova" value={voluntario.cpf} />
      <label>
        Usuário APROVEC
        <select name="userId" required defaultValue="">
          <option value="" disabled>Selecione</option>
          {usuarios.map((u) => (
            <option key={u.id} value={u.id}>{u.name}</option>
          ))}
        </select>
      </label>
      <button type="submit" disabled={pending}>{pending ? 'Vinculando…' : 'Vincular'}</button>
      {state.error && <p role="alert" className="error">{state.error}</p>}
    </form>
  );
}

export function DesvincularForm({ userId }: { userId: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(desvincularVoluntario, {});
  return (
    <form action={formAction}>
      <input type="hidden" name="userId" value={userId} />
      <button type="submit" className="secondary" disabled={pending}>{pending ? 'Removendo…' : 'Desvincular'}</button>
      {state.error && <p role="alert" className="error">{state.error}</p>}
    </form>
  );
}
