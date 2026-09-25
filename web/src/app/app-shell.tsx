import Link from 'next/link';
import { Icon, type IconName } from './components/app-icon';
import type { Me } from '@/lib/types';

export type ActivePage = 'overview' | 'wallet' | 'closing' | 'settings' | 'arvore' | 'participantes' | 'remuneracao';

const NAV_ITEMS: { id: ActivePage; icon: IconName; label: string; href: string | null; permission?: string }[] = [
  { id: 'overview', icon: 'grid', label: 'Minha recorrência', href: '/' },
  { id: 'wallet', icon: 'wallet', label: 'Minha carteira', href: '/carteira' },
  { id: 'closing', icon: 'calendar', label: 'Fechamento', href: null },
  { id: 'settings', icon: 'settings', label: 'Configurações', href: '/configuracoes/integracoes', permission: 'integracoes.gerenciar' },
  { id: 'arvore', icon: 'people', label: 'Árvore comissionada', href: '/administracao/arvore', permission: 'estrutura.visualizar' },
  { id: 'participantes', icon: 'people', label: 'Participantes', href: '/administracao/participantes', permission: 'estrutura.visualizar' },
  { id: 'remuneracao', icon: 'shield', label: 'Remuneração', href: null },
];

export function AppShell({ me, active, children }: { me: Me; active: ActivePage; children: React.ReactNode }) {
  const initials = me.name.split(' ').slice(0, 2).map((part) => part[0]).join('');
  const activeItem = NAV_ITEMS.find((item) => item.id === active);

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <img src="/logo-aprovec.webp" alt="APROVEC Brasil" width={173} height={45} />
          <div className="brand-product">Recorrência comercial</div>
        </div>
        <nav aria-label="Navegação principal">
          {NAV_ITEMS.filter((item) => !item.permission || me.permissions.some((p) => p.key === item.permission)).map((item) =>
            item.href ? (
              <Link
                key={item.id}
                href={item.href}
                className={item.id === active ? 'nav-item active' : 'nav-item'}
                aria-current={item.id === active ? 'page' : undefined}
              >
                <Icon name={item.icon} />
                <span>{item.label}</span>
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
