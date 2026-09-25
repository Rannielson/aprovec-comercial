'use server';

import { revalidatePath } from 'next/cache';
import { redirect } from 'next/navigation';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

type FaixaInput = { quantidadeMin: string; quantidadeMax: string; valorPorPlaca: string };
type RegraInput = { tipo: string; nivel: string; taxa: string };

export async function salvarPlanoCarreira(_: FormState, formData: FormData): Promise<FormState> {
  const id = formData.get('id') ? String(formData.get('id')) : null;
  const recorrenciaAtiva = formData.get('recorrenciaAtiva') === '1';
  const metaMinimaContratosRaw = String(formData.get('metaMinimaContratos') ?? '');
  const bonusExtraValorRaw = String(formData.get('bonusExtraValor') ?? '');

  let faixas: FaixaInput[];
  let regras: RegraInput[];
  try {
    faixas = JSON.parse(String(formData.get('faixasJson') ?? '[]'));
    regras = JSON.parse(String(formData.get('regrasJson') ?? '[]'));
  } catch {
    return { error: messageFor('plano_carreira.dados_invalidos') };
  }

  const body = {
    name: String(formData.get('name') ?? ''),
    status: formData.get('status') ? String(formData.get('status')) : undefined,
    classificacao: String(formData.get('classificacao') ?? ''),
    janelaApuracaoDias: Number(formData.get('janelaApuracaoDias') ?? 0),
    metaMinimaContratos: metaMinimaContratosRaw ? Number(metaMinimaContratosRaw) : null,
    metaMinimaFonteData: metaMinimaContratosRaw ? String(formData.get('metaMinimaFonteData') ?? '') : null,
    bonusExtraValor: bonusExtraValorRaw ? Number(bonusExtraValorRaw) : null,
    recorrenciaAtiva,
    faixas: faixas.map((f) => ({
      quantidadeMin: Number(f.quantidadeMin),
      quantidadeMax: f.quantidadeMax ? Number(f.quantidadeMax) : null,
      valorPorPlaca: Number(f.valorPorPlaca),
    })),
    regrasRecorrencia: regras.map((r) => ({
      tipo: r.tipo,
      nivel: r.nivel ? Number(r.nivel) : null,
      taxa: Number(r.taxa),
    })),
  };

  try {
    if (id) {
      await apiFetch(`/planos-carreira/${id}`, { method: 'PUT', body });
    } else {
      await apiFetch('/planos-carreira', { method: 'POST', body });
    }
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/remuneracao');
  redirect('/administracao/remuneracao');
}
