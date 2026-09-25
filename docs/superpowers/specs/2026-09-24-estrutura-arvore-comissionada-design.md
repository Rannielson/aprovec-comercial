# Estrutura — Árvore comissionada (Fase 4) — Design

**Specs anteriores relacionadas:**
- `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md` (arquitetura geral do SaaS — `hierarchy_paths`, motor de comissão)
- `docs/superpowers/specs/2026-09-24-app-shell-design.md` (Fase 2a — casca fixa)
- `docs/superpowers/specs/2026-09-24-hinova-integracao-design.md` (Fase 3 — vínculo voluntário↔usuário, mantido separado desta fase)

## Contexto e objetivo

Hoje a montagem da hierarquia (quem supervisiona quem) só existe via chamada direta à API — não há nenhuma tela. Esta fase constrói a área "Administração" da sidebar, com fidelidade visual ao mockup (`mockup/network-ui.js`, `mockup/tree-layout.js`): a tela **Árvore comissionada** (visual em pirâmide, com alternância para uma **Lista**) e a tela **Participantes** (tabela com busca e cadastro). Isso permite montar manualmente a primeira leva de vendedores — decisão já registrada nesta sessão: o crescimento orgânico via convite (pessoa se cadastra sozinha e entra vinculada a quem indicou) é uma fase futura, adiada; por ora a árvore é montada à mão pelo admin.

## Descobertas que simplificam o design

A investigação nesta sessão mostrou que **quase todo o backend necessário já existe**:

- `GET /users` (`estrutura.visualizar`) já retorna `id`, `name`, `email`, `status`, `supervisorId`, `roleIds` — falta só o tipo TypeScript no frontend.
- `POST /users` (`usuarios.convidar`) já cria um usuário podendo informar `supervisorId` na hora — mas exige `estrutura.editar` adicionalmente quando `supervisorId` é enviado (regra de segurança já implementada: posicionar alguém na hierarquia é tão sensível quanto conceder papel).
- `PUT /users/{id}/supervisor` (`estrutura.editar`) já move alguém na árvore, com trava contra ciclo (`hierarchy.cycle`) e recomputação automática de todo o subgrupo — via trigger em `hierarchy_paths`, não precisa de nada novo.
- **A profundidade da estrutura não tem limite** — só a comissão de 2% é sempre do vínculo direto (1 nível), já é assim no motor de comissão hoje. A árvore em si pode ter quantos níveis o admin quiser (confirmado contra o próprio mockup: em "Participantes", João → Maria → Pedro é uma cadeia de 3 níveis).
- Os números "recebidos"/"comissão" dos cartões do mockup batem exatamente com o que `GET /commissions/{competencia}` já calcula: o `Base` da regra `own` de uma pessoa é o que ela recebeu na própria carteira naquele mês; o `Base` da regra `global` é o recebido de toda a base (usado no cartão do coordenador/gestor).
- A página monta a árvore combinando, do lado do servidor (Server Component), a resposta de `GET /users` (estrutura) com a de `GET /commissions/{competencia}` (valores) — reaproveitando dois endpoints já testados, sem duplicar lógica de cálculo. O único endpoint que precisa mudar é `POST /users`, estendido para opcionalmente criar o vínculo Hinova na mesma transação (ver Backend e Decisão registrada abaixo).
- **A Hinova nem sempre tem e-mail cadastrado para o voluntário** — confirmado contra dados reais numa investigação paralela nesta mesma sessão (vários registros retornam `"email": ""`). O e-mail do novo usuário não pode vir da Hinova; precisa ser digitado pelo admin no momento da criação, senão o convite (link de definir senha) nunca chega a lugar nenhum.

## Decisão registrada nesta sessão

**Correção de uma decisão inicial desta mesma spec:** para esta primeira montagem manual, adicionar alguém à árvore **não é um formulário de nome/e-mail digitado à mão** — é a mesma busca já construída na Fase 3 (`Listar Voluntário`, campo de texto por nome, endpoint `GET /integracoes/hinova/voluntarios?query=`). O admin busca, encontra a pessoa na base da Hinova e, ao escolher o resultado, o sistema cria o usuário no APROVEC **e** o vínculo com o `codigo_voluntario` **na mesma ação**, já na posição certa da árvore (filho de quem clicou o "+", ou raiz nova). O nome vem da Hinova (editável); **o e-mail é sempre digitado pelo admin nesse momento**, já que a Hinova não garante esse dado — sem e-mail válido não há convite. Criar o participante e vincular à Hinova deixam de ser dois passos em duas telas — viram uma ação só, específica desta fase de montagem inicial. A tela de Configurações → Integrações (Fase 3) continua existindo, útil para vínculos avulsos fora do fluxo de montagem da árvore.

## Mapeamento de componentes

| Mockup | BFF real | Adaptação |
|---|---|---|
| Seletor "Explorar como" define o menu de Administração | Item de navegação "Administração" na sidebar, visível só para quem tem `estrutura.visualizar` — sem seletor de perfil (já removido desde a Fase 2a) | Sub-itens: **Árvore comissionada**, **Participantes**, **Remuneração** |
| `Árvore comissionada` — pirâmide + alternância Árvore/Lista, zoom, enquadrar, filtro "Todas as árvores"/"Árvore de X" | Página nova `web/src/app/administracao/arvore/page.tsx` | Visual portado de `mockup/tree-layout.js` (algoritmo de posicionamento) e `mockup/network-ui.js` (pirâmide/SVG das linhas), agora orientado por dados reais (`supervisorId`) em vez do modelo estático do mockup |
| Cartão de cada pessoa (nome, %, recebidos, comissão total) | Mesmo cartão, dados de `GET /users` + `GET /commissions/{competencia}` combinados | `%` vem do tipo de regra aplicável (own=7 para quem não tem papel de coordenador; a taxa exata vem do plano de comissão ativo, não fixa) |
| Botão "+" abaixo de cada pessoa (adicionar indicado) | Abre campo de busca por nome (reaproveita `GET /integracoes/hinova/voluntarios?query=`, Fase 3); ao escolher um resultado, chama `POST /users` (estendido, ver Backend) com `supervisorId` preenchido + dados do voluntário escolhido | Some quando a pessoa está `desligado`; resultados já vinculados (`jaVinculado`) aparecem desabilitados, mesmo tratamento da Fase 3 |
| Botão "+ Nova árvore" | Mesmo fluxo de busca, sem `supervisorId` | Cria uma nova raiz independente |
| `Participantes` — tabela (nome, perfil, supervisor), busca, contadores, "Novo participante" | Página nova `web/src/app/administracao/participantes/page.tsx` | Mesma fonte de dados (`GET /users`), sem o visual de pirâmide — filtro por texto feito no servidor (Server Component + querystring, mesmo padrão já usado em `carteira`); "Novo participante" abre o mesmo fluxo de busca por voluntário Hinova |
| `Remuneração` — taxas 7/2/1% | Item de navegação presente, mas desabilitado ("Em breve") | Edição de regras de comissão é tema à parte, fora desta fase |

## Backend

**Nenhuma migration nova.** Um endpoint existente precisa ser estendido; o resto a página só consome:
- `GET /users` (já existe) — lista de participantes com `supervisorId`.
- **`POST /users` (estendido)** — ganha três campos opcionais: `codigoVoluntario`, `nomeHinova`, `cpfHinova`. Quando vierem preenchidos, o handler, na mesma transação que já cria o usuário, insere também a linha em `hinova_voluntario_mapping` (mesma tabela e mesmas regras de unicidade da Fase 3 — 409 `hinova.vinculo_duplicado` se o código já estiver vinculado a outro usuário). Continua exigindo `estrutura.editar` quando `supervisorId` é enviado (regra já existente, inalterada) e passa a exigir também `integracoes.gerenciar` quando os campos de Hinova são enviados — criar o vínculo é tão sensível quanto os outros dois. `Name`/`Email` continuam obrigatórios como já são hoje (preenchidos no frontend a partir do resultado da busca, não digitados).
- `PUT /users/{id}/supervisor` (já existe) — mover participante entre árvores.
- `GET /integracoes/hinova/voluntarios?query=` (já existe, Fase 3) — busca por nome na base da Hinova, usada pelo campo de texto do "+"/"Nova árvore"/"Novo participante".
- `GET /commissions/{competencia}` (já existe) — valores de recebido/comissão por pessoa, para a competência selecionada (mesmo seletor de competência já usado em `tenant-home.tsx`).

## Frontend

**Novo:** `web/src/lib/types.ts` — tipo `UserNode` (`id`, `name`, `email`, `status`, `supervisorId`, `roleIds`) espelhando `UserRow` da API; reaproveita `RuleTotal`/`BeneficiaryCommission` já existentes para os valores.

**Novo:** `web/src/app/app-shell.tsx` — item "Administração" na sidebar (ícone novo), visível só com `estrutura.visualizar`, com os 3 sub-itens acima (o terceiro, "Remuneração", sempre desabilitado nesta fase — mesmo padrão visual já usado para "Fechamento").

**Novo:** `web/src/app/administracao/arvore/page.tsx` — Server Component: busca `GET /users` + `GET /commissions/{competencia}` (competência atual por padrão, com seletor), monta a árvore em memória a partir de `supervisorId`, renderiza o componente de pirâmide (client component, já que zoom/pan/clique são interativos) ou a lista, conforme o toggle Árvore/Lista (estado na querystring). Formulário de criar/mover participante como Server Actions (mesmo padrão de `web/src/app/configuracoes/integracoes/actions.ts`).

**Novo:** `web/src/app/administracao/participantes/page.tsx` — Server Component: tabela com busca por nome/e-mail (querystring), contadores (total de participantes/consultores/coordenadores), botão "Novo participante" abrindo o mesmo formulário da árvore.

**Novo (ícones):** os usados pelo mockup nesta área (`people`/`shield`/já existem alguns; confirmar quais faltam em `app-icon.tsx` ao implementar).

**Não afetado:** `Configurações → Integrações` (Fase 3, vínculo Hinova continua lá, sem mudança).

## Testes

- Novos testes de API para o `POST /users` estendido: criar com dados de Hinova grava o vínculo na mesma transação; código já vinculado a outro usuário devolve 409 `hinova.vinculo_duplicado` sem criar o usuário (nem o vínculo); enviar campos de Hinova sem `integracoes.gerenciar` devolve 403; caminho sem campos de Hinova continua idêntico ao comportamento atual (regressão).
- Sem migration nova — nenhum teste de banco novo esperado, além dos acima (que são de API).
- Testes de frontend: montagem da árvore a partir de uma lista plana de `supervisorId` (função pura, testável sem servidor) — casos: várias raízes, cadeia de 3+ níveis, participante desligado.
- E2E: admin cria uma raiz nova, adiciona um indicado direto, confirma que aparece na tabela de Participantes com o supervisor certo.

## Riscos e decisões (resolvidos nesta spec)

1. **Adicionar à árvore é buscar na Hinova, não digitar um formulário** — corrige a primeira versão desta spec (que tinha separado criação de participante e vínculo Hinova em duas telas). Para esta fase de montagem manual, as duas coisas acontecem juntas, numa ação só, via `POST /users` estendido. A tela de Configurações → Integrações (Fase 3) continua existindo para vínculos avulsos fora do fluxo de montagem.
2. **Sem limite de profundidade na árvore** — corrige uma suposição errada feita mais cedo nesta sessão; a comissão continua limitada a 1 nível (upline direto), sem mudança no motor.
3. **Remuneração fica "Em breve"** — edição de taxas de comissão é tema separado, fora do escopo desta fase.
4. **`POST /users` estendido em vez de endpoint novo** — evita duplicar a lógica de convite/e-mail/auditoria que o endpoint já tem; o custo é um handler um pouco maior, aceitável dado que os três campos novos são opcionais e não afetam o caminho já existente quando ausentes.
5. **Composição client-side de `/users` + `/commissions`** — risco: se isso se mostrar difícil de manter com muitos participantes, pode valer um endpoint dedicado numa fase seguinte; não antecipar essa otimização agora (YAGNI).
