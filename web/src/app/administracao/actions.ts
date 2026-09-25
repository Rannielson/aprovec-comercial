'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function criarParticipante(_: FormState, formData: FormData): Promise<FormState> {
  const supervisorId = String(formData.get('supervisorId') ?? '');
  try {
    await apiFetch('/users', {
      method: 'POST',
      body: {
        name: String(formData.get('nomeHinova') ?? ''),
        email: String(formData.get('email') ?? ''),
        supervisorId: supervisorId || undefined,
        codigoVoluntario: String(formData.get('codigoVoluntario') ?? ''),
        nomeHinova: String(formData.get('nomeHinova') ?? ''),
        cpfHinova: String(formData.get('cpfHinova') ?? ''),
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/arvore');
  revalidatePath('/administracao/participantes');
  return { message: 'Participante adicionado.' };
}
