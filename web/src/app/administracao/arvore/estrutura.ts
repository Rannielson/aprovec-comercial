import type { BeneficiaryCommission, Commissions, UserNode } from '@/lib/types';

/** A seller in the commission pyramid, with this competência's display values. */
export type Participante = {
  id: string;
  name: string;
  email: string;
  status: string;
  /** The supervisor as stored — may point at someone outside the pyramid (a gestor, a desligado). */
  supervisorId: string | null;
  /** The supervisor as drawn — null (a root) whenever `supervisorId` isn't another pyramid seller. */
  parentId: string | null;
  /**
   * Rate of the `own` rule. Without an `own` entry of their own (e.g. brand-new, no paid boletos), the
   * tenant-wide own rate seen on anyone else this competência; null only when nobody has one.
   */
  rate: number | null;
  /** Base of the `own` rule — what was received in their wallet. 0 without an `own` entry. */
  recebido: number;
  /** Everything they earned this competência (own + upline). 0 when absent from the commissions response. */
  comissao: number;
  /** Rate they earn on a direct indicado's wallet (their level-1 `upline` entry), when known. */
  referralRate: number | null;
  /**
   * True when the viewer's `comissoes.visualizar` scope doesn't cover this person — recebido/comissao/rate
   * are 0/null placeholders, not real zeros.
   */
  valuesHidden: boolean;
};

/** Someone paid by a `global` rule — drawn in the fixed "gestão global" band, not in the pyramid. */
export type Gestor = { id: string; name: string; rate: number; recebido: number; comissao: number };

export type Estrutura = {
  sellers: Participante[];
  gestores: Gestor[];
  /** Tenant-wide rates observed this competência, for the legend and the "Nova árvore" card. */
  rates: { own: number | null; referral: number | null; global: number | null };
};

const isReferral = (r: BeneficiaryCommission['byRule'][number]) => r.ruleType === 'upline' && r.level === 1;

/**
 * Who the viewer's `comissoes.visualizar` scope actually covers, from the `users` list's
 * `supervisorId` edges — null means everyone (no additional hiding needed).
 */
function visibleIds(users: UserNode[], viewer: { id: string; commissionScope: string | null }): Set<string> | null {
  if (viewer.commissionScope === null || viewer.commissionScope === 'tenant') return null; // null = everyone visible
  if (viewer.commissionScope === 'own') return new Set([viewer.id]);

  const childrenOf = new Map<string, string[]>();
  for (const u of users) {
    if (u.supervisorId === null) continue;
    const list = childrenOf.get(u.supervisorId) ?? [];
    list.push(u.id);
    childrenOf.set(u.supervisorId, list);
  }

  if (viewer.commissionScope === 'direct') {
    return new Set([viewer.id, ...(childrenOf.get(viewer.id) ?? [])]);
  }

  // subtree
  const visible = new Set([viewer.id]);
  const queue = [viewer.id];
  while (queue.length > 0) {
    const id = queue.shift()!;
    for (const child of childrenOf.get(id) ?? []) {
      if (!visible.has(child)) {
        visible.add(child);
        queue.push(child);
      }
    }
  }
  return visible;
}

export function buildEstrutura(
  users: UserNode[],
  commissions: Commissions | null,
  viewer: { id: string; commissionScope: string | null },
): Estrutura {
  const beneficiaries = commissions?.beneficiaries ?? [];
  const byUser = new Map(beneficiaries.map((b) => [b.userId, b]));
  const userById = new Map(users.map((u) => [u.id, u]));

  const gestores: Gestor[] = [];
  for (const b of beneficiaries) {
    const globals = b.byRule.filter((r) => r.ruleType === 'global');
    if (globals.length === 0) continue;
    gestores.push({
      id: b.userId,
      name: userById.get(b.userId)?.name ?? b.name ?? 'Gestor',
      rate: globals[0].rate,
      recebido: globals[0].base,
      comissao: globals.reduce((sum, r) => sum + r.amount, 0),
    });
  }
  gestores.sort((a, b) => a.name.localeCompare(b.name, 'pt-BR'));
  const gestorIds = new Set(gestores.map((g) => g.id));

  const sellerUsers = users.filter((u) => !gestorIds.has(u.id) && u.status !== 'desligado');
  const sellerIds = new Set(sellerUsers.map((u) => u.id));
  const visible = visibleIds(users, viewer);

  const sellers = sellerUsers.map((u): Participante => {
    const b = byUser.get(u.id);
    const own = b?.byRule.find((r) => r.ruleType === 'own');
    return {
      id: u.id,
      name: u.name,
      email: u.email,
      status: u.status,
      supervisorId: u.supervisorId,
      // A supervisor outside the pyramid would leave this person neither a root nor anyone's child,
      // silently dropping them (and their whole branch) from treeLayout's output.
      parentId: u.supervisorId !== null && sellerIds.has(u.supervisorId) ? u.supervisorId : null,
      rate: own?.rate ?? null,
      recebido: own?.base ?? 0,
      comissao: b?.total ?? 0,
      referralRate: b?.byRule.find(isReferral)?.rate ?? null,
      // An `own`/`direct`/`subtree`-scoped viewer only ever gets a partial `beneficiaries[]` from
      // /commissions (whatever `app.commission_beneficiaries()` covers for their scope), so anyone
      // outside that coverage has no entry there — their absence says nothing about their activity,
      // and their zeros must not be shown as real.
      valuesHidden: visible !== null && !visible.has(u.id),
    };
  });

  const allRules = beneficiaries.flatMap((b) => b.byRule);
  const rates = {
    own: allRules.find((r) => r.ruleType === 'own')?.rate ?? null,
    referral: allRules.find(isReferral)?.rate ?? null,
    global: gestores[0]?.rate ?? null,
  };

  // The active commission plan is tenant-wide (one `own` rate, one level-1 `upline` rate), so someone
  // without an entry of their own yet — a brand-new participant — still shows the rate that applies to them.
  for (const s of sellers) {
    // A hidden person's rate stays a null placeholder too (see `valuesHidden`).
    if (!s.valuesHidden) s.rate ??= rates.own;
    s.referralRate ??= rates.referral;
  }

  return { sellers, gestores, rates };
}
