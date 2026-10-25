'use client';

import { useActionState, useEffect, useRef } from 'react';
import type { FormState } from '@/lib/form-state';
import { redefinirSenhaParticipante } from '../../../actions';

export function RedefinirSenhaForm({ userId }: { userId: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(redefinirSenhaParticipante, {});
  const formRef = useRef<HTMLFormElement>(null);

  useEffect(() => {
    if (state.message) formRef.current?.reset();
  }, [state]);

  return (
    <form action={formAction} className="form" ref={formRef}>
      <input type="hidden" name="id" value={userId} />
      <label>
        Nova senha
        <input type="password" name="password" minLength={10} maxLength={128} autoComplete="new-password" required />
      </label>
      <label>
        Confirme a nova senha
        <input type="password" name="confirmation" minLength={10} maxLength={128} autoComplete="new-password" required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      {state.message && <p role="status" className="success">{state.message}</p>}
      <div className="inline">
        <button type="submit" disabled={pending}>{pending ? 'Redefinindo…' : 'Redefinir senha'}</button>
      </div>
    </form>
  );
}
