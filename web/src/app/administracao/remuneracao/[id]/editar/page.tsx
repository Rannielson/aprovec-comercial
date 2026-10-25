import { notFound, redirect } from 'next/navigation';
import { AppShell } from '../../../../app-shell';
import { ApiError, apiFetch, currentHost } from '@/lib/api';
import { currentCompetencia, isCompetencia, previousCompetencia } from '@/lib/format';
import { messageFor } from '@/lib/errors';
import type { ApuracaoBonusPlacas, HinovaMapeamento, Me, PlanoCarreira } from '@/lib/types';
import { PlanoCarreiraForm } from '../../plano-carreira-form';
import { ApuracaoBonusCard } from './apuracao-bonus-card';

export default async function EditarPlanoCarreiraPage({
  params,
  searchParams,
}: {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ competencia?: string; participanteId?: string }>;
}) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const has = (key: string) => me.permissions.some((p) => p.key === key);
  if (!has('regras_comissao.editar')) redirect('/administracao/remuneracao');
  if (!has('regras_comissao.visualizar')) redirect('/administracao/remuneracao');

  const { id } = await params;
  const planos = await apiFetch<PlanoCarreira[]>('/planos-carreira');
  const plano = planos.find((p) => p.id === id);
  if (!plano) notFound();
  const canApurar = has('integracoes.gerenciar');
  const mapeamentos = canApurar
    ? (await apiFetch<HinovaMapeamento[]>('/integracoes/hinova/mapeamentos')).sort((a, b) => a.userName.localeCompare(b.userName, 'pt-BR'))
    : [];
  const filtros = await searchParams;
  const competencia = filtros.competencia ?? previousCompetencia(currentCompetencia());
  const participanteId = filtros.participanteId ?? '';
  let apuracao: ApuracaoBonusPlacas | null = null;
  let apuracaoError: string | null = null;
  if (canApurar && filtros.competencia && filtros.participanteId) {
    if (!isCompetencia(competencia) || !mapeamentos.some((m) => m.userId === participanteId)) {
      apuracaoError = 'Escolha uma competência e um consultor válidos.';
    } else {
      try {
        apuracao = await apiFetch<ApuracaoBonusPlacas>(
          `/planos-carreira/${id}/apuracao?competencia=${competencia}&participanteId=${participanteId}`,
          { timeoutMs: 120_000 },
        );
      } catch (error) {
        apuracaoError = error instanceof ApiError ? messageFor(error.code) : 'Não foi possível consultar os pagamentos na Hinova.';
      }
    }
  }

  return (
    <AppShell me={me} active="remuneracao">
      <div className="shell">
        <div className="page-heading">
          <div>
            <p className="eyebrow">Administração comercial</p>
            <h1>Editar plano de carreira</h1>
          </div>
          <a className="action-link" href="/administracao/remuneracao">
            Voltar
          </a>
        </div>
        <section className="card">
          <PlanoCarreiraForm plano={plano} />
        </section>
        {canApurar && (
          <section className="card apuracao-card">
            <div className="apuracao-header">
              <div>
                <p className="eyebrow">Bônus de produção</p>
                <h2>Apuração de veículos novos pagos</h2>
              </div>
              <p>Selecione o mês e o consultor para ver o resultado por veículo único.</p>
            </div>
            <form method="get" className="apuracao-filtros">
              <label>
                Mês de produção
                <input type="month" name="competencia" defaultValue={competencia} required />
              </label>
              <label>
                Consultor
                <select name="participanteId" defaultValue={participanteId} required>
                  <option value="">Selecione</option>
                  {mapeamentos.map((m) => <option key={m.userId} value={m.userId}>{m.userName}</option>)}
                </select>
              </label>
              <button type="submit">Apurar bônus</button>
            </form>
            {apuracaoError && <p role="alert" className="error">{apuracaoError}</p>}
            {apuracao && <ApuracaoBonusCard apuracao={apuracao} plano={plano} />}
          </section>
        )}
      </div>
    </AppShell>
  );
}
