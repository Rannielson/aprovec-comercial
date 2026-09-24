'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function salvarCredenciaisHinova(_: FormState, formData: FormData): Promise<FormState> {
  try {
    await apiFetch('/integracoes/hinova/credenciais', {
      method: 'PUT',
      body: {
        usuario: String(formData.get('usuario') ?? ''),
        senha: String(formData.get('senha') ?? ''),
        tokenSga: String(formData.get('tokenSga') ?? ''),
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/configuracoes/integracoes');
  return { message: 'Credenciais salvas.' };
}

export async function vincularVoluntario(_: FormState, formData: FormData): Promise<FormState> {
  try {
    await apiFetch('/integracoes/hinova/mapeamentos', {
      method: 'POST',
      body: {
        userId: String(formData.get('userId') ?? ''),
        codigoVoluntario: String(formData.get('codigoVoluntario') ?? ''),
        nomeHinova: String(formData.get('nomeHinova') ?? ''),
        cpfHinova: String(formData.get('cpfHinova') ?? ''),
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/configuracoes/integracoes');
  return { message: 'Voluntário vinculado.' };
}

export async function desvincularVoluntario(_: FormState, formData: FormData): Promise<FormState> {
  try {
    await apiFetch(`/integracoes/hinova/mapeamentos/${String(formData.get('userId') ?? '')}`, { method: 'DELETE' });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/configuracoes/integracoes');
  return { message: 'Vínculo removido.' };
}
