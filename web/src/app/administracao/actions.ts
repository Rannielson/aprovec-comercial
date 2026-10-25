'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch, currentHost } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';
import type { Me } from '@/lib/types';

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
    const hasPlanos = formData.get('hasPlanos') === '1';
    if (hasPlanos) {
      const planoCarreiraIdRaw = String(formData.get('planoCarreiraId') ?? '');
      await apiFetch(`/users/${id}/plano-carreira`, {
        method: 'PUT',
        body: { planoCarreiraId: planoCarreiraIdRaw || null },
      });
    }
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/participantes');
  return { message: 'Participante atualizado.' };
}

export async function redefinirSenhaParticipante(_: FormState, formData: FormData): Promise<FormState> {
  const host = await currentHost();
  if (host.kind !== 'tenant') return { error: 'Acesso não autorizado.' };

  const me = await apiFetch<Me>('/me');
  if (!me.roleTemplates.includes('administrador') || !me.permissions.some((p) => p.key === 'usuarios.convidar')) {
    return { error: 'Seu perfil não permite redefinir senhas.' };
  }

  const id = String(formData.get('id') ?? '');
  const password = String(formData.get('password') ?? '');
  const confirmation = String(formData.get('confirmation') ?? '');
  if (!/^[0-9a-f-]{36}$/i.test(id)) return { error: 'Participante inválido.' };
  if (password.length < 10 || password.length > 128) return { error: 'A senha precisa ter entre 10 e 128 caracteres.' };
  if (password !== confirmation) return { error: 'As senhas não coincidem.' };

  try {
    await apiFetch(`/users/${id}/password`, { method: 'PUT', body: { password } });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  return { message: 'Senha redefinida com sucesso.' };
}

export async function salvarCorteContrato(_: FormState, formData: FormData): Promise<FormState> {
  const id = String(formData.get('id') ?? '');
  const rawDate = String(formData.get('cutoffDate') ?? '');
  if (!/^[0-9a-f-]{36}$/i.test(id)) return { error: 'Participante inválido.' };
  if (rawDate && !/^\d{4}-\d{2}-\d{2}$/.test(rawDate)) return { error: 'Informe uma data de corte válida.' };

  try {
    await apiFetch(`/commission-contract-cutoffs/${id}`, {
      method: 'PUT', body: { cutoffDate: rawDate || null },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath(`/administracao/participantes/${id}/editar`);
  return { message: rawDate ? 'Data de corte salva.' : 'Data de corte removida.' };
}
