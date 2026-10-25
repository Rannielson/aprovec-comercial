'use client';

import { useState } from 'react';
import { formatDate } from '@/lib/format';
import type { SolicitacaoCadastro } from '@/lib/types';
import { messageFor } from '@/lib/errors';
import { SolicitacaoActions } from './solicitacao-actions';

export function SolicitacaoRow({ solicitacao: s, activeId, onStart, onFinish }: {
  solicitacao: SolicitacaoCadastro;
  activeId: string | null;
  onStart: (id: string) => boolean;
  onFinish: (id: string) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const detailId = `cadastro-${s.id}`;
  const pendente = s.status === 'pendente';
  const emAndamento = pendente && s.processarAutomaticamente && !s.erroCodigo;
  const status = s.status === 'aprovado' ? 'Cadastrado na Hinova'
    : s.status === 'rejeitado' ? 'Rejeitado'
      : s.erroCodigo ? 'Revisão necessária'
        : emAndamento ? (s.processandoEm ? 'Cadastrando na Hinova…' : 'Aguardando processamento')
          : 'Aguardando revisão';
  const actionMode = !pendente || emAndamento ? null
    : s.erroCodigo === 'solicitacao.cpf_ja_cadastrado' ? 'review' : 'retry';
  const fields = [
    ['Nome completo', s.nome],
    ['CPF', s.cpf],
    ['Celular', s.celular],
    ['E-mail', s.email],
    ['CEP', s.cep],
    ['Logradouro', s.logradouro],
    ['Número', s.numero],
    ['Complemento', s.complemento || 'Não informado'],
    ['Bairro', s.bairro],
    ['Cidade', s.cidade],
    ['Estado', s.estado],
    ['Indicado por', s.indicadorNome],
    ['Recebido em', formatDate(s.criadoEm.slice(0, 10))],
  ] as const;

  return (
    <>
      <tr>
        <td>
          <strong>{s.nome}</strong>
          <button type="button" className="solicitacao-detail-toggle" aria-expanded={expanded}
            aria-controls={detailId} onClick={() => setExpanded((open) => !open)}>
            {expanded ? 'Ocultar cadastro' : 'Ver cadastro completo'}
          </button>
        </td>
        <td>{s.cpf}</td>
        <td>{s.celular}<br /><span className="muted">{s.email}</span></td>
        <td>{s.indicadorNome}</td>
        <td>{formatDate(s.criadoEm.slice(0, 10))}</td>
        <td>
          <strong>{status}</strong>
          {s.erroCodigo && <p className="muted">{messageFor(s.erroCodigo)}</p>}
          {s.conviteErro && <p className="error">Cadastro concluído, mas o convite por e-mail não foi enviado.</p>}
        </td>
        <td>{actionMode && <SolicitacaoActions id={s.id} email={s.email} mode={actionMode}
          retryLabel={s.processarAutomaticamente ? 'Tentar novamente' : 'Processar agora'}
          activeId={activeId} onStart={onStart} onFinish={onFinish} />}</td>
      </tr>
      {expanded && (
        <tr className="solicitacao-detail-row" id={detailId}>
          <td colSpan={7}>
            <div className="solicitacao-detail-panel">
              <strong>Dados informados na solicitação</strong>
              <dl className="solicitacao-detail-grid">
                {fields.map(([label, value]) => (
                  <div key={label}>
                    <dt>{label}</dt>
                    <dd>{value}</dd>
                  </div>
                ))}
              </dl>
            </div>
          </td>
        </tr>
      )}
    </>
  );
}
