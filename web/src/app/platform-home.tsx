import { apiFetch } from '@/lib/api';
import { currentCompetencia } from '@/lib/format';
import type { PlatformMe, PlatformTenant } from '@/lib/types';
import { CreateTenantForm } from './create-tenant-form';
import { setTenantStatus } from './platform-actions';

export async function PlatformHome() {
  const [me, tenants] = await Promise.all([
    apiFetch<PlatformMe>('/platform/me'),
    apiFetch<PlatformTenant[]>('/platform/tenants'),
  ]);

  return (
    <main className="shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">Administração da plataforma · {me.email}</p>
          <h1>Empresas</h1>
        </div>
        <form action="/logout" method="post">
          <button type="submit" className="secondary">Sair</button>
        </form>
      </header>

      <section className="card">
        <h2>Nova empresa</h2>
        <CreateTenantForm defaultMonth={currentCompetencia()} />
      </section>

      <section className="card">
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Empresa</th>
                <th>Endereço</th>
                <th>Situação</th>
                <th />
              </tr>
            </thead>
            <tbody>
              {tenants.map((tenant) => (
                <tr key={tenant.id}>
                  <td>{tenant.name}</td>
                  <td>{tenant.slug}</td>
                  <td>{tenant.status === 'ativo' ? 'Ativa' : 'Suspensa'}</td>
                  <td className="number">
                    <form action={setTenantStatus}>
                      <input type="hidden" name="id" value={tenant.id} />
                      <input type="hidden" name="status" value={tenant.status === 'ativo' ? 'suspenso' : 'ativo'} />
                      <button type="submit" className="secondary">
                        {tenant.status === 'ativo' ? 'Suspender' : 'Reativar'}
                      </button>
                    </form>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </main>
  );
}
