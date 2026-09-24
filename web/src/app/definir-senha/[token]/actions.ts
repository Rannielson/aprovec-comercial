'use server';

import { redirect } from 'next/navigation';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';
import { setSession } from '@/lib/session';
import type { Session } from '@/lib/types';

export async function setPassword(_: FormState, formData: FormData): Promise<FormState> {
  const password = String(formData.get('password') ?? '');
  if (password !== String(formData.get('confirmation') ?? '')) return { error: 'As senhas não conferem.' };

  try {
    const session = await apiFetch<Session>('/auth/set-password', {
      method: 'POST',
      body: { token: String(formData.get('token') ?? ''), password },
      token: null,
    });
    await setSession(session.token, session.absoluteExpiresAt);
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  redirect('/');
}
