'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { setPassword } from './actions';

export function SetPasswordForm({ token }: { token: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(setPassword, {});
  return (
    <form action={formAction} className="form">
      <input type="hidden" name="token" value={token} />
      <label>
        Nova senha
        <input name="password" type="password" autoComplete="new-password" minLength={10} maxLength={128} required />
      </label>
      <label>
        Confirme a senha
        <input name="confirmation" type="password" autoComplete="new-password" minLength={10} maxLength={128} required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Salvando…' : 'Salvar senha'}</button>
    </form>
  );
}
