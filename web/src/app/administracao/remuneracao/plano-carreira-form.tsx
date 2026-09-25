'use client';

import { useActionState, useState, type FormEvent } from 'react';
import type { FormState } from '@/lib/form-state';
import type { PlanoCarreira } from '@/lib/types';
import { descreverPlanoCarreira } from '@/lib/plano-carreira-summary';
import { Icon } from '../../components/app-icon';
import { salvarPlanoCarreira } from './actions';

type FaixaRow = { quantidadeMin: string; quantidadeMax: string; valorPorPlaca: string };
type RegraRow = { tipo: 'propria' | 'upline'; nivel: string; taxa: string };

export function PlanoCarreiraForm({ plano }: { plano?: PlanoCarreira }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(salvarPlanoCarreira, {});
  const [name, setName] = useState(plano?.name ?? '');
  const [classificacao, setClassificacao] = useState<string>(plano?.classificacao ?? 'clt_interno');
  const [status, setStatus] = useState(plano?.status ?? 'ativo');
  const [janelaApuracaoDias, setJanelaApuracaoDias] = useState(String(plano?.janelaApuracaoDias ?? 30));
  const [metaMinimaAtiva, setMetaMinimaAtiva] = useState(plano?.metaMinimaContratos != null);
  const [metaMinimaContratos, setMetaMinimaContratos] = useState(plano?.metaMinimaContratos != null ? String(plano.metaMinimaContratos) : '');
  const [metaMinimaFonteData, setMetaMinimaFonteData] = useState(plano?.metaMinimaFonteData ?? 'contrato');
  const [bonusExtraValor, setBonusExtraValor] = useState(plano?.bonusExtraValor != null ? String(plano.bonusExtraValor) : '');
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
  const [clientError, setClientError] = useState<string | null>(null);

  const resumo = descreverPlanoCarreira({
    faixas: faixas.flatMap((f) => {
      const valor = Number(f.valorPorPlaca);
      return valor > 0 ? [{ valorPorPlaca: valor }] : [];
    }),
    regrasRecorrencia: recorrenciaAtiva
      ? regras.flatMap((r) => {
          const taxa = Number(r.taxa);
          if (!(taxa > 0)) return [];
          return [{ tipo: r.tipo, nivel: r.tipo === 'upline' ? Number(r.nivel) || null : null, taxa }];
        })
      : [],
  });

  function hasDuplicateRegra(rows: RegraRow[]): boolean {
    const seen = new Set<string>();
    for (const r of rows) {
      const key = r.tipo === 'propria' ? 'propria' : `upline:${r.nivel}`;
      if (seen.has(key)) return true;
      seen.add(key);
    }
    return false;
  }

  function handleSubmit(e: FormEvent<HTMLFormElement>) {
    setClientError(null);
    if (recorrenciaAtiva && regras.length === 0) {
      e.preventDefault();
      setClientError('Ative ao menos uma taxa de recorrência ou desmarque "Ativar recorrência adicional".');
      return;
    }
    if (hasDuplicateRegra(regras)) {
      e.preventDefault();
      setClientError('Taxas de recorrência duplicadas: só é permitida uma linha "própria" e uma linha por nível de upline.');
      return;
    }
    for (const faixa of faixas) {
      if (faixa.quantidadeMax !== '' && Number(faixa.quantidadeMax) < Number(faixa.quantidadeMin)) {
        e.preventDefault();
        setClientError('O valor máximo de uma faixa não pode ser menor que o mínimo.');
        return;
      }
    }
  }

  return (
    <form action={formAction} onSubmit={handleSubmit} className="plano-form">
      {plano && <input type="hidden" name="id" value={plano.id} />}

      <div className="plano-section">
        <div className="plano-grid">
          <label>
            Nome
            <input type="text" name="name" value={name} onChange={(e) => setName(e.target.value)} required />
          </label>
          <label>
            Classificação
            <select name="classificacao" value={classificacao} onChange={(e) => setClassificacao(e.target.value)} required>
              <option value="clt_interno">CLT Interno</option>
              <option value="clt_externo">CLT Externo</option>
              <option value="so_externo">Só Externo</option>
            </select>
          </label>
          {plano && (
            <label>
              Status
              <select name="status" value={status} onChange={(e) => setStatus(e.target.value)}>
                <option value="ativo">Ativo</option>
                <option value="inativo">Inativo</option>
              </select>
            </label>
          )}
          <label>
            Janela de apuração (dias)
            <input
              type="number"
              name="janelaApuracaoDias"
              value={janelaApuracaoDias}
              onChange={(e) => setJanelaApuracaoDias(e.target.value)}
              min={1}
              required
            />
          </label>
        </div>
      </div>

      {resumo && (
        <div className="plano-summary">
          <Icon name="receipt" />
          <span>
            Resumo do plano: <strong>{resumo}</strong>
          </span>
        </div>
      )}

      <div className="plano-section">
        <div className="plano-section-head">
          <Icon name="wallet" />
          <div>
            <h2>Bonificação por placa</h2>
            <p>Valor pago por veículo novo, de acordo com a faixa de quantidade no período.</p>
          </div>
        </div>
        {faixas.map((faixa, i) => (
          <div key={i} className="row-card">
            <label>
              Mínimo
              <input
                type="number"
                value={faixa.quantidadeMin}
                min={1}
                onChange={(e) => setFaixas(faixas.map((f, j) => (j === i ? { ...f, quantidadeMin: e.target.value } : f)))}
                required
              />
            </label>
            <label>
              Máximo (vazio = sem limite)
              <input
                type="number"
                value={faixa.quantidadeMax}
                min={faixa.quantidadeMin || undefined}
                onChange={(e) => setFaixas(faixas.map((f, j) => (j === i ? { ...f, quantidadeMax: e.target.value } : f)))}
              />
            </label>
            <label>
              Valor por placa
              <input
                type="number"
                step="0.01"
                value={faixa.valorPorPlaca}
                min={0.01}
                onChange={(e) => setFaixas(faixas.map((f, j) => (j === i ? { ...f, valorPorPlaca: e.target.value } : f)))}
                required
              />
            </label>
            <button
              type="button"
              className="icon-button"
              aria-label="Remover faixa"
              onClick={() => setFaixas(faixas.filter((_, j) => j !== i))}
            >
              <Icon name="trash" />
            </button>
          </div>
        ))}
        <button
          type="button"
          className="text-button plano-add"
          onClick={() => setFaixas([...faixas, { quantidadeMin: '', quantidadeMax: '', valorPorPlaca: '' }])}
        >
          <Icon name="plus" />
          Adicionar faixa
        </button>
        <input type="hidden" name="faixasJson" value={JSON.stringify(faixas)} />
      </div>

      <div className="plano-section">
        <label className="inline">
          <input type="checkbox" checked={metaMinimaAtiva} onChange={(e) => setMetaMinimaAtiva(e.target.checked)} />
          Exigir meta mínima de contratos novos
        </label>
        {metaMinimaAtiva && (
          <div className="plano-grid">
            <label>
              Meta mínima de contratos
              <input
                type="number"
                name="metaMinimaContratos"
                value={metaMinimaContratos}
                onChange={(e) => setMetaMinimaContratos(e.target.value)}
                min={1}
                required
              />
            </label>
            <label>
              Fonte da data
              <select name="metaMinimaFonteData" value={metaMinimaFonteData} onChange={(e) => setMetaMinimaFonteData(e.target.value)} required>
                <option value="contrato">Data do contrato</option>
                <option value="cadastro">Data de cadastro</option>
              </select>
            </label>
          </div>
        )}
      </div>

      <div className="plano-section">
        <label>
          Bônus extra (regime especial)
          <input
            type="number"
            name="bonusExtraValor"
            step="0.01"
            min={0.01}
            value={bonusExtraValor}
            onChange={(e) => setBonusExtraValor(e.target.value)}
          />
        </label>
        <p className="muted">Deixe em branco se este plano não tem valor extra fixo.</p>
      </div>

      <div className="plano-section">
        <div className="plano-section-head">
          <Icon name="percent" />
          <div>
            <h2>Recorrência adicional</h2>
            <p>Percentual recorrente sobre a carteira própria e/ou sobre os níveis de upline.</p>
          </div>
        </div>
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
          <>
            {regras.map((regra, i) => (
              <div key={i} className="row-card">
                <label>
                  Tipo
                  <select
                    value={regra.tipo}
                    onChange={(e) =>
                      setRegras(
                        regras.map((r, j) =>
                          j === i
                            ? { ...r, tipo: e.target.value as 'propria' | 'upline', nivel: e.target.value === 'propria' ? '' : r.nivel }
                            : r,
                        ),
                      )
                    }
                  >
                    <option value="propria">Própria</option>
                    <option value="upline">Upline</option>
                  </select>
                </label>
                {regra.tipo === 'upline' && (
                  <label>
                    Nível
                    <input
                      type="number"
                      value={regra.nivel}
                      min={1}
                      onChange={(e) => setRegras(regras.map((r, j) => (j === i ? { ...r, nivel: e.target.value } : r)))}
                      required
                    />
                  </label>
                )}
                <label>
                  Taxa (ex: 0.07 = 7%)
                  <input
                    type="number"
                    step="0.0001"
                    value={regra.taxa}
                    min={0.0001}
                    max={1}
                    onChange={(e) => setRegras(regras.map((r, j) => (j === i ? { ...r, taxa: e.target.value } : r)))}
                    required
                  />
                </label>
                <button
                  type="button"
                  className="icon-button"
                  aria-label="Remover taxa"
                  onClick={() => setRegras(regras.filter((_, j) => j !== i))}
                >
                  <Icon name="trash" />
                </button>
              </div>
            ))}
            <button
              type="button"
              className="text-button plano-add"
              onClick={() => setRegras([...regras, { tipo: 'propria', nivel: '', taxa: '' }])}
            >
              <Icon name="plus" />
              Adicionar taxa
            </button>
          </>
        )}
        <input type="hidden" name="recorrenciaAtiva" value={recorrenciaAtiva ? '1' : ''} />
        <input type="hidden" name="regrasJson" value={JSON.stringify(regras)} />
      </div>

      {clientError && (
        <p role="alert" className="error">
          {clientError}
        </p>
      )}
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
