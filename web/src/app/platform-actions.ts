'use server';

import { revalidatePath } from 'next/cache';
import { redirect } from 'next/navigation';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function createTenant(_: FormState, formData: FormData): Promise<FormState> {
  const adminEmail = String(formData.get('adminEmail') ?? '').trim();
  const usePlan = formData.get('planTemplate') === 'aprovec';
  const planMonth = String(formData.get('planMonth') ?? '');

  try {
    await apiFetch('/platform/tenants', {
      method: 'POST',
      body: {
        slug: String(formData.get('slug') ?? '').trim(),
        name: String(formData.get('name') ?? '').trim(),
        adminName: String(formData.get('adminName') ?? '').trim(),
        adminEmail,
        planTemplate: usePlan ? 'aprovec' : null,
        planEffectiveFrom: usePlan && planMonth ? `${planMonth}-01` : null,
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }

  revalidatePath('/');
  return { message: `Empresa criada. O convite foi enviado para ${adminEmail}.` };
}

export async function setTenantStatus(formData: FormData): Promise<void> {
  const id = encodeURIComponent(String(formData.get('id') ?? ''));
  try {
    await apiFetch(`/platform/tenants/${id}/status`, {
      method: 'POST',
      body: { status: String(formData.get('status') ?? '') },
    });
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) redirect('/login');
    // No error-display mechanism is wired to this action (it's a plain <form action>, not
    // useActionState), so on any other API error we just send the admin back to a fresh
    // reload of the page rather than the default error page.
    if (error instanceof ApiError) redirect('/');
    throw error;
  }
  revalidatePath('/');
}
