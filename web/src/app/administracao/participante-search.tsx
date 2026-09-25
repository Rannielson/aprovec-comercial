'use client';

import { useActionState, useState } from 'react';
import type { FormState } from '@/lib/form-state';
import type { HinovaVoluntario } from '@/lib/types';
import { criarParticipante } from './actions';

export function ParticipanteSearch({
  voluntarios,
  supervisorId,
  supervisorLabel,
  searchQuery,
  preserveParams,
}: {
  voluntarios: HinovaVoluntario[];
  supervisorId: string | null;
  supervisorLabel: string;
  /** The current `buscarParticipante` value, shown back in the search box. */
  searchQuery?: string;
  /** Other querystring values the page needs to survive the search's GET round-trip. */
  preserveParams?: Record<string, string>;
}) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(criarParticipante, {});
  const [selected, setSelected] = useState<HinovaVoluntario | null>(null);

  if (state.message) {
    return <p className="success">{state.message}</p>;
  }

  return (
    <div className="participante-search">
      <p className="muted">{supervisorLabel}</p>
      {!selected ? (
        <form method="get" className="search-field">
          {Object.entries(preserveParams ?? {}).map(([name, value]) => (
            <input key={name} type="hidden" name={name} value={value} />
          ))}
          <input
            type="search"
            name="buscarParticipante"
            placeholder="Buscar por nome"
            aria-label="Buscar voluntário por nome"
            defaultValue={searchQuery ?? ''}
          />
        </form>
      ) : null}
      {!selected &&
        voluntarios.map((v) => (
          <button
            key={v.codigo}
            type="button"
            className="participante-option"
            disabled={v.jaVinculado}
            onClick={() => setSelected(v)}
          >
            <strong>{v.nome}</strong>
            {v.jaVinculado ? <span className="badge neutral">Vinculado a {v.vinculadoA}</span> : <span className="muted">{v.cpf}</span>}
          </button>
        ))}
      {selected && (
        <form action={formAction} className="form">
          <input type="hidden" name="supervisorId" value={supervisorId ?? ''} />
          <input type="hidden" name="codigoVoluntario" value={selected.codigo} />
          <input type="hidden" name="nomeHinova" value={selected.nome} />
          <input type="hidden" name="cpfHinova" value={selected.cpf} />
          <p>
            Adicionar <strong>{selected.nome}</strong>?
          </p>
          <label>
            E-mail
            <input type="email" name="email" required autoComplete="off" />
          </label>
          <label>
            Senha inicial
            <input type="password" name="password" required minLength={10} maxLength={128} autoComplete="new-password" />
          </label>
          <p className="muted">A Hinova nem sempre tem e-mail cadastrado — confirme o e-mail correto, é para onde vai o convite de acesso.</p>
          <p className="muted">A senha vale para o primeiro acesso — combine com {selected.nome} por fora (WhatsApp, telefone). Mínimo de 10 caracteres.</p>
          {state.error && <p role="alert" className="error">{state.error}</p>}
          <div className="inline">
            <button type="submit" disabled={pending}>{pending ? 'Adicionando…' : 'Confirmar'}</button>
            <button type="button" className="secondary" onClick={() => setSelected(null)}>Cancelar</button>
          </div>
        </form>
      )}
    </div>
  );
}
