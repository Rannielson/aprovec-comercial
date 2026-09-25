'use server';

import { redirect } from 'next/navigation';
import { ApiError, apiFetch, currentHost } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';
import { setSession } from '@/lib/session';
import type { Session } from '@/lib/types';

export async function login(_: FormState, formData: FormData): Promise<FormState> {
  const host = await currentHost();
  const path = host.kind === 'platform' ? '/platform/auth/login' : '/auth/login';
  try {
    const session = await apiFetch<Session>(path, {
      method: 'POST',
      body: { email: String(formData.get('email') ?? ''), password: String(formData.get('password') ?? '') },
      token: null,
    });
    await setSession(session.token, session.absoluteExpiresAt);
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  redirect('/');
}
