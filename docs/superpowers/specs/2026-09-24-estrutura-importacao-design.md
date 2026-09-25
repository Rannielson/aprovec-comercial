# Estrutura — Importar vendedores da Hinova e montar a hierarquia (Fase 4) — Design

**Specs anteriores relacionadas:**
- `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md` (motor de comissão, RBAC, hierarquia)
- `docs/superpowers/specs/2026-09-24-hinova-integracao-design.md` (Fase 3 — credenciais e mapeamento voluntário↔usuário)

## Contexto e objetivo

O objetivo desta fase é validar, com vendedores e dados reais, se a árvore de comissão funciona de fato: 7% sobre a carteira própria, 2% de quem cada vendedor indicou diretamente (1 nível), e 1% do coordenador sobre a base total. O motor de comissão já calcula isso corretamente — falta poder montar a hierarquia com pessoas reais, já vinculadas ao código de voluntário da Hinova, para depois importar boletos pagos de verdade e ver a distribuição.

**Decisão desta sessão:** nesta fase só tratamos vendedores que **já existem** na Hinova (obtidos via `Listar Voluntário`, já implementado na Fase 3). Cadastro de vendedor novo (sem código Hinova ainda), link de convite próprio por vendedor e o status "pendente de aprovação" ficam para uma fase futura, junto do cadastro automático na Hinova (`Voluntario - Cadastrar`/`Buscar`). A importação de boletos pagos (para validar a distribuição de fato) é a fase seguinte a esta.

## Descobertas que moldam o design

- **Toda a capacidade de backend necessária já existe** — nenhuma migration ou endpoint novo nesta fase:
  - `POST /users` (`src/Recorrencia.Api/Users/UserEndpoints.cs`) já cria o usuário, atribui papel(is), define o supervisor e envia o e-mail de convite com link de definir senha (validade 72h). Testado agora contra o ambiente real (`GET /users` logado como admin), resposta confere com o esperado: `{ id, name, email, status, supervisorId, roleIds }`.
  - `PUT /users/{id}/supervisor` já troca o supervisor de alguém existente.
  - `GET /roles` já lista os papéis do tenant com `sourceTemplateKey` — usado para achar o id do papel "consultor" a atribuir por padrão.
  - `GET /integracoes/hinova/voluntarios`, `POST/GET/DELETE /integracoes/hinova/mapeamentos` (Fase 3) já buscam voluntários na Hinova e gerenciam o vínculo.
- **A Hinova nem sempre tem e-mail cadastrado para o voluntário** (confirmado nos dados reais — vários registros com `"email": ""`). O e-mail do novo usuário precisa ser digitado pelo admin no momento da importação; não pode vir automaticamente da Hinova.
- Reforçando decisão já registrada na Fase 3: `cooperativas`/`codigo_classificacao` da Hinova não têm papel na hierarquia — a árvore continua 100% definida dentro do APROVEC.

## Frontend

Estende `web/src/app/configuracoes/integracoes/page.tsx` — **sem tela nova, sem item novo na sidebar**:

- Cada linha da tabela "Mapeamento de voluntários" que ainda não está vinculada (`jaVinculado: false`) ganha, ao lado do formulário de vincular a um usuário já existente (`VincularForm`, já existe), uma opção **"Criar vendedor"**: abre um mini-formulário com:
  - **Nome** — pré-preenchido com `voluntario.nome` (retornado pela Hinova), editável.
  - **E-mail** — obrigatório, digitado pelo admin (a Hinova não garante esse dado).
  - **Supervisor** — `<select>` opcional, listando os vendedores já importados nesta tela (vazio = raiz, recebe 7% sem indicação).
- Ao enviar, uma nova Server Action (`web/src/app/configuracoes/integracoes/actions.ts`) faz, em sequência:
  1. `GET /roles`, localizando o papel com `sourceTemplateKey === 'consultor'` (cacheável por requisição, não precisa de estado novo).
  2. `POST /users` com `{ name, email, supervisorId, roleIds: [idDoConsultor] }`.
  3. `POST /integracoes/hinova/mapeamentos` com `{ userId: <id retornado>, codigoVoluntario, nomeHinova, cpfHinova }` (mesmo formato já usado por `vincularVoluntario`).
  - Se o passo 3 falhar depois do passo 2 ter sucesso, o usuário fica criado mas aparece como "não vinculado" na busca — pode ser vinculado manualmente depois pela mesma tela (não é uma transação atômica entre os dois sistemas; ver Riscos).
- A tabela "Vínculos atuais" (já existe) ganha:
  - Uma coluna **Supervisor**, buscando a lista de usuários via `GET /users` e cruzando por `supervisorId`.
  - Um `<select>` por linha para trocar o supervisor (chama `PUT /users/{id}/supervisor` numa nova Server Action).
- **Não afetados:** `app-shell.tsx` (sem item de navegação novo), backend (`src/Recorrencia.Api/**`), banco (`src/Recorrencia.Db/**`).

## Backend

Nenhuma mudança. Reaproveita integralmente os endpoints já existentes citados acima.

## Testes

- `npm run typecheck && npm run lint && npm run build` limpos.
- Verificação manual/E2E: importar 2-3 vendedores (contra o fake da Hinova em dev/E2E) formando uma árvore de 2 níveis (1 raiz + 1 supervisionado por ela), confirmar que aparecem corretamente na tabela com o supervisor certo, e que trocar o supervisor de um vendedor já importado funciona.
- Sem testes de API novos — nenhum endpoint novo foi criado.

## Riscos e decisões (resolvidos nesta spec)

1. **E-mail é sempre digitado pelo admin**, nunca inferido da Hinova — decisão já registrada acima.
2. **Pendente de aprovação, convite auto-serviço e cadastro automático na Hinova (`Cadastrar`) ficam para uma fase futura** — decisão explícita desta sessão; esta fase cobre só a importação de vendedores que já têm código na Hinova.
3. **Criar vendedor + vincular ao código Hinova não é atômico** — são duas chamadas de API em sequência (criar usuário no APROVEC, depois criar o vínculo). Se a segunda falhar, o vendedor fica criado sem vínculo, visível e corrigível na mesma tela — aceitável para o volume inicial (poucas dezenas de vendedores sendo importados manualmente), não justifica uma transação distribuída nesta fase.
4. **Boleto pago de verdade fica para a fase seguinte** — esta fase só resolve "quem é quem" (identidade + hierarquia); a validação da distribuição de comissão sobre pagamentos reais depende da importação de boletos, que é o próximo passo depois desta fase estar pronta.
