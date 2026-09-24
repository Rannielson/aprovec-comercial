# Minha carteira (Fase 2b) — Design

**Specs anteriores relacionadas:**
- `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md` (arquitetura geral do SaaS)
- `docs/superpowers/specs/2026-09-24-mockup-visual-port-design.md` (Fase 1)
- `docs/superpowers/specs/2026-09-24-app-shell-design.md` (Fase 2a — casca fixa)

## Contexto e objetivo

O mockup (`mockup/app.js`, função `wallet()`) mostra a lista de boletos do vendedor, boleto a boleto: abas de filtro por situação, busca por nome/placa, tabela paginada, resumo de resultados. Esta fase constrói essa tela de verdade — endpoint novo na API (não existe hoje; só há consultas internas usadas pelo motor de comissão) e a página `/carteira`, ligada ao item de navegação "Minha carteira" (hoje desabilitado na sidebar).

**Decisão registrada nesta sessão:** os boletos hoje só existem via dados de desenvolvimento (`DevSeed.cs`); de onde eles vêm em produção (integração com um ERP externo) é uma decisão em aberto, deliberadamente adiada. Esta fase constrói sobre os dados como eles já existem na tabela `boletos`, sem assumir nada sobre a origem futura.

## Descobertas que moldam o design

- A permissão `carteira.visualizar` já existe no catálogo RBAC desde o Plano 1 (`0006_rbac_catalog.sql`), com escopo `own` para `consultor` e `tenant` para `coordenador` — nunca foi usada até agora.
- O escopo `tenant` **não** é tratado pela função genérica `app.visible_owner_ids(scope)` (que só resolve `own`/`direct`/`subtree`) — o endpoint de comissões contorna isso com uma função dedicada, `app.commission_beneficiaries()`, que trata `tenant` como "todos os usuários do tenant" antes de cair no caso genérico. Esta fase segue o mesmo padrão: uma nova função `app.carteira_owner_ids()`.
- A tabela `boletos` real (`0010_boletos_fechamentos.sql`) tem `associado_nome`, `placa`, `valor`, `status` (`a_vencer`/`recebido`/`atraso`/`cancelado`), `vencimento`, `pago_em` — **não tem** modelo de veículo nem telefone (eram decorativos no mockup, não têm campo real). A coluna "Associado / veículo" do mockup vira apenas "Associado" com a placa.
- `pago_em` só existe quando `status = 'recebido'` (restrição no banco). "Sua comissão" por boleto só é maior que zero nesse caso; para os outros status, é sempre R$ 0,00 — igual ao mockup.
- A taxa usada para calcular "sua comissão" é a da regra tipo `own` do plano de comissão vigente no mês de `pago_em` (mesma lógica de seleção de plano já usada pelo motor de comissão, `app.commission_plan_for`) — não precisa de nenhuma mudança no motor, só uma consulta nova.
- O botão de "ver detalhes" por linha (abre um drawer no mockup) **não é construído nesta fase** — sem componente de drawer ainda, mesma exclusão já aplicada nas fases anteriores.
- Sem filtragem/busca instantânea via JavaScript como no mockup — segue o padrão já estabelecido no BFF (Server Components + formulários/links `GET`, como o seletor de competência já usa). Trocar de aba, buscar ou paginar recarrega a página com os parâmetros na URL.
- Os quatro status de boleto mapeiam diretamente nas variantes de `.badge` que já existem (`recebido`→verde já existe, `atraso`→`.badge.warning` já existe, `a_vencer`→`.badge.neutral` já existe) — só falta **uma** variante nova, `.badge.cancelado`, reaproveitando os tokens `--status-red`/`--status-red-bg` que já existem em `globals.css` mas nunca foram usados como badge.

## Mapeamento de componentes

| Mockup | BFF real | Adaptação |
|---|---|---|
| `wallet()` — abas de filtro + busca + tabela + paginação + nota | `web/src/app/carteira/page.tsx` novo | Filtros/busca/paginação via query string (`?status=&query=&page=`), não JS live. |
| `table()` (linhas de boleto: avatar, nome, placa, vencimento, valor, situação, comissão) | Tabela nova dentro de `carteira/page.tsx` | Sem coluna de veículo/telefone (não existem); sem botão de detalhe (sem drawer). |
| `.filter-tabs` (Todos/Recebidos/Atraso/Cancelados, com contagem) | Links que trocam `?status=` | Contagens vêm do endpoint (por status, dentro do escopo visível do usuário). |
| `.search-field` | Formulário `GET` com campo de busca (nome ou placa) | Busca ao enviar o formulário, não a cada tecla. |
| `.pagination` | Links `?page=N-1`/`?page=N+1` | 8 itens por página, igual ao mockup. |
| `.results-summary` (contagem + valor total + comissão apurada) | Mesmo resumo, calculado pelo endpoint sobre o filtro atual | — |
| `.wallet-note` | Nota estática igual ao mockup | — |
| Botão "ver detalhes" por linha (abre drawer) | — | **Não construído.** Sem componente de drawer. |
| Item "Minha carteira" na sidebar | `web/src/app/app-shell.tsx` — `href: null` → `href: '/carteira'` | Deixa de mostrar "Em breve". |

## Backend

**Novo:** `src/Recorrencia.Db/Scripts/0013_carteira.sql` — função `app.carteira_owner_ids()` (mesmo padrão de `commission_beneficiaries()`: resolve `app.scope_for('carteira.visualizar')`, trata `tenant` como todos os usuários do tenant, senão cai em `app.visible_owner_ids`).

**Novo:** `src/Recorrencia.Api/Carteira/CarteiraEndpoints.cs` — `GET /carteira` (`.RequirePermission("carteira.visualizar")`), parâmetros de query `status`, `query`, `page`, `pageSize` (padrão 8). Retorna: os itens da página atual (nome, placa, valor, situação, vencimento, data de pagamento, dias em atraso quando aplicável, comissão), a contagem por status (para as abas), e o total de valor/comissão do filtro atual (para o resumo). O cálculo de "sua comissão" por boleto usa a taxa `own` do plano vigente no mês de `pago_em`; para boletos sem `pago_em`, a comissão é sempre 0.

## Frontend

**Novo:** `web/src/app/carteira/page.tsx` — Server Component, mesmo padrão de guarda de host que `login/`/`redefinir-senha/` (404 fora do tenant), verifica `carteira.visualizar` em `me.permissions` (mesmo padrão de `canSeeCommissions` em `tenant-home.tsx`; sem a permissão, mostra o mesmo cartão "Seu perfil não inclui acesso"). Renderizado dentro de `<AppShell active="wallet">`.

**Novo (CSS):** tokens/classes do mockup para `.wallet-toolbar`/`.filter-tabs`/`.filter-tab`/`.search-field`/`.results-summary`/`.pagination`/`.wallet-note`/`.empty-state`/`.associate-button`/`.row-avatar`/`.plate`/`.amount`(+variantes)/`.late-days`, mais a variante nova `.badge.cancelado`.

**Novo (ícones):** `search`, `back`, `chevron` adicionados a `web/src/app/components/app-icon.tsx` (mesmos paths do mockup, mesmo padrão da Fase 2a).

**Novo (formatação):** `formatDate(value: string): string` em `web/src/lib/format.ts` (`'2026-09-05'` → `'05/09/2026'`), seguindo o mesmo padrão de `Date.UTC` já usado por `formatCompetencia` para evitar problemas de fuso horário.

**Modificado:** `web/src/app/app-shell.tsx` — item "Minha carteira" ganha `href: '/carteira'`.

**Não afetados:** `login/`, `definir-senha/`, `redefinir-senha/`, `not-found.tsx`, `error.tsx`, `platform-*`, `tenant-home.tsx` (não muda nada — a faixa de comissão e a tabela detalhada por regra continuam como estão).

## Testes

- Novos testes de banco (`tests/Recorrencia.Db.Tests`) para `app.carteira_owner_ids()`: escopo `own` retorna só o próprio usuário; escopo `tenant` retorna todos; usuário sem a permissão retorna vazio.
- Novos testes de API (`tests/Recorrencia.Api.Tests`) para `GET /carteira`: filtro por status, busca por nome/placa, paginação, contagens e totais corretos, comissão por boleto calculada corretamente (incluindo o caso de boleto não recebido → comissão zero), 403 sem a permissão.
- Novo teste unitário para `formatDate` em `web/src/lib/format.test.ts`.
- `npm run typecheck && npm run lint && npm run build` limpos.
- Verificação manual/E2E: entrar como `joao@aprovec.local`, abrir "Minha carteira" pela sidebar (deixa de estar desabilitado), confirmar abas/busca/paginação/resumo contra os dados semeados.

## Riscos e decisões (resolvidos nesta spec)

1. A origem real dos dados de boletos (integração com ERP) fica deliberadamente fora de escopo — decisão já registrada com o usuário nesta sessão.
2. A ordenação padrão da lista (sem especificação no mockup) fica definida como `coalesce(pago_em, vencimento) desc` (atividade mais recente primeiro) — uma escolha razoável, não uma exigência do mockup.
3. O botão de detalhe por linha e a busca/filtragem instantânea via JS são deliberadamente não replicados — mesmas exclusões já aplicadas nas fases anteriores (sem drawer; sem interatividade client-side além dos formulários já existentes).
