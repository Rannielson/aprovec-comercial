'use client';

import { Fragment, useEffect } from 'react';
import { Icon } from '../../components/app-icon';
import { formatCompetencia, formatMoney, formatPercent, ruleLabel } from '@/lib/format';
import type { Gestor, Participante } from './estrutura';

const round2 = (value: number) => Math.round(value * 100) / 100;

const initials = (name: string) =>
  name
    .split(' ')
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0])
    .join('');

export type PersonDetailTarget = { kind: 'seller'; id: string } | { kind: 'gestor'; id: string };

export function PersonDetail({
  target,
  people,
  gestores,
  competencia,
  showValues,
  canAddChild,
  onClose,
  onAddIndicado,
}: {
  target: PersonDetailTarget;
  people: Record<string, Participante>;
  gestores: Gestor[];
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

  if (target.kind === 'gestor') {
    const g = gestores.find((candidate) => candidate.id === target.id);
    if (!g) return null;
    return (
      <Overlay title="Composição da comissão" subtitle={`${g.name} · ${subtitle}`} onClose={onClose}>
        <div className="drawer-person">
          <span className="avatar manager-avatar">
            <Icon name="shield" />
          </span>
          <div>
            <h3>{g.name}</h3>
            <p>Gestão de toda a base</p>
          </div>
        </div>
        <dl className="detail-list">
          <div>
            <dt>Recebimentos de toda a base</dt>
            <dd>{money(g.recebido)}</dd>
          </div>
          <div>
            <dt>Taxa de gestão</dt>
            <dd>{formatPercent(g.rate)}</dd>
          </div>
        </dl>
        <section className="calculation">
          <span className="eyebrow">TOTAL APURADO</span>
          <div>
            <strong>{money(g.comissao)}</strong>
          </div>
          <p>Recebimentos de toda a operação × taxa de gestão. Cada boleto pago entra uma única vez.</p>
        </section>
      </Overlay>
    );
  }

  const p = people[target.id];
  if (!p) return null;

  // Direct reports only (this person's level-1 supervision) — the per-person breakdown below can't
  // drill into level 2+ without walking the whole subtree, so it stays scoped to what it can show
  // accurately. The detail-list above already lists every level from `uplineRules`, level 1 included.
  const diretos = Object.values(people).filter((candidate) => candidate.parentId === p.id);
  const nivel1 = p.uplineRules.find((r) => r.level === 1) ?? null;

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
        <h3>Supervisão direta</h3>
        {diretos.length === 0 ? (
          <p>Nenhum consultor supervisionado diretamente.</p>
        ) : (
          diretos.map((s) => (
            <p key={s.id}>
              {s.name}
              {nivel1 !== null && !p.valuesHidden && !s.valuesHidden
                ? ` · ${formatMoney(s.recebido)} × ${formatPercent(nivel1.rate)} = ${formatMoney(round2(s.recebido * nivel1.rate))}`
                : ''}
            </p>
          ))
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
