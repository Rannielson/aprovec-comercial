'use server';

import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function solicitarCadastro(_: FormState, formData: FormData): Promise<FormState> {
  const token = String(formData.get('token') ?? '');
  try {
    await apiFetch(`/convite-links/${token}/solicitacoes`, {
      method: 'POST',
      token: null,
      body: {
        nome: String(formData.get('nome') ?? ''),
        cpf: String(formData.get('cpf') ?? ''),
        celular: String(formData.get('celular') ?? ''),
        email: String(formData.get('email') ?? ''),
        cep: String(formData.get('cep') ?? ''),
        logradouro: String(formData.get('logradouro') ?? ''),
        numero: String(formData.get('numero') ?? ''),
        complemento: String(formData.get('complemento') ?? ''),
        bairro: String(formData.get('bairro') ?? ''),
        cidade: String(formData.get('cidade') ?? ''),
        estado: String(formData.get('estado') ?? ''),
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  return { message: 'Solicitação enviada! Você vai receber um e-mail quando ela for aprovada.' };
}
