'use server';

import { redirect } from 'next/navigation';
import { ApiError, platformApiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';
import { setPlatformSession } from '@/lib/session';
import type { Session } from '@/lib/types';

export async function adminLogin(_: FormState, formData: FormData): Promise<FormState> {
  try {
    const session = await platformApiFetch<Session>('/platform/auth/login', {
      method: 'POST',
      body: { email: String(formData.get('email') ?? ''), password: String(formData.get('password') ?? '') },
      token: null,
    });
    await setPlatformSession(session.token, session.absoluteExpiresAt);
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  redirect('/admin');
}
