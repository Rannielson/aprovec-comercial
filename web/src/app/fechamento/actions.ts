'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function confirmarFechamento(_: FormState, formData: FormData): Promise<FormState> {
  const competencia = String(formData.get('competencia') ?? '');
  try {
    await apiFetch(`/fechamentos/${competencia}/confirm`, { method: 'POST', timeoutMs: 180_000 });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/fechamento');
  return { message: 'Demonstrativo confirmado.' };
}

export async function definirBonusExtra(_: FormState, formData: FormData): Promise<FormState> {
  const competencia = String(formData.get('competencia') ?? '');
  const beneficiarioId = String(formData.get('beneficiarioId') ?? '');
  const aprovado = formData.get('aprovado') === '1';
  try {
    await apiFetch(`/fechamentos/${competencia}/bonus-extra/${beneficiarioId}`,
      { method: 'PUT', body: { aprovado } });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/fechamento');
  return { message: aprovado ? 'Bônus extra aprovado.' : 'Aprovação do bônus extra removida.' };
}

export async function provisionarFechamento(_: FormState, formData: FormData): Promise<FormState> {
  const competencia = String(formData.get('competencia') ?? '');
  try {
    await apiFetch(`/fechamentos/${competencia}/provision`, { method: 'POST' });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/fechamento');
  return { message: 'Competência provisionada.' };
}
