'use client';

import { useState, type FormEvent } from 'react';
import type { FormState } from '@/lib/form-state';
import { aprovarSolicitacao, rejeitarSolicitacao, reprocessarSolicitacao, vincularVoluntarioExistente, type ApprovalState } from './actions';

type ActionKind = 'approve' | 'reject' | 'link' | 'retry';

export function SolicitacaoActions({ id, email, mode, retryLabel, activeId, onStart, onFinish }: {
  id: string;
  email: string;
  mode: 'review' | 'retry';
  retryLabel: string;
  activeId: string | null;
  onStart: (id: string) => boolean;
  onFinish: (id: string) => void;
}) {
  const [approveState, setApproveState] = useState<ApprovalState>({});
  const [rejectState, setRejectState] = useState<FormState>({});
  const [linkState, setLinkState] = useState<FormState>({});
  const [retryState, setRetryState] = useState<FormState>({});
  const [working, setWorking] = useState<ActionKind | null>(null);
  const busy = activeId !== null || working !== null;
  const existente = approveState.existente;
  const emailDiferente = existente?.email && existente.email.trim().toLowerCase() !== email.trim().toLowerCase();

  async function submit(event: FormEvent<HTMLFormElement>, kind: ActionKind) {
    event.preventDefault();
    const formData = new FormData(event.currentTarget);
    if (!onStart(id)) return;
    setWorking(kind);
    if (kind === 'approve') setApproveState({});
    if (kind === 'reject') setRejectState({});
    if (kind === 'link') setLinkState({});
    if (kind === 'retry') setRetryState({});
    try {
      if (kind === 'approve') setApproveState(await aprovarSolicitacao({}, formData));
      if (kind === 'reject') setRejectState(await rejeitarSolicitacao({}, formData));
      if (kind === 'link') setLinkState(await vincularVoluntarioExistente({}, formData));
      if (kind === 'retry') setRetryState(await reprocessarSolicitacao({}, formData));
    } catch {
      const error = { error: 'Não foi possível concluir a solicitação. Verifique se ela continua pendente e tente novamente.' };
      if (kind === 'approve') setApproveState(error);
      if (kind === 'reject') setRejectState(error);
      if (kind === 'link') setLinkState(error);
      if (kind === 'retry') setRetryState(error);
    } finally {
      setWorking(null);
      onFinish(id);
    }
  }

  return (
    <div className="solicitacao-actions">
      <div className="inline">
        {mode === 'retry' ? (
          <form onSubmit={(event) => void submit(event, 'retry')}>
            <input type="hidden" name="id" value={id} />
            <button type="submit" disabled={busy}>{working === 'retry' ? 'Enviando…' : retryLabel}</button>
          </form>
        ) : (
          <form onSubmit={(event) => void submit(event, 'approve')}>
            <input type="hidden" name="id" value={id} />
            <button type="submit" disabled={busy}>{working === 'approve' ? 'Verificando…' : 'Verificar vínculo'}</button>
          </form>
        )}
        <form onSubmit={(event) => void submit(event, 'reject')}>
          <input type="hidden" name="id" value={id} />
          <button type="submit" className="secondary" disabled={busy}>{working === 'reject' ? 'Rejeitando…' : 'Rejeitar'}</button>
        </form>
      </div>
      {approveState.error && <p role="alert" className="error">{approveState.error}</p>}
      {rejectState.error && <p role="alert" className="error">{rejectState.error}</p>}
      {retryState.error && <p role="alert" className="error">{retryState.error}</p>}
      {existente?.encontrado && (
        <div className="solicitacao-existing" role="status">
          <strong>Cadastro encontrado na Hinova</strong>
          <p>Nome: {existente.nome}</p>
          <p>Código: {existente.codigoVoluntario}</p>
          {existente.email && <p>E-mail na Hinova: {existente.email}</p>}
          {emailDiferente && <p className="solicitacao-existing-warning">O e-mail da Hinova é diferente do informado na solicitação. Confira a identidade antes de vincular.</p>}
          {existente.jaVinculado ? (
            <p>Este voluntário já está vinculado a outra conta da plataforma.</p>
          ) : (
            <>
              <p>Ao confirmar, criaremos apenas a conta de acesso vinculada a este voluntário. O convite para definir a senha será enviado para {email}.</p>
              <form onSubmit={(event) => void submit(event, 'link')}>
                <input type="hidden" name="id" value={id} />
                <input type="hidden" name="codigoVoluntario" value={existente.codigoVoluntario ?? ''} />
                <button type="submit" disabled={busy}>{working === 'link' ? 'Vinculando…' : 'Vincular e enviar convite'}</button>
              </form>
            </>
          )}
          {linkState.error && <p role="alert" className="error">{linkState.error}</p>}
        </div>
      )}
    </div>
  );
}
