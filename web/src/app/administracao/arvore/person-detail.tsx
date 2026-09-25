'use client';

import { Fragment, useEffect } from 'react';
import { Icon } from '../../components/app-icon';
import { formatCompetencia, formatMoney, formatPercent, ruleLabel } from '@/lib/format';
import type { Participante } from './estrutura';

const round2 = (value: number) => Math.round(value * 100) / 100;

const initials = (name: string) =>
  name
    .split(' ')
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0])
    .join('');

export type PersonDetailTarget = { kind: 'seller'; id: string };

export function PersonDetail({
  target,
  people,
  competencia,
  showValues,
  canAddChild,
  onClose,
  onAddIndicado,
}: {
  target: PersonDetailTarget;
  people: Record<string, Participante>;
  competencia: string;
  showValues: boolean;
  canAddChild: boolean;
  onClose: () => void;
  onAddIndicado: (personId: string) => void;
}) {
  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [onClose]);

  const money = (value: number) => (showValues ? formatMoney(value) : '—');
  const subtitle = `${formatCompetencia(competencia)}`;

  const p = people[target.id];
  if (!p) return null;

  // Level 1 (direct reports) and level 2 (their own reports, i.e. p's grandchildren), grouped by
  // lineage rather than two disconnected flat lists — grandchildren render nested under the direct
  // report that actually produced them, so the trail (who leads to whom) stays visible.
  const diretos = Object.values(people).filter((candidate) => candidate.parentId === p.id);
  const netosOf = (directId: string) => Object.values(people).filter((candidate) => candidate.parentId === directId);
  const nivel1 = p.uplineRules.find((r) => r.level === 1) ?? null;
  const nivel2 = p.uplineRules.find((r) => r.level === 2) ?? null;

  return (
    <Overlay title="Composição da comissão" subtitle={`${p.name} · ${subtitle}`} onClose={onClose}>
      <div className="drawer-person">
        <span className="avatar network-avatar">{initials(p.name)}</span>
        <div>
          <h3>{p.name}</h3>
          <p>{p.email}</p>
        </div>
      </div>
      <dl className="detail-list">
        <div>
          <dt>Base própria recebida</dt>
          <dd>{p.valuesHidden ? '—' : money(p.recebido)}</dd>
        </div>
        <div>
          <dt>Comissão própria{p.rate !== null && !p.valuesHidden ? ` · ${formatPercent(p.rate)}` : ''}</dt>
          <dd>{p.valuesHidden ? '—' : money(p.ownAmount)}</dd>
        </div>
        {p.uplineRules.map((rule) => (
          <Fragment key={rule.level}>
            <div>
              <dt>Base da supervisão · {ruleLabel(rule)}</dt>
              <dd>{money(rule.base)}</dd>
            </div>
            <div>
              <dt>Comissão de supervisão · {ruleLabel(rule)} · {formatPercent(rule.rate)}</dt>
              <dd>{money(rule.amount)}</dd>
            </div>
          </Fragment>
        ))}
      </dl>
      <section className="calculation">
        <span className="eyebrow">TOTAL APURADO</span>
        <div>
          <strong>{p.valuesHidden ? '—' : money(p.comissao)}</strong>
        </div>
        <p>Comissão da carteira própria + comissão de cada nível de supervisão.</p>
      </section>
      <div className="rules-section">
        <h3>Árvore de indicados</h3>
        {diretos.length === 0 ? (
          <p>Nenhum consultor supervisionado diretamente.</p>
        ) : (
          <div className="mini-tree">
            {diretos.map((direto) => {
              const netos = netosOf(direto.id);
              return (
                <div className="mini-tree-branch" key={direto.id}>
                  <div className="mini-tree-node">
                    <span className="avatar network-avatar">{initials(direto.name)}</span>
                    <div>
                      <strong>{direto.name}</strong>
                      <small>
                        {nivel1 !== null && !p.valuesHidden && !direto.valuesHidden
                          ? `${formatMoney(direto.recebido)} × ${formatPercent(nivel1.rate)} = ${formatMoney(round2(direto.recebido * nivel1.rate))}`
                          : '1º nível'}
                      </small>
                    </div>
                  </div>
                  {netos.length > 0 && (
                    <div className="mini-tree-children">
                      {netos.map((neto) => (
                        <div className="mini-tree-node is-neto" key={neto.id}>
                          <span className="avatar network-avatar">{initials(neto.name)}</span>
                          <div>
                            <strong>{neto.name}</strong>
                            <small>
                              {nivel2 !== null && !p.valuesHidden && !neto.valuesHidden
                                ? `${formatMoney(neto.recebido)} × ${formatPercent(nivel2.rate)} = ${formatMoney(round2(neto.recebido * nivel2.rate))}`
                                : '2º nível'}
                            </small>
                          </div>
                        </div>
                      ))}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        )}
      </div>
      <div className="detail-actions">
        {canAddChild && p.status !== 'desligado' && (
          <button type="button" onClick={() => onAddIndicado(p.id)}>
            <Icon name="plus" />
            Adicionar indicado
          </button>
        )}
        <button type="button" className="secondary" disabled title="Em breve">
          Editar cadastro e vínculo
          <Icon name="arrow" />
        </button>
      </div>
    </Overlay>
  );
}

function Overlay({ title, subtitle, onClose, children }: { title: string; subtitle: string; onClose: () => void; children: React.ReactNode }) {
  return (
    <div className="overlay">
      <button type="button" className="overlay-backdrop" aria-label="Fechar detalhe" onClick={onClose} />
      <section className="drawer" role="dialog" aria-modal="true" aria-labelledby="drawer-title">
        <div className="drawer-header">
          <div>
            <h2 id="drawer-title">{title}</h2>
            <p>{subtitle}</p>
          </div>
          <button type="button" className="icon-button" aria-label="Fechar detalhe" onClick={onClose}>
            <Icon name="close" />
          </button>
        </div>
        <div className="drawer-content">{children}</div>
      </section>
    </div>
  );
}
