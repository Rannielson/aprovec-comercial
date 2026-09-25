import Link from 'next/link';
import { Icon, type IconName } from './components/app-icon';
import type { Me } from '@/lib/types';

export type ActivePage = 'overview' | 'wallet' | 'commissions' | 'closing' | 'settings' | 'arvore' | 'participantes' | 'remuneracao' | 'convites';

const NAV_ITEMS: { id: ActivePage; icon: IconName; label: string; href: string | null; permission?: string; minScope?: string }[] = [
  { id: 'overview', icon: 'grid', label: 'Minha recorrência', href: '/' },
  { id: 'wallet', icon: 'wallet', label: 'Minha carteira', href: '/carteira' },
  { id: 'commissions', icon: 'percent', label: 'Comissões', href: '/comissoes' },
  { id: 'closing', icon: 'calendar', label: 'Fechamento', href: '/fechamento' },
  { id: 'settings', icon: 'settings', label: 'Configurações', href: '/configuracoes/integracoes', permission: 'integracoes.gerenciar' },
  // Consultor also holds estrutura.visualizar (scope 'direct', per 0006_rbac_catalog.sql) so they
  // can see their own referrals, but the tree visualization itself is a whole-base view meant for
  // Coordenador ('tenant' scope) and Administrador -- Consultor gets Participantes only.
  { id: 'arvore', icon: 'people', label: 'Árvore comissionada', href: '/administracao/arvore', permission: 'estrutura.visualizar', minScope: 'tenant' },
  { id: 'participantes', icon: 'people', label: 'Participantes', href: '/administracao/participantes', permission: 'estrutura.visualizar' },
  { id: 'convites', icon: 'people', label: 'Convites', href: '/administracao/convites', permission: 'usuarios.convidar' },
  { id: 'remuneracao', icon: 'shield', label: 'Remuneração', href: '/administracao/remuneracao', permission: 'regras_comissao.visualizar' },
];

// The mockup gives Administrador its own exclusive console, not the union of every permission-gated
// item -- Administrador holds every permission, so the permission-based filter below would otherwise
// show them everything (Minha carteira, Comissões, etc. included). Configurações stays in: it's where
// the Hinova/SGA integration (credentials, voluntário mapping) lives, and only Administrador can reach
// it (integracoes.gerenciar is administrador-only). Fechamento stays in too, even though the mockup's
// admin menu omits it: only Administrador holds fechamento.confirmar/provisionar
// (0006_rbac_catalog.sql), so this is the only page that can host those actions -- without it, nothing
// in the UI could ever confirm or provisionar a competência.
const ADMIN_ONLY_NAV_IDS: ActivePage[] = ['arvore', 'participantes', 'convites', 'remuneracao', 'settings', 'closing'];

export function AppShell({
  me,
  active,
  children,
  convitesPendentes,
}: {
  me: Me;
  active: ActivePage;
  children: React.ReactNode;
  convitesPendentes?: number;
}) {
  const initials = me.name.split(' ').slice(0, 2).map((part) => part[0]).join('');
  const activeItem = NAV_ITEMS.find((item) => item.id === active);
  // Only Administrador (and nothing else) gets the exclusive admin console; anyone holding a
  // custom role alongside -- or any other/no template -- keeps the regular permission-based menu.
  const isAdminOnly = me.roleTemplates.length === 1 && me.roleTemplates[0] === 'administrador';
  const visibleItems = isAdminOnly
    ? NAV_ITEMS.filter((item) => ADMIN_ONLY_NAV_IDS.includes(item.id))
    : NAV_ITEMS.filter(
        (item) =>
          !item.permission ||
          me.permissions.some((p) => p.key === item.permission && (!item.minScope || p.scope === item.minScope)),
      );

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <img src="/logo-aprovec.webp" alt="APROVEC Brasil" width={173} height={45} />
          <div className="brand-product">Recorrência comercial</div>
        </div>
        <nav aria-label="Navegação principal">
          {visibleItems.map((item) =>
            item.href ? (
              <Link
                key={item.id}
                href={item.href}
                className={item.id === active ? 'nav-item active' : 'nav-item'}
                aria-current={item.id === active ? 'page' : undefined}
              >
                <Icon name={item.icon} />
                <span>{item.label}</span>
                {item.id === 'convites' && !!convitesPendentes && <span className="nav-count">{convitesPendentes}</span>}
              </Link>
            ) : (
              <span key={item.id} className="nav-item disabled">
                <Icon name={item.icon} />
                <span>{item.label}</span>
                <span className="nav-soon">Em breve</span>
              </span>
            ),
          )}
        </nav>
        <div className="sidebar-bottom">
          <div className="sidebar-note">
            Cada pagamento.
            <br />
            <strong>Um resultado que cresce.</strong>
          </div>
          <div className="seller">
            <span className="avatar">{initials}</span>
            <div>
              <strong>{me.name}</strong>
            </div>
            <span className="seller-dot" aria-hidden="true" />
          </div>
        </div>
        <form action="/logout" method="post" className="seller-logout">
          <button type="submit">
            <Icon name="logout" />
            Sair
          </button>
        </form>
      </aside>
      <div className="workspace">
        <header className="shell-topbar">
          <div className="breadcrumb">
            Comercial <span>/</span> <strong>{activeItem?.label ?? ''}</strong>
          </div>
        </header>
        <main>{children}</main>
        <footer className="app-footer">
          <span>APROVEC Brasil <span>·</span> Recorrência comercial</span>
        </footer>
      </div>
    </div>
  );
}
