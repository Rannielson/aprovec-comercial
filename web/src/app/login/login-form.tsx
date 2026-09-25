'use client';

import Link from 'next/link';
import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { login } from './actions';

export function LoginForm({ platform }: { platform: boolean }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(login, {});
  return (
    <form action={formAction} className="form">
      <label>
        E-mail
        <input name="email" type="email" autoComplete="username" required />
      </label>
      <label>
        Senha
        <input name="password" type="password" autoComplete="current-password" required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Entrando…' : 'Entrar'}</button>
      {!platform && <Link href="/redefinir-senha">Esqueci minha senha</Link>}
    </form>
  );
}
