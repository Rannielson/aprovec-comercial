import { describe, expect, it } from 'vitest';
import type { Commissions, RuleTotal, UserNode } from '@/lib/types';
import { buildEstrutura } from './estrutura';

const user = (id: string, supervisorId: string | null = null, status = 'ativo'): UserNode => ({
  id,
  name: id.toUpperCase(),
  email: `${id}@exemplo.com`,
  status,
  supervisorId,
  roleIds: [],
});

const rule = (ruleType: RuleTotal['ruleType'], rate: number, base: number, level: number | null = null): RuleTotal => ({
  ruleType,
  level,
  groupId: ruleType === 'global' ? 'g1' : null,
  groupName: ruleType === 'global' ? 'Gestão' : null,
  rate,
  base,
  amount: Math.round(rate * base * 100) / 100,
});

const commissions = (beneficiaries: Commissions['beneficiaries']): Commissions => ({
  competencia: '2026-09',
  status: 'apuracao',
  source: 'live',
  total: beneficiaries.reduce((s, b) => s + b.total, 0),
  beneficiaries,
});

describe('buildEstrutura', () => {
  it('puts anyone with a global rule in the gestor band, not the pyramid', () => {
    const result = buildEstrutura(
      [user('gestor'), user('ana')],
      commissions([
        { userId: 'gestor', name: 'GESTOR', total: 100, byRule: [rule('global', 0.01, 10000)] },
        { userId: 'ana', name: 'ANA', total: 70, byRule: [rule('own', 0.07, 1000)] },
      ]),
    );
    expect(result.gestores).toEqual([{ id: 'gestor', name: 'GESTOR', rate: 0.01, recebido: 10000, comissao: 100 }]);
    expect(result.sellers.map((s) => s.id)).toEqual(['ana']);
    expect(result.rates).toEqual({ own: 0.07, referral: null, global: 0.01 });
  });

  it('renders zeros for a participant with no commission entry at all', () => {
    const result = buildEstrutura([user('ana'), user('novo', 'ana')], commissions([]));
    const novo = result.sellers.find((s) => s.id === 'novo')!;
    expect(novo).toMatchObject({ parentId: 'ana', rate: null, recebido: 0, comissao: 0, referralRate: null });
  });

  it('shows the tenant-wide rates on a new participant once anyone has them', () => {
    const result = buildEstrutura(
      [user('ana'), user('novo', 'ana')],
      commissions([
        { userId: 'ana', name: 'ANA', total: 70, byRule: [rule('own', 0.07, 1000), rule('upline', 0.02, 0, 1)] },
      ]),
    );
    const novo = result.sellers.find((s) => s.id === 'novo')!;
    expect(novo).toMatchObject({ rate: 0.07, referralRate: 0.02, recebido: 0, comissao: 0 });
  });

  it('works without any commissions response (no comissoes.visualizar)', () => {
    const result = buildEstrutura([user('ana')], null);
    expect(result.gestores).toEqual([]);
    expect(result.sellers[0]).toMatchObject({ recebido: 0, comissao: 0 });
  });

  it('uses own base as recebido and the full total as comissão', () => {
    const result = buildEstrutura(
      [user('ana'), user('bia', 'ana')],
      commissions([
        { userId: 'ana', name: 'ANA', total: 90, byRule: [rule('own', 0.07, 1000), rule('upline', 0.02, 1000, 1)] },
        { userId: 'bia', name: 'BIA', total: 70, byRule: [rule('own', 0.07, 1000)] },
      ]),
    );
    const ana = result.sellers.find((s) => s.id === 'ana')!;
    expect(ana).toMatchObject({ rate: 0.07, recebido: 1000, comissao: 90, referralRate: 0.02 });
    expect(result.rates.referral).toBe(0.02);
  });

  it('excludes desligados and re-roots anyone whose supervisor is outside the pyramid', () => {
    const result = buildEstrutura(
      [user('gestor'), user('saiu', null, 'desligado'), user('ana', 'gestor'), user('bia', 'saiu'), user('caio', 'ana')],
      commissions([{ userId: 'gestor', name: 'GESTOR', total: 10, byRule: [rule('global', 0.01, 1000)] }]),
    );
    const byId = Object.fromEntries(result.sellers.map((s) => [s.id, s]));
    expect(Object.keys(byId).sort()).toEqual(['ana', 'bia', 'caio']);
    expect(byId.ana.parentId).toBeNull();
    expect(byId.ana.supervisorId).toBe('gestor');
    expect(byId.bia.parentId).toBeNull();
    expect(byId.caio.parentId).toBe('ana');
  });
});
