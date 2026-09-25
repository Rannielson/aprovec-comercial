'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { createTenant } from './platform-actions';

export function CreateTenantForm({ defaultMonth }: { defaultMonth: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(createTenant, {});
  return (
    <form action={formAction} className="form">
      <label>
        Nome da empresa
        <input name="name" required />
      </label>
      <label>
        Endereço (subdomínio)
        <input name="slug" pattern="[a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?" placeholder="minha-empresa" required />
      </label>
      <label>
        Nome do administrador
        <input name="adminName" required />
      </label>
      <label>
        E-mail do administrador
        <input name="adminEmail" type="email" required />
      </label>
      <div className="inline">
        <label>
          Plano de comissão
          <select name="planTemplate" defaultValue="aprovec">
            <option value="aprovec">Modelo APROVEC</option>
            <option value="">Sem plano</option>
          </select>
        </label>
        <label>
          Início da vigência
          <input name="planMonth" type="month" defaultValue={defaultMonth} />
        </label>
      </div>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      {state.message && <p role="status" className="success">{state.message}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Criando…' : 'Criar empresa'}</button>
    </form>
  );
}
