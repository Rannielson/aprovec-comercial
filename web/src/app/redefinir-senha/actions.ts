'use server';

import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function requestReset(_: FormState, formData: FormData): Promise<FormState> {
  try {
    await apiFetch('/auth/password-reset', {
      method: 'POST',
      body: { email: String(formData.get('email') ?? '') },
      token: null,
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  return { message: 'Se o e-mail estiver cadastrado, você vai receber um link para definir uma nova senha.' };
}
