'use client';

import Link from 'next/link';
import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { requestReset } from './actions';

export function ResetForm() {
  const [state, formAction, pending] = useActionState<FormState, FormData>(requestReset, {});
  return (
    <form action={formAction} className="form">
      <label>
        E-mail
        <input name="email" type="email" autoComplete="username" required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      {state.message && <p role="status" className="success">{state.message}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Enviando…' : 'Enviar link'}</button>
      <Link href="/login">Voltar para o login</Link>
    </form>
  );
}
