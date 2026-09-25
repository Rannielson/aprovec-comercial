'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { salvarCredenciaisHinova } from './actions';

export function CredenciaisForm() {
  const [state, formAction, pending] = useActionState<FormState, FormData>(salvarCredenciaisHinova, {});
  return (
    <form action={formAction} className="form">
      <label>
        Usuário
        <input name="usuario" type="text" autoComplete="off" required />
      </label>
      <label>
        Senha
        <input name="senha" type="password" autoComplete="off" required />
      </label>
      <label>
        Token da SGA
        <input name="tokenSga" type="password" autoComplete="off" required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      {state.message && <p className="success">{state.message}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Salvando…' : 'Salvar credenciais'}</button>
    </form>
  );
}
