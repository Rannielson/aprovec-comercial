import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const hooks = vi.hoisted(() => ({
  refresh: vi.fn(),
  pending: false,
  setup: undefined as (() => (() => void)) | undefined,
}));
vi.mock('next/navigation', () => ({ useRouter: () => ({ refresh: hooks.refresh }) }));
vi.mock('react', () => ({
  useEffect: (setup: () => (() => void)) => { hooks.setup = setup; },
  useTransition: () => [hooks.pending, (action: () => void) => action()],
}));
import { AutoRefresh } from './auto-refresh';

describe('atualização automática da carteira e do dashboard', () => {
  let doc: EventTarget & { visibilityState: string; activeElement: { matches: () => boolean } | null };
  let browser: EventTarget;
  let connection: { onLine: boolean };
  let cleanup: (() => void) | undefined;
  function mount() { AutoRefresh(); cleanup = hooks.setup!(); }

  beforeEach(() => {
    vi.useFakeTimers();
    hooks.refresh.mockClear();
    hooks.pending = false;
    doc = Object.assign(new EventTarget(), { visibilityState: 'visible', activeElement: null });
    browser = new EventTarget();
    connection = { onLine: true };
    vi.stubGlobal('document', doc);
    vi.stubGlobal('window', browser);
    vi.stubGlobal('navigator', connection);
  });
  afterEach(() => { cleanup?.(); vi.useRealTimers(); vi.unstubAllGlobals(); });

  it('consulta os dados em até 30 segundos sem navegar para outra URL', () => {
    mount();
    vi.advanceTimersByTime(29_999);
    expect(hooks.refresh).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(hooks.refresh).toHaveBeenCalledOnce();
  });

  it('pausa em segundo plano e offline; retoma ao voltar para a aba ou recuperar a conexão', () => {
    mount();
    doc.visibilityState = 'hidden';
    vi.advanceTimersByTime(60_000);
    expect(hooks.refresh).not.toHaveBeenCalled();
    doc.visibilityState = 'visible';
    doc.dispatchEvent(new Event('visibilitychange'));
    expect(hooks.refresh).toHaveBeenCalledTimes(1);
    connection.onLine = false;
    vi.advanceTimersByTime(30_000);
    expect(hooks.refresh).toHaveBeenCalledTimes(1);
    connection.onLine = true;
    browser.dispatchEvent(new Event('online'));
    expect(hooks.refresh).toHaveBeenCalledTimes(2);
  });

  it('aguarda o consultor terminar de editar um filtro', () => {
    mount();
    doc.activeElement = { matches: () => true };
    vi.advanceTimersByTime(30_000);
    expect(hooks.refresh).not.toHaveBeenCalled();
    doc.dispatchEvent(new Event('focusout'));
    doc.activeElement = null;
    vi.advanceTimersByTime(1);
    expect(hooks.refresh).toHaveBeenCalledOnce();
  });

  it('evita sobrepor uma atualização pendente e remove os timers ao sair da tela', () => {
    hooks.pending = true;
    mount();
    vi.advanceTimersByTime(30_000);
    browser.dispatchEvent(new Event('focus'));
    expect(hooks.refresh).not.toHaveBeenCalled();
    cleanup?.();
    hooks.pending = false;
    mount();
    doc.dispatchEvent(new Event('focusout'));
    cleanup?.();
    vi.advanceTimersByTime(60_000);
    browser.dispatchEvent(new Event('focus'));
    expect(hooks.refresh).not.toHaveBeenCalled();
  });
});
