'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

type RoleSummary = { id: string; sourceTemplateKey: string | null };

export async function criarParticipante(_: FormState, formData: FormData): Promise<FormState> {
  const supervisorId = String(formData.get('supervisorId') ?? '');

  // A participant created here needs a role to have any permissions after accepting their
  // invite. Reaching this action already requires integracoes.gerenciar, which only the
  // administrador template grants (see 0006_rbac_catalog.sql/0014_hinova_integracao.sql), so
  // this actor always also holds usuarios.gerenciar_perfis -- GET /roles's own gate -- and this
  // lookup can't be the reason a real admin fails to add someone.
  let roleIds: string[] | undefined;
  try {
    const roles = await apiFetch<RoleSummary[]>('/roles');
    const consultor = roles.find((r) => r.sourceTemplateKey === 'consultor');
    if (consultor) roleIds = [consultor.id];
  } catch {
    // No role assigned is better than blocking creation on this lookup.
  }

  try {
    await apiFetch('/users', {
      method: 'POST',
      body: {
        name: String(formData.get('nomeHinova') ?? ''),
        email: String(formData.get('email') ?? ''),
        password: String(formData.get('password') ?? ''),
        supervisorId: supervisorId || undefined,
        codigoVoluntario: String(formData.get('codigoVoluntario') ?? ''),
        nomeHinova: String(formData.get('nomeHinova') ?? ''),
        cpfHinova: String(formData.get('cpfHinova') ?? ''),
        roleIds,
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

export async function editarParticipante(_: FormState, formData: FormData): Promise<FormState> {
  const id = String(formData.get('id') ?? '');
  const name = String(formData.get('name') ?? '');
  const email = String(formData.get('email') ?? '');
  const hasMapeamento = formData.get('hasMapeamento') === '1';
  const password = String(formData.get('password') ?? '');

  try {
    await apiFetch(`/users/${id}`, { method: 'PUT', body: { name, email } });
    if (hasMapeamento) {
      await apiFetch(`/integracoes/hinova/mapeamentos/${id}`, {
        method: 'PUT',
        body: {
          codigoVoluntario: String(formData.get('codigoVoluntario') ?? ''),
          nomeHinova: String(formData.get('nomeHinova') ?? ''),
          cpfHinova: String(formData.get('cpfHinova') ?? ''),
        },
      });
    }
    if (password.length > 0) {
      await apiFetch(`/users/${id}/password`, { method: 'PUT', body: { password } });
    }
    const planoCarreiraIdRaw = String(formData.get('planoCarreiraId') ?? '');
    await apiFetch(`/users/${id}/plano-carreira`, {
      method: 'PUT',
      body: { planoCarreiraId: planoCarreiraIdRaw || null },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/participantes');
  return { message: 'Participante atualizado.' };
}
