import { describe, expect, it } from 'vitest';
import { dashboardBreakdown } from './payout-dashboard';
import type { PagamentoBeneficiario } from './types';

describe('dashboardBreakdown', () => {
  it('separa carteira e bônus da receita dos níveis e conserva o total a receber', () => {
    const person = {
      total: 220,
      arvore: 140,
      placas: 70,
      extra: 10,
      regrasArvore: [
        { ruleType: 'own', level: null, groupId: null, groupName: null, rate: 0.07, base: 1000, amount: 70 },
        { ruleType: 'upline', level: 1, groupId: null, groupName: null, rate: 0.02, base: 2000, amount: 40 },
        { ruleType: 'upline', level: 2, groupId: null, groupName: null, rate: 0.01, base: 3000, amount: 30 },
      ],
    } as PagamentoBeneficiario;

    const result = dashboardBreakdown(person);
    expect(result.direct).toBe(150);
    expect(result.team).toBe(70);
    expect(result.direct + result.team).toBe(person.total);
    expect(result.teamRules.map((rule) => rule.level)).toEqual([1, 2]);
  });
});
