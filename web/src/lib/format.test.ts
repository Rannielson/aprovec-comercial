import { describe, expect, it } from 'vitest';
import { currentCompetencia, formatCompetencia, formatDate, formatMoney, formatPercent, isCompetencia, previousCompetencia, ruleLabel, statusBadgeClass } from './format';

describe('format', () => {
  it('formata dinheiro em reais', () => {
    expect(formatMoney(1600)).toBe('R$ 1.600,00');
    expect(formatMoney(0.12)).toBe('R$ 0,12');
  });

  it('formata percentuais', () => {
    expect(formatPercent(0.07)).toBe('7%');
    expect(formatPercent(0.025)).toBe('2,5%');
  });

  it('escreve a competência por extenso', () => {
    expect(formatCompetencia('2026-09')).toBe('setembro de 2026');
  });

  it('usa o mês de São Paulo como competência atual', () => {
    expect(currentCompetencia(new Date('2026-10-01T02:00:00Z'))).toBe('2026-09');
    expect(currentCompetencia(new Date('2026-10-01T04:00:00Z'))).toBe('2026-10');
  });

  it('valida competências', () => {
    expect(isCompetencia('2026-09')).toBe(true);
    expect(isCompetencia('2026-13')).toBe(false);
    expect(isCompetencia(undefined)).toBe(false);
  });

  it('nomeia as regras', () => {
    expect(ruleLabel({ ruleType: 'own', level: null, groupName: null })).toBe('Carteira própria');
    expect(ruleLabel({ ruleType: 'upline', level: 2, groupName: null })).toBe('Supervisão · 2º nível');
    expect(ruleLabel({ ruleType: 'global', level: null, groupName: 'Coordenação' })).toBe('Coordenação');
  });

  it('calcula a competência anterior', () => {
    expect(previousCompetencia('2026-09')).toBe('2026-08');
    expect(previousCompetencia('2026-01')).toBe('2025-12');
  });

  it('escolhe a classe de badge pela situação', () => {
    expect(statusBadgeClass('apuracao')).toBe('badge progress');
    expect(statusBadgeClass('conferencia')).toBe('badge warning');
    expect(statusBadgeClass('confirmado')).toBe('badge recebido');
    expect(statusBadgeClass('provisionado')).toBe('badge neutral');
    expect(statusBadgeClass('desconhecido')).toBe('badge');
  });

  it('formata datas no padrão brasileiro', () => {
    expect(formatDate('2026-09-05')).toBe('05/09/2026');
    expect(formatDate('2026-01-20')).toBe('20/01/2026');
  });
});
