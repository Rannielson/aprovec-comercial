# Convite por indicação — cadastro auto-serviço com aprovação (Fase 5) — Design

**Specs anteriores relacionadas:**
- `docs/superpowers/specs/2026-09-24-fundacao-saas-design.md` (motor de comissão, RBAC, hierarquia)
- `docs/superpowers/specs/2026-09-24-hinova-integracao-design.md` (Fase 3 — credenciais e mapeamento voluntário↔usuário; deixou `Cadastrar Voluntário`/`Buscar Voluntário` explicitamente fora de escopo)
- `docs/superpowers/specs/2026-09-24-estrutura-importacao-design.md` (Fase 4 — importação de vendedores já existentes na Hinova; deixou "link de convite próprio por vendedor" e "pendente de aprovação" explicitamente para uma fase futura — esta)
- `docs/superpowers/specs/2026-09-24-estrutura-arvore-comissionada-design.md` (deixou "crescimento orgânico via convite" explicitamente adiado)

## Contexto e objetivo

Até aqui, todo vendedor entra no APROVEC porque um administrador o cadastra manualmente, escolhendo um voluntário já existente na Hinova. Esta fase entrega o crescimento orgânico: qualquer consultor ativo pode compartilhar seu próprio link de indicação; quem recebe o link preenche um formulário simples (sem precisar de login); a solicitação cai numa fila de aprovação do administrador; ao aprovar, o sistema cadastra o novo voluntário de verdade na Hinova (endpoint `Cadastrar Voluntário`, nunca antes usado neste projeto), já vinculado ao código de quem indicou, cria o usuário no APROVEC com o supervisor correto (o indicador) e dispara o convite de acesso — tudo automático, sem digitação manual do admin.

## Descobertas que moldam o design

- **`POST /voluntario/cadastrar` é upsert por CPF, não create-only.** A própria documentação da Hinova diz: "se o CPF do voluntário for igual a um existente na base, esse voluntário será alterado com os dados enviados na requisição." Cadastrar um CPF que já existe **sobrescreve silenciosamente** o registro existente (exceto cooperativas, que esse endpoint não altera). Isso é uma operação perigosa se o CPF digitado no formulário público estiver errado ou já pertencer a outra pessoa cadastrada na Hinova por qualquer canal.
- **A resposta de `Cadastrar` já traz o código do voluntário recém-criado** — confirmado pelo usuário com uma chamada real: `{"mensagem": "OK", "codigo_voluntario": "131"}`. Não é preciso uma segunda chamada para recuperar o código.
- **`GET buscar/voluntario/:cpfOuCodigo`** busca por CPF ou código e retorna, entre outros campos, `codigo_voluntario`, `nome`, `cooperativas: [{codigo_cooperativa, nome_cooperativa}]`. Usamos esse endpoint por dois motivos: (1) checar duplicidade de CPF **antes** de cadastrar; (2) resolver o(s) `codigo_cooperativa` do indicador, já que `cooperativas[]` no `Cadastrar` precisa de código numérico, e o que já temos hoje (`ListarVoluntariosAsync`) só guarda nomes de cooperativa.
- **Campos aceitos por `Cadastrar Voluntário`:** `nome` e `cpf` (obrigatórios); opcionais — `telefone`, `celular`, `telefone_comercial`, `logradouro`, `numero`, `complemento`, `bairro`, `cidade`, `estado`, `cep`, `email`, `cooperativas[]` (por código), `codigo_voluntario_vinculado` (exatamente o "código do voluntário indicação" que precisamos), `formato_pagamento`, `valor_pagamento`, `codigo_classificacao`, `obs`, `radio`, `radio_auxiliar`.
- **Decisão desta sessão:** `formato_pagamento`, `valor_pagamento` e `codigo_classificacao` não são coletados nem enviados — são sobre como a própria Hinova paga o voluntário diretamente, sem relação com o motor de comissão 100% interno do APROVEC (decisão já registrada na Fase 3). `obs` é preenchido automaticamente pelo backend (não é um campo do formulário) com uma nota de rastreabilidade, ex.: `"Cadastrado via indicação de {nome do indicador} em {data}."`.
- **Nenhuma tabela ou coluna de "pendente" existe hoje.** `users.status` só aceita `('convidado', 'ativo', 'desligado')` (`0003_users_hierarchy.sql`). Confirmado por busca no repositório: não existe link de convite auto-serviço, rastreio de referência, nem fila de aprovação em lugar nenhum do código atual.
- **A árvore comissionada é 100% definida por `users.supervisor_id`.** Não há mecanismo alternativo — `hierarchy_paths` é só uma tabela derivada, mantida por trigger a partir dessa coluna (`0003_users_hierarchy.sql`). Alocar o novo consultor na árvore = definir `supervisor_id = indicador.id` na hora de criar o usuário.
- **`POST /users` já faz, atomicamente, exatamente o que precisamos fazer na aprovação** (criar usuário, atribuir papel, definir supervisor, vincular ao Hinova, mandar e-mail de convite) — implementado na Fase anterior (`UserEndpoints.cs`). A aprovação reaproveita essa mesma lógica em vez de duplicá-la.
- **Reforço de decisões já tomadas em sessões anteriores:** e-mail nunca vem de fora (é sempre digitado — aqui, pelo próprio candidato, no formulário); `codigo_voluntario_vinculado` é sempre o código de quem realmente enviou o link, nunca escolhido manualmente; cooperativa é herdada do indicador, nunca escolhida pelo candidato.

## Modelo de dados

### Tabela nova: `solicitacoes_cadastro`

Guarda a candidatura crua enquanto pendente. **Nenhuma linha em `users` é criada até a aprovação** — isso evita que gente não aprovada entre na hierarquia, no RBAC ou em qualquer cálculo de comissão.

```sql
create table solicitacoes_cadastro (
  id uuid primary key default gen_random_uuid(),
  tenant_id uuid not null references tenants (id),
  indicador_user_id uuid not null,
  nome text not null,
  cpf text not null,
  celular text not null,
  email text not null,
  cep text not null,
  logradouro text not null,
  numero text not null,
  complemento text,
  bairro text not null,
  cidade text not null,
  estado text not null,
  status text not null default 'pendente' check (status in ('pendente', 'aprovado', 'rejeitado')),
  criado_em timestamptz not null default now(),
  resolvido_em timestamptz,
  resolvido_por uuid,
  user_id_resultante uuid,
  foreign key (tenant_id, indicador_user_id) references users (tenant_id, id),
  foreign key (tenant_id, resolvido_por) references users (tenant_id, id),
  foreign key (tenant_id, user_id_resultante) references users (tenant_id, id),
  check (status = 'pendente' or (resolvido_em is not null and resolvido_por is not null)),
  check (status <> 'aprovado' or user_id_resultante is not null)
);
```

RLS ativado e forçado, seguindo o padrão de toda tabela tenant-scoped do projeto. Visualização/aprovação gated pelas mesmas permissões que já protegem a criação manual (ver seção RBAC). Sem trigger de imutabilidade — diferente de `commission_plans`/`fechamentos`, não há razão de negócio para congelar isso depois de resolvido.

### Tabela nova: `convite_links`

Um link permanente e reutilizável por usuário, gerado sob demanda na primeira vez que a pessoa pede para ver/copiar o próprio link.

```sql
create table convite_links (
  tenant_id uuid not null references tenants (id),
  user_id uuid not null,
  token_hash text not null,
  criado_em timestamptz not null default now(),
  primary key (tenant_id, user_id),
  unique (token_hash),
  foreign key (tenant_id, user_id) references users (tenant_id, id)
);
```

Segue exatamente o padrão já usado em `invite_tokens`/`Tokens.New()`/`Tokens.Hash()`: 32 bytes aleatórios, base64url; só o hash SHA-256 é persistido; o token puro só existe no link entregue à pessoa. Diferença chave: **não expira e não é de uso único** — é o link pessoal e permanente do consultor, para compartilhar quantas vezes quiser.

## Fluxo público (sem login)

**Pré-requisito para gerar/usar o link:** o usuário só consegue ver seu link de indicação se **já estiver vinculado a um código de voluntário na Hinova** (`hinova_voluntario_mapping` já tem uma linha para ele). Sem isso não há como resolver `codigo_voluntario_vinculado` nem a cooperativa do candidato. Quem ainda não está vinculado vê uma mensagem explicando isso, sem link.

- **Onde ver o próprio link:** um novo cartão na página "Minha recorrência" (`web/src/app/tenant-home.tsx`), visível para qualquer usuário com um `hinova_voluntario_mapping`. Botão "Copiar meu link de indicação".
- **`GET /convite-links/me`** (autenticado, sem permissão extra alem de estar logado) — devolve o link do usuário atual, gerando-o (via `app.create_convite_link` ou lógica equivalente em C#) na primeira chamada se ainda não existir.
- **A página pública:** `web/src/app/indicar/[token]/page.tsx`, fora do `AppShell` (sem sidebar/topbar — página solo, como `login`/`definir-senha`). Resolve o token via `GET /convite-links/{token}` (público, sem autenticação) para mostrar "Você foi indicado por {nome do indicador}" e o nome/marca do tenant. Token inválido/indicador desligado → mensagem de link expirado, sem formulário.
- **Formulário:** Nome, CPF, Celular (obrigatório), E-mail, CEP (com autopreenchimento de logradouro/bairro/cidade/estado via [ViaCEP](https://viacep.com.br) direto no navegador — chamada pública, sem chave, sem envolver o backend), Número, Complemento (opcional). Nenhum campo de cooperativa ou código de indicação aparece — são resolvidos automaticamente no backend a partir do indicador.
- **`POST /convite-links/{token}/solicitacoes`** (público, sem autenticação) — valida os campos, grava uma linha em `solicitacoes_cadastro` com `status = 'pendente'` e `indicador_user_id` resolvido a partir do token. Como é um endpoint público de escrita, leva um limite de taxa por IP (reaproveitando o padrão já existente em `LoginThrottle`, adaptado para este uso) para não virar vetor de spam.

## Fluxo de aprovação (administrador)

**Nova página:** `web/src/app/administracao/convites/page.tsx`, com um contador de pendências no item de menu (mesmo padrão visual do "Fechamento" no mockup, que já tem um badge `nav-count`). Lista cada solicitação pendente com todos os dados preenchidos e o nome de quem indicou.

**`POST /solicitacoes-cadastro/{id}/aprovar`** — dentro de uma única transação:

1. Carrega a solicitação (`for update`) e confirma `status = 'pendente'`.
2. `GET buscar/voluntario/{cpf}` na Hinova.
   - **Se encontrar um voluntário existente:** aborta com `409 solicitacao.cpf_ja_cadastrado`, devolvendo o nome/código encontrado. O frontend mostra esse aviso claramente; o administrador resolve manualmente (corrige o CPF e tenta de novo, rejeita a solicitação, ou trata fora do fluxo). **Nenhuma chamada de escrita é feita na Hinova neste caso.**
   - Se não encontrar (404 da Hinova): segue.
3. `GET buscar/voluntario/{codigo do indicador}` para obter `cooperativas[].codigo_cooperativa` do indicador (o `hinova_voluntario_mapping` do indicador já dá o código; esta chamada só resolve os códigos de cooperativa, que `ListarVoluntariosAsync` não guarda).
4. `POST /voluntario/cadastrar` com os dados da solicitação + `cooperativas` do indicador + `codigo_voluntario_vinculado` = código do indicador + `obs` gerado automaticamente. Recebe `codigo_voluntario` na resposta.
5. Cria o usuário e insere `hinova_voluntario_mapping` com o `codigo_voluntario` recebido (`supervisor_id` = indicador, papel Consultor por padrão). Como isso precisa acontecer dentro da mesma transação da aprovação (junto com as chamadas à Hinova e a atualização da solicitação), a criação do usuário deve ser extraída de `InviteAsync` (`UserEndpoints.cs`) para um método interno reaproveitável por ambos os fluxos — não uma chamada HTTP interna a `POST /users`.
6. Atualiza a solicitação: `status = 'aprovado'`, `resolvido_em`/`resolvido_por`/`user_id_resultante`.
7. Envia o e-mail de "definir senha" (mesmo template/fluxo de 72h já existente).

**`POST /solicitacoes-cadastro/{id}/rejeitar`** — só marca `status = 'rejeitado'`, `resolvido_em`/`resolvido_por`. Sem e-mail para o candidato (decisão desta sessão — pode ser adicionado depois se fizer falta).

## RBAC

Nenhuma permissão nova. Aprovar/rejeitar exige as mesmas duas permissões que já protegem a criação manual com vínculo Hinova: `usuarios.convidar` + `integracoes.gerenciar` — hoje isso é só o Administrador. Ver a fila de pendências usa a mesma checagem. Gerar/ver o próprio link de indicação não exige nenhuma permissão além de estar autenticado (qualquer consultor ativo e Hinova-vinculado pode indicar alguém).

## Hinova client — dois métodos novos

`IHinovaClient` ganha:

```csharp
Task<HinovaVoluntarioDetalhe?> BuscarVoluntarioAsync(string tokenUsuario, string cpfOuCodigo, CancellationToken ct);
Task<string> CadastrarVoluntarioAsync(string tokenUsuario, CadastrarVoluntarioRequest request, CancellationToken ct);
```

`BuscarVoluntarioAsync` devolve `null` quando a Hinova responde "não encontrado" (não lança exceção para esse caso — é um resultado esperado, não um erro). `CadastrarVoluntarioAsync` devolve o `codigo_voluntario` da resposta. Ambos implementados em `HinovaClient.cs` (chamadas reais) e em `DevFakeHinovaClient.cs` (fake determinístico para dev/E2E, incluindo simular o caso de CPF duplicado para testar o bloqueio).

## Testes

- Testes de unidade para `BuscarVoluntarioAsync`/`CadastrarVoluntarioAsync` no `HinovaClientTests.cs` (já existe um arquivo assim para os dois métodos atuais).
- Teste de API cobrindo: submissão pública cria a solicitação corretamente; aprovação com CPF novo cria usuário+mapping+email; aprovação com CPF duplicado bloqueia sem tocar a Hinova em escrita; rejeição não cria usuário; endpoints públicos funcionam sem sessão; endpoints de aprovação exigem as duas permissões.
- `npm run typecheck && npm run lint && npm run build` limpos no frontend.
- E2E (Playwright) cobrindo o caminho feliz completo: gerar link → preencher formulário → aparecer na fila → aprovar → novo usuário recebe e-mail → consegue definir senha e logar.

## Riscos e decisões (resolvidos nesta sessão)

1. **CPF duplicado na Hinova bloqueia a aprovação, não avisa e deixa passar** — decisão explícita do usuário: mais seguro exigir resolução manual do que arriscar sobrescrever um cadastro existente por engano.
2. **Rejeição não notifica o candidato por e-mail** — decisão explícita, para não abrir uma frente de "fluxo de notificação" antes de validar o resto; fica registrado como possível melhoria futura.
3. **Indicador precisa já estar Hinova-vinculado para gerar/usar o link** — sem isso não há `codigo_voluntario_vinculado` nem cooperativa para resolver; evita um estado inconsistente na Hinova.
4. **`formato_pagamento`/`valor_pagamento`/`codigo_classificacao` do lado Hinova não são coletados nem usados** — são sobre pagamento direto pela Hinova, sem relação com o motor de comissão do APROVEC.
5. **A operação de aprovação não é atômica entre APROVEC e Hinova** — se a chamada de `Cadastrar` na Hinova tiver sucesso mas a transação local falhar depois (ou vice-versa), pode sobrar um voluntário cadastrado na Hinova sem usuário correspondente no APROVEC. Aceitável nesta fase pelo mesmo motivo já registrado na Fase 4 (poucas dezenas de aprovações, não justifica uma transação distribuída); um administrador que notar o problema pode vincular manualmente pela tela de Integrações já existente.
