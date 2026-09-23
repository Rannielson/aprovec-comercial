# Fundação 4 — Web (Next.js BFF), teste E2E e containers: plano de implementação

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Criar o BFF em Next.js que resolve a empresa pelo subdomínio, guarda a sessão em cookie `httpOnly` e conversa com a API interna. Ele entrega login, definição e redefinição de senha, a visão inicial de comissões e a administração de empresas da plataforma. Também inclui o teste E2E e o Docker Compose completo.

**Architecture:** Next.js (App Router) com Server Components e Server Actions. O navegador nunca fala com a API C#: toda chamada passa por `apiFetch` no servidor Next.js, que envia `X-Internal-Key`, `X-Tenant-Host`, IP, user agent e o token do cookie. O cookie é host-only (sem atributo `Domain`), então fica preso ao subdomínio da empresa.

**Tech Stack:** Next.js 16, React 19, TypeScript, Vitest, Playwright, Docker Compose.

**Spec:** `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md` (seções 2, 3 e 4)

**Ordem dos planos:** plano 4 de 4. Depende do plano 3 (API) estar completo.

**Escopo das telas:** as telas definitivas estão fora do spec. Este plano entrega só o necessário para usar e verificar a fundação: login, senha, visão de comissões e administração de empresas, com estilo neutro. O visual da marca e as telas do mockup ficam para os próximos planos.

## Global Constraints

- Node 22. Next.js 16 (App Router, `src/`, TypeScript, sem Tailwind).
- Em Server Components e Server Actions, `cookies()`, `headers()`, `params` e `searchParams` são assíncronos (`await`).
- `redirect()` e `notFound()` lançam exceções: nunca os chame dentro de um `try` cujo `catch` possa engoli-los.
- Arquivos que importam `server-only` não podem ser importados por testes Vitest. Lógica testável fica em módulos puros (`host.ts`, `api-headers.ts`, `format.ts`, `errors.ts`).
- Variáveis do servidor web: `API_INTERNAL_URL`, `API_INTERNAL_KEY`, `ROOT_DOMAIN` (ex.: `localhost:3000`) e `COOKIE_SECURE` (`true`/`false`; se ausente, `true` só em produção).
- Cookie de sessão: nome `sid`, `httpOnly`, `sameSite=lax`, `path=/`, expira no `absoluteExpiresAt` devolvido pela API, sem `Domain`.
- Subdomínio `admin` = plataforma. Outros subdomínios válidos = empresa. Host fora de `*.{ROOT_DOMAIN}` = 404.
- IP do cliente: o **último** item de `X-Forwarded-For` (o que o proxy confiável acrescentou).
- Textos da interface em português do Brasil. Valores em `R$` com `Intl.NumberFormat('pt-BR')`. Competência padrão no fuso `America/Sao_Paulo`.
- Desenvolvimento: `http://aprovec.localhost:3000` (empresa) e `http://admin.localhost:3000` (plataforma). Chromium resolve `*.localhost` para 127.0.0.1 sem configuração.
- Commits: mensagens convencionais (`feat(web): ...`), terminando com a linha de atribuição indicada pelo ambiente.

## Mapa de arquivos

```
web/
  package.json, next.config.ts, tsconfig.json, vitest.config.ts, playwright.config.ts, .env.local.example, Dockerfile, .dockerignore
  src/lib/host.ts, host.test.ts               subdomínio → empresa/plataforma
  src/lib/api-headers.ts, api-headers.test.ts cabeçalhos para a API interna
  src/lib/format.ts, format.test.ts           dinheiro, percentual, competência, rótulos
  src/lib/errors.ts, errors.test.ts           código de erro → mensagem
  src/lib/types.ts                            contratos da API
  src/lib/form-state.ts                       estado das Server Actions
  src/lib/env.ts                              variáveis de ambiente (server-only)
  src/lib/session.ts                          cookie de sessão (server-only)
  src/lib/api.ts                              apiFetch + ApiError (server-only)
  src/app/layout.tsx, globals.css
  src/app/api/health/route.ts
  src/app/page.tsx, tenant-home.tsx, platform-home.tsx, create-tenant-form.tsx, platform-actions.ts
  src/app/login/page.tsx, login-form.tsx, actions.ts
  src/app/logout/route.ts
  src/app/definir-senha/[token]/page.tsx, set-password-form.tsx, actions.ts
  src/app/redefinir-senha/page.tsx, reset-form.tsx, actions.ts
  e2e/prepare.mjs, e2e/emails.ts, e2e/fundacao.spec.ts
src/Recorrencia.Db/Dockerfile, src/Recorrencia.Api/Dockerfile, .dockerignore, docker-compose.yml, .env.example, README.md
```

---

### Task 1: Projeto Next.js e resolução do host

**Files:**
- Create: `web/` (via `create-next-app`), `web/vitest.config.ts`, `web/.env.local.example`, `web/src/lib/host.ts`, `web/src/lib/host.test.ts`
- Modify: `web/next.config.ts`, `web/package.json`, `web/src/app/layout.tsx`, `web/src/app/page.tsx`, `web/src/app/globals.css`
- Create: `web/src/app/api/health/route.ts`

**Interfaces:**
- Produces: `type HostInfo = { kind: 'tenant'; slug: string } | { kind: 'platform' } | { kind: 'unknown' }`; `PLATFORM_SUBDOMAIN = 'admin'`; `parseHost(host: string | null | undefined, rootDomain: string): HostInfo`; `apiHostHeader(host: HostInfo): string | null`.
- Produces: `GET /api/health` → `{ status: 'ok' }` (usado pelo Playwright para saber que o servidor subiu).

- [ ] **Step 1: Criar o projeto**

```bash
npx create-next-app@16 web --ts --app --src-dir --eslint --no-tailwind --import-alias "@/*" --use-npm --yes
cd web
npm install server-only
npm install -D vitest @playwright/test
npx playwright install chromium
rm -f src/app/page.module.css public/*.svg
touch public/.gitkeep
cd ..
```

Se algum flag não existir na versão instalada, rode `npx create-next-app@16 web` de forma interativa com as mesmas escolhas: TypeScript, ESLint, App Router, `src/`, sem Tailwind, alias `@/*`, npm.

- [ ] **Step 2: Configurar**

`web/next.config.ts`:

```ts
import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  output: 'standalone',
  allowedDevOrigins: ['*.localhost'],
};

export default nextConfig;
```

`web/vitest.config.ts`:

```ts
import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: { include: ['src/**/*.test.ts'] },
});
```

Em `web/package.json`, dentro de `scripts`, acrescente:

```json
    "test": "vitest run",
    "typecheck": "tsc --noEmit",
    "e2e": "node e2e/prepare.mjs && playwright test"
```

`web/.env.local.example`:

```
API_INTERNAL_URL=http://127.0.0.1:5080
API_INTERNAL_KEY=chave-interna-de-desenvolvimento-32+
ROOT_DOMAIN=localhost:3000
COOKIE_SECURE=false
```

```bash
cp web/.env.local.example web/.env.local
```

`web/.env.local` já é ignorado pelo `.gitignore` gerado pelo `create-next-app` (`.env*`). Confirme com `git check-ignore web/.env.local`.

`web/src/app/layout.tsx` (substitui o gerado; sem `next/font/google`, para o build não depender de rede):

```tsx
import type { Metadata } from 'next';
import './globals.css';

export const metadata: Metadata = {
  title: 'Recorrência comercial',
};

export default function RootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="pt-BR">
      <body>{children}</body>
    </html>
  );
}
```

`web/src/app/globals.css` (substitui o gerado):

```css
:root {
  --ink: #202023;
  --muted: #6c6c73;
  --line: #e7e5e5;
  --paper: #f7f7f7;
  --surface: #ffffff;
  --accent: #ab090a;
  --accent-strong: #0c0c0e;
  --error: #a33e46;
  --ok: #17673a;
  font-family: Inter, system-ui, -apple-system, 'Segoe UI', sans-serif;
  color: var(--ink);
  background: var(--paper);
}

* { box-sizing: border-box; }
body { margin: 0; min-height: 100vh; background: var(--paper); }
h1, h2 { margin: 0; letter-spacing: -0.02em; }
h1 { font-size: 1.6rem; }
h2 { font-size: 1.1rem; }
a { color: var(--accent); }

.auth { display: grid; place-items: center; min-height: 100vh; padding: 16px; }
.shell { max-width: 1040px; margin: 0 auto; padding: 24px 16px; display: grid; gap: 16px; }
.card { background: var(--surface); border: 1px solid var(--line); border-radius: 16px 0 16px 0; padding: 24px; display: grid; gap: 16px; width: 100%; max-width: 100%; }
.auth .card { max-width: 400px; }
.topbar { display: flex; justify-content: space-between; align-items: center; gap: 16px; flex-wrap: wrap; }
.section-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; flex-wrap: wrap; }
.eyebrow { margin: 0; color: var(--accent); font-size: 0.75rem; font-weight: 600; letter-spacing: 0.08em; text-transform: uppercase; }
.muted { margin: 0; color: var(--muted); }
.total { margin: 4px 0 0; font-size: 2rem; font-weight: 700; letter-spacing: -0.03em; }
.badge { border: 1px solid var(--line); border-radius: 999px; padding: 4px 12px; font-size: 0.8rem; color: var(--muted); }

.form { display: grid; gap: 12px; }
.inline { display: flex; gap: 8px; align-items: flex-end; flex-wrap: wrap; }
label { display: grid; gap: 4px; font-size: 0.85rem; color: var(--muted); }
input, select { font: inherit; color: var(--ink); padding: 10px 12px; border: 1px solid var(--line); border-radius: 8px; background: var(--surface); min-width: 0; }
button { font: inherit; cursor: pointer; padding: 10px 16px; border: 1px solid var(--accent-strong); border-radius: 12px 0 12px 0; background: var(--accent-strong); color: #fff; }
button:hover:not(:disabled) { background: var(--accent); border-color: var(--accent); }
button:disabled { opacity: 0.6; cursor: wait; }
button.secondary { background: var(--surface); color: var(--ink); border-color: var(--line); }
.error { margin: 0; color: var(--error); }
.success { margin: 0; color: var(--ok); }

.table-wrap { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
th, td { text-align: left; padding: 10px 8px; border-bottom: 1px solid var(--line); white-space: nowrap; }
th { color: var(--muted); font-weight: 500; }
td.number, th.number { text-align: right; }
tr.subtotal td { font-weight: 600; }

@media (prefers-color-scheme: dark) {
  :root { --ink: #f2f2f3; --muted: #a4a4ab; --line: #2c2c31; --paper: #111113; --surface: #19191c; --accent: #ff6b6b; --accent-strong: #f2f2f3; }
  button { color: #111113; }
  button.secondary { color: var(--ink); }
}
```

`web/src/app/page.tsx` (provisório, substituído na Task 3):

```tsx
export default function HomePage() {
  return <main className="shell"><h1>Recorrência comercial</h1></main>;
}
```

`web/src/app/api/health/route.ts`:

```ts
export function GET() {
  return Response.json({ status: 'ok' });
}
```

- [ ] **Step 3: Escrever os testes (falhando)**

`web/src/lib/host.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { apiHostHeader, parseHost } from './host';

describe('parseHost', () => {
  it('reconhece a empresa pelo subdomínio', () => {
    expect(parseHost('aprovec.localhost:3000', 'localhost:3000')).toEqual({ kind: 'tenant', slug: 'aprovec' });
    expect(parseHost('Aprovec.Plataforma.com.br', 'plataforma.com.br')).toEqual({ kind: 'tenant', slug: 'aprovec' });
  });

  it('reconhece a plataforma', () => {
    expect(parseHost('admin.localhost:3000', 'localhost:3000')).toEqual({ kind: 'platform' });
  });

  it.each([
    ['localhost:3000'],
    ['a.b.localhost:3000'],
    ['aprovec.outrodominio.com'],
    ['-x.localhost:3000'],
    [''],
    [null],
  ])('trata %s como desconhecido', (host) => {
    expect(parseHost(host, 'localhost:3000')).toEqual({ kind: 'unknown' });
  });
});

describe('apiHostHeader', () => {
  it('envia o slug ou admin', () => {
    expect(apiHostHeader({ kind: 'tenant', slug: 'aprovec' })).toBe('aprovec');
    expect(apiHostHeader({ kind: 'platform' })).toBe('admin');
    expect(apiHostHeader({ kind: 'unknown' })).toBeNull();
  });
});
```

- [ ] **Step 4: Rodar e confirmar que falha**

Run: `cd web && npm test`
Expected: FAIL (`./host` não existe).

- [ ] **Step 5: Implementar**

`web/src/lib/host.ts`:

```ts
export type HostInfo =
  | { kind: 'tenant'; slug: string }
  | { kind: 'platform' }
  | { kind: 'unknown' };

export const PLATFORM_SUBDOMAIN = 'admin';

const SUBDOMAIN = /^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/;

export function parseHost(host: string | null | undefined, rootDomain: string): HostInfo {
  const normalizedHost = (host ?? '').trim().toLowerCase();
  const suffix = `.${rootDomain.trim().toLowerCase()}`;
  if (suffix === '.' || !normalizedHost.endsWith(suffix)) return { kind: 'unknown' };

  const subdomain = normalizedHost.slice(0, -suffix.length);
  if (!SUBDOMAIN.test(subdomain)) return { kind: 'unknown' };

  return subdomain === PLATFORM_SUBDOMAIN ? { kind: 'platform' } : { kind: 'tenant', slug: subdomain };
}

export function apiHostHeader(host: HostInfo): string | null {
  if (host.kind === 'tenant') return host.slug;
  if (host.kind === 'platform') return PLATFORM_SUBDOMAIN;
  return null;
}
```

- [ ] **Step 6: Rodar testes, tipos e build**

Run: `cd web && npm test && npm run typecheck && npm run build`
Expected: testes PASS, sem erros de tipo, build concluído.

- [ ] **Step 7: Commit**

```bash
git add web
git commit -m "feat(web): scaffold Next.js BFF and subdomain host parsing"
```

---

### Task 2: Cliente da API, sessão, formatação e mensagens

**Files:**
- Create: `web/src/lib/{api-headers.ts,api-headers.test.ts,format.ts,format.test.ts,errors.ts,errors.test.ts,types.ts,form-state.ts,env.ts,session.ts,api.ts}`

**Interfaces:**
- Consumes: `HostInfo`, `parseHost`, `apiHostHeader` (Task 1).
- Produces:
  - `clientIpFrom(forwardedFor: string | null | undefined): string | undefined` e `buildApiHeaders(input: ApiHeaderInput): Record<string, string>`, onde `ApiHeaderInput = { host: HostInfo; internalKey: string; token?: string; forwardedFor?: string | null; userAgent?: string | null; hasBody: boolean }`.
  - `formatMoney(value: number)`, `formatPercent(rate: number)`, `formatCompetencia(competencia: string)`, `currentCompetencia(now?: Date)`, `isCompetencia(value: string | undefined)`, `ruleLabel(rule)` e `statusLabels`.
  - `messageFor(code: string): string`.
  - Tipos em `types.ts`: `Permission`, `Me`, `TenantInfo`, `Session`, `RuleTotal`, `BeneficiaryCommission`, `Commissions`, `PlatformTenant` e `PlatformMe`.
  - `type FormState = { error?: string; message?: string }`.
  - `env.API_INTERNAL_URL`, `env.API_INTERNAL_KEY` e `env.ROOT_DOMAIN` (getters que falham se a variável faltar).
  - `SESSION_COOKIE = 'sid'`, `setSession(token, absoluteExpiresAt)` e `clearSession()`.
  - `class ApiError(status, code)`, `currentHost(): Promise<HostInfo>` e `apiFetch<T = void>(path, { method?, body?, token? }): Promise<T>`. Com `token` omitido, usa o cookie. Com `token: null`, não envia token.

- [ ] **Step 1: Escrever os testes (falhando)**

`web/src/lib/api-headers.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { buildApiHeaders, clientIpFrom } from './api-headers';

describe('clientIpFrom', () => {
  it('usa o último endereço, que é o acrescentado pelo proxy', () => {
    expect(clientIpFrom('1.1.1.1, 203.0.113.9')).toBe('203.0.113.9');
    expect(clientIpFrom('203.0.113.9')).toBe('203.0.113.9');
    expect(clientIpFrom('')).toBeUndefined();
    expect(clientIpFrom(null)).toBeUndefined();
  });
});

describe('buildApiHeaders', () => {
  it('monta os cabeçalhos da empresa com sessão e corpo', () => {
    expect(
      buildApiHeaders({
        host: { kind: 'tenant', slug: 'aprovec' },
        internalKey: 'chave',
        token: 'tok',
        forwardedFor: '203.0.113.9',
        userAgent: 'Navegador',
        hasBody: true,
      }),
    ).toEqual({
      Accept: 'application/json',
      'X-Internal-Key': 'chave',
      'X-Tenant-Host': 'aprovec',
      'X-Client-Ip': '203.0.113.9',
      'X-Client-User-Agent': 'Navegador',
      Authorization: 'Bearer tok',
      'Content-Type': 'application/json',
    });
  });

  it('omite o que não existe', () => {
    expect(buildApiHeaders({ host: { kind: 'platform' }, internalKey: 'chave', hasBody: false })).toEqual({
      Accept: 'application/json',
      'X-Internal-Key': 'chave',
      'X-Tenant-Host': 'admin',
    });
  });

  it('recusa host desconhecido', () => {
    expect(() => buildApiHeaders({ host: { kind: 'unknown' }, internalKey: 'chave', hasBody: false })).toThrow();
  });
});
```

`web/src/lib/format.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { currentCompetencia, formatCompetencia, formatMoney, formatPercent, isCompetencia, ruleLabel } from './format';

describe('format', () => {
  it('formata dinheiro em reais', () => {
    expect(formatMoney(1600)).toBe('R$ 1.600,00');
    expect(formatMoney(0.12)).toBe('R$ 0,12');
  });

  it('formata percentuais', () => {
    expect(formatPercent(0.07)).toBe('7%');
    expect(formatPercent(0.025)).toBe('2,5%');
  });

  it('escreve a competência por extenso', () => {
    expect(formatCompetencia('2026-09')).toBe('setembro de 2026');
  });

  it('usa o mês de São Paulo como competência atual', () => {
    expect(currentCompetencia(new Date('2026-10-01T02:00:00Z'))).toBe('2026-09');
    expect(currentCompetencia(new Date('2026-10-01T04:00:00Z'))).toBe('2026-10');
  });

  it('valida competências', () => {
    expect(isCompetencia('2026-09')).toBe(true);
    expect(isCompetencia('2026-13')).toBe(false);
    expect(isCompetencia(undefined)).toBe(false);
  });

  it('nomeia as regras', () => {
    expect(ruleLabel({ ruleType: 'own', level: null, groupName: null })).toBe('Carteira própria');
    expect(ruleLabel({ ruleType: 'upline', level: 2, groupName: null })).toBe('Supervisão · 2º nível');
    expect(ruleLabel({ ruleType: 'global', level: null, groupName: 'Coordenação' })).toBe('Coordenação');
  });
});
```

`web/src/lib/errors.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { messageFor } from './errors';

describe('messageFor', () => {
  it('traduz códigos conhecidos', () => {
    expect(messageFor('auth.invalid_credentials')).toBe('E-mail ou senha incorretos.');
    expect(messageFor('invite.invalid')).toBe('Este link expirou ou já foi usado. Peça um novo.');
  });

  it('tem uma mensagem genérica para o resto', () => {
    expect(messageFor('qualquer.coisa')).toBe('Não foi possível concluir. Tente novamente.');
  });
});
```

- [ ] **Step 2: Rodar e confirmar que falha**

Run: `cd web && npm test`
Expected: FAIL (módulos não existem).

- [ ] **Step 3: Implementar os módulos puros**

`web/src/lib/api-headers.ts`:

```ts
import { apiHostHeader, type HostInfo } from './host';

export type ApiHeaderInput = {
  host: HostInfo;
  internalKey: string;
  token?: string;
  forwardedFor?: string | null;
  userAgent?: string | null;
  hasBody: boolean;
};

export function clientIpFrom(forwardedFor: string | null | undefined): string | undefined {
  const last = forwardedFor?.split(',').map((part) => part.trim()).filter(Boolean).at(-1);
  return last || undefined;
}

export function buildApiHeaders(input: ApiHeaderInput): Record<string, string> {
  const tenantHost = apiHostHeader(input.host);
  if (!tenantHost) throw new Error('Host desconhecido.');

  const headers: Record<string, string> = {
    Accept: 'application/json',
    'X-Internal-Key': input.internalKey,
    'X-Tenant-Host': tenantHost,
  };
  const ip = clientIpFrom(input.forwardedFor);
  if (ip) headers['X-Client-Ip'] = ip;
  if (input.userAgent) headers['X-Client-User-Agent'] = input.userAgent;
  if (input.token) headers.Authorization = `Bearer ${input.token}`;
  if (input.hasBody) headers['Content-Type'] = 'application/json';
  return headers;
}
```

`web/src/lib/types.ts`:

```ts
export type Permission = { key: string; scope: string | null };

export type Me = {
  id: string;
  name: string;
  email: string;
  tenant: { id: string; slug: string; name: string };
  permissions: Permission[];
  modules: string[];
};

export type TenantInfo = { slug: string; name: string; platform: boolean };

export type Session = { token: string; absoluteExpiresAt: string };

export type RuleTotal = {
  ruleType: 'own' | 'upline' | 'global';
  level: number | null;
  groupId: string | null;
  groupName: string | null;
  rate: number;
  base: number;
  amount: number;
};

export type BeneficiaryCommission = { userId: string; name: string | null; total: number; byRule: RuleTotal[] };

export type Commissions = {
  competencia: string;
  status: string;
  source: 'live' | 'snapshot';
  total: number;
  beneficiaries: BeneficiaryCommission[];
};

export type PlatformTenant = { id: string; slug: string; name: string; status: 'ativo' | 'suspenso'; createdAt: string };

export type PlatformMe = { id: string; email: string };
```

`web/src/lib/form-state.ts`:

```ts
export type FormState = { error?: string; message?: string };
```

`web/src/lib/format.ts`:

```ts
import type { RuleTotal } from './types';

const money = new Intl.NumberFormat('pt-BR', { style: 'currency', currency: 'BRL' });
const percent = new Intl.NumberFormat('pt-BR', { style: 'percent', maximumFractionDigits: 2 });
const monthName = new Intl.DateTimeFormat('pt-BR', { month: 'long', year: 'numeric', timeZone: 'UTC' });
const saoPauloMonth = new Intl.DateTimeFormat('en-CA', { timeZone: 'America/Sao_Paulo', year: 'numeric', month: '2-digit' });

export const statusLabels: Record<string, string> = {
  apuracao: 'Em apuração',
  conferencia: 'Em conferência',
  confirmado: 'Confirmado',
  provisionado: 'Provisionado',
};

export function formatMoney(value: number): string {
  return money.format(value);
}

export function formatPercent(rate: number): string {
  return percent.format(rate);
}

export function formatCompetencia(competencia: string): string {
  const [year, month] = competencia.split('-').map(Number);
  return monthName.format(new Date(Date.UTC(year, month - 1, 1)));
}

export function currentCompetencia(now: Date = new Date()): string {
  const parts = saoPauloMonth.formatToParts(now);
  const year = parts.find((p) => p.type === 'year')?.value;
  const month = parts.find((p) => p.type === 'month')?.value;
  return `${year}-${month}`;
}

export function isCompetencia(value: string | undefined): value is string {
  return !!value && /^\d{4}-(0[1-9]|1[0-2])$/.test(value);
}

export function ruleLabel(rule: Pick<RuleTotal, 'ruleType' | 'level' | 'groupName'>): string {
  switch (rule.ruleType) {
    case 'own':
      return 'Carteira própria';
    case 'upline':
      return `Supervisão · ${rule.level}º nível`;
    case 'global':
      return rule.groupName ?? 'Grupo';
  }
}
```

`web/src/lib/errors.ts`:

```ts
const messages: Record<string, string> = {
  'auth.invalid_credentials': 'E-mail ou senha incorretos.',
  'auth.too_many_attempts': 'Muitas tentativas. Aguarde alguns minutos e tente de novo.',
  'auth.weak_password': 'A senha precisa ter entre 10 e 128 caracteres.',
  'auth.forbidden': 'Você não tem permissão para esta ação.',
  'auth.unauthenticated': 'Sua sessão expirou. Entre novamente.',
  'invite.invalid': 'Este link expirou ou já foi usado. Peça um novo.',
  'tenant.not_found': 'Empresa não encontrada.',
  'tenant.invalid_slug': 'Use só letras minúsculas, números e hífen no endereço.',
  'tenant.slug_taken': 'Esse endereço já está em uso.',
  'tenant.invalid_request': 'Preencha todos os campos.',
  'users.invalid_email': 'Informe um e-mail válido.',
  'plan.invalid_effective_from': 'Escolha o mês de início da vigência.',
};

export function messageFor(code: string): string {
  return messages[code] ?? 'Não foi possível concluir. Tente novamente.';
}
```

- [ ] **Step 4: Implementar os módulos do servidor**

`web/src/lib/env.ts`:

```ts
import 'server-only';

function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`Variável de ambiente ${name} não definida.`);
  return value;
}

export const env = {
  get API_INTERNAL_URL() {
    return required('API_INTERNAL_URL');
  },
  get API_INTERNAL_KEY() {
    return required('API_INTERNAL_KEY');
  },
  get ROOT_DOMAIN() {
    return required('ROOT_DOMAIN');
  },
};
```

`web/src/lib/session.ts`:

```ts
import 'server-only';
import { cookies } from 'next/headers';

export const SESSION_COOKIE = 'sid';

function secureCookies(): boolean {
  const configured = process.env.COOKIE_SECURE;
  return configured ? configured === 'true' : process.env.NODE_ENV === 'production';
}

export async function setSession(token: string, absoluteExpiresAt: string): Promise<void> {
  (await cookies()).set(SESSION_COOKIE, token, {
    httpOnly: true,
    secure: secureCookies(),
    sameSite: 'lax',
    path: '/',
    expires: new Date(absoluteExpiresAt),
  });
}

export async function clearSession(): Promise<void> {
  (await cookies()).delete(SESSION_COOKIE);
}
```

`web/src/lib/api.ts`:

```ts
import 'server-only';
import { cookies, headers } from 'next/headers';
import { buildApiHeaders } from './api-headers';
import { env } from './env';
import { parseHost, type HostInfo } from './host';
import { SESSION_COOKIE } from './session';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
  ) {
    super(code);
    this.name = 'ApiError';
  }
}

type ApiRequest = {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
  token?: string | null;
};

export async function currentHost(): Promise<HostInfo> {
  return parseHost((await headers()).get('host'), env.ROOT_DOMAIN);
}

export async function apiFetch<T = void>(path: string, request: ApiRequest = {}): Promise<T> {
  const incoming = await headers();
  const host = parseHost(incoming.get('host'), env.ROOT_DOMAIN);
  if (host.kind === 'unknown') throw new ApiError(404, 'tenant.not_found');

  const token = request.token === undefined ? (await cookies()).get(SESSION_COOKIE)?.value : (request.token ?? undefined);
  const response = await fetch(`${env.API_INTERNAL_URL}${path}`, {
    method: request.method ?? 'GET',
    headers: buildApiHeaders({
      host,
      internalKey: env.API_INTERNAL_KEY,
      token,
      forwardedFor: incoming.get('x-forwarded-for'),
      userAgent: incoming.get('user-agent'),
      hasBody: request.body !== undefined,
    }),
    body: request.body === undefined ? undefined : JSON.stringify(request.body),
    cache: 'no-store',
  });

  const text = await response.text();
  const data = text ? JSON.parse(text) : undefined;
  if (!response.ok) throw new ApiError(response.status, data?.code ?? 'internal.error');
  return data as T;
}
```

- [ ] **Step 5: Rodar testes e tipos**

Run: `cd web && npm test && npm run typecheck`
Expected: PASS e sem erros de tipo.

- [ ] **Step 6: Commit**

```bash
git add web/src/lib
git commit -m "feat(web): add internal API client, session cookie, formatting and error messages"
```

---

### Task 3: Login, logout e visão de comissões

**Files:**
- Create: `web/src/app/login/{page.tsx,login-form.tsx,actions.ts}`, `web/src/app/logout/route.ts`, `web/src/app/tenant-home.tsx`
- Modify: `web/src/app/page.tsx`

**Interfaces:**
- Consumes: `apiFetch`, `ApiError`, `currentHost`, `setSession`, `clearSession`, `SESSION_COOKIE`, `messageFor`, formatação e tipos (Task 2).
- Produces:
  - `/login`: no host de empresa, chama `POST /auth/login`; no host `admin`, chama `POST /platform/auth/login`. Host desconhecido: 404.
  - `POST /logout`: revoga na API, apaga o cookie e redireciona (303) para `/login`.
  - `/` na empresa: título `Olá, {nome}`, nome da empresa, total de comissões da competência (`?competencia=yyyy-MM`, padrão: mês atual em São Paulo) e tabela por pessoa e regra. Sem sessão ou com 401: redireciona para `/login`.
  - `/` na plataforma: a Task 5 implementa `PlatformHome`. Nesta task, `page.tsx` já faz o desvio, e `platform-home.tsx` fica provisório.
  - Rótulos que o E2E usa: campos `E-mail` e `Senha`, botões `Entrar` e `Sair`.

- [ ] **Step 1: Implementar o login**

`web/src/app/login/actions.ts`:

```ts
'use server';

import { redirect } from 'next/navigation';
import { ApiError, apiFetch, currentHost } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';
import { setSession } from '@/lib/session';
import type { Session } from '@/lib/types';

export async function login(_: FormState, formData: FormData): Promise<FormState> {
  const host = await currentHost();
  const path = host.kind === 'platform' ? '/platform/auth/login' : '/auth/login';
  try {
    const session = await apiFetch<Session>(path, {
      method: 'POST',
      body: { email: String(formData.get('email') ?? ''), password: String(formData.get('password') ?? '') },
      token: null,
    });
    await setSession(session.token, session.absoluteExpiresAt);
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  redirect('/');
}
```

`web/src/app/login/login-form.tsx`:

```tsx
'use client';

import Link from 'next/link';
import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { login } from './actions';

export function LoginForm({ platform }: { platform: boolean }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(login, {});
  return (
    <form action={formAction} className="form">
      <label>
        E-mail
        <input name="email" type="email" autoComplete="username" required />
      </label>
      <label>
        Senha
        <input name="password" type="password" autoComplete="current-password" required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Entrando…' : 'Entrar'}</button>
      {!platform && <Link href="/redefinir-senha">Esqueci minha senha</Link>}
    </form>
  );
}
```

`web/src/app/login/page.tsx`:

```tsx
import { notFound } from 'next/navigation';
import { ApiError, apiFetch } from '@/lib/api';
import type { TenantInfo } from '@/lib/types';
import { LoginForm } from './login-form';

export default async function LoginPage() {
  let tenant: TenantInfo;
  try {
    tenant = await apiFetch<TenantInfo>('/tenant', { token: null });
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) notFound();
    throw error;
  }

  return (
    <main className="auth">
      <section className="card">
        <p className="eyebrow">{tenant.platform ? 'Plataforma' : 'Recorrência comercial'}</p>
        <h1>{tenant.name}</h1>
        <LoginForm platform={tenant.platform} />
      </section>
    </main>
  );
}
```

`web/src/app/logout/route.ts`:

```ts
import { NextResponse } from 'next/server';
import { apiFetch } from '@/lib/api';
import { clearSession } from '@/lib/session';

export async function POST(request: Request) {
  await apiFetch('/auth/logout', { method: 'POST' }).catch(() => undefined);
  await clearSession();
  return NextResponse.redirect(new URL('/login', request.url), 303);
}
```

- [ ] **Step 2: Implementar a visão da empresa**

`web/src/app/tenant-home.tsx`:

```tsx
import { apiFetch } from '@/lib/api';
import {
  currentCompetencia,
  formatCompetencia,
  formatMoney,
  formatPercent,
  isCompetencia,
  ruleLabel,
  statusLabels,
} from '@/lib/format';
import type { Commissions, Me } from '@/lib/types';

export async function TenantHome({ competencia }: { competencia?: string }) {
  const me = await apiFetch<Me>('/me');
  const month = isCompetencia(competencia) ? competencia : currentCompetencia();
  const canSeeCommissions = me.permissions.some((p) => p.key === 'comissoes.visualizar');
  const commissions = canSeeCommissions ? await apiFetch<Commissions>(`/commissions/${month}`) : null;

  return (
    <main className="shell">
      <header className="topbar">
        <div>
          <p className="eyebrow">{me.tenant.name}</p>
          <h1>Olá, {me.name}</h1>
        </div>
        <form action="/logout" method="post">
          <button type="submit" className="secondary">Sair</button>
        </form>
      </header>

      {commissions ? (
        <section className="card">
          <div className="section-head">
            <div>
              <p className="eyebrow">Comissões · {formatCompetencia(month)}</p>
              <p className="total">{formatMoney(commissions.total)}</p>
            </div>
            <span className="badge">{statusLabels[commissions.status] ?? commissions.status}</span>
          </div>

          <form className="inline" method="get">
            <label>
              Competência
              <input type="month" name="competencia" defaultValue={month} />
            </label>
            <button type="submit" className="secondary">Ver</button>
          </form>

          {commissions.beneficiaries.length === 0 ? (
            <p className="muted">Nenhuma comissão nesta competência.</p>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>Pessoa</th>
                    <th>Origem</th>
                    <th className="number">Taxa</th>
                    <th className="number">Base paga</th>
                    <th className="number">Comissão</th>
                  </tr>
                </thead>
                {commissions.beneficiaries.map((person) => (
                  <tbody key={person.userId}>
                    {person.byRule.map((rule, index) => (
                      <tr key={`${rule.ruleType}-${rule.level}-${rule.groupId}-${rule.rate}`}>
                        <td>{index === 0 ? (person.name ?? 'Participante') : ''}</td>
                        <td>{ruleLabel(rule)}</td>
                        <td className="number">{formatPercent(rule.rate)}</td>
                        <td className="number">{formatMoney(rule.base)}</td>
                        <td className="number">{formatMoney(rule.amount)}</td>
                      </tr>
                    ))}
                    {person.byRule.length > 1 && (
                      <tr className="subtotal">
                        <td />
                        <td colSpan={3}>Total</td>
                        <td className="number">{formatMoney(person.total)}</td>
                      </tr>
                    )}
                  </tbody>
                ))}
              </table>
            </div>
          )}
        </section>
      ) : (
        <section className="card">
          <p className="muted">Seu perfil não inclui acesso às comissões.</p>
        </section>
      )}
    </main>
  );
}
```

`web/src/app/platform-home.tsx` (provisório, substituído na Task 5):

```tsx
export async function PlatformHome() {
  return <main className="shell"><h1>Empresas</h1></main>;
}
```

`web/src/app/page.tsx` (substitui o provisório):

```tsx
import { cookies } from 'next/headers';
import { notFound, redirect } from 'next/navigation';
import { ApiError, currentHost } from '@/lib/api';
import { SESSION_COOKIE } from '@/lib/session';
import { PlatformHome } from './platform-home';
import { TenantHome } from './tenant-home';

export default async function HomePage({ searchParams }: { searchParams: Promise<{ competencia?: string }> }) {
  const host = await currentHost();
  if (host.kind === 'unknown') notFound();
  if (!(await cookies()).has(SESSION_COOKIE)) redirect('/login');

  const { competencia } = await searchParams;
  let content: React.ReactNode;
  try {
    content = host.kind === 'platform' ? await PlatformHome() : await TenantHome({ competencia });
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) redirect('/login');
    throw error;
  }
  return content;
}
```

- [ ] **Step 3: Verificar tipos e build**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: sem erros.

- [ ] **Step 4: Verificar manualmente com a API**

Com o banco do compose migrado e semeado (plano 3, Task 11, Step 8), em dois terminais:

```bash
cd src/Recorrencia.Api && dotnet run --launch-profile http
```

```bash
cd web && npm run dev
```

Abra `http://aprovec.localhost:3000/` no navegador. Esperado: redireciona para `/login`. Entre com `joao@aprovec.local` / `senha-dev-123` e veja `Olá, João Silva`. Em `/?competencia=2026-09`, o total é R$ 1.600,00, com as linhas "Carteira própria" (R$ 1.400,00) e "Supervisão · 1º nível" (R$ 200,00). `Sair` volta ao login. `http://localhost:3000/` responde 404.

- [ ] **Step 5: Commit**

```bash
git add web/src/app
git commit -m "feat(web): add login, logout and commission overview"
```

---

### Task 4: Definir e redefinir senha

**Files:**
- Create: `web/src/app/definir-senha/[token]/{page.tsx,set-password-form.tsx,actions.ts}`
- Create: `web/src/app/redefinir-senha/{page.tsx,reset-form.tsx,actions.ts}`

**Interfaces:**
- Consumes: `apiFetch`, `ApiError`, `setSession`, `messageFor`, `FormState`, `Session` (Task 2).
- Produces:
  - `/definir-senha/{token}` (link do convite e da redefinição): campos `Nova senha` e `Confirme a senha`, botão `Salvar senha`. Chama `POST /auth/set-password`, cria a sessão e redireciona para `/`.
  - `/redefinir-senha`: campo `E-mail`, botão `Enviar link`. Chama `POST /auth/password-reset` e mostra sempre a mesma confirmação.

- [ ] **Step 1: Implementar definir senha**

`web/src/app/definir-senha/[token]/actions.ts`:

```ts
'use server';

import { redirect } from 'next/navigation';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';
import { setSession } from '@/lib/session';
import type { Session } from '@/lib/types';

export async function setPassword(_: FormState, formData: FormData): Promise<FormState> {
  const password = String(formData.get('password') ?? '');
  if (password !== String(formData.get('confirmation') ?? '')) return { error: 'As senhas não conferem.' };

  try {
    const session = await apiFetch<Session>('/auth/set-password', {
      method: 'POST',
      body: { token: String(formData.get('token') ?? ''), password },
      token: null,
    });
    await setSession(session.token, session.absoluteExpiresAt);
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  redirect('/');
}
```

`web/src/app/definir-senha/[token]/set-password-form.tsx`:

```tsx
'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { setPassword } from './actions';

export function SetPasswordForm({ token }: { token: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(setPassword, {});
  return (
    <form action={formAction} className="form">
      <input type="hidden" name="token" value={token} />
      <label>
        Nova senha
        <input name="password" type="password" autoComplete="new-password" minLength={10} maxLength={128} required />
      </label>
      <label>
        Confirme a senha
        <input name="confirmation" type="password" autoComplete="new-password" minLength={10} maxLength={128} required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Salvando…' : 'Salvar senha'}</button>
    </form>
  );
}
```

`web/src/app/definir-senha/[token]/page.tsx`:

```tsx
import { SetPasswordForm } from './set-password-form';

export default async function SetPasswordPage({ params }: { params: Promise<{ token: string }> }) {
  const { token } = await params;
  return (
    <main className="auth">
      <section className="card">
        <p className="eyebrow">Acesso</p>
        <h1>Defina sua senha</h1>
        <p className="muted">Use de 10 a 128 caracteres.</p>
        <SetPasswordForm token={token} />
      </section>
    </main>
  );
}
```

- [ ] **Step 2: Implementar redefinir senha**

`web/src/app/redefinir-senha/actions.ts`:

```ts
'use server';

import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function requestReset(_: FormState, formData: FormData): Promise<FormState> {
  try {
    await apiFetch('/auth/password-reset', {
      method: 'POST',
      body: { email: String(formData.get('email') ?? '') },
      token: null,
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  return { message: 'Se o e-mail estiver cadastrado, você vai receber um link para definir uma nova senha.' };
}
```

`web/src/app/redefinir-senha/reset-form.tsx`:

```tsx
'use client';

import Link from 'next/link';
import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { requestReset } from './actions';

export function ResetForm() {
  const [state, formAction, pending] = useActionState<FormState, FormData>(requestReset, {});
  return (
    <form action={formAction} className="form">
      <label>
        E-mail
        <input name="email" type="email" autoComplete="username" required />
      </label>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      {state.message && <p role="status" className="success">{state.message}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Enviando…' : 'Enviar link'}</button>
      <Link href="/login">Voltar para o login</Link>
    </form>
  );
}
```

`web/src/app/redefinir-senha/page.tsx`:

```tsx
import { ResetForm } from './reset-form';

export default function ResetPasswordPage() {
  return (
    <main className="auth">
      <section className="card">
        <p className="eyebrow">Acesso</p>
        <h1>Redefinir senha</h1>
        <p className="muted">Informe o e-mail usado no acesso.</p>
        <ResetForm />
      </section>
    </main>
  );
}
```

- [ ] **Step 3: Verificar tipos, lint e build**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: sem erros.

- [ ] **Step 4: Verificar manualmente**

Com a API e o `npm run dev` rodando, em `http://aprovec.localhost:3000/redefinir-senha`, peça o link para `maria@aprovec.local`. O arquivo mais recente em `tmp/emails/` contém o link `http://aprovec.localhost:3000/definir-senha/...`. Ao abri-lo e salvar `senha-nova-maria`, a página vai para `/` com `Olá, Maria Oliveira`. Depois de testar, volte a senha de desenvolvimento repetindo o fluxo com `senha-dev-123`.

- [ ] **Step 5: Commit**

```bash
git add web/src/app/definir-senha web/src/app/redefinir-senha
git commit -m "feat(web): add set-password and password reset pages"
```

---

### Task 5: Administração de empresas da plataforma

**Files:**
- Create: `web/src/app/platform-actions.ts`, `web/src/app/create-tenant-form.tsx`
- Modify: `web/src/app/platform-home.tsx`

**Interfaces:**
- Consumes: `apiFetch`, `ApiError`, `messageFor`, `FormState`, `PlatformTenant`, `PlatformMe`, `currentCompetencia` (Task 2).
- Produces:
  - `/` no host `admin`: título `Empresas`, formulário `Nova empresa` e tabela de empresas com as ações `Suspender`/`Reativar`.
  - Campos do formulário: `Nome da empresa`, `Endereço (subdomínio)`, `Nome do administrador`, `E-mail do administrador`, `Plano de comissão` (`Modelo APROVEC` ou `Sem plano`), `Início da vigência` (mês) e o botão `Criar empresa`. Mensagem de sucesso: `Empresa criada. O convite foi enviado para {email}.`
  - Server Actions `createTenant(state, formData)` e `setTenantStatus(formData)`.

- [ ] **Step 1: Implementar as ações**

`web/src/app/platform-actions.ts`:

```ts
'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function createTenant(_: FormState, formData: FormData): Promise<FormState> {
  const adminEmail = String(formData.get('adminEmail') ?? '').trim();
  const usePlan = formData.get('planTemplate') === 'aprovec';
  const planMonth = String(formData.get('planMonth') ?? '');

  try {
    await apiFetch('/platform/tenants', {
      method: 'POST',
      body: {
        slug: String(formData.get('slug') ?? '').trim(),
        name: String(formData.get('name') ?? '').trim(),
        adminName: String(formData.get('adminName') ?? '').trim(),
        adminEmail,
        planTemplate: usePlan ? 'aprovec' : null,
        planEffectiveFrom: usePlan && planMonth ? `${planMonth}-01` : null,
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }

  revalidatePath('/');
  return { message: `Empresa criada. O convite foi enviado para ${adminEmail}.` };
}

export async function setTenantStatus(formData: FormData): Promise<void> {
  const id = encodeURIComponent(String(formData.get('id') ?? ''));
  await apiFetch(`/platform/tenants/${id}/status`, {
    method: 'POST',
    body: { status: String(formData.get('status') ?? '') },
  });
  revalidatePath('/');
}
```

- [ ] **Step 2: Implementar o formulário e a página**

`web/src/app/create-tenant-form.tsx`:

```tsx
'use client';

import { useActionState } from 'react';
import type { FormState } from '@/lib/form-state';
import { createTenant } from './platform-actions';

export function CreateTenantForm({ defaultMonth }: { defaultMonth: string }) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(createTenant, {});
  return (
    <form action={formAction} className="form">
      <label>
        Nome da empresa
        <input name="name" required />
      </label>
      <label>
        Endereço (subdomínio)
        <input name="slug" pattern="[a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?" placeholder="minha-empresa" required />
      </label>
      <label>
        Nome do administrador
        <input name="adminName" required />
      </label>
      <label>
        E-mail do administrador
        <input name="adminEmail" type="email" required />
      </label>
      <div className="inline">
        <label>
          Plano de comissão
          <select name="planTemplate" defaultValue="aprovec">
            <option value="aprovec">Modelo APROVEC</option>
            <option value="">Sem plano</option>
          </select>
        </label>
        <label>
          Início da vigência
          <input name="planMonth" type="month" defaultValue={defaultMonth} />
        </label>
      </div>
      {state.error && <p role="alert" className="error">{state.error}</p>}
      {state.message && <p role="status" className="success">{state.message}</p>}
      <button type="submit" disabled={pending}>{pending ? 'Criando…' : 'Criar empresa'}</button>
    </form>
  );
}
```

`web/src/app/platform-home.tsx` (substitui o provisório):

```tsx
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
```

- [ ] **Step 3: Verificar tipos, lint e build**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: sem erros.

- [ ] **Step 4: Verificar manualmente**

Em `http://admin.localhost:3000/`, entre com `admin@plataforma.local` / `senha-dev-123`. A tabela lista `APROVEC`. Crie uma empresa `teste-manual` e confirme a mensagem de sucesso, a nova linha na tabela e o e-mail em `tmp/emails/`. `Suspender` troca a situação para `Suspensa`, e `http://teste-manual.localhost:3000/login` passa a responder 404. `Reativar` desfaz.

- [ ] **Step 5: Commit**

```bash
git add web/src/app
git commit -m "feat(web): add platform tenant administration"
```

---

### Task 6: Teste de ponta a ponta

**Files:**
- Create: `web/playwright.config.ts`, `web/e2e/prepare.mjs`, `web/e2e/emails.ts`, `web/e2e/fundacao.spec.ts`

**Interfaces:**
- Consumes: tudo o que foi construído nos planos 1 a 4; `seed-dev` e `FileEmailSender` (plano 3); `.env` na raiz (plano 1, Task 1).
- Produces: `npm run e2e` (em `web/`), que prepara o banco, sobe API e web e roda os cenários.

- [ ] **Step 1: Preparação do ambiente**

`web/e2e/prepare.mjs`:

```js
import { execSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

function loadDotEnv() {
  const file = [path.join(root, '.env'), path.join(root, '.env.example')].find((f) => existsSync(f));
  return Object.fromEntries(
    readFileSync(file, 'utf8')
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line && !line.startsWith('#') && line.includes('='))
      .map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]),
  );
}

const env = { ...process.env, ...loadDotEnv() };
const run = (command, cwd = root) => execSync(command, { cwd, stdio: 'inherit', env });

run('docker compose up -d --wait db');
run('dotnet run --project src/Recorrencia.Db -- bootstrap');
run('dotnet run --project src/Recorrencia.Db -- migrate');
run('dotnet build src/Recorrencia.Api');
run('dotnet run --no-build --launch-profile http -- seed-dev', path.join(root, 'src/Recorrencia.Api'));
```

A API é compilada aqui, antes do Playwright subir os servidores com `--no-build`. Isso evita duas compilações simultâneas do mesmo projeto. O `seed-dev` roda dentro do diretório da API porque o ASP.NET usa o diretório atual como content root (onde estão os `appsettings`).

- [ ] **Step 2: Configuração do Playwright**

`web/playwright.config.ts`:

```ts
import { defineConfig, devices } from '@playwright/test';

const webEnv = {
  API_INTERNAL_URL: 'http://127.0.0.1:5080',
  API_INTERNAL_KEY: 'chave-interna-de-desenvolvimento-32+',
  ROOT_DOMAIN: 'localhost:3000',
  COOKIE_SECURE: 'false',
};

export default defineConfig({
  testDir: './e2e',
  workers: 1,
  expect: { timeout: 15_000 },
  use: { ...devices['Desktop Chrome'], trace: 'retain-on-failure' },
  webServer: [
    {
      command: 'dotnet run --no-build --launch-profile http',
      cwd: '../src/Recorrencia.Api',
      url: 'http://127.0.0.1:5080/health',
      reuseExistingServer: true,
      timeout: 120_000,
    },
    {
      command: 'npm run dev',
      url: 'http://127.0.0.1:3000/api/health',
      reuseExistingServer: true,
      timeout: 120_000,
      env: webEnv,
    },
  ],
});
```

`web/e2e/emails.ts`:

```ts
import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';

const outbox = path.resolve(__dirname, '../../tmp/emails');

export async function latestLinkFor(email: string, timeoutMs = 10_000): Promise<string> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const files = (await readdir(outbox).catch(() => [] as string[])).sort().reverse();
    for (const file of files) {
      const content = await readFile(path.join(outbox, file), 'utf8');
      const link = content.match(/https?:\/\/\S+\/definir-senha\/[A-Za-z0-9_-]+/);
      if (content.includes(`Para: ${email}`) && link) return link[0];
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`Nenhum e-mail com link para ${email}.`);
}
```

- [ ] **Step 3: Escrever os cenários**

`web/e2e/fundacao.spec.ts`:

```ts
import { expect, test, type Page } from '@playwright/test';
import { latestLinkFor } from './emails';

const tenant = 'http://aprovec.localhost:3000';
const platform = 'http://admin.localhost:3000';
const password = 'senha-dev-123';

async function login(page: Page, baseUrl: string, email: string, secret = password) {
  await page.goto(`${baseUrl}/login`);
  await page.getByLabel('E-mail', { exact: true }).fill(email);
  await page.getByLabel('Senha', { exact: true }).fill(secret);
  await page.getByRole('button', { name: 'Entrar' }).click();
}

test('consultor entra, vê a comissão de setembro e sai', async ({ page }) => {
  await page.goto(`${tenant}/`);
  await expect(page).toHaveURL(`${tenant}/login`);

  await login(page, tenant, 'joao@aprovec.local');
  await expect(page.getByRole('heading', { name: 'Olá, João Silva' })).toBeVisible();

  await page.goto(`${tenant}/?competencia=2026-09`);
  await expect(page.locator('.total')).toHaveText(/R\$\s1\.600,00/);
  await expect(page.getByRole('cell', { name: 'Carteira própria' })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Supervisão · 1º nível' })).toBeVisible();

  await page.getByRole('button', { name: 'Sair' }).click();
  await expect(page).toHaveURL(`${tenant}/login`);
});

test('coordenação vê o total do exemplo do PDF', async ({ page }) => {
  await login(page, tenant, 'coordenacao@aprovec.local');
  await page.goto(`${tenant}/?competencia=2026-09`);
  await expect(page.locator('.total')).toHaveText(/R\$\s3\.100,00/);
});

test('senha errada mostra a mensagem de erro', async ({ page }) => {
  await login(page, tenant, 'pedro@aprovec.local', 'senha-errada-123');
  await expect(page.getByRole('alert')).toHaveText('E-mail ou senha incorretos.');
});

test('pedido de redefinição mostra sempre a mesma confirmação', async ({ page }) => {
  await page.goto(`${tenant}/redefinir-senha`);
  await page.getByLabel('E-mail', { exact: true }).fill('ninguem@aprovec.local');
  await page.getByRole('button', { name: 'Enviar link' }).click();
  await expect(page.getByRole('status')).toContainText('Se o e-mail estiver cadastrado');
});

test('plataforma cria empresa e a administradora define a senha pelo convite', async ({ page }) => {
  const slug = `e2e-${Date.now()}`;
  const adminEmail = `dona@${slug}.local`;

  await login(page, platform, 'admin@plataforma.local');
  await expect(page.getByRole('heading', { name: 'Empresas' })).toBeVisible();

  await page.getByLabel('Nome da empresa').fill('Associação E2E');
  await page.getByLabel('Endereço (subdomínio)').fill(slug);
  await page.getByLabel('Nome do administrador').fill('Dona E2E');
  await page.getByLabel('E-mail do administrador').fill(adminEmail);
  await page.getByRole('button', { name: 'Criar empresa' }).click();
  await expect(page.getByRole('status')).toContainText('Empresa criada');
  await expect(page.getByRole('cell', { name: slug })).toBeVisible();

  await page.goto(await latestLinkFor(adminEmail));
  await page.getByLabel('Nova senha').fill('senha-da-dona-1');
  await page.getByLabel('Confirme a senha').fill('senha-da-dona-1');
  await page.getByRole('button', { name: 'Salvar senha' }).click();

  await expect(page.getByRole('heading', { name: 'Olá, Dona E2E' })).toBeVisible();
  await expect(page.getByText('Associação E2E')).toBeVisible();
});
```

- [ ] **Step 4: Rodar o E2E**

Pare qualquer `dotnet run` ou `npm run dev` manual que esteja nas portas 5080/3000. Depois:

```bash
cd web && npm run e2e
```

Expected: 5 cenários PASS. Em caso de falha, o trace fica em `web/test-results/` (`npx playwright show-trace <arquivo>`).

- [ ] **Step 5: Commit**

```bash
git add web/playwright.config.ts web/e2e web/package.json
git commit -m "test(web): add end-to-end scenarios for login, commissions and tenant provisioning"
```

---

### Task 7: Containers e documentação de desenvolvimento

**Files:**
- Create: `src/Recorrencia.Db/Dockerfile`, `src/Recorrencia.Api/Dockerfile`, `web/Dockerfile`, `web/.dockerignore`, `.dockerignore`
- Modify: `docker-compose.yml`, `.env.example`, `README.md`

**Interfaces:**
- Consumes: comandos `bootstrap`/`migrate` (plano 1), API (plano 3), web (Tasks 1–5).
- Produces: `docker compose up -d --build` sobe `db`, `migrate` (roda e termina), `api` (só na rede interna, porta 8080) e `web` (`127.0.0.1:3000`).

- [ ] **Step 1: Dockerfiles**

`src/Recorrencia.Db/Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Recorrencia.Db/Recorrencia.Db.csproj src/Recorrencia.Db/
RUN dotnet restore src/Recorrencia.Db/Recorrencia.Db.csproj
COPY src/Recorrencia.Db/ src/Recorrencia.Db/
RUN dotnet publish src/Recorrencia.Db/Recorrencia.Db.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "Recorrencia.Db.dll"]
```

`src/Recorrencia.Api/Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Recorrencia.Db/Recorrencia.Db.csproj src/Recorrencia.Db/
COPY src/Recorrencia.Domain/Recorrencia.Domain.csproj src/Recorrencia.Domain/
COPY src/Recorrencia.Api/Recorrencia.Api.csproj src/Recorrencia.Api/
RUN dotnet restore src/Recorrencia.Api/Recorrencia.Api.csproj
COPY src/ src/
RUN dotnet publish src/Recorrencia.Api/Recorrencia.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Recorrencia.Api.dll"]
```

`web/Dockerfile`:

```dockerfile
FROM node:22-alpine AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci

FROM node:22-alpine AS build
WORKDIR /app
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN npm run build

FROM node:22-alpine
WORKDIR /app
ENV NODE_ENV=production PORT=3000 HOSTNAME=0.0.0.0
COPY --from=build /app/.next/standalone ./
COPY --from=build /app/.next/static ./.next/static
COPY --from=build /app/public ./public
USER node
EXPOSE 3000
CMD ["node", "server.js"]
```

`web/.dockerignore`:

```
node_modules
.next
test-results
playwright-report
.env*.local
```

`.dockerignore` (raiz):

```
.git
**/bin
**/obj
web
mockup
design
docs
tests
tmp
```

- [ ] **Step 2: Compose completo e variáveis**

Acrescente ao `.env.example`:

```
INTERNAL_API_KEY=chave-interna-de-desenvolvimento-32+
ROOT_DOMAIN=localhost:3000
WEB_SCHEME=http
COOKIE_SECURE=false
```

e acrescente as mesmas linhas ao seu `.env` local.

`docker-compose.yml` (substitui o do plano 1):

```yaml
services:
  db:
    image: pgvector/pgvector:pg17
    environment:
      POSTGRES_DB: recorrencia
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?defina POSTGRES_PASSWORD no .env}
    ports:
      - "127.0.0.1:55432:5432"
    volumes:
      - db-data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d recorrencia"]
      interval: 5s
      timeout: 3s
      retries: 20

  migrate:
    build:
      context: .
      dockerfile: src/Recorrencia.Db/Dockerfile
    entrypoint: ["sh", "-c", "dotnet Recorrencia.Db.dll bootstrap && dotnet Recorrencia.Db.dll migrate"]
    environment:
      DB_SUPERUSER_CONNECTION: Host=db;Port=5432;Database=recorrencia;Username=postgres;Password=${POSTGRES_PASSWORD}
      DB_OWNER_CONNECTION: Host=db;Port=5432;Database=recorrencia;Username=app_owner;Password=${DB_OWNER_PASSWORD}
      DB_OWNER_PASSWORD: ${DB_OWNER_PASSWORD}
      DB_APP_USER_PASSWORD: ${DB_APP_USER_PASSWORD}
      DB_SUPERADMIN_PASSWORD: ${DB_SUPERADMIN_PASSWORD}
    depends_on:
      db:
        condition: service_healthy
    restart: "no"

  api:
    build:
      context: .
      dockerfile: src/Recorrencia.Api/Dockerfile
    environment:
      ConnectionStrings__App: Host=db;Port=5432;Database=recorrencia;Username=app_user;Password=${DB_APP_USER_PASSWORD}
      ConnectionStrings__Superadmin: Host=db;Port=5432;Database=recorrencia;Username=app_superadmin;Password=${DB_SUPERADMIN_PASSWORD}
      Internal__Key: ${INTERNAL_API_KEY:?defina INTERNAL_API_KEY no .env}
      Web__Scheme: ${WEB_SCHEME:-https}
      Web__RootDomain: ${ROOT_DOMAIN:?defina ROOT_DOMAIN no .env}
    expose:
      - "8080"
    depends_on:
      migrate:
        condition: service_completed_successfully
    restart: unless-stopped

  web:
    build:
      context: ./web
    environment:
      API_INTERNAL_URL: http://api:8080
      API_INTERNAL_KEY: ${INTERNAL_API_KEY}
      ROOT_DOMAIN: ${ROOT_DOMAIN}
      COOKIE_SECURE: ${COOKIE_SECURE:-true}
    ports:
      - "127.0.0.1:3000:3000"
    depends_on:
      - api
    restart: unless-stopped

volumes:
  db-data:
```

- [ ] **Step 3: Verificar os containers**

Pare servidores locais nas portas 3000/5080 e rode:

```bash
docker compose up -d --build
docker compose ps
docker compose run --rm -e ASPNETCORE_ENVIRONMENT=Development api seed-dev
curl -s -o /dev/null -w '%{http_code}\n' -H 'Host: aprovec.localhost:3000' http://127.0.0.1:3000/login
curl -s -o /dev/null -w '%{http_code}\n' -H 'Host: localhost:3000' http://127.0.0.1:3000/login
```

Expected: `migrate` com estado `exited (0)`; `db`, `api` e `web` em execução; `seed-dev` cria os dados ou avisa que eles já existem; o primeiro `curl` imprime `200` e o segundo `404`. A porta 8080 da API não aparece em `docker compose ps` como publicada. Depois, abra `http://aprovec.localhost:3000` no navegador e entre com `joao@aprovec.local` / `senha-dev-123`.

Encerre com `docker compose down` (sem `-v`, para manter os dados).

- [ ] **Step 4: Documentar no README**

Acrescente ao final de `README.md`:

````markdown
## Plataforma (fundação)

A partir do mockup, a plataforma real é um SaaS multiempresa: API interna em C# (`src/Recorrencia.Api`), BFF em Next.js (`web/`) e PostgreSQL com RLS (`src/Recorrencia.Db`). O desenho está em `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md`.

### Pré-requisitos

.NET SDK 10, Node 22 e Docker (no macOS, via Colima). Antes de rodar os testes .NET:

```sh
colima start
export DOCKER_HOST="unix://$HOME/.colima/default/docker.sock"
export TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock
```

### Desenvolvimento

```sh
cp .env.example .env
cp web/.env.local.example web/.env.local
docker compose up -d db
set -a; source .env; set +a
dotnet run --project src/Recorrencia.Db -- bootstrap
dotnet run --project src/Recorrencia.Db -- migrate
(cd src/Recorrencia.Api && dotnet run --launch-profile http -- seed-dev)
(cd src/Recorrencia.Api && dotnet run --launch-profile http)
(cd web && npm install && npm run dev)
```

- Empresa de exemplo: http://aprovec.localhost:3000 (`joao@aprovec.local`, `maria@aprovec.local`, `pedro@aprovec.local`, `coordenacao@aprovec.local`, `admin@aprovec.local`).
- Plataforma: http://admin.localhost:3000 (`admin@plataforma.local`).
- Senha de todos no desenvolvimento: `senha-dev-123`.
- E-mails (convites e redefinições) são gravados em `tmp/emails/`.

### Testes

```sh
dotnet test
(cd web && npm test)
(cd web && npm run e2e)
```

### Containers

`docker compose up -d --build` sobe banco, migrações, API (sem porta publicada) e web em `127.0.0.1:3000`. Em produção, coloque um proxy reverso com TLS curinga (`*.seu-dominio`) na frente do `web`, defina `ROOT_DOMAIN`, `WEB_SCHEME=https` e `COOKIE_SECURE=true`, e garanta que o proxy sobrescreva `X-Forwarded-For`. O envio de e-mail por SMTP ainda não está implementado: até lá, os e-mails aparecem no log da API.
````

- [ ] **Step 5: Commit**

```bash
git add src/Recorrencia.Db/Dockerfile src/Recorrencia.Api/Dockerfile web/Dockerfile web/.dockerignore .dockerignore docker-compose.yml .env.example README.md
git commit -m "build: containerize db migrations, API and web, and document development setup"
```
