'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';
import type { HinovaBoletoSync } from '@/lib/types';

type SyncState = FormState & { jobId?: string };

export async function iniciarSyncBoletos(_: SyncState, formData: FormData): Promise<SyncState> {
  const competencia = String(formData.get('competencia') ?? '');
  if (!/^\d{4}-(0[1-9]|1[0-2])$/.test(competencia))
    return { error: 'Selecione um mês válido.' };
  try {
    const job = await apiFetch<HinovaBoletoSync>('/integracoes/hinova/boletos/syncs', {
      method: 'POST', body: { competencia },
    });
    return { message: `Sincronização de ${competencia.slice(5)}/${competencia.slice(0, 4)} iniciada.`, jobId: job.id };
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
}

export async function listarSyncBoletos(): Promise<HinovaBoletoSync[]> {
  return apiFetch<HinovaBoletoSync[]>('/integracoes/hinova/boletos/syncs');
}

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
