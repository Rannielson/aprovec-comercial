# Port do mockup aprovado para o BFF (Fase 1 — vendedor) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Aplicar a identidade visual do mockup aprovado (`mockup/`) às páginas do BFF que já existem e já funcionam com dados reais — login, definir senha, redefinir senha, página inicial da empresa, 404/erro — sem criar nenhum endpoint novo e sem mudar nenhuma lógica de sessão/autenticação/dados.

**Architecture:** Todo o trabalho é CSS + JSX de apresentação em `web/src/app/**`. Uma reescrita de `web/src/app/globals.css` estabelece os tokens de design (cor, tipografia, raio, badges) e os componentes compartilhados (cartão, botão, formulário, tabela); a página inicial da empresa (`tenant-home.tsx`) ganha uma "faixa de comissão" (hero) e um card de fechamento anterior, ambos usando dados que a API já expõe hoje.

**Tech Stack:** Next.js 16 (App Router, Server Components), CSS puro em `globals.css` (sem Tailwind, sem CSS modules — segue o padrão já estabelecido no projeto), Vitest para os testes de `web/src/lib`.

**Spec:** `docs/superpowers/specs/2026-09-24-mockup-visual-port-design.md`

## Global Constraints

- Zero endpoints novos na API, zero mudança em `src/Recorrencia.Api`/`src/Recorrencia.Db`.
- Zero mudança de lógica em `web/src/lib/**` além de duas funções puras novas descritas na Task 2 (nenhuma mudança em `apiFetch`, sessão, ou Server Actions existentes).
- Tema único, claro — sem o bloco `@media (prefers-color-scheme: dark)` do `globals.css` atual.
- Sem casca fixa (sidebar/topbar de navegação) e sem theming por tenant nesta fase — ver spec.
- Tokens de cor extraídos literalmente de `mockup/brand.css` + `mockup/styles.css` (valores exatos, não aproximados).
- Fontes self-hospedadas (Poppins 400/500/600, Inter variável) via `@font-face` com `font-display: swap`, copiadas de `mockup/assets/fonts/`.
- Textos da interface em português do Brasil (já é o padrão do projeto).
- Commits: mensagens convencionais (`feat(web): ...` / `style(web): ...`), terminando com a linha de atribuição indicada pelo ambiente.

---

**Nota sobre login/definir-senha/redefinir-senha/404/erro:** a spec lista esses arquivos como "afetados", mas uma verificação (`grep` das `className` usadas em cada um) confirmou que todos já usam exatamente as classes que a Task 1 redefine (`.auth`, `.card`, `.eyebrow`, `.muted`, `.form`, `button`, `button.secondary`, `.error`, `.success`) e nenhuma outra. Isso significa que essas 5 páginas não precisam de nenhuma edição de `.tsx` — elas herdam a nova identidade automaticamente assim que a Task 1 substitui `globals.css`. Não há task dedicada a elas; a Task 4 verifica visualmente o resultado.

### Task 1: Tokens de design, fontes, logo e componentes de base

**Files:**
- Create: `web/public/fonts/poppins-regular.woff2`, `web/public/fonts/poppins-medium.woff2`, `web/public/fonts/poppins-semibold.woff2`, `web/public/fonts/inter-variable.woff2` (copiados de `mockup/assets/fonts/`)
- Create: `web/public/logo-aprovec.webp` (copiado de `mockup/assets/logo-aprovec.webp`)
- Modify: `web/src/app/globals.css` (reescrita completa)

**Interfaces:**
- Consumes: nada (fundação).
- Produces: as variáveis CSS `--brand-red`, `--brand-red-dark`, `--brand-black`, `--paper`, `--surface`, `--ink`, `--muted`, `--line`, `--green`, `--green-bg`, `--amber`, `--amber-bg`, `--status-red`, `--status-red-bg`, `--accent`, `--accent-strong`, `--error`, `--ok`; as classes `.auth`, `.shell`, `.card`, `.topbar`, `.section-head`, `.eyebrow`, `.muted`, `.total`, `.badge` (+ variantes `.progress`/`.warning`/`.recebido`/`.neutral`), `.form`, `.inline`, `button`/`button.secondary`, `.error`, `.success`, `.table-wrap`/`table`/`th`/`td`/`.number`/`.subtotal`. Tasks 2 e 3 consomem essas classes; nenhuma classe nova de componente específico da Task 3 (`.commission-band` etc.) é criada aqui.

- [ ] **Step 1: Copiar as fontes e o logo**

```bash
mkdir -p web/public/fonts
cp mockup/assets/fonts/poppins-regular.woff2 web/public/fonts/
cp mockup/assets/fonts/poppins-medium.woff2 web/public/fonts/
cp mockup/assets/fonts/poppins-semibold.woff2 web/public/fonts/
cp mockup/assets/fonts/inter-variable.woff2 web/public/fonts/
cp mockup/assets/logo-aprovec.webp web/public/logo-aprovec.webp
```

- [ ] **Step 2: Reescrever `web/src/app/globals.css`**

Substitua o arquivo inteiro por:

```css
@font-face { font-family: Poppins; src: url('/fonts/poppins-regular.woff2') format('woff2'); font-style: normal; font-weight: 400; font-display: swap; }
@font-face { font-family: Poppins; src: url('/fonts/poppins-medium.woff2') format('woff2'); font-style: normal; font-weight: 500; font-display: swap; }
@font-face { font-family: Poppins; src: url('/fonts/poppins-semibold.woff2') format('woff2'); font-style: normal; font-weight: 600; font-display: swap; }
@font-face { font-family: Inter; src: url('/fonts/inter-variable.woff2') format('woff2'); font-style: normal; font-weight: 100 900; font-display: swap; }

:root {
  --brand-red: #da0000;
  --brand-red-dark: #ab090a;
  --brand-black: #0c0c0e;
  --paper: #f7f7f7;
  --surface: #ffffff;
  --ink: #202023;
  --muted: #6c6c73;
  --line: #e7e5e5;
  --green: #17673a;
  --green-bg: #ebf5e8;
  --amber: #8c6118;
  --amber-bg: #fff5df;
  --status-red: #a33e46;
  --status-red-bg: #fceded;
  --accent: var(--brand-red-dark);
  --accent-strong: var(--brand-black);
  --error: var(--status-red);
  --ok: var(--green);
  font-family: Poppins, Arial, sans-serif;
  color: var(--ink);
  background: var(--paper);
}

* { box-sizing: border-box; }
body { margin: 0; min-height: 100vh; background: var(--paper); }
h1, h2 { margin: 0; letter-spacing: -0.02em; font-family: Inter, Arial, sans-serif; color: var(--brand-black); }
h1 { font-size: 1.6rem; font-weight: 700; letter-spacing: -0.045em; }
h2 { font-size: 1.1rem; font-weight: 650; }
a { color: var(--accent); }

.auth { display: grid; place-items: center; min-height: 100vh; padding: 16px; }
.shell { max-width: 1040px; margin: 0 auto; padding: 24px 16px; display: grid; gap: 16px; }
.card { background: var(--surface); border: 1px solid var(--line); border-radius: 16px 0 16px 0; padding: 24px; display: grid; gap: 16px; width: 100%; max-width: 100%; }
.auth .card { max-width: 400px; }
.topbar { display: flex; justify-content: space-between; align-items: center; gap: 16px; flex-wrap: wrap; }
.section-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 16px; flex-wrap: wrap; }
.eyebrow { margin: 0; color: #a52b2b; font-size: 0.75rem; font-weight: 600; letter-spacing: 0.08em; text-transform: uppercase; }
.muted { margin: 0; color: var(--muted); }
.total { margin: 4px 0 0; font-family: Inter, Arial, sans-serif; font-size: 2rem; font-weight: 700; letter-spacing: -0.03em; }

.badge { display: inline-flex; align-items: center; border: 1px solid var(--line); border-radius: 999px; padding: 4px 12px; font-size: 0.8rem; color: var(--muted); background: var(--surface); }
.badge.progress { border-color: transparent; background: #f2eded; color: #7b5556; }
.badge.neutral { border-color: transparent; background: #f0eded; color: #74656b; }
.badge.recebido { border-color: transparent; background: var(--green-bg); color: var(--green); }
.badge.warning { border-color: transparent; background: var(--amber-bg); color: var(--amber); }

.form { display: grid; gap: 12px; }
.inline { display: flex; gap: 8px; align-items: flex-end; flex-wrap: wrap; }
label { display: grid; gap: 4px; font-size: 0.85rem; color: var(--muted); }
input, select { font: inherit; color: var(--ink); padding: 10px 12px; border: 1px solid var(--line); border-radius: 8px; background: var(--surface); min-width: 0; }
button { font: inherit; font-weight: 500; cursor: pointer; padding: 10px 16px; border: 1px solid var(--accent-strong); border-radius: 12px 0 12px 0; background: var(--accent-strong); color: #fff; }
button:hover:not(:disabled) { background: var(--accent); border-color: var(--accent); }
button:disabled { opacity: 0.6; cursor: wait; }
button.secondary { background: var(--surface); color: var(--ink); border-color: var(--line); }
button.secondary:hover:not(:disabled) { color: var(--accent); border-color: #d1b7b8; background: #fff6f5; }
.error { margin: 0; color: var(--error); }
.success { margin: 0; color: var(--ok); }

.table-wrap { overflow-x: auto; }
table { width: 100%; border-collapse: collapse; font-size: 0.9rem; }
th, td { text-align: left; padding: 10px 8px; border-bottom: 1px solid var(--line); white-space: nowrap; }
th { color: var(--muted); font-weight: 500; font-size: 0.8rem; }
td.number, th.number { text-align: right; font-variant-numeric: tabular-nums; }
tr.subtotal td { font-weight: 600; }
```

- [ ] **Step 3: Verificar tipos, lint e build**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: sem erros.

- [ ] **Step 4: Verificar visualmente que o login já reflete a nova identidade**

Com a API e `npm run dev` rodando (ver README, seção Desenvolvimento), abra `http://aprovec.localhost:3000/login`. Esperado: fundo cinza-claro (`#f7f7f7`), cartão branco com o canto superior-direito e inferior-esquerdo retos e os outros dois arredondados (raio assimétrico `16px 0 16px 0`), rótulo vermelho-escuro "RECORRÊNCIA COMERCIAL" em maiúsculas, botão "Entrar" preto que fica vermelho-escuro no hover.

- [ ] **Step 5: Commit**

```bash
git add web/public/fonts web/public/logo-aprovec.webp web/src/app/globals.css
git commit -m "style(web): apply APROVEC brand tokens, fonts and base components"
```

---

### Task 2: Funções de formatação para competência anterior e cor de situação

**Files:**
- Modify: `web/src/lib/format.ts`
- Test: `web/src/lib/format.test.ts`

**Interfaces:**
- Consumes: nada de novo (usa o mesmo padrão de `formatCompetencia`/`isCompetencia` já existentes no arquivo).
- Produces: `previousCompetencia(competencia: string): string` e `statusBadgeClass(status: string): string`. A Task 3 importa e usa ambas.

- [ ] **Step 1: Escrever os testes que falham**

Adicione ao final do `describe('format', ...)` em `web/src/lib/format.test.ts` (e adicione `previousCompetencia, statusBadgeClass` à lista de imports no topo do arquivo):

```ts
  it('calcula a competência anterior', () => {
    expect(previousCompetencia('2026-09')).toBe('2026-08');
    expect(previousCompetencia('2026-01')).toBe('2025-12');
  });

  it('escolhe a classe de badge pela situação', () => {
    expect(statusBadgeClass('apuracao')).toBe('badge progress');
    expect(statusBadgeClass('conferencia')).toBe('badge warning');
    expect(statusBadgeClass('confirmado')).toBe('badge recebido');
    expect(statusBadgeClass('provisionado')).toBe('badge neutral');
    expect(statusBadgeClass('desconhecido')).toBe('badge');
  });
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `cd web && npm test`
Expected: FAIL — `previousCompetencia is not defined` / `statusBadgeClass is not defined`.

- [ ] **Step 3: Implementar as duas funções**

Adicione ao final de `web/src/lib/format.ts`:

```ts
export function previousCompetencia(competencia: string): string {
  const [year, month] = competencia.split('-').map(Number);
  const previous = new Date(Date.UTC(year, month - 2, 1));
  return `${previous.getUTCFullYear()}-${String(previous.getUTCMonth() + 1).padStart(2, '0')}`;
}

export function statusBadgeClass(status: string): string {
  switch (status) {
    case 'apuracao':
      return 'badge progress';
    case 'conferencia':
      return 'badge warning';
    case 'confirmado':
      return 'badge recebido';
    case 'provisionado':
      return 'badge neutral';
    default:
      return 'badge';
  }
}
```

- [ ] **Step 4: Rodar os testes e confirmar que passam**

Run: `cd web && npm test`
Expected: PASS — todos os testes de `format.test.ts`, incluindo os 2 novos.

- [ ] **Step 5: Typecheck e lint**

Run: `cd web && npm run typecheck && npm run lint`
Expected: sem erros.

- [ ] **Step 6: Commit**

```bash
git add web/src/lib/format.ts web/src/lib/format.test.ts
git commit -m "feat(web): add previousCompetencia and statusBadgeClass helpers"
```

---

### Task 3: Faixa de comissão e card de fechamento anterior na página inicial

**Files:**
- Modify: `web/src/app/tenant-home.tsx`
- Modify: `web/src/app/globals.css`
- Modify: `web/e2e/fundacao.spec.ts`

**Interfaces:**
- Consumes: `apiFetch` (`@/lib/api`), `currentCompetencia`/`formatCompetencia`/`formatMoney`/`formatPercent`/`isCompetencia`/`previousCompetencia`/`ruleLabel`/`statusBadgeClass` (`@/lib/format` — as duas últimas vêm da Task 2), `Commissions`/`Me` (`@/lib/types`). Classes CSS de base da Task 1 (`.shell`, `.topbar`, `.eyebrow`, `.muted`, `.badge`, `.inline`, `.table-wrap`, `table`, `.number`, `.subtotal`, `.card`).
- Produces: nenhuma interface nova para outras tasks (esta é a última página tocada nesta fase).

**Por que a faixa de comissão e a tabela detalhada convivem:** o mockup já faz isso na própria tela de comissões (`commissions()` em `app.js`) — mostra o total em destaque e, embaixo, o detalhamento por origem. Aqui, a faixa mostra sempre os totais do usuário logado (achado pelo `userId` dele em `commissions.beneficiaries`); a tabela abaixo continua mostrando todos os beneficiários visíveis (que pode ser só o próprio usuário, ou também subordinados, dependendo do escopo de quem está vendo). Não há tentativa de esconder a tabela quando ela "repete" a faixa — replicar o número em granularidades diferentes é intencional, como no mockup.

- [ ] **Step 1: Adicionar as classes da faixa de comissão e do card de fechamento ao `globals.css`**

Remova as duas regras `.section-head { ... }` e `.total { ... }` do arquivo — `tenant-home.tsx` era o único arquivo em `web/src/app` que usava essas classes (confirmado por `grep -rn 'section-head\|"total"' web/src/app --include="*.tsx"`), e o Step 2 abaixo remove o uso delas. Adicione ao final do arquivo:

```css
.commission-band { background: var(--brand-black); border-radius: 20px 0 20px 0; padding: 28px; display: flex; align-items: center; gap: 24px; flex-wrap: wrap; color: #fff; }
.commission-total { min-width: 220px; padding-right: 24px; border-right: 1px solid #ffffff26; }
.commission-band .overline { font-size: 0.7rem; font-weight: 500; color: #dad7d8; }
.hero-number { font-family: Inter, Arial, sans-serif; font-size: 2.4rem; font-weight: 650; letter-spacing: -0.045em; margin: 8px 0; font-variant-numeric: tabular-nums; }
.commission-total .subtle { font-size: 0.7rem; color: #b7b3b5; }
.commission-part { display: flex; align-items: center; gap: 12px; }
.rate-badge { width: 42px; height: 46px; background: #ffffff12; color: #ffb2ae; border-radius: 8px; display: flex; align-items: center; justify-content: center; font-size: 1.3rem; font-weight: 500; flex-shrink: 0; }
.rate-badge span { font-size: 0.7rem; margin-left: 2px; }
.rate-badge.secondary { background: #ffffff0d; color: #d4d0d3; }
.commission-part .subtle { font-size: 0.7rem; color: #d9d5d6; display: block; }
.commission-part strong { display: block; font-family: Inter, Arial, sans-serif; font-size: 1.3rem; font-weight: 600; color: #fff; margin-top: 2px; font-variant-numeric: tabular-nums; }
.commission-part .formula { font-size: 0.65rem; color: #ada8ab; display: block; margin-top: 4px; }
.commission-band .badge { margin-left: auto; }

.closing-card { background: #fff9f8; border: 1px solid #e8d1d1; border-top: 3px solid var(--brand-red-dark); border-radius: 18px 0 18px 0; padding: 22px; display: grid; gap: 6px; }
.closing-eyebrow { font-size: 0.65rem; font-weight: 600; letter-spacing: 0.08em; color: #a32a2a; text-transform: uppercase; }
.closing-card h2 { color: #910606; font-size: 1.05rem; }
.closing-amount { font-family: Inter, Arial, sans-serif; font-size: 1.5rem; font-weight: 650; color: #910606; margin: 4px 0; }
.closing-card .badge { justify-self: start; }

@media (max-width: 600px) {
  .commission-band { flex-direction: column; align-items: flex-start; }
  .commission-total { border-right: 0; border-bottom: 1px solid #ffffff26; padding-right: 0; padding-bottom: 16px; width: 100%; }
}
```

- [ ] **Step 2: Reescrever `web/src/app/tenant-home.tsx`**

Substitua o arquivo inteiro por:

```tsx
import { apiFetch } from '@/lib/api';
import {
  currentCompetencia,
  formatCompetencia,
  formatMoney,
  formatPercent,
  isCompetencia,
  previousCompetencia,
  ruleLabel,
  statusBadgeClass,
  statusLabels,
} from '@/lib/format';
import type { Commissions, Me } from '@/lib/types';

export async function TenantHome({ competencia }: { competencia?: string }) {
  const me = await apiFetch<Me>('/me');
  const month = isCompetencia(competencia) ? competencia : currentCompetencia();
  const canSeeCommissions = me.permissions.some((p) => p.key === 'comissoes.visualizar');
  const commissions = canSeeCommissions ? await apiFetch<Commissions>(`/commissions/${month}`) : null;
  const previous = canSeeCommissions ? await apiFetch<Commissions>(`/commissions/${previousCompetencia(month)}`) : null;
  const own = commissions?.beneficiaries.find((b) => b.userId === me.id) ?? null;

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
        <>
          <section className="commission-band">
            <div className="commission-total">
              <span className="overline">Comissão apurada · {formatCompetencia(month)}</span>
              <div className="hero-number">{formatMoney(own ? own.total : commissions.total)}</div>
              <span className="subtle">Sobre pagamentos confirmados</span>
            </div>
            {own?.byRule.map((rule, index) => (
              <div className="commission-part" key={`${rule.ruleType}-${rule.level}-${rule.groupId}-${rule.rate}`}>
                <div className={index === 0 ? 'rate-badge' : 'rate-badge secondary'}>
                  {Math.round(rule.rate * 100)}
                  <span>%</span>
                </div>
                <div>
                  <span className="subtle">{ruleLabel(rule)}</span>
                  <strong>{formatMoney(rule.amount)}</strong>
                  <span className="formula">{formatMoney(rule.base)} · {formatPercent(rule.rate)}</span>
                </div>
              </div>
            ))}
            <span className={statusBadgeClass(commissions.status)}>{statusLabels[commissions.status] ?? commissions.status}</span>
          </section>

          <form className="inline" method="get">
            <label>
              Competência
              <input type="month" name="competencia" defaultValue={month} />
            </label>
            <button type="submit" className="secondary">Ver</button>
          </form>

          {previous && (previous.total > 0 || previous.beneficiaries.length > 0) && (
            <section className="closing-card">
              <p className="closing-eyebrow">Fechamento anterior</p>
              <h2>{formatCompetencia(previousCompetencia(month))}</h2>
              <p className="closing-amount">{formatMoney(previous.total)}</p>
              <span className={statusBadgeClass(previous.status)}>{statusLabels[previous.status] ?? previous.status}</span>
            </section>
          )}

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
        </>
      ) : (
        <section className="card">
          <p className="muted">Seu perfil não inclui acesso às comissões.</p>
        </section>
      )}
    </main>
  );
}
```

- [ ] **Step 3: Verificar tipos, lint e build**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: sem erros.

- [ ] **Step 4: Rodar os testes unitários (nenhum deveria ter sido afetado)**

Run: `cd web && npm test`
Expected: PASS — mesma contagem de testes de antes (esta task não adiciona nem remove testes; `tenant-home.tsx` não é testado por Vitest, é um Server Component verificado manualmente e pelo E2E).

- [ ] **Step 5: Atualizar os seletores do E2E e verificar o valor real da faixa de comissão**

`web/e2e/fundacao.spec.ts` tem duas asserções em `.locator('.total')` (linhas ~30 e ~41) que quebram porque esta task remove a classe `.total` de `tenant-home.tsx` (o total agora está em `.hero-number`, dentro da faixa de comissão). Troque as duas ocorrências de `page.locator('.total')` para `page.locator('.hero-number')`.

**Atenção com o valor esperado para coordenação:** a faixa de comissão mostra `own.total` (o total PESSOAL de quem está logado) quando essa pessoa aparece como beneficiária, não `commissions.total` (que pode incluir subordinados visíveis para um coordenador). O teste antigo esperava `R$ 3.100,00` para `coordenacao@aprovec.local` — esse valor pode ter representado o agregado de toda a equipe, não o corte pessoal dela. Antes de rodar o E2E, suba a API e o `web` localmente, faça login como `coordenacao@aprovec.local` em `http://aprovec.localhost:3000/?competencia=2026-09`, e leia o valor real exibido em `.hero-number`. Se for igual a `R$ 3.100,00`, nada muda. Se for diferente, atualize a expressão regular da segunda asserção (linha ~41) para o valor real observado — isso é o comportamento correto (o total pessoal dela, não o da equipe), não um bug a corrigir de outra forma.

- [ ] **Step 6: Rodar o E2E**

```bash
cd web && npm run e2e
```

Expected: os 5 cenários em `web/e2e/fundacao.spec.ts` passam, incluindo os dois ajustados no Step 5. Se qualquer outro seletor quebrar por causa das mudanças desta task, ajuste-o da mesma forma (identifique a causa real antes de mudar o valor esperado).

- [ ] **Step 7: Verificar manualmente contra o mockup**

Com `joao@aprovec.local` / `senha-dev-123` em `http://aprovec.localhost:3000/`, confirme: faixa preta com o total em destaque (`R$ 1.600,00`), dois chips — "Carteira própria" (7%, `R$ 1.400,00`) e "Supervisão · 1º nível" (2%, `R$ 200,00`) —, badge de situação "Em apuração" à direita da faixa, e a tabela detalhada abaixo continua mostrando as mesmas linhas. Compare a faixa lado a lado com `commissionBand()` em `mockup/app.js` (abra o mockup com `python3 -m http.server` em `mockup/` ou similar).

- [ ] **Step 8: Commit**

```bash
git add web/src/app/tenant-home.tsx web/src/app/globals.css web/e2e/fundacao.spec.ts
git commit -m "feat(web): add commission hero band and previous closing teaser to tenant home"
```

---

### Task 4: Verificação visual final contra o mockup

**Files:**
- Nenhum arquivo de código — esta task só produz um registro de verificação (pode ser o corpo do relatório do executor; não precisa de arquivo novo no repositório).

**Interfaces:**
- Consumes: todas as páginas tocadas nas Tasks 1–3.
- Produces: nada para tasks futuras (esta é a última task da Fase 1).

- [ ] **Step 1: Preparar o ambiente**

Com o banco migrado e semeado (ver README), rode a API e o `npm run dev` do `web/`, ou suba tudo via `docker compose up -d --build` (ver README, seção Containers).

- [ ] **Step 2: Percorrer cada página afetada e comparar com o mockup**

Abra cada uma das páginas a seguir num navegador real (ou via ferramenta de browser automatizado) e compare com a página/trecho correspondente do mockup (`mockup/index.html`, aberto localmente):

1. `http://aprovec.localhost:3000/login` — comparar com o padrão de cartão/botão do mockup (não há tela de login no mockup; confirmar que usa os mesmos tokens: fundo `#f7f7f7`, cartão branco com raio assimétrico, botão preto/vermelho).
2. `http://aprovec.localhost:3000/redefinir-senha` — mesmo padrão visual do login.
3. Solicitar redefinição para `maria@aprovec.local`, abrir o link em `tmp/emails/`, chegar em `/definir-senha/<token>` — mesmo padrão visual.
4. `http://aprovec.localhost:3000/` logado como `joao@aprovec.local` — comparar a faixa de comissão com `commissionBand()` do mockup (cores, proporções, badge de situação).
5. Uma URL inexistente, ex. `http://aprovec.localhost:3000/qualquer-coisa` — página 404 com o mesmo padrão visual de cartão.

- [ ] **Step 3: Checar os tokens fundamentais em cada página**

Para cada página do Step 2, confirme visualmente (ou via DevTools):
- Cor de fundo da página: `#f7f7f7`.
- Fonte de títulos (`h1`/`h2`) é Inter; fonte de rótulos/botões é Poppins.
- Botões primários são pretos (`#0c0c0e`) e ficam vermelho-escuros (`#ab090a`) no hover.
- Nenhum elemento usa azul (o tema antigo) — se algo azul aparecer, é sinal de CSS não migrado; investigar e corrigir antes de fechar esta task.

- [ ] **Step 4: Confirmar que nenhum modo escuro automático aparece**

Com o navegador em modo escuro do sistema, recarregue `http://aprovec.localhost:3000/login`. Esperado: a página continua no tema claro (fundo `#f7f7f7`), sem inverter cores — confirma que o bloco `@media (prefers-color-scheme: dark)` foi removido corretamente na Task 1.

- [ ] **Step 5: Relatar o resultado**

Sem commit nesta task (nenhum arquivo de código muda). Se o Step 2, 3 ou 4 encontrar uma divergência visual, corrija-a como parte desta mesma task (pode tocar `web/src/app/globals.css` e os arquivos `.tsx` das Tasks 1–3), rode novamente `npm run typecheck && npm run lint && npm run build`, e comite a correção com:

```bash
git add web/src/app/globals.css
git commit -m "fix(web): correct visual regressions found in the mockup parity check"
```

Se nada precisar de correção, registre no relatório final que a Fase 1 está visualmente fiel ao mockup aprovado dentro do escopo definido na spec.
