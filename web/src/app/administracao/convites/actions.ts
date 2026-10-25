'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export type VoluntarioExistente = {
  encontrado: boolean;
  codigoVoluntario: string | null;
  nome: string | null;
  email: string | null;
  jaVinculado: boolean;
};

export type ApprovalState = FormState & { existente?: VoluntarioExistente };

export async function aprovarSolicitacao(_: ApprovalState, formData: FormData): Promise<ApprovalState> {
  const id = String(formData.get('id') ?? '');
  try {
    await apiFetch(`/solicitacoes-cadastro/${id}/aprovar`, { method: 'POST', timeoutMs: 60_000 });
  } catch (error) {
    if (error instanceof ApiError) {
      if (error.code === 'solicitacao.cpf_ja_cadastrado') {
        try {
          const existente = await apiFetch<VoluntarioExistente>(`/solicitacoes-cadastro/${id}/voluntario-existente`);
          if (existente.encontrado) return { existente };
        } catch (lookupError) {
          if (lookupError instanceof ApiError) return { error: messageFor(lookupError.code) };
          throw lookupError;
        }
      }
      return { error: messageFor(error.code) };
    }
    throw error;
  }
  revalidatePath('/administracao/convites');
  return { message: 'Solicitação aprovada. O novo participante já recebeu o convite por e-mail.' };
}

export async function vincularVoluntarioExistente(_: FormState, formData: FormData): Promise<FormState> {
  const id = String(formData.get('id') ?? '');
  const codigoVoluntario = String(formData.get('codigoVoluntario') ?? '');
  try {
    await apiFetch(`/solicitacoes-cadastro/${id}/vincular-existente`, {
      method: 'POST', body: { codigoVoluntario }, timeoutMs: 60_000,
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/convites');
  return { message: 'Participante vinculado à Hinova. Convite para definir a senha enviado por e-mail.' };
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

export async function reprocessarSolicitacao(_: FormState, formData: FormData): Promise<FormState> {
  const id = String(formData.get('id') ?? '');
  try {
    await apiFetch(`/solicitacoes-cadastro/${id}/reprocessar`, { method: 'POST' });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/convites');
  return { message: 'Cadastro reenviado para processamento.' };
}
