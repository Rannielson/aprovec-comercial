import type { RuleTotal } from './types';

const money = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
const percent = new Intl.NumberFormat('pt-BR', { style: 'percent', maximumFractionDigits: 2 });
const monthName = new Intl.DateTimeFormat('pt-BR', { month: 'long', year: 'numeric', timeZone: 'UTC' });
const saoPauloMonth = new Intl.DateTimeFormat('en-CA', { timeZone: 'America/Sao_Paulo', year: 'numeric', month: '2-digit' });

export const statusLabels: Record<string, string> = {
  apuracao: 'Em apuração',
  conferencia: 'Em conferência',
  confirmado: 'Confirmado',
  provisionado: 'Provisionado',
};

export function formatMoney(value: number): string {
  // Node/ICU formats BRL currency with a non-breaking space (U+00A0) after "R$";
  // normalize to a regular space for predictable rendering/comparison.
  return money.format(value).replace(/ /g, ' ');
}

export function formatPercent(rate: number): string {
  return percent.format(rate);
}

export function formatCompetencia(competencia: string): string {
  const [year, month] = competencia.split('-').map(Number);
  return monthName.format(new Date(Date.UTC(year, month - 1, 1)));
}

export function currentCompetencia(now: Date = new Date()): string {
  const parts = saoPauloMonth.formatToParts(now);
  const year = parts.find((p) => p.type === 'year')?.value;
  const month = parts.find((p) => p.type === 'month')?.value;
  return `${year}-${month}`;
}

export function isCompetencia(value: string | undefined): value is string {
  return !!value && /^\d{4}-(0[1-9]|1[0-2])$/.test(value);
}

export function ruleLabel(rule: Pick<RuleTotal, 'ruleType' | 'level' | 'groupName'>): string {
  switch (rule.ruleType) {
    case 'own':
      return 'Carteira própria';
    case 'upline':
      return `Supervisão · ${rule.level}º nível`;
    case 'global':
      return rule.groupName ?? 'Grupo';
  }
}

export function previousCompetencia(competencia: string): string {
  const [year, month] = competencia.split('-').map(Number);
  const previous = new Date(Date.UTC(year, month - 2, 1));
  return `${previous.getUTCFullYear()}-${String(previous.getUTCMonth() + 1).padStart(2, '0')}`;
}

export function statusBadgeClass(status: string): string {
  switch (status) {
    case 'apuracao':
      return 'badge progress';
    case 'conferencia':
      return 'badge warning';
    case 'confirmado':
      return 'badge recebido';
    case 'provisionado':
      return 'badge neutral';
    default:
      return 'badge';
  }
}
