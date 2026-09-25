type Regra = { tipo: 'propria' | 'upline'; nivel: number | null; taxa: number };
type Faixa = { valorPorPlaca: number };

function formatPercent(taxa: number): string {
  const pct = Math.round(taxa * 100 * 100) / 100;
  return Number.isInteger(pct) ? String(pct) : String(pct.toFixed(2)).replace(/0+$/, '').replace(/\.$/, '');
}

function formatMoeda(valor: number): string {
  return valor.toLocaleString('pt-BR', { minimumFractionDigits: valor % 1 === 0 ? 0 : 2, maximumFractionDigits: 2 });
}

/** "Modelo 7-2-1": própria primeiro, depois upline em ordem crescente de nível. */
export function descreverModeloRecorrencia(regras: Regra[]): string | null {
  if (regras.length === 0) return null;
  const propria = regras.find((r) => r.tipo === 'propria');
  const uplines = regras.filter((r) => r.tipo === 'upline').sort((a, b) => (a.nivel ?? 0) - (b.nivel ?? 0));
  const partes = [...(propria ? [formatPercent(propria.taxa)] : []), ...uplines.map((u) => formatPercent(u.taxa))];
  return partes.length > 0 ? `Modelo ${partes.join('-')}` : null;
}

/** "R$ 50–120 por placa", ou "R$ 50 por placa" quando todas as faixas pagam o mesmo valor. */
export function descreverFaixasBonus(faixas: Faixa[]): string | null {
  if (faixas.length === 0) return null;
  const valores = faixas.map((f) => f.valorPorPlaca);
  const min = Math.min(...valores);
  const max = Math.max(...valores);
  return min === max ? `R$ ${formatMoeda(min)} por placa` : `R$ ${formatMoeda(min)}–${formatMoeda(max)} por placa`;
}

/** Resumo combinado: modelo de recorrência + faixa de bonificação numa linha só. */
export function descreverPlanoCarreira(plano: { faixas: Faixa[]; regrasRecorrencia: Regra[] }): string | null {
  const modelo = descreverModeloRecorrencia(plano.regrasRecorrencia);
  const bonus = descreverFaixasBonus(plano.faixas);
  if (modelo && bonus) return `${modelo} · ${bonus}`;
  return modelo ?? bonus ?? null;
}
