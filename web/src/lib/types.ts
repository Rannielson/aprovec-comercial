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
  source: 'live' | 'snapshot' | 'live_review';
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

export type HinovaBoletoSync = {
  id: string;
  competencia: string;
  status: 'queued' | 'running' | 'completed' | 'failed';
  fase: 'na_fila' | 'aguardando_nova_tentativa' | 'iniciando' | 'buscando_pagamentos' | 'buscando_vencimentos' | 'importando_pagamentos' | 'importando_vencimentos' | 'concluido' | 'falhou';
  requestedAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  totalBoletos: number | null;
  importados: number | null;
  semVinculo: number | null;
  conflitoMultiploVendedor: number | null;
  naoPago: number | null;
  vencimentoRetornados: number | null;
  vencimentoVinculados: number | null;
  pagamentosConsultados: number;
  pagamentosProcessados: number;
  vencimentosConsultados: number;
  vencimentosProcessados: number;
  errorCode: string | null;
};

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

export type UserNode = {
  id: string;
  name: string;
  email: string;
  status: string;
  supervisorId: string | null;
  roleIds: string[];
  planoCarreiraId: string | null;
};

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

export type CommissionDownline = {
  userId: string;
  name: string;
  level: number;
  rate: number;
  base: number;
  amount: number;
  pagamentos: number;
};

export type Fechamento = { competencia: string; status: string; confirmadoEm: string | null; provisionadoEm: string | null };

export type BonusPlacaDetalhe = {
  placa: string;
  codigoVeiculo: string;
  dataContrato: string;
  dataPagamento: string;
  nossoNumero: string;
};

export type PagamentoBeneficiario = {
  userId: string;
  name: string;
  arvore: number;
  placas: number;
  extra: number;
  total: number;
  regrasArvore: RuleTotal[];
  bonusPlacas: {
    competenciaProducao: string;
    planoNome: string;
    dataLimitePagamento: string;
    quantidade: number;
    valorUnitario: number | null;
    valor: number;
    placas: BonusPlacaDetalhe[];
  } | null;
  bonusExtra: { planoNome: string; valor: number; aprovadoEm: string } | null;
  extraDisponivel: number | null;
};

export type Pagamento = {
  competencia: string;
  status: string;
  previa: boolean;
  arvore: number;
  placas: number;
  extra: number;
  total: number;
  beneficiarios: PagamentoBeneficiario[];
};

export type LostCommission = {
  available: boolean;
  total: number;
  emAtraso: number;
  cancelados: number;
  boletos: number;
};

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
  status: 'pendente' | 'aprovado' | 'rejeitado';
  processarAutomaticamente: boolean;
  processandoEm: string | null;
  erroCodigo: string | null;
  resolvidoEm: string | null;
  conviteEnviadoEm: string | null;
  conviteErro: string | null;
};

export type FaixaBonus = { id: string; quantidadeMin: number; quantidadeMax: number | null; valorPorPlaca: number };

export type RegraRecorrencia = { id: string; tipo: 'propria' | 'upline'; nivel: number | null; taxa: number };

export type PlanoCarreira = {
  id: string;
  name: string;
  status: string;
  classificacao: 'clt_interno' | 'clt_externo' | 'so_externo';
  janelaApuracaoDias: number;
  metaMinimaContratos: number | null;
  metaMinimaFonteData: string | null;
  bonusExtraValor: number | null;
  recorrenciaAtiva: boolean;
  faixas: FaixaBonus[];
  regrasRecorrencia: RegraRecorrencia[];
};

export type ApuracaoBonusPlacas = {
  competencia: string;
  planoId: string;
  participanteId: string;
  participanteNome: string;
  planoAtribuido: boolean;
  dataLimitePagamento: string;
  apuradoAte: string;
  final: boolean;
  placasPagas: number;
  valorPorPlaca: number | null;
  valorBonus: number;
  placas: {
    placa: string;
    codigoVeiculo: string;
    dataContrato: string;
    dataPagamento: string;
    nossoNumero: string;
  }[];
};
