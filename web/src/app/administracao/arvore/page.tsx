import { notFound } from 'next/navigation';
import { AppShell } from '../../app-shell';
import { ApiError, apiFetch, currentHost } from '@/lib/api';
import { Icon } from '../../components/app-icon';
import { messageFor } from '@/lib/errors';
import { currentCompetencia, formatCompetencia, formatMoney, formatPercent } from '@/lib/format';
import { treeLayout } from '@/lib/tree-layout';
import type { Commissions, HinovaVoluntario, Me, UserNode } from '@/lib/types';
import { NOVA_ARVORE } from './constants';
import { buildEstrutura, type Participante } from './estrutura';
import { Pyramid } from './pyramid';

export default async function ArvorePage({
  searchParams,
}: {
  searchParams: Promise<{ root?: string; modo?: string; buscarParticipante?: string; adicionar?: string }>;
}) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const has = (key: string) => me.permissions.some((p) => p.key === key);
  if (!has('estrutura.visualizar')) {
    return (
      <AppShell me={me} active="arvore">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso à estrutura comercial.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const { root, modo, buscarParticipante, adicionar } = await searchParams;
  const mode = modo === 'lista' ? 'lista' : 'arvore';
  const searchQuery = buscarParticipante?.trim() ?? '';

  // Each API call below has its own permission; the page degrades instead of failing when one is missing.
  const showValues = has('comissoes.visualizar');
  // Existence alone isn't enough: an `own` scope returns only the viewer's own row (see buildEstrutura).
  const commissionScope = me.permissions.find((p) => p.key === 'comissoes.visualizar')?.scope ?? null;
  // POST /users with Hinova fields needs usuarios.convidar + integracoes.gerenciar (and the voluntário
  // search needs integracoes.gerenciar); setting a supervisor additionally needs estrutura.editar.
  const canAddRoot = has('usuarios.convidar') && has('integracoes.gerenciar');
  const canAddChild = canAddRoot && has('estrutura.editar');

  const competencia = currentCompetencia();
  const [users, commissions] = await Promise.all([
    apiFetch<UserNode[]>('/users'),
    showValues ? apiFetch<Commissions>(`/commissions/${competencia}`) : Promise.resolve(null),
  ]);

  const { sellers, rates } = buildEstrutura(users, commissions, { id: me.id, commissionScope });
  const people: Record<string, Participante> = Object.fromEntries(sellers.map((s) => [s.id, s]));
  const roots = sellers.filter((s) => s.parentId === null).map((s) => ({ id: s.id, name: s.name }));
  // Like the mockup, only a real tree top is a valid `root`; anything else falls back to all trees.
  const selectedRoot = root && roots.some((r) => r.id === root) ? root : '';

  const layout = treeLayout(
    sellers.map((s) => ({ id: s.id, parentId: s.parentId })),
    { rootId: selectedRoot || null, newRoot: canAddRoot },
  );

  const initialTarget =
    adicionar === NOVA_ARVORE && canAddRoot ? NOVA_ARVORE : adicionar && canAddChild && people[adicionar] ? adicionar : null;

  let voluntarios: HinovaVoluntario[] = [];
  let searchError: string | null = null;
  // Fetched unconditionally (not just when searchQuery is set): which node's "+" panel is open is
  // client-only state (no navigation on click), so the list has to already be here, ready to show
  // in full, the moment any panel opens -- not just after the admin types something.
  if (mode === 'arvore' && canAddRoot) {
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

  const money = (value: number) => (showValues ? formatMoney(value) : '—');
  const userName = new Map(users.map((u) => [u.id, u.name]));

  function hrefFor(nextMode: 'arvore' | 'lista'): string {
    const p = new URLSearchParams();
    if (selectedRoot) p.set('root', selectedRoot);
    if (nextMode === 'lista') p.set('modo', 'lista');
    const qs = p.toString();
    return qs ? `/administracao/arvore?${qs}` : '/administracao/arvore';
  }

  return (
    <AppShell me={me} active="arvore">
      <div className="shell">
        <div className="page-heading with-action">
          <div>
            <p className="eyebrow">Administração comercial</p>
            <h1>Árvore comissionada</h1>
            <p className="muted">Construa cada ramificação, pessoa por pessoa.</p>
          </div>
          {canAddRoot && (
            <a className="action-link" href={`/administracao/arvore?adicionar=${NOVA_ARVORE}`}>
              <Icon name="plus" />
              Nova árvore
            </a>
          )}
        </div>

        <section className="card pyramid-panel">
          <div className="pyramid-header">
            <div>
              <h2>Uma base. Várias árvores.</h2>
              <p>
                {sellers.length} {sellers.length === 1 ? 'vendedor' : 'vendedores'} · {roots.length}{' '}
                {roots.length === 1 ? 'árvore' : 'árvores'} · competência de {formatCompetencia(competencia)}
                {canAddChild && mode === 'arvore' ? ' · Use o + abaixo de cada pessoa para adicionar um indicado.' : ''}
              </p>
            </div>
            <div className="segmented" role="group" aria-label="Visualização da estrutura">
              <a href={hrefFor('arvore')} className={mode === 'arvore' ? 'selected' : undefined} aria-current={mode === 'arvore' ? 'page' : undefined}>
                <Icon name="people" />
                Árvore
              </a>
              <a href={hrefFor('lista')} className={mode === 'lista' ? 'selected' : undefined} aria-current={mode === 'lista' ? 'page' : undefined}>
                <Icon name="grid" />
                Lista
              </a>
            </div>
          </div>

          {mode === 'arvore' ? (
            <Pyramid
              layout={layout}
              people={people}
              rates={{ own: rates.own }}
              roots={roots}
              selectedRoot={selectedRoot}
              showValues={showValues}
              canAddChild={canAddChild}
              competencia={competencia}
              initialTarget={initialTarget}
              voluntarios={voluntarios}
              searchQuery={searchQuery}
              searchError={searchError}
            />
          ) : (
            <div className="table-wrap">
              <table className="pyramid-list">
                <thead>
                  <tr>
                    <th>Participante</th>
                    <th>Perfil</th>
                    <th>Indicador direto</th>
                    <th className="number">Taxa</th>
                    <th className="number">Recebidos na carteira</th>
                    <th className="number">Comissão total</th>
                  </tr>
                </thead>
                <tbody>
                  {sellers.length === 0 ? (
                    <tr>
                      <td colSpan={6} className="empty-state">
                        <Icon name="people" />
                        <strong>Nenhum participante ainda</strong>
                        <span>Comece uma nova árvore para cadastrar o primeiro vendedor.</span>
                      </td>
                    </tr>
                  ) : (
                    <>
                      {sellers.map((s) => (
                        <tr key={s.id}>
                          <td>
                            <PersonLabel name={s.name} detail={s.email} />
                          </td>
                          <td>
                            <span className="badge progress">Vendedor</span>
                          </td>
                          <td>{s.supervisorId ? (userName.get(s.supervisorId) ?? '—') : 'Sem indicador'}</td>
                          <td className="number">{s.rate !== null && !s.valuesHidden ? formatPercent(s.rate) : '—'}</td>
                          <td className="number amount">{s.valuesHidden ? '—' : money(s.recebido)}</td>
                          <td className={s.comissao > 0 && !s.valuesHidden ? 'number amount earned' : 'number amount muted'}>
                            {s.valuesHidden ? '—' : money(s.comissao)}
                          </td>
                        </tr>
                      ))}
                    </>
                  )}
                </tbody>
              </table>
            </div>
          )}

          <div className="pyramid-legend">
            <span>
              <i className="legend-direct" />
              Indicador{rates.referral !== null ? `: ${formatPercent(rates.referral)}` : ''} sobre o nível direto
            </span>
            <span>
              {rates.own !== null && <b>{formatPercent(rates.own)}</b>} Carteira própria de cada vendedor
            </span>
          </div>
        </section>

        <div className="network-rule-strip">
          <Icon name="people" />
          <div>
            <strong>Cada indicado pode formar a sua própria ramificação.</strong>
            <p>
              A árvore cresce em novos níveis. Cada pessoa recebe sobre a própria carteira e sobre as carteiras de seus
              indicados, conforme o plano de remuneração. Novos participantes começam sem recebimentos.
            </p>
          </div>
        </div>
      </div>
    </AppShell>
  );
}

function PersonLabel({ name, detail }: { name: string; detail: string }) {
  return (
    <div className="associate-button">
      <span className="row-avatar network-avatar">
        {name
          .split(' ')
          .filter(Boolean)
          .slice(0, 2)
          .map((part) => part[0])
          .join('')}
      </span>
      <span>
        <strong>{name}</strong>
        <span className="plate">{detail}</span>
      </span>
    </div>
  );
}
