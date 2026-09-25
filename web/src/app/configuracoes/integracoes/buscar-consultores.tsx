'use client';

import { useEffect, useState } from 'react';
import { Icon } from '../../components/app-icon';
import { maskCpf } from '@/lib/format';
import type { HinovaVoluntario } from '@/lib/types';

export function BuscarConsultores({ voluntarios }: { voluntarios: HinovaVoluntario[] }) {
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (!open) return;
    const onKeyDown = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    document.addEventListener('keydown', onKeyDown);
    return () => document.removeEventListener('keydown', onKeyDown);
  }, [open]);

  return (
    <>
      <button type="button" className="secondary" onClick={() => setOpen(true)}>
        <Icon name="search" />
        Buscar Consultores
      </button>
      {open && (
        <div className="overlay overlay-center">
          <button type="button" className="overlay-backdrop" aria-label="Fechar" onClick={() => setOpen(false)} />
          <section className="modal-card" role="dialog" aria-modal="true" aria-labelledby="buscar-consultores-title">
            <div className="drawer-header">
              <div>
                <h2 id="buscar-consultores-title">Consultores ativos na Hinova</h2>
                <p>
                  {voluntarios.length} {voluntarios.length === 1 ? 'consultor encontrado' : 'consultores encontrados'}
                </p>
              </div>
              <button type="button" className="icon-button" aria-label="Fechar" onClick={() => setOpen(false)}>
                <Icon name="close" />
              </button>
            </div>
            <div className="modal-body table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Nome completo</th>
                    <th>CPF</th>
                    <th>Telefone</th>
                    <th>Cooperativa</th>
                    <th>Vínculo</th>
                  </tr>
                </thead>
                <tbody>
                  {voluntarios.length === 0 ? (
                    <tr>
                      <td colSpan={5} className="empty-state">
                        <Icon name="people" />
                        <strong>Nenhum consultor encontrado</strong>
                      </td>
                    </tr>
                  ) : (
                    voluntarios.map((v) => (
                      <tr key={v.codigo}>
                        <td>{v.nome}</td>
                        <td>{maskCpf(v.cpf)}</td>
                        <td>{v.telefone ?? '—'}</td>
                        <td>{v.cooperativas.length > 0 ? v.cooperativas.join(', ') : '—'}</td>
                        <td>
                          {v.jaVinculado ? (
                            <span className="badge neutral">Vinculado a {v.vinculadoA}</span>
                          ) : (
                            <span className="badge progress">Disponível</span>
                          )}
                        </td>
                      </tr>
                    ))
                  )}
                </tbody>
              </table>
            </div>
          </section>
        </div>
      )}
    </>
  );
}
