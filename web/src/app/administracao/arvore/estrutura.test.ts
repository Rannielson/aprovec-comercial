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
  planoCarreiraId: null,
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

// The existing tests exercise admin-equivalent visibility (a tenant-wide commissions scope).
const ADMIN = { id: 'admin-id', commissionScope: 'tenant' };

const commissions = (beneficiaries: Commissions['beneficiaries']): Commissions => ({
  competencia: '2026-09',
  status: 'apuracao',
  source: 'live',
  total: beneficiaries.reduce((s, b) => s + b.total, 0),
  beneficiaries,
});

describe('buildEstrutura', () => {
  it('renders zeros for a participant with no commission entry at all', () => {
    const result = buildEstrutura([user('ana'), user('novo', 'ana')], commissions([]), ADMIN);
    const novo = result.sellers.find((s) => s.id === 'novo')!;
    expect(novo).toMatchObject({ parentId: 'ana', rate: null, recebido: 0, comissao: 0, referralRate: null });
  });

  it('shows the tenant-wide rates on a new participant once anyone has them', () => {
    const result = buildEstrutura(
      [user('ana'), user('novo', 'ana')],
      commissions([
        { userId: 'ana', name: 'ANA', total: 70, byRule: [rule('own', 0.07, 1000), rule('upline', 0.02, 0, 1)] },
      ]),
      ADMIN,
    );
    const novo = result.sellers.find((s) => s.id === 'novo')!;
    expect(novo).toMatchObject({ rate: 0.07, referralRate: 0.02, recebido: 0, comissao: 0 });
  });

  it('works without any commissions response (no comissoes.visualizar)', () => {
    const result = buildEstrutura([user('ana')], null, ADMIN);
    expect(result.sellers[0]).toMatchObject({ recebido: 0, comissao: 0 });
  });

  it('uses own base as recebido and the full total as comissão', () => {
    const result = buildEstrutura(
      [user('ana'), user('bia', 'ana')],
      commissions([
        { userId: 'ana', name: 'ANA', total: 90, byRule: [rule('own', 0.07, 1000), rule('upline', 0.02, 1000, 1)] },
        { userId: 'bia', name: 'BIA', total: 70, byRule: [rule('own', 0.07, 1000)] },
      ]),
      ADMIN,
    );
    const ana = result.sellers.find((s) => s.id === 'ana')!;
    expect(ana).toMatchObject({ rate: 0.07, recebido: 1000, comissao: 90, referralRate: 0.02 });
    expect(result.rates.referral).toBe(0.02);
  });

  it('exposes every upline level separately, not just level 1, with the own amount already computed', () => {
    // ana -> bia -> caio: ana earns level-1 supervision on bia AND level-2 on caio, at the same
    // rate here but from a distinct rule/base each -- a single combined "referral" figure would
    // conflate the two (this was a real bug: it showed the total non-own commission next to a
    // base that only covered the direct report).
    const result = buildEstrutura(
      [user('ana'), user('bia', 'ana'), user('caio', 'bia')],
      commissions([
        {
          userId: 'ana',
          name: 'ANA',
          total: 1700,
          byRule: [rule('own', 0.07, 20000), rule('upline', 0.02, 10000, 1), rule('upline', 0.02, 5000, 2)],
        },
      ]),
      ADMIN,
    );
    const ana = result.sellers.find((s) => s.id === 'ana')!;
    expect(ana.ownAmount).toBe(1400);
    expect(ana.uplineRules).toEqual([
      { ruleType: 'upline', level: 1, groupId: null, groupName: null, rate: 0.02, base: 10000, amount: 200 },
      { ruleType: 'upline', level: 2, groupId: null, groupName: null, rate: 0.02, base: 5000, amount: 100 },
    ]);
    // The two levels together account for the rest of the total (1700 = 1400 + 200 + 100).
    const uplineTotal = ana.uplineRules.reduce((sum, r) => sum + r.amount, 0);
    expect(ana.ownAmount + uplineTotal).toBe(ana.comissao);
    // Tenant-wide, so a level's rate is known even for someone who has no entry of their own
    // this competência (e.g. bia, with no upline rule at all above).
    expect(result.rates.uplineRates).toEqual([
      { level: 1, rate: 0.02 },
      { level: 2, rate: 0.02 },
    ]);
  });

  it('hides uplineRules (not just recebido/comissao) for a person outside the viewer\'s scope', () => {
    const result = buildEstrutura(
      [user('ana'), user('bia', 'ana')],
      commissions([{ userId: 'bia', name: 'BIA', total: 70, byRule: [rule('own', 0.07, 1000)] }]),
      { id: 'someone-else', commissionScope: 'own' },
    );
    const bia = result.sellers.find((s) => s.id === 'bia')!;
    expect(bia.valuesHidden).toBe(true);
    expect(bia.uplineRules).toEqual([]);
  });

  it('excludes desligados and re-roots anyone whose supervisor is outside the pyramid', () => {
    const result = buildEstrutura(
      // ana's stored supervisorId points at someone who was never even in this users list (a
      // stale reference) -- she must still be drawn as a root, not silently dropped.
      [user('saiu', null, 'desligado'), user('ana', 'inexistente'), user('bia', 'saiu'), user('caio', 'ana')],
      commissions([]),
      ADMIN,
    );
    const byId = Object.fromEntries(result.sellers.map((s) => [s.id, s]));
    expect(Object.keys(byId).sort()).toEqual(['ana', 'bia', 'caio']);
    expect(byId.ana.parentId).toBeNull();
    expect(byId.ana.supervisorId).toBe('inexistente');
    expect(byId.bia.parentId).toBeNull();
    expect(byId.caio.parentId).toBe('ana');
  });

  it("hides values an 'own'-scoped viewer can't actually see, even when the input has them", () => {
    const result = buildEstrutura(
      [user('ana'), user('bia', 'ana')],
      commissions([
        { userId: 'ana', name: 'ANA', total: 90, byRule: [rule('own', 0.07, 1000), rule('upline', 0.02, 1000, 1)] },
        { userId: 'bia', name: 'BIA', total: 70, byRule: [rule('own', 0.07, 1000)] },
      ]),
      { id: 'ana', commissionScope: 'own' },
    );
    const byId = Object.fromEntries(result.sellers.map((s) => [s.id, s]));
    expect(byId.ana.valuesHidden).toBe(false);
    expect(byId.ana).toMatchObject({ rate: 0.07, recebido: 1000, comissao: 90 });
    expect(byId.bia.valuesHidden).toBe(true);
  });

  it("keeps a hidden report's rate a null placeholder instead of the tenant-wide fallback", () => {
    const result = buildEstrutura(
      [user('ana'), user('bia', 'ana')],
      commissions([{ userId: 'ana', name: 'ANA', total: 70, byRule: [rule('own', 0.07, 1000)] }]),
      { id: 'ana', commissionScope: 'own' },
    );
    const bia = result.sellers.find((s) => s.id === 'bia')!;
    expect(bia).toMatchObject({ valuesHidden: true, rate: null, recebido: 0, comissao: 0 });
  });

  it('does not hide anyone for full-visibility commission scopes', () => {
    for (const commissionScope of ['tenant', null]) {
      const result = buildEstrutura([user('ana'), user('bia', 'ana')], commissions([]), { id: 'ana', commissionScope });
      expect(result.sellers.every((s) => !s.valuesHidden)).toBe(true);
    }
  });

  it("hides people outside a direct-scoped or subtree-scoped viewer's real coverage", () => {
    // ana -> bia -> caio (chain); dora is unrelated (root, no relation to ana)
    const users = [user('ana'), user('bia', 'ana'), user('caio', 'bia'), user('dora')];

    const direct = buildEstrutura(users, commissions([]), { id: 'ana', commissionScope: 'direct' });
    const byIdDirect = Object.fromEntries(direct.sellers.map((s) => [s.id, s]));
    expect(byIdDirect.ana.valuesHidden).toBe(false);
    expect(byIdDirect.bia.valuesHidden).toBe(false);
    expect(byIdDirect.caio.valuesHidden).toBe(true); // two levels down, outside 'direct'
    expect(byIdDirect.dora.valuesHidden).toBe(true); // unrelated

    const subtree = buildEstrutura(users, commissions([]), { id: 'ana', commissionScope: 'subtree' });
    const byIdSubtree = Object.fromEntries(subtree.sellers.map((s) => [s.id, s]));
    expect(byIdSubtree.ana.valuesHidden).toBe(false);
    expect(byIdSubtree.bia.valuesHidden).toBe(false);
    expect(byIdSubtree.caio.valuesHidden).toBe(false); // subtree reaches deeper
    expect(byIdSubtree.dora.valuesHidden).toBe(true); // still unrelated
  });
});
