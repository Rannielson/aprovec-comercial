'use client';

import { Fragment, useActionState, useEffect, useState } from 'react';
import type { HinovaBoletoSync } from '@/lib/types';
import { messageFor } from '@/lib/errors';
import { iniciarSyncBoletos, listarSyncBoletos } from './actions';

function label(month: string) {
  const [year, number] = month.slice(0, 7).split('-');
  return `${number}/${year}`;
}

function statusLabel(status: HinovaBoletoSync['status']) {
  return { queued: 'Na fila', running: 'Sincronizando', completed: 'Concluída', failed: 'Falhou' }[status];
}

function progressInfo(job: HinovaBoletoSync) {
  switch (job.fase) {
    case 'aguardando_nova_tentativa':
      return { title: 'Consulta interrompida · aguardando nova tentativa automática', current: 0, total: null, verb: 'analisados' };
    case 'buscando_pagamentos':
      return { title: 'Consultando pagamentos na Hinova', current: job.pagamentosConsultados, total: job.totalBoletos, verb: 'consultados' };
    case 'importando_pagamentos':
      return { title: 'Gravando pagamentos', current: job.pagamentosProcessados, total: job.totalBoletos, verb: 'analisados' };
    case 'buscando_vencimentos':
      return { title: 'Consultando boletos por vencimento', current: job.vencimentosConsultados, total: job.vencimentoRetornados, verb: 'consultados' };
    case 'importando_vencimentos':
      return { title: 'Atualizando a carteira e os boletos em aberto', current: job.vencimentosProcessados, total: job.vencimentoRetornados, verb: 'analisados' };
    default:
      return { title: job.status === 'completed' ? 'Sincronização finalizada' : job.status === 'failed' ? 'Sincronização interrompida' : 'Preparando sincronização', current: 0, total: null, verb: 'analisados' };
  }
}

export function BoletoSyncCard({ initialJobs, currentMonth }: {
  initialJobs: HinovaBoletoSync[];
  currentMonth: string;
}) {
  const [jobs, setJobs] = useState(initialJobs);
  const [state, formAction, pending] = useActionState(iniciarSyncBoletos, {});
  const active = jobs.some((job) => job.status === 'queued' || job.status === 'running');

  useEffect(() => {
    if (!state.jobId) return;
    let cancelled = false;
    listarSyncBoletos().then((next) => { if (!cancelled) setJobs(next); }).catch(() => {});
    return () => { cancelled = true; };
  }, [state.jobId]);

  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    const timer = setInterval(() => {
      listarSyncBoletos().then((next) => { if (!cancelled) setJobs(next); }).catch(() => {});
    }, 3000);
    return () => { cancelled = true; clearInterval(timer); };
  }, [active]);

  return (
    <section className="card" aria-labelledby="boleto-sync-title">
      <div>
        <p className="eyebrow">Hinova · boletos</p>
        <h2 id="boleto-sync-title">Sincronização de boletos</h2>
        <p className="muted">Escolha o mês de referência. Primeiro importamos os pagamentos pela data de pagamento; depois atualizamos a carteira pela data de vencimento, incluindo os boletos em aberto. Os registros são identificados pelo nosso número, sem duplicação.</p>
      </div>

      <p className="muted">A rotina automática roda às 5h, no horário de Recife. Após os pagamentos dos últimos cinco dias, atualiza os vencimentos do mês atual e dos meses anteriores com pendências importadas. Se houver falha, realiza até três tentativas, com intervalo de 15 minutos.</p>

      <form action={formAction} className="inline">
        <label>
          Mês de referência
          <input type="month" name="competencia" defaultValue={currentMonth} max={currentMonth} required />
        </label>
        <button type="submit" disabled={pending}>{pending ? 'Iniciando…' : 'Sincronizar boletos'}</button>
      </form>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      {state.message && <p role="status" className="success">{state.message}</p>}

      <div>
        <h3>Últimas sincronizações</h3>
        {jobs.length === 0 ? (
          <p className="muted">Nenhuma sincronização iniciada por esta tela.</p>
        ) : (
          <div className="table-wrap">
            <table>
              <thead><tr><th>Mês</th><th>Estado</th><th>Pagos importados</th><th>Sem vínculo</th><th>Conflitos</th><th>Atualização</th></tr></thead>
              <tbody>
                {jobs.slice(0, 6).map((job) => {
                  const progress = progressInfo(job);
                  return <Fragment key={job.id}>
                  <tr>
                    <td>{label(job.competencia)}</td>
                    <td>
                      <span className={`badge ${job.status === 'completed' ? 'recebido' : job.status === 'failed' ? 'cancelado' : 'neutral'}`}>
                        {statusLabel(job.status)}
                      </span>
                    </td>
                    <td>{job.importados ?? 0}</td>
                    <td>{job.semVinculo ?? '—'}</td>
                    <td>{job.conflitoMultiploVendedor ?? '—'}</td>
                    <td>{new Date(job.finishedAt ?? job.requestedAt).toLocaleString('pt-BR', { timeZone: 'America/Recife' })}</td>
                  </tr>
                  <tr className="sync-detail-row"><td colSpan={6}>
                    <div className="sync-progress-line">
                      <strong>{progress.title}</strong>
                      {job.status === 'running' && <span>{progress.current} de {progress.total ?? '…'} {progress.verb}</span>}
                    </div>
                    {job.status === 'running' && <progress className="sync-progress" value={progress.total ? progress.current : undefined} max={progress.total ?? 1} aria-label={progress.title} />}
                    <p className="muted sync-counts">Pagamentos: {job.pagamentosProcessados} de {job.totalBoletos ?? '…'} analisados · {job.importados ?? 0} importados.</p>
                    <p className="muted sync-counts">Carteira por vencimento: {job.vencimentosProcessados} de {job.vencimentoRetornados ?? '…'} analisados · {job.vencimentoVinculados ?? 0} vinculados.</p>
                    {job.fase === 'aguardando_nova_tentativa' && <p className="muted" role="status">A consulta falhou. Uma nova tentativa ocorrerá automaticamente em até 15 minutos; a execução ainda não foi concluída.</p>}
                    {job.status === 'failed' && <p className="error">{messageFor(job.errorCode ?? 'internal.error')} Os boletos já gravados permanecem salvos; você pode repetir o mês sem duplicá-los.</p>}
                    {job.status === 'completed' && <p className="success">Sincronização concluída sem erro.</p>}
                  </td></tr>
                  </Fragment>;
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </section>
  );
}
