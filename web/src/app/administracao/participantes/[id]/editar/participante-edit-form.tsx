'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import type { HinovaMapeamento, UserNode } from '@/lib/types';
import { editarParticipante } from '../../../actions';

export function ParticipanteEditForm({ user, mapeamento }: { user: UserNode; mapeamento: HinovaMapeamento | null }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(editarParticipante, {});

  return (
    <form action={formAction} className="form">
      <input type="hidden" name="id" value={user.id} />
      <label>
        Nome
        <input type="text" name="name" defaultValue={user.name} required />
      </label>
      <label>
        E-mail
        <input type="email" name="email" defaultValue={user.email} required />
      </label>
      {mapeamento && (
        <>
          <input type="hidden" name="hasMapeamento" value="1" />
          <label>
            Código de voluntário (Hinova)
            <input type="text" name="codigoVoluntario" defaultValue={mapeamento.codigoVoluntario} required />
          </label>
          <label>
            Nome na Hinova
            <input type="text" name="nomeHinova" defaultValue={mapeamento.nomeHinova} required />
          </label>
          <label>
            CPF na Hinova
            <input type="text" name="cpfHinova" defaultValue={mapeamento.cpfHinova} required />
          </label>
        </>
      )}
      <label>
        Nova senha
        <input type="password" name="password" minLength={10} maxLength={128} autoComplete="new-password" />
      </label>
      <p className="muted">Deixe em branco para não alterar a senha.</p>
      {state.error && (
        <p role="alert" className="error">
          {state.error}
        </p>
      )}
      {state.message && <p className="success">{state.message}</p>}
      <div className="inline">
        <button type="submit" disabled={pending}>
          {pending ? 'Salvando…' : 'Salvar'}
        </button>
      </div>
    </form>
  );
}
