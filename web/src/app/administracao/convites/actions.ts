'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function aprovarSolicitacao(_: FormState, formData: FormData): Promise<FormState> {
  const id = String(formData.get('id') ?? '');
  try {
    await apiFetch(`/solicitacoes-cadastro/${id}/aprovar`, { method: 'POST' });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/convites');
  return { message: 'Solicitação aprovada. O novo participante já recebeu o convite por e-mail.' };
}

export async function rejeitarSolicitacao(_: FormState, formData: FormData): Promise<FormState> {
  const id = String(formData.get('id') ?? '');
  try {
    await apiFetch(`/solicitacoes-cadastro/${id}/rejeitar`, { method: 'POST' });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/convites');
  return { message: 'Solicitação rejeitada.' };
}
