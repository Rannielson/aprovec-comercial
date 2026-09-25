'use client';

import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import { useRouter } from 'next/navigation';
import { Icon } from '../../components/app-icon';
import { formatMoney, formatPercent } from '@/lib/format';
import type { TreeLayout, TreeNode } from '@/lib/tree-layout';
import type { HinovaVoluntario } from '@/lib/types';
import { ParticipanteSearch } from '../participante-search';
import { NOVA_ARVORE } from './constants';
import type { Participante } from './estrutura';
import { PersonDetail, type PersonDetailTarget } from './person-detail';

const MIN_ZOOM = 0.4;
const MAX_ZOOM = 1.4;
const PANEL_WIDTH = 320;

const initials = (name: string) =>
  name
    .split(' ')
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0])
    .join('');

const firstName = (name: string) => name.split(' ')[0].slice(0, 14);

export function Pyramid({
  layout,
  people,
  rates,
  roots,
  selectedRoot,
  showValues,
  canAddChild,
  initialTarget,
  voluntarios,
  searchQuery,
  searchError,
  competencia,
}: {
  layout: TreeLayout;
  people: Record<string, Participante>;
  rates: { own: number | null; uplineRates: { level: number; rate: number }[] };
  /** Every forest root, for the "Árvore em exibição" selector (independent of the current `root` filter). */
  roots: { id: string; name: string }[];
  selectedRoot: string;
  /** False when the viewer lacks `comissoes.visualizar` — amounts render as "—". */
  showValues: boolean;
  canAddChild: boolean;
  /** Which add panel is open on load (`?adicionar=`): a node id, NOVA_ARVORE, or null. */
  initialTarget: string | null;
  voluntarios: HinovaVoluntario[];
  searchQuery: string;
  searchError: string | null;
  /** Competência shown in the "Composição da comissão" detail drawer's subtitle. */
  competencia: string;
}) {
  const router = useRouter();
  const [zoom, setZoom] = useState(1);
  const [target, setTarget] = useState<string | null>(initialTarget);
  const [detail, setDetail] = useState<PersonDetailTarget | null>(null);
  const [panning, setPanning] = useState(false);
  const viewportRef = useRef<HTMLDivElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const pendingScroll = useRef<((viewport: HTMLDivElement) => void) | null>(null);
  const drag = useRef<{ x: number; y: number; left: number; top: number } | null>(null);

  const money = (value: number) => (showValues ? formatMoney(value) : '—');
  const { nodeWidth, nodeHeight, brand } = layout;
  const center = layout.width / 2;

  // Center the forest horizontally on load and whenever a different tree is chosen (mockup: afterRender).
  useEffect(() => {
    const v = viewportRef.current;
    if (v) v.scrollLeft = Math.max(0, (v.scrollWidth - v.clientWidth) / 2);
  }, [selectedRoot]);

  // Bring an opened add panel into view and put the cursor in its search box.
  useEffect(() => {
    const panel = panelRef.current;
    if (!panel) return;
    panel.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    panel.querySelector<HTMLInputElement>('input:not([type="hidden"])')?.focus({ preventScroll: true });
  }, [target]);

  // Scroll adjustments that must run after the new zoom has been laid out (mockup: refreshTree / fit-tree).
  useLayoutEffect(() => {
    const v = viewportRef.current;
    const apply = pendingScroll.current;
    pendingScroll.current = null;
    if (v && apply) apply(v);
  }, [zoom]);

  function zoomTo(next: number) {
    const v = viewportRef.current;
    if (v) {
      // Keep whatever is at the middle of the viewport in the middle after rescaling.
      const cx = (v.scrollLeft + v.clientWidth / 2) / zoom;
      const cy = (v.scrollTop + v.clientHeight / 2) / zoom;
      pendingScroll.current = (el) => {
        el.scrollLeft = cx * next - el.clientWidth / 2;
        el.scrollTop = cy * next - el.clientHeight / 2;
      };
    }
    setZoom(next);
  }

  function fit() {
    const v = viewportRef.current;
    if (!v) return;
    const next = Math.max(MIN_ZOOM, Math.min(1, (v.clientWidth - 32) / layout.width, (v.clientHeight - 32) / layout.height));
    const recenter = (el: HTMLDivElement) => {
      el.scrollLeft = Math.max(0, (el.scrollWidth - el.clientWidth) / 2);
      el.scrollTop = 0;
    };
    if (next === zoom) recenter(v);
    else {
      pendingScroll.current = recenter;
      setZoom(next);
    }
  }

  // Drag the dotted background to pan (mouse only — touch already scrolls natively).
  function onPointerDown(e: PointerEvent<HTMLDivElement>) {
    if (e.pointerType !== 'mouse' || e.button !== 0) return;
    if ((e.target as HTMLElement).closest('button, a, input, select, label, .pyramid-add-panel')) return;
    const v = e.currentTarget;
    drag.current = { x: e.clientX, y: e.clientY, left: v.scrollLeft, top: v.scrollTop };
    v.setPointerCapture(e.pointerId);
    setPanning(true);
  }

  function onPointerMove(e: PointerEvent<HTMLDivElement>) {
    const d = drag.current;
    if (!d) return;
    e.currentTarget.scrollLeft = d.left - (e.clientX - d.x);
    e.currentTarget.scrollTop = d.top - (e.clientY - d.y);
  }

  function endPan(e: PointerEvent<HTMLDivElement>) {
    if (!drag.current) return;
    drag.current = null;
    if (e.currentTarget.hasPointerCapture(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId);
    setPanning(false);
  }

  function onPanelKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    if (e.key === 'Escape') setTarget(null);
  }

  // --- Connector lines -------------------------------------------------------------------------
  const bandBottom = brand.y + brand.height;
  const junctionY = bandBottom + 31;
  const rootAnchors = layout.nodes.filter((n) => n.depth === 0).map((n) => ({ x: n.x + nodeWidth / 2, y: n.y }));
  if (layout.newRoot) rootAnchors.push({ x: layout.newRoot.x + nodeWidth / 2, y: layout.newRoot.y });

  // Every root's connector drops from the brand card, straight down to the junction line, then across.
  const brandPaths = rootAnchors.map((p) => `M${center} ${bandBottom} V${junctionY} H${p.x} V${p.y}`);

  // Level-2 (avô → neto) bypass connectors: computed for the whole forest, but only the pair(s)
  // touching the person whose drawer is open are drawn (see `visibleGrandparentEdges` below) — with
  // every pair always on, a long vertical chain stacks them into one illegible line (real bug, seen
  // once the tree grew past 3 levels). Selecting a person is how you trace their own 1% relationship.
  const nodesById = new Map(layout.nodes.map((n) => [n.id, n]));
  const grandparentEdges: { grandparent: TreeNode; node: TreeNode }[] = layout.nodes.flatMap((node) => {
    if (node.parentId === null) return [];
    const parent = nodesById.get(node.parentId);
    if (!parent || parent.parentId === null) return [];
    const grandparent = nodesById.get(parent.parentId);
    return grandparent ? [{ grandparent, node }] : [];
  });
  const visibleGrandparentEdges =
    detail?.kind === 'seller'
      ? grandparentEdges.filter((e) => e.grandparent.id === detail.id || e.node.id === detail.id)
      : [];

  // --- Add panel -------------------------------------------------------------------------------
  const targetNode = target && target !== NOVA_ARVORE ? layout.nodes.find((n) => n.id === target) : undefined;
  let panel: {
    key: string;
    title: string;
    subtitle: string;
    supervisorId: string | null;
    anchorX: number;
    anchorY: number;
  } | null = null;
  if (targetNode) {
    const person = people[targetNode.id];
    panel = {
      key: targetNode.id,
      title: 'Adicionar indicado',
      subtitle: `Indicação direta de ${person.name}`,
      supervisorId: targetNode.id,
      anchorX: targetNode.x + nodeWidth / 2,
      anchorY: targetNode.y + nodeHeight + 42,
    };
  } else if (target === NOVA_ARVORE) {
    // Without a placeholder slot (a single tree is in view), open right under the brand band.
    panel = {
      key: NOVA_ARVORE,
      title: 'Nova árvore',
      subtitle: 'Vendedor no topo de uma nova ramificação',
      supervisorId: null,
      anchorX: layout.newRoot ? layout.newRoot.x + nodeWidth / 2 : center,
      anchorY: layout.newRoot ? layout.newRoot.y + nodeHeight + 12 : bandBottom + 12,
    };
  }
  // The panel lives on the unscaled stage (so it stays readable at any zoom), pinned under its anchor.
  const panelLeft = panel
    ? Math.max(8, Math.min(panel.anchorX * zoom - PANEL_WIDTH / 2, layout.width * zoom - PANEL_WIDTH - 8))
    : 0;

  const newRootNote = rates.own !== null ? `${formatPercent(rates.own)} sobre a própria carteira` : null;

  return (
    <>
      <div className="pyramid-toolbar">
        <label>
          <Icon name="people" />
          <select
            aria-label="Árvore em exibição"
            value={selectedRoot}
            onChange={(e) => {
              const id = e.target.value;
              router.push(id ? `/administracao/arvore?root=${encodeURIComponent(id)}` : '/administracao/arvore');
            }}
          >
            <option value="">Todas as árvores</option>
            {roots.map((r) => (
              <option key={r.id} value={r.id}>
                Árvore de {r.name}
              </option>
            ))}
          </select>
        </label>
        <div className="pyramid-zoom">
          <button
            type="button"
            aria-label="Diminuir zoom"
            disabled={zoom <= MIN_ZOOM}
            onClick={() => zoomTo(Math.max(MIN_ZOOM, Math.round((zoom - 0.1) * 100) / 100))}
          >
            −
          </button>
          <span aria-live="polite">{Math.round(zoom * 100)}%</span>
          <button
            type="button"
            aria-label="Aumentar zoom"
            disabled={zoom >= MAX_ZOOM}
            onClick={() => zoomTo(Math.min(MAX_ZOOM, Math.round((zoom + 0.1) * 100) / 100))}
          >
            +
          </button>
          <button type="button" className="pyramid-fit" onClick={fit}>
            Enquadrar
          </button>
        </div>
      </div>

      <div
        ref={viewportRef}
        className={panning ? 'pyramid-viewport is-panning' : 'pyramid-viewport'}
        tabIndex={0}
        role="region"
        aria-label="Árvores de indicação. Role ou arraste para explorar as ramificações."
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={endPan}
        onPointerCancel={endPan}
      >
        <div className="pyramid-stage" style={{ width: layout.width * zoom, height: layout.height * zoom }}>
          <div
            className="pyramid-canvas"
            style={{ width: layout.width, height: layout.height, transform: `scale(${zoom})` }}
          >
            <svg className="pyramid-lines" width={layout.width} height={layout.height} aria-hidden="true">
              {brandPaths.map((d) => (
                <path key={d} className="brand-connector" d={d} />
              ))}
              {layout.edges.map(({ from, to }) => {
                const x = from.x + nodeWidth / 2;
                const y = from.y + nodeHeight;
                const toX = to.x + nodeWidth / 2;
                const parent = people[from.id];
                const label =
                  parent.referralRate !== null
                    ? `${formatPercent(parent.referralRate)} para ${firstName(parent.name)}`
                    : `Indicação · ${firstName(parent.name)}`;
                return (
                  <g key={to.id}>
                    <path className="direct-connector" d={`M${x} ${y} V${y + 56} H${toX} V${to.y}`} />
                    <rect className="connector-label-bg" x={toX - 60} y={to.y - 24} width={120} height={18} rx={5} />
                    <text className="connector-label" x={toX} y={to.y - 11}>
                      {label}
                    </text>
                  </g>
                );
              })}
              {visibleGrandparentEdges.map(({ grandparent, node }) => {
                const avo = people[grandparent.id];
                const rate2 = avo.uplineRules.find((r) => r.level === 2)?.rate ?? null;
                const label = rate2 !== null ? `${formatPercent(rate2)} para ${firstName(avo.name)}` : null;
                const bypassX = grandparent.x + nodeWidth + 32;
                const gpY = grandparent.y + nodeHeight / 2;
                const nodeY = node.y + nodeHeight / 2;
                const labelY = (gpY + nodeY) / 2;
                const labelX = bypassX + 62;
                return (
                  <g key={`${grandparent.id}-${node.id}`}>
                    <path
                      className="upline2-connector"
                      d={`M${grandparent.x + nodeWidth} ${gpY} H${bypassX} V${nodeY} H${node.x + nodeWidth}`}
                    />
                    {label && (
                      <>
                        <rect className="connector-label-bg" x={labelX - 62} y={labelY - 9} width={124} height={18} rx={5} />
                        <text className="connector-label upline2" x={labelX} y={labelY + 4}>
                          {label}
                        </text>
                      </>
                    )}
                  </g>
                );
              })}
            </svg>

            <div className="pyramid-brand" style={{ left: center - brand.width / 2, top: brand.y }}>
              <img src="/logo-aprovec.webp" alt="APROVEC Brasil" width={132} height={35} />
            </div>

            {layout.nodes.map((node) => {
              const p = people[node.id];
              const open = target === node.id;
              return (
                <div key={node.id} className="pyramid-node" data-person-id={node.id} style={{ left: node.x, top: node.y }}>
                  <button
                    type="button"
                    className="pyramid-person"
                    aria-label={`Ver composição da comissão de ${p.name}`}
                    onClick={() => setDetail({ kind: 'seller', id: node.id })}
                  >
                    <span className="pyramid-person-top">
                      <span className="avatar network-avatar">{initials(p.name)}</span>
                      <span>
                        <strong title={p.name}>{p.name}</strong>
                        <small>{node.depth === 0 ? 'Topo da árvore' : 'Carteira própria'}</small>
                      </span>
                      <span className="pyramid-rate">{p.rate !== null && !p.valuesHidden ? formatPercent(p.rate) : '—'}</span>
                    </span>
                    <span className="pyramid-person-value">
                      <small>Recebidos na carteira</small>
                      <strong>{p.valuesHidden ? '—' : money(p.recebido)}</strong>
                    </span>
                    <span className="pyramid-person-total">
                      <span>Comissão total</span>
                      <strong>{p.valuesHidden ? '—' : money(p.comissao)}</strong>
                    </span>
                  </button>
                  <div className="pyramid-node-actions">
                    {canAddChild ? (
                      <button
                        type="button"
                        className={open ? 'pyramid-add is-open' : 'pyramid-add'}
                        aria-label={`Adicionar indicado a ${p.name}`}
                        title={`Adicionar indicado a ${p.name}`}
                        aria-expanded={open}
                        onClick={() => setTarget(open ? null : node.id)}
                      >
                        <Icon name="plus" />
                      </button>
                    ) : (
                      <span className="pyramid-junction" />
                    )}
                    {node.childCount > 0 && (
                      <span className="pyramid-children">
                        {node.childCount} {node.childCount === 1 ? 'direto' : 'diretos'}
                      </span>
                    )}
                  </div>
                </div>
              );
            })}

            {layout.newRoot && (
              <button
                type="button"
                className="pyramid-new-root"
                style={{ left: layout.newRoot.x, top: layout.newRoot.y }}
                aria-expanded={target === NOVA_ARVORE}
                onClick={() => setTarget(target === NOVA_ARVORE ? null : NOVA_ARVORE)}
              >
                <span>
                  <Icon name="plus" />
                </span>
                <strong>Nova árvore</strong>
                <small>
                  Adicione um vendedor no topo
                  <br />
                  de uma nova ramificação
                </small>
                {newRootNote && <em>{newRootNote}</em>}
              </button>
            )}
          </div>

          {panel && (
            <div
              ref={panelRef}
              className="pyramid-add-panel"
              role="group"
              aria-label={panel.title}
              style={{ left: panelLeft, top: panel.anchorY * zoom, width: PANEL_WIDTH }}
              onKeyDown={onPanelKeyDown}
            >
              <div className="pyramid-add-panel-head">
                <div>
                  <strong>{panel.title}</strong>
                  <small>{panel.subtitle}</small>
                </div>
                <button type="button" className="icon-button" aria-label="Fechar" onClick={() => setTarget(null)}>
                  ×
                </button>
              </div>
              {searchError && <p className="error">{searchError}</p>}
              <ParticipanteSearch
                key={panel.key}
                voluntarios={voluntarios}
                supervisorId={panel.supervisorId}
                supervisorLabel={
                  panel.supervisorId ? 'Busque o voluntário na Hinova e confirme o e-mail.' : 'Sem indicador · topo de uma nova árvore.'
                }
                searchQuery={searchQuery}
                preserveParams={{ adicionar: panel.key, ...(selectedRoot ? { root: selectedRoot } : {}) }}
              />
              {searchQuery && !searchError && voluntarios.length === 0 && (
                <p className="muted">Nenhum voluntário encontrado para “{searchQuery}”.</p>
              )}
            </div>
          )}
        </div>
      </div>
      {detail && (
        <PersonDetail
          target={detail}
          people={people}
          competencia={competencia}
          showValues={showValues}
          canAddChild={canAddChild}
          uplineRates={rates.uplineRates}
          onClose={() => setDetail(null)}
          onAddIndicado={(personId) => {
            setDetail(null);
            setTarget(personId);
          }}
        />
      )}
    </>
  );
}
