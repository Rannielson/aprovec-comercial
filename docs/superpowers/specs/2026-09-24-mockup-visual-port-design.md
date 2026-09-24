# Port do mockup aprovado para o BFF (Fase 1 — vendedor) — Design

**Spec anterior relacionada:** `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md` (arquitetura geral do SaaS — este documento não a altera).

## Contexto e objetivo

O mockup estático em `mockup/` (`index.html` + `app.js` + `brand.css`/`styles.css`) é a identidade visual aprovada da APROVEC para o produto: um dashboard com navegação por papel (vendedor/gestor/admin), visualização de árvore hierárquica, tabelas de boletos, drawers e um fluxo de fechamento em etapas. O BFF real (`web/src/app/**`), construído no Plano 4, hoje é funcional mas usa um tema genérico (`web/src/app/globals.css`) sem relação com essa identidade.

Esta fase porta a identidade visual do mockup — cores, tipografia, componentes de cartão/botão/tabela — para as páginas do BFF que **já existem e já funcionam com dados reais**: login, definir senha, redefinir senha, e a página inicial da empresa (hoje `tenant-home.tsx`). Nenhuma rota, Server Action, sessão ou chamada à API muda. Nenhum endpoint novo é criado.

Fases seguintes (fora deste documento): "Minha carteira" (lista de boletos, precisa de endpoint novo), fluxo de fechamento completo, visões de gestor/admin, árvore hierárquica, casca fixa (sidebar/topbar), theming por tenant.

## Descobertas que restringem o escopo

- O mockup **não tem tela de login** — ele assume sessão já estabelecida. Login/definir-senha/redefinir-senha não têm uma tela-modelo exata; a identidade é estendida a partir dos componentes de botão/campo/cartão já definidos em `brand.css`/`styles.css`.
- A tela "Minha recorrência" do mockup (`overview()` em `app.js`) inclui "Saúde da minha carteira" (contagens por status) e "Últimos recebimentos" (tabela boleto a boleto) — ambos exigem dados por boleto que a API não expõe hoje (só `/commissions/{competencia}`, agregado). Esses dois blocos **ficam fora desta fase**.
- O estilo final do mockup é a combinação de `styles.css` (estrutura, tema azul por padrão) **sobreposto por** `brand.css` (identidade APROVEC vermelho/preto). As duas folhas precisam ser lidas juntas — `brand.css` isolado não é o visual final.
- O mockup assume exatamente 2 "partes" de comissão (própria + 1 indicação direta). O motor de comissão real suporta N regras/níveis por tenant (`byRule[]`). A faixa de comissão precisa ser generalizada para renderizar um "chip" por regra retornada pela API, não 2 chips fixos.
- A visão atual já suporta múltiplos beneficiários na mesma competência (ex.: um coordenador vendo a própria linha + a de subordinados). O mockup só modela uma pessoa. A tabela detalhada multi-beneficiário que já existe é mantida (com os novos tokens visuais), abaixo da faixa de comissão — que passa a mostrar apenas os totais do usuário logado.

## Tokens de design (extraídos de `mockup/brand.css` + `mockup/styles.css`)

```css
--brand-red: #da0000;
--brand-red-dark: #ab090a;   /* já usado como --accent hoje */
--brand-black: #0c0c0e;      /* já usado como --accent-strong hoje */
--paper: #f7f7f7;
--surface: #ffffff;
--ink: #202023;
--muted: #6c6c73;
--line: #e7e5e5;
--radius: 12px;
--green: #17673a; --green-bg: #ebf5e8;
--amber: #8c6118; --amber-bg: #fff5df;
--red-status: #a33e46; --red-status-bg: #fceded;
```

Tipografia: Poppins (400/500/600, self-hosted, UI/labels/botões) + Inter (números, títulos — `h1`, valores monetários, `.hero-number`). Os 4 arquivos `.woff2` em `mockup/assets/fonts/` são copiados para `web/public/fonts/` e declarados via `@font-face` em `globals.css` (ou `next/font/local`, equivalente).

**Removido:** o bloco `@media (prefers-color-scheme: dark)` do `globals.css` atual. O modelo aprovado é um tema único claro; um modo escuro automático não faz parte dele.

## Mapeamento de componentes

| Mockup (`app.js`/`brand.css`/`styles.css`) | BFF real | Adaptação |
|---|---|---|
| `.commission-band` / `.commission-part` / `.rate-badge` / `.hero-number` | Topo de `tenant-home.tsx` | Generalizado: 1 chip por item de `commissions.beneficiaries[meId].byRule[]` (rótulo via `ruleLabel` já existente), não fixo em 2. Total via `commissions.total` já existente. |
| `.right-column` → `.closing-card` | Novo bloco em `tenant-home.tsx` | **Viável, implementar.** Sem simulação de fechamento (checkbox/NF/provisionamento — isso é da fase de fechamento completo, fora do escopo). É um teaser estático: chama `/commissions/{competência anterior}` (mesmo endpoint, mês anterior — já existe, não precisa de endpoint novo) e mostra o total + `status` daquela competência com o badge já existente. Se a competência anterior não tiver nenhuma comissão (`total === 0` e sem beneficiários), omitir o card. |
| `.referral-card` / `.referral-person` / `.referral-bottom` | — | **Não implementar nesta fase.** O mockup mostra o detalhamento pessoa a pessoa de quem o vendedor indicou; a API agrega por tipo de regra (`byRule[]`), não por pessoa de origem — não há como popular a lista de pessoas sem um endpoint novo. O valor agregado da regra `upline`/nível 1 (quando existir) já aparece na tabela detalhada mantida abaixo; nenhuma informação é perdida, só o cartão dedicado com nomes fica para uma fase futura. |
| Tabela detalhada por pessoa/regra (já existe) | `tenant-home.tsx` | Mantida como está funcionalmente; reestilizada com os tokens de tabela do mockup (`th`/`td`/`.amount`/`.status`). |
| `.page-heading` / `.eyebrow` / `.period` | Cabeçalho de `tenant-home.tsx` | Reestilizado; o seletor de competência (`<input type="month">`) continua funcional, só ganha o visual de `.period-value`. |
| `.primary-button` / `.secondary-button` | Botões em toda a app | Substituem as regras atuais de `button`/`button.secondary` em `globals.css`. |
| Nenhum equivalente no mockup | `login/`, `definir-senha/`, `redefinir-senha/` | Reestilizados com os mesmos tokens/componentes (cartão, campo, botão), sem tela-modelo exata para copiar 1:1. |
| `.status` (recebido/atraso/cancelado/progress/neutral) | `statusLabels`/badge em `tenant-home.tsx` | Reaproveita as classes de cor do mockup para os badges de situação do fechamento. |

**Fora desta fase (não implementar agora):** `.sidebar`/`.topbar`/`.nav-item` (casca fixa), `.health-grid`, `.wallet-*`, `.tree`/`.tree-edge`, `.drawer`/`.overlay`, `.toast`, `.stepper`/`.closing-layout`/`.statement-*` (fluxo de fechamento completo), `.commission-tabs`/`.source-summary` (abas de origem).

## Arquivos afetados

- `web/public/fonts/*.woff2` (novos, copiados do mockup)
- `web/public/logo-aprovec.webp` (novo, copiado do mockup)
- `web/src/app/globals.css` (reescrito: tokens, tipografia, botões, cartões, tabela; remove dark-mode)
- `web/src/app/login/page.tsx`, `login-form.tsx` (markup ajustado aos novos tokens/classes; nenhuma mudança de lógica)
- `web/src/app/definir-senha/[token]/page.tsx`, `set-password-form.tsx` (idem)
- `web/src/app/redefinir-senha/page.tsx`, `reset-form.tsx` (idem)
- `web/src/app/tenant-home.tsx` (reestruturado: faixa de comissão dinâmica + cartões condicionais + tabela reestilizada)
- `web/src/app/not-found.tsx`, `error.tsx` (reestilizados para consistência visual, sem mudança de lógica)

**Não afetados:** `web/src/lib/**` (nenhuma mudança de dados/sessão/auth), `web/src/app/platform-*` (fora de escopo — visão de plataforma não faz parte do mockup do vendedor), qualquer arquivo em `src/Recorrencia.Api`/`src/Recorrencia.Db` (zero mudança de backend).

## Testes

- `web/src/lib/format.test.ts` etc. — inalterados, continuam passando (nenhuma lógica de `lib/` muda).
- Verificação visual manual (Playwright ou browser real) das páginas afetadas após a mudança, comparando com o mockup lado a lado nos pontos definidos na tabela de mapeamento.
- `npm run typecheck && npm run lint && npm run build` devem continuar limpos.
- Não é necessário snapshot testing de CSS — fidelidade visual é verificada manualmente contra o mockup.

## Riscos e decisões (resolvidos nesta spec)

1. Confirmado via `src/Recorrencia.Api/Commissions/CommissionEndpoints.cs` e `web/src/lib/types.ts`: o card de fechamento do mês anterior é viável sem endpoint novo; o card de indicações diretas com nomes não é (ver tabela acima) e fica fora desta fase.
2. **Fontes self-hospedadas** aumentam o peso do bundle; aceitável para fidelidade visual, mas usar `font-display: swap` (já é o padrão do mockup) para não bloquear renderização.
