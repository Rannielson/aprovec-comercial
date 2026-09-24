import { notFound } from 'next/navigation';
import { AppShell } from '../app-shell';
import { apiFetch, currentHost } from '@/lib/api';
import { Icon } from '../components/app-icon';
import { boletoStatusBadgeClass, boletoStatusLabels, formatDate, formatMoney } from '@/lib/format';
import type { Me, Wallet } from '@/lib/types';

const STATUS_TABS: { value: string; label: string }[] = [
  { value: 'todos', label: 'Todos' },
  { value: 'recebido', label: 'Recebidos' },
  { value: 'atraso', label: 'Em atraso' },
  { value: 'cancelado', label: 'Cancelados' },
];

export default async function CarteiraPage({
  searchParams,
}: {
  searchParams: Promise<{ status?: string; query?: string; page?: string }>;
}) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();

  const me = await apiFetch<Me>('/me');
  const canSeeCarteira = me.permissions.some((p) => p.key === 'carteira.visualizar');
  if (!canSeeCarteira) {
    return (
      <AppShell me={me} active="wallet">
        <div className="shell">
          <section className="card">
            <p className="muted">Seu perfil não inclui acesso à carteira.</p>
          </section>
        </div>
      </AppShell>
    );
  }

  const { status, query, page } = await searchParams;
  const activeStatus = status && status !== 'todos' ? status : 'todos';
  const currentPage = Math.max(1, Number(page) || 1);

  const apiParams = new URLSearchParams();
  if (activeStatus !== 'todos') apiParams.set('status', activeStatus);
  if (query) apiParams.set('query', query);
  apiParams.set('page', String(currentPage));

  const wallet = await apiFetch<Wallet>(`/carteira?${apiParams.toString()}`);
  const totalPages = Math.max(1, Math.ceil(wallet.totalCount / wallet.pageSize));

  function hrefFor(overrides: { status?: string; page?: number }): string {
    const p = new URLSearchParams();
    const s = overrides.status ?? activeStatus;
    const pg = overrides.page ?? currentPage;
    if (s !== 'todos') p.set('status', s);
    if (query) p.set('query', query);
    if (pg !== 1) p.set('page', String(pg));
    const qs = p.toString();
    return qs ? `/carteira?${qs}` : '/carteira';
  }

  return (
    <AppShell me={me} active="wallet">
      <div className="shell">
        <div className="page-heading">
          <p className="eyebrow">Painel do vendedor</p>
          <h1>Minha carteira</h1>
          <p className="muted">Os pagamentos e a saúde da base, boleto por boleto.</p>
        </div>

        <section className="card wallet-panel">
          <div className="wallet-toolbar">
            <div className="filter-tabs">
              {STATUS_TABS.map((tab) => (
                <a
                  key={tab.value}
                  href={hrefFor({ status: tab.value, page: 1 })}
                  className={tab.value === activeStatus ? 'filter-tab selected' : 'filter-tab'}
                >
                  {tab.label}
                  <span>{tab.value === 'todos' ? wallet.totalAllCount : wallet.statusCounts[tab.value] ?? 0}</span>
                </a>
              ))}
            </div>
            <form className="search-field" method="get">
              <Icon name="search" />
              {activeStatus !== 'todos' && <input type="hidden" name="status" value={activeStatus} />}
              <input type="search" name="query" placeholder="Buscar nome ou placa" defaultValue={query ?? ''} aria-label="Buscar associado ou placa" />
            </form>
          </div>

          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Associado</th>
                  <th>Vencimento</th>
                  <th className="number">Valor do boleto</th>
                  <th>Situação</th>
                  <th className="number">Sua comissão</th>
                </tr>
              </thead>
              <tbody>
                {wallet.items.length === 0 ? (
                  <tr>
                    <td colSpan={5} className="empty-state">
                      <Icon name="search" />
                      <strong>Nenhum boleto encontrado</strong>
                      <span>Tente outro nome, placa ou situação.</span>
                    </td>
                  </tr>
                ) : (
                  wallet.items.map((item) => (
                    <tr key={item.id}>
                      <td>
                        <div className="associate-button">
                          <span className="row-avatar">
                            {item.associadoNome.split(' ').slice(0, 2).map((p) => p[0]).join('')}
                          </span>
                          <span>
                            <strong>{item.associadoNome}</strong>
                            {item.placa && <span className="plate">{item.placa}</span>}
                          </span>
                        </div>
                      </td>
                      <td>
                        {formatDate(item.vencimento)}
                        {item.diasAtraso != null && <span className="late-days">{item.diasAtraso} dias em atraso</span>}
                      </td>
                      <td className="number amount">{formatMoney(item.valor)}</td>
                      <td>
                        <span className={boletoStatusBadgeClass(item.status)}>{boletoStatusLabels[item.status] ?? item.status}</span>
                      </td>
                      <td className={item.comissao > 0 ? 'number amount earned' : 'number amount muted'}>{formatMoney(item.comissao)}</td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>

          <div className="results-summary">
            <span>{wallet.totalCount} {wallet.totalCount === 1 ? 'boleto' : 'boletos'} no filtro</span>
            <span>Valor dos boletos <strong>{formatMoney(wallet.totalValor)}</strong></span>
            <span>Comissão apurada <strong>{formatMoney(wallet.totalComissao)}</strong></span>
          </div>

          <div className="pagination">
            <span>
              {wallet.totalCount === 0
                ? '0 boletos'
                : `${(currentPage - 1) * wallet.pageSize + 1}–${Math.min(currentPage * wallet.pageSize, wallet.totalCount)} de ${wallet.totalCount} boletos`}
            </span>
            <div>
              {currentPage > 1 ? (
                <a href={hrefFor({ page: currentPage - 1 })} className="icon-button" aria-label="Página anterior">
                  <Icon name="back" />
                </a>
              ) : (
                <span className="icon-button" aria-hidden="true">
                  <Icon name="back" />
                </span>
              )}
              <span>Página <strong>{currentPage}</strong> de {totalPages}</span>
              {currentPage < totalPages ? (
                <a href={hrefFor({ page: currentPage + 1 })} className="icon-button" aria-label="Próxima página">
                  <Icon name="chevron" />
                </a>
              ) : (
                <span className="icon-button" aria-hidden="true">
                  <Icon name="chevron" />
                </span>
              )}
            </div>
          </div>
        </section>

        <p className="wallet-note">
          <Icon name="info" />
          A comissão é gerada após a confirmação do pagamento. A situação do associado pode variar.
        </p>
      </div>
    </AppShell>
  );
}
