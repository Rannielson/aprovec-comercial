export type Permission = { key: string; scope: string | null };

export type Me = {
  id: string;
  name: string;
  email: string;
  tenant: { id: string; slug: string; name: string };
  permissions: Permission[];
  modules: string[];
};

export type TenantInfo = { slug: string; name: string; platform: boolean };

export type Session = { token: string; absoluteExpiresAt: string };

export type RuleTotal = {
  ruleType: 'own' | 'upline' | 'global';
  level: number | null;
  groupId: string | null;
  groupName: string | null;
  rate: number;
  base: number;
  amount: number;
};

export type BeneficiaryCommission = { userId: string; name: string | null; total: number; byRule: RuleTotal[] };

export type Commissions = {
  competencia: string;
  status: string;
  source: 'live' | 'snapshot';
  total: number;
  beneficiaries: BeneficiaryCommission[];
};

export type PlatformTenant = { id: string; slug: string; name: string; status: 'ativo' | 'suspenso'; createdAt: string };

export type PlatformMe = { id: string; email: string };

export type WalletItem = {
  id: string;
  associadoNome: string;
  placa: string | null;
  valor: number;
  status: string;
  vencimento: string;
  pagoEm: string | null;
  diasAtraso: number | null;
  comissao: number;
};

export type Wallet = {
  items: WalletItem[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalAllCount: number;
  statusCounts: Record<string, number>;
  totalValor: number;
  totalComissao: number;
};
