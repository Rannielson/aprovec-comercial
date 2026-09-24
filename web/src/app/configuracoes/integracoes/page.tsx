import { notFound } from 'next/navigation';
import { AppShell } from '../../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import { Icon } from '../../components/app-icon';
import { formatDate } from '@/lib/format';
import type { HinovaCredenciaisStatus, HinovaMapeamento, HinovaVoluntario, Me, UserSummary } from '@/lib/types';
import { CredenciaisForm } from './credenciais-form';
import { DesvincularForm, VincularForm } from './mapeamento-form';

export default async function IntegracoesPage({
  searchParams,
}: {
  searchParams: Promise<{ query?: string }>;
}) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const canManage = me.permissions.some((p) => p.key === 'integracoes.gerenciar');
  if (!canManage) {
    return (
      <AppShell me={me} active="settings">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso às integrações.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const { query } = await searchParams;
  const status = await apiFetch<HinovaCredenciaisStatus>('/integracoes/hinova/credenciais');
  const mapeamentos = await apiFetch<HinovaMapeamento[]>('/integracoes/hinova/mapeamentos');

  let voluntarios: HinovaVoluntario[] = [];
  let usuarios: UserSummary[] = [];
  let searchError: string | null = null;
  if (status.configurado) {
    try {
      const params = new URLSearchParams();
      if (query) params.set('query', query);
      voluntarios = await apiFetch<HinovaVoluntario[]>(`/integracoes/hinova/voluntarios?${params.toString()}`);
      usuarios = (await apiFetch<UserSummary[]>('/users')).filter((u) => u.status === 'ativo');
    } catch {
      searchError = 'Não foi possível buscar voluntários na Hinova agora. Tente novamente em instantes.';
    }
  }

  return (
    <AppShell me={me} active="settings">
      <div className="shell">
        <div className="page-heading">
          <p className="eyebrow">Configurações</p>
          <h1>Integrações</h1>
          <p className="muted">Conecte a Hinova e ligue voluntários aos usuários do APROVEC.</p>
        </div>

        <section className="card">
          <h2>Credenciais Hinova</h2>
          <p className="muted">
            {status.configurado
              ? `Configurado em ${formatDate(status.atualizadoEm!.slice(0, 10))} por ${status.atualizadoPor}.`
              : 'Ainda não configurado.'}
          </p>
          <CredenciaisForm />
        </section>

        {status.configurado && (
          <section className="card">
            <h2>Mapeamento de voluntários</h2>
            <form method="get" className="search-field">
              <Icon name="search" />
              <input type="search" name="query" placeholder="Buscar por nome" defaultValue={query ?? ''} aria-label="Buscar voluntário por nome" />
            </form>

            {searchError && <p className="error">{searchError}</p>}

            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Voluntário</th>
                    <th>CPF</th>
                    <th>Vínculo</th>
                  </tr>
                </thead>
                <tbody>
                  {voluntarios.map((v) => (
                    <tr key={v.codigo}>
                      <td>{v.nome}</td>
                      <td>{v.cpf}</td>
                      <td>
                        <VincularForm voluntario={v} usuarios={usuarios} />
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        )}

        <section className="card">
          <h2>Vínculos atuais</h2>
          {mapeamentos.length === 0 ? (
            <p className="muted">Nenhum voluntário vinculado ainda.</p>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Usuário APROVEC</th>
                    <th>Voluntário Hinova</th>
                    <th>Vinculado em</th>
                    <th />
                  </tr>
                </thead>
                <tbody>
                  {mapeamentos.map((m) => (
                    <tr key={m.userId}>
                      <td>{m.userName}</td>
                      <td>{m.nomeHinova}</td>
                      <td>{formatDate(m.mappedAt.slice(0, 10))}</td>
                      <td><DesvincularForm userId={m.userId} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </section>
      </div>
    </AppShell>
  );
}
