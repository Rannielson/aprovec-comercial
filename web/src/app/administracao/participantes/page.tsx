import { notFound } from 'next/navigation';
import { AppShell } from '../../app-shell';
import { ApiError, apiFetch, currentHost } from '@/lib/api';
import { Icon } from '../../components/app-icon';
import { messageFor } from '@/lib/errors';
import type { HinovaVoluntario, Me, UserNode } from '@/lib/types';
import { ParticipanteSearch } from '../participante-search';

/** Shape this page needs from `GET /roles` — only `usuarios.gerenciar_perfis` holders can call it. */
type RoleSummary = { id: string; sourceTemplateKey: string | null };

const TEMPLATE_LABELS: Record<string, string> = {
  administrador: 'Administrador',
  coordenador: 'Coordenador',
  consultor: 'Consultor',
};
// Most senior template wins when someone holds more than one role.
const TEMPLATE_PRIORITY = ['administrador', 'coordenador', 'consultor'];

export default async function ParticipantesPage({
  searchParams,
}: {
  searchParams: Promise<{ query?: string; buscarParticipante?: string; adicionar?: string }>;
}) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const has = (key: string) => me.permissions.some((p) => p.key === key);
  if (!has('estrutura.visualizar')) {
    return (
      <AppShell me={me} active="participantes">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso à estrutura comercial.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const { query, buscarParticipante, adicionar } = await searchParams;
  const filterQuery = query?.trim() ?? '';
  const searchQuery = buscarParticipante?.trim() ?? '';
  // POST /users with Hinova fields needs usuarios.convidar + integracoes.gerenciar; since this button
  // always creates a root (supervisorId: null), estrutura.editar isn't needed (unlike a child add).
  const canAdd = has('usuarios.convidar') && has('integracoes.gerenciar');
  const adicionarOpen = canAdd && adicionar === '1';

  // GET /users has no server-side query param (confirmed against UserEndpoints.ListAsync), so the
  // name/e-mail filter below runs client-side, same as the brief calls for.
  const users = await apiFetch<UserNode[]>('/users');

  // GET /roles needs usuarios.gerenciar_perfis, a permission this page's own viewers (e.g. a
  // coordenador, who only has estrutura.visualizar) don't necessarily hold — so the perfil
  // breakdown degrades to a generic label instead of failing the page when it's missing.
  const canSeeRoles = has('usuarios.gerenciar_perfis');
  const roles = canSeeRoles ? await apiFetch<RoleSummary[]>('/roles') : [];
  const templateByRoleId = new Map(roles.map((r) => [r.id, r.sourceTemplateKey]));

  function perfilFor(roleIds: string[]): string {
    if (!canSeeRoles) return 'Participante';
    const templates = new Set(roleIds.map((id) => templateByRoleId.get(id)).filter((t): t is string => !!t));
    const key = TEMPLATE_PRIORITY.find((t) => templates.has(t));
    return key ? TEMPLATE_LABELS[key] : 'Participante';
  }

  const userName = new Map(users.map((u) => [u.id, u.name]));
  const normalize = (s: string) => s.toLocaleLowerCase('pt-BR');
  const filtered = filterQuery
    ? users.filter((u) => normalize(`${u.name} ${u.email}`).includes(normalize(filterQuery)))
    : users;

  const totalConsultores = canSeeRoles ? users.filter((u) => perfilFor(u.roleIds) === 'Consultor').length : null;
  const totalCoordenadores = canSeeRoles ? users.filter((u) => perfilFor(u.roleIds) === 'Coordenador').length : null;

  let voluntarios: HinovaVoluntario[] = [];
  let searchError: string | null = null;
  // Fetched with an empty query too (not just once searchQuery is set) -- the panel should show
  // every active voluntário right away, not stay empty until the admin types something.
  if (adicionarOpen) {
    try {
      voluntarios = await apiFetch<HinovaVoluntario[]>(
        `/integracoes/hinova/voluntarios?${new URLSearchParams({ query: searchQuery }).toString()}`,
      );
    } catch (error) {
      searchError =
        error instanceof ApiError
          ? messageFor(error.code)
          : 'Não foi possível buscar voluntários na Hinova agora. Tente novamente em instantes.';
    }
  }

  function hrefFor(overrides: { adicionar?: string | null }): string {
    const p = new URLSearchParams();
    if (filterQuery) p.set('query', filterQuery);
    const nextAdicionar = overrides.adicionar !== undefined ? overrides.adicionar : adicionar;
    if (nextAdicionar) p.set('adicionar', nextAdicionar);
    const qs = p.toString();
    return qs ? `/administracao/participantes?${qs}` : '/administracao/participantes';
  }

  return (
    <AppShell me={me} active="participantes">
      <div className="shell">
        <div className="page-heading with-action">
          <div>
            <p className="eyebrow">Administração comercial</p>
            <h1>Participantes</h1>
            <p className="muted">Cadastre as pessoas e defina quem indicou cada vendedor.</p>
          </div>
          {canAdd && !adicionarOpen && (
            <a className="action-link" href={hrefFor({ adicionar: '1' })}>
              <Icon name="plus" />
              Novo participante
            </a>
          )}
        </div>

        {adicionarOpen && (
          <section className="card">
            <div className="pyramid-header">
              <div>
                <h2>Novo participante</h2>
                <p>Busque o voluntário na Hinova e confirme o e-mail.</p>
              </div>
              <a className="action-link" href={hrefFor({ adicionar: null })}>
                Cancelar
              </a>
            </div>
            {searchError && <p className="error">{searchError}</p>}
            <ParticipanteSearch
              voluntarios={voluntarios}
              supervisorId={null}
              supervisorLabel="Sem indicador · topo de uma nova árvore."
              searchQuery={searchQuery}
              preserveParams={{ adicionar: '1', ...(filterQuery ? { query: filterQuery } : {}) }}
            />
            {searchQuery && !searchError && voluntarios.length === 0 && (
              <p className="muted">Nenhum voluntário encontrado para “{searchQuery}”.</p>
            )}
          </section>
        )}

        <section className="card">
          <div className="pyramid-header">
            <div>
              <h2>Pessoas da operação</h2>
              <p>
                {users.length} {users.length === 1 ? 'participante' : 'participantes'}
                {canSeeRoles ? (
                  <>
                    {' '}
                    · {totalConsultores} {totalConsultores === 1 ? 'consultor' : 'consultores'} · {totalCoordenadores}{' '}
                    {totalCoordenadores === 1 ? 'coordenador' : 'coordenadores'} de toda a base
                  </>
                ) : null}
              </p>
            </div>
            <form method="get" className="search-field">
              {adicionar && <input type="hidden" name="adicionar" value={adicionar} />}
              <Icon name="search" />
              <input
                type="search"
                name="query"
                placeholder="Buscar nome ou e-mail"
                defaultValue={query ?? ''}
                aria-label="Buscar participantes"
              />
            </form>
          </div>

          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Nome</th>
                  <th>E-mail</th>
                  <th>Perfil</th>
                  <th>Supervisor</th>
                </tr>
              </thead>
              <tbody>
                {filtered.length === 0 ? (
                  <tr>
                    <td colSpan={4} className="empty-state">
                      <Icon name="people" />
                      <strong>Nenhum participante encontrado</strong>
                      <span>Tente outro nome ou e-mail.</span>
                    </td>
                  </tr>
                ) : (
                  filtered.map((u) => (
                    <tr key={u.id}>
                      <td>{u.name}</td>
                      <td>{u.email}</td>
                      <td>
                        <span className={perfilFor(u.roleIds) === 'Consultor' ? 'badge progress' : 'badge neutral'}>
                          {perfilFor(u.roleIds)}
                        </span>
                      </td>
                      <td>{u.supervisorId ? (userName.get(u.supervisorId) ?? '—') : 'Sem indicador'}</td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </section>
      </div>
    </AppShell>
  );
}
