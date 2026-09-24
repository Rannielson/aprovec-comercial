# Casca fixa (sidebar/topbar) do BFF (Fase 2a) — Design

**Specs anteriores relacionadas:**
- `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md` (arquitetura geral do SaaS)
- `docs/superpowers/specs/2026-09-24-mockup-visual-port-design.md` (Fase 1 — reskin puro, já mesclada)

## Contexto e objetivo

O mockup (`mockup/`) é uma SPA com uma casca fixa — sidebar de navegação + topbar — em torno de qualquer página. O BFF real hoje (pós Fase 1) não tem essa casca: `tenant-home.tsx` renderiza seu próprio cabeçalho solto (`Olá, {nome}` + botão Sair) dentro de `<main className="shell">`.

Esta fase constrói a casca fixa fiel ao mockup (`mockup/app.js:197`, combinada com `styles.css`/`brand.css`) e a aplica à única página autenticada de tenant que existe hoje (`/`). A navegação já prevê os itens para "Minha carteira" e "Fechamento" (fases futuras, ainda não implementadas) marcados como indisponíveis. Zero endpoint novo, zero mudança de autenticação/sessão.

## Descobertas que restringem o escopo

O mockup é uma demo sem autenticação real: parte da sua casca não tem equivalente com dado real e não deve ser copiada literalmente.

| Elemento do mockup | Decisão |
|---|---|
| `<select id="profile-select">` "Explorar como" (troca de papel livremente) | **Removido.** No app real, quem você é vem de `/me` (RBAC real), não de uma escolha na tela — replicar isso seria uma UI de troca de privilégio sem controle algum. |
| `<span class="demo-badge">Dados demonstrativos</span>` | **Removido.** Os dados são reais. |
| Botão `data-action="rules"` "Regras do demonstrativo" (abre um drawer) | **Removido por ora.** Não existe componente de drawer construído ainda; é um elemento decorativo sem função sem ele. |
| `.seller small` (rótulo de papel: "Consultor comercial"/"Gestora da base"/...) | **Removido.** RBAC modular real permite múltiplos papéis simultâneos por usuário — não há um único "papel" para rotular. Fica só nome + avatar + indicador de sessão ativa (`.seller-dot`). |
| 4 itens de navegação (Visão geral, Minha carteira, Comissões, Fechamento) | **3 itens.** "Visão geral" e "Comissões" do mockup já são uma página só hoje (`/`, decisão já tomada na Fase 1) — vira "Minha recorrência" (ativo). "Minha carteira" e "Fechamento" continuam como itens de navegação, mas **desabilitados** ("Em breve") até suas próprias fases. |
| `<span class="nav-count" id="closing-count">1</span>` no item Fechamento | **Removido.** É uma contagem de pendência simulada; sem página de Fechamento real, não há dado para contar. |
| Botão de logout | **Não existe no mockup** (é uma demo sem sessão). O app real precisa de um — adicionado junto ao bloco do vendedor na sidebar, reaproveitando a Server Action de logout já existente (`POST /logout`). |
| `mockup/assets/logo-aprovec.webp` | Já copiado para `web/public/logo-aprovec.webp` na Fase 1, mas nunca referenciado — **finalmente usado** aqui. |
| Ícones (`icon()` em `mockup/app.js:16-42`) | Os paths SVG exatos (`grid`, `wallet`, `calendar`, `logout`) são portados para um componente `Icon` local — só os 4 usados nesta fase, não o conjunto inteiro do mockup. |
| Breakpoints responsivos do mockup (`styles.css` tem 5 tiers: 1600/1250/1050/760/540/360px) | Simplificado para **2 estados**: desktop (sidebar fixa lateral) e `≤760px` (sidebar vira uma barra horizontal no topo, replicando exatamente o comportamento de `styles.css` nesse breakpoint). Os tiers intermediários (1250/1050px) do mockup são otimizações finas de espaçamento que não afetam a estrutura — omitidos nesta fase. |

## Tokens de design (extraídos de `mockup/styles.css` + `mockup/brand.css`, camada combinada)

A sidebar usa fundo **vermelho-escuro** (`--brand-red-dark`, `#ab090a`) — não preto — com o item ativo em preto (`--brand-black`) e canto assimétrico, seguindo o mesmo motivo de canto já usado em `.card`/`.commission-band`/botões:

```css
/* sidebar */
--sidebar-bg: var(--brand-red-dark);      /* #ab090a — brand.css sobrescreve o --navy padrão do styles.css */
--sidebar-text: #fff0ee;
--sidebar-text-muted: #f4cdca;
--sidebar-hover-bg: #940000;
--sidebar-active-bg: var(--brand-black);  /* #0c0c0e */
--sidebar-border: #ffffff36;

/* topbar/breadcrumb */
--breadcrumb-text: #333338;
--breadcrumb-sep: #b7b2b2;
```

Larguras/medidas: sidebar `224px` fixa (desktop), `.nav-item` altura mínima `47px`, raio `7px` (inativo) / `14px 0 14px 0` (ativo — asimétrico), avatar `38px` circular, topbar altura `68px`.

## Mapeamento de componentes

| Mockup | BFF real | Adaptação |
|---|---|---|
| `<aside class="sidebar">` (logo + nav + sidebar-note + seller) | `web/src/app/app-shell.tsx` novo — `<AppShell active={...}>` | Estrutura idêntica; conteúdo dinâmico (nome real, item ativo real) em vez de estado de demo. |
| `<nav>` com 4 `.nav-item` | Array fixo de 3 itens (`overview`/`wallet`/`closing`) | 2 dos 3 (`wallet`/`closing`) renderizam como `<span>` desabilitado com badge "Em breve", não `<Link>` — não há página para navegar ainda. |
| `.seller` (avatar + nome + papel + dot) | Mesmo bloco, sem o rótulo de papel | Avatar = iniciais de `me.name` (mesma lógica já usada no mockup: primeiras letras das 2 primeiras palavras). |
| Logout | Não existe no mockup | Novo pequeno botão/form "Sair" junto ao bloco do vendedor, reaproveitando `POST /logout` já existente. |
| `<header class="topbar">` (breadcrumb + seletor de papel + demo-badge) | Só o breadcrumb ("Comercial / {título da página ativa}") | Seletor de papel e demo-badge removidos (ver tabela de descobertas). |
| `<main id="main-content">` | `<main>` do `AppShell`, envolvendo `{children}` | `tenant-home.tsx` para de renderizar seu próprio `<main className="shell">` e passa a ser só o conteúdo (faixa de comissão, card de fechamento, tabela) dentro de `<div className="shell">`, que por sua vez fica dentro do `<main>` do `AppShell`. |
| `heading("Minha recorrência", "Acompanhe sua carteira e a origem de cada comissão.")` (`app.js:199`) | Novo cabeçalho de página em `tenant-home.tsx`, substituindo o antigo `<h1>Olá, {nome}</h1>` | **Mudança de conteúdo, não só de posição:** o cumprimento pessoal ("Olá, {nome}") deixa de existir como título de página — o nome já aparece de forma persistente no bloco do vendedor na sidebar. O título da página passa a ser "Minha recorrência" (fiel ao mockup), com o eyebrow "PAINEL DO VENDEDOR" e o subtítulo do mockup. **Isso muda o texto que o E2E verifica** — ver seção Testes. |
| `<footer class="app-footer">` (marca + botão "Regras") | Só a linha de marca | Botão "Regras do demonstrativo" removido (sem drawer). |
| Ícones `grid`/`wallet`/`calendar`/`logout` | `web/src/app/icon.tsx` novo | Paths SVG copiados literalmente de `mockup/app.js:16-42` (mesmo `viewBox`, `stroke-width`, etc.) — só os 4 usados. |

## Arquivos afetados

- Create: `web/src/app/icon.tsx` — componente `Icon({ name, className })`.
- Create: `web/src/app/app-shell.tsx` — componente async `AppShell({ active, children })`, busca `/me`.
- Modify: `web/src/app/tenant-home.tsx` — remove o `<header>` próprio; envolve o conteúdo em `<AppShell active="overview">`; troca `<main className="shell">` por `<div className="shell">`.
- Modify: `web/src/app/globals.css` — adiciona as regras de `.sidebar`/`.brand`/`.nav-item`/`.seller`/`.topbar`/`.breadcrumb`/`.app-footer` (desktop + breakpoint `≤760px`).

**Não afetados:** `login/`, `definir-senha/`, `redefinir-senha/`, `not-found.tsx`, `error.tsx` (continuam no layout `.auth`/`.card`, sem casca — o mockup também não os teria dentro da casca, já que não existem nele). `platform-*` (fora de escopo, mockup não modela administração de plataforma). Nenhum arquivo em `src/Recorrencia.Api`/`src/Recorrencia.Db`.

## Testes

- `npm run typecheck && npm run lint && npm run build` devem continuar limpos.
- `web/e2e/fundacao.spec.ts` verifica hoje `getByRole('heading', { name: 'Olá, João Silva' })` — esse heading não existe mais (ver tabela de mapeamento). A asserção precisa mudar para o novo título de página ("Minha recorrência") ou para um elemento do bloco do vendedor na sidebar (ex. `getByText('João Silva')` dentro da sidebar) — decidir qual ao implementar, mas a mudança é obrigatória, não opcional. O botão "Sair" continua com o mesmo texto/papel (`getByRole('button', { name: 'Sair' })`), só muda de posição na tela — esse seletor não precisa mudar.
- Verificação visual manual contra `mockup/app.js:197` (a casca), especialmente o fundo vermelho-escuro da sidebar (não preto) e o raio assimétrico do item ativo.

## Riscos e decisões (resolvidos nesta spec)

1. O rótulo de papel do vendedor não tem equivalente no RBAC modular real — resolvido removendo-o (ver tabela de descobertas), não inventando um substituto.
2. Itens de navegação para páginas que não existem ainda (`wallet`/`closing`) são elementos não-clicáveis com indicação visual de "Em breve", não links quebrados nem itens ocultos — mantém a promessa visual do mockup (3 itens sempre visíveis) sem fingir que a navegação funciona.
