'use client';

import { useActionState, useState } from 'react';
import type { FormState } from '@/lib/form-state';
import type { PlanoCarreira } from '@/lib/types';
import { salvarPlanoCarreira } from './actions';

type FaixaRow = { quantidadeMin: string; quantidadeMax: string; valorPorPlaca: string };
type RegraRow = { tipo: 'propria' | 'upline'; nivel: string; taxa: string };

export function PlanoCarreiraForm({ plano }: { plano?: PlanoCarreira }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(salvarPlanoCarreira, {});
  const [metaMinimaAtiva, setMetaMinimaAtiva] = useState(plano?.metaMinimaContratos != null);
  const [recorrenciaAtiva, setRecorrenciaAtiva] = useState(plano?.recorrenciaAtiva ?? false);
  const [faixas, setFaixas] = useState<FaixaRow[]>(
    plano?.faixas.map((f) => ({
      quantidadeMin: String(f.quantidadeMin),
      quantidadeMax: f.quantidadeMax === null ? '' : String(f.quantidadeMax),
      valorPorPlaca: String(f.valorPorPlaca),
    })) ?? [],
  );
  const [regras, setRegras] = useState<RegraRow[]>(
    plano?.regrasRecorrencia.map((r) => ({ tipo: r.tipo, nivel: r.nivel === null ? '' : String(r.nivel), taxa: String(r.taxa) })) ?? [],
  );

  return (
    <form action={formAction} className="form">
      {plano && <input type="hidden" name="id" value={plano.id} />}
      <label>
        Nome
        <input type="text" name="name" defaultValue={plano?.name} required />
      </label>
      <label>
        Classificação
        <select name="classificacao" defaultValue={plano?.classificacao ?? 'clt_interno'} required>
          <option value="clt_interno">CLT Interno</option>
          <option value="clt_externo">CLT Externo</option>
          <option value="so_externo">Só Externo</option>
        </select>
      </label>
      {plano && (
        <label>
          Status
          <select name="status" defaultValue={plano.status}>
            <option value="ativo">Ativo</option>
            <option value="inativo">Inativo</option>
          </select>
        </label>
      )}
      <label>
        Janela de apuração (dias)
        <input type="number" name="janelaApuracaoDias" defaultValue={plano?.janelaApuracaoDias ?? 30} min={1} required />
      </label>

      <fieldset>
        <legend>Faixas de bônus por placa</legend>
        {faixas.map((faixa, i) => (
          <div key={i} className="inline">
            <input
              type="number"
              placeholder="Mínimo"
              value={faixa.quantidadeMin}
              min={1}
              onChange={(e) => setFaixas(faixas.map((f, j) => (j === i ? { ...f, quantidadeMin: e.target.value } : f)))}
              required
            />
            <input
              type="number"
              placeholder="Máximo (vazio = sem limite)"
              value={faixa.quantidadeMax}
              onChange={(e) => setFaixas(faixas.map((f, j) => (j === i ? { ...f, quantidadeMax: e.target.value } : f)))}
            />
            <input
              type="number"
              placeholder="Valor por placa"
              step="0.01"
              value={faixa.valorPorPlaca}
              min={0.01}
              onChange={(e) => setFaixas(faixas.map((f, j) => (j === i ? { ...f, valorPorPlaca: e.target.value } : f)))}
              required
            />
            <button type="button" className="secondary" onClick={() => setFaixas(faixas.filter((_, j) => j !== i))}>
              Remover
            </button>
          </div>
        ))}
        <button type="button" className="secondary" onClick={() => setFaixas([...faixas, { quantidadeMin: '', quantidadeMax: '', valorPorPlaca: '' }])}>
          Adicionar faixa
        </button>
        <input type="hidden" name="faixasJson" value={JSON.stringify(faixas)} />
      </fieldset>

      <label className="inline">
        <input type="checkbox" checked={metaMinimaAtiva} onChange={(e) => setMetaMinimaAtiva(e.target.checked)} />
        Exigir meta mínima de contratos novos
      </label>
      {metaMinimaAtiva && (
        <>
          <label>
            Meta mínima de contratos
            <input type="number" name="metaMinimaContratos" defaultValue={plano?.metaMinimaContratos ?? ''} min={1} required />
          </label>
          <label>
            Fonte da data
            <select name="metaMinimaFonteData" defaultValue={plano?.metaMinimaFonteData ?? 'contrato'} required>
              <option value="contrato">Data do contrato</option>
              <option value="cadastro">Data de cadastro</option>
            </select>
          </label>
        </>
      )}

      <label>
        Bônus extra (regime especial)
        <input type="number" name="bonusExtraValor" step="0.01" min={0.01} defaultValue={plano?.bonusExtraValor ?? ''} />
      </label>
      <p className="muted">Deixe em branco se este plano não tem valor extra fixo.</p>

      <label className="inline">
        <input
          type="checkbox"
          checked={recorrenciaAtiva}
          onChange={(e) => {
            setRecorrenciaAtiva(e.target.checked);
            if (!e.target.checked) setRegras([]);
          }}
        />
        Ativar recorrência adicional (própria/upline)
      </label>
      {recorrenciaAtiva && (
        <fieldset>
          <legend>Taxas de recorrência</legend>
          {regras.map((regra, i) => (
            <div key={i} className="inline">
              <select
                value={regra.tipo}
                onChange={(e) =>
                  setRegras(
                    regras.map((r, j) =>
                      j === i ? { ...r, tipo: e.target.value as 'propria' | 'upline', nivel: e.target.value === 'propria' ? '' : r.nivel } : r,
                    ),
                  )
                }
              >
                <option value="propria">Própria</option>
                <option value="upline">Upline</option>
              </select>
              {regra.tipo === 'upline' && (
                <input
                  type="number"
                  placeholder="Nível"
                  value={regra.nivel}
                  min={1}
                  onChange={(e) => setRegras(regras.map((r, j) => (j === i ? { ...r, nivel: e.target.value } : r)))}
                  required
                />
              )}
              <input
                type="number"
                placeholder="Taxa (ex: 0.07 = 7%)"
                step="0.0001"
                value={regra.taxa}
                min={0.0001}
                max={1}
                onChange={(e) => setRegras(regras.map((r, j) => (j === i ? { ...r, taxa: e.target.value } : r)))}
                required
              />
              <button type="button" className="secondary" onClick={() => setRegras(regras.filter((_, j) => j !== i))}>
                Remover
              </button>
            </div>
          ))}
          <button type="button" className="secondary" onClick={() => setRegras([...regras, { tipo: 'propria', nivel: '', taxa: '' }])}>
            Adicionar taxa
          </button>
        </fieldset>
      )}
      <input type="hidden" name="recorrenciaAtiva" value={recorrenciaAtiva ? '1' : ''} />
      <input type="hidden" name="regrasJson" value={JSON.stringify(regras)} />

      {state.error && (
        <p role="alert" className="error">
          {state.error}
        </p>
      )}
      {state.message && <p className="success">{state.message}</p>}
      <div className="inline">
        <button type="submit" disabled={pending}>
          {pending ? 'Salvando…' : 'Salvar'}
        </button>
      </div>
    </form>
  );
}
