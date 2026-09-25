export type Permission = { key: string; scope: string | null };

export type Me = {
  id: string;
  name: string;
  email: string;
  tenant: { id: string; slug: string; name: string };
  permissions: Permission[];
  modules: string[];
  /** Template keys behind this user's roles (e.g. `['administrador']`); custom roles contribute nothing. */
  roleTemplates: string[];
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

export type CommissionPlanRule = { id: string; type: 'own' | 'upline' | 'global'; rate: number; level: number | null; groupId: string | null };

export type CommissionPlan = { id: string; name: string; effectiveFrom: string; status: 'rascunho' | 'ativo'; rules: CommissionPlanRule[] };

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

export type UserSummary = { id: string; name: string; email: string; status: string };

export type HinovaCredenciaisStatus = { configurado: boolean; atualizadoEm: string | null; atualizadoPor: string | null };

export type HinovaVoluntario = {
  codigo: string;
  nome: string;
  cpf: string;
  telefone: string | null;
  cooperativas: string[];
  jaVinculado: boolean;
  vinculadoA: string | null;
};

export type HinovaMapeamento = {
  userId: string;
  userName: string;
  codigoVoluntario: string;
  nomeHinova: string;
  cpfHinova: string;
  mappedAt: string;
};

export type UserNode = { id: string; name: string; email: string; status: string; supervisorId: string | null; roleIds: string[] };

export type CommissionEntry = {
  boletoId: string;
  originUserId: string;
  originName: string | null;
  ruleType: 'own' | 'upline' | 'global';
  level: number | null;
  rate: number;
  base: number;
  amount: number;
  associadoNome: string | null;
  placa: string | null;
  pagoEm: string | null;
};

export type Fechamento = { competencia: string; status: string; confirmadoEm: string | null; provisionadoEm: string | null };

export type ConviteLink = { url: string };

export type ConviteLinkPublico = { indicadorNome: string; tenantNome: string };

export type SolicitacaoCadastro = {
  id: string;
  nome: string;
  cpf: string;
  celular: string;
  email: string;
  cep: string;
  logradouro: string;
  numero: string;
  complemento: string | null;
  bairro: string;
  cidade: string;
  estado: string;
  indicadorUserId: string;
  indicadorNome: string;
  criadoEm: string;
};
