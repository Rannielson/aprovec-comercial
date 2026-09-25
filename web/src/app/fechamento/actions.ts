'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function confirmarFechamento(_: FormState, formData: FormData): Promise<FormState> {
  const competencia = String(formData.get('competencia') ?? '');
  try {
    await apiFetch(`/fechamentos/${competencia}/confirm`, { method: 'POST' });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/fechamento');
  return { message: 'Demonstrativo confirmado.' };
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
