# Fundação do SaaS de recorrência comercial — design

Data: 2026-09-23
Escopo: stack, banco de dados, multi-tenância com RLS, autenticação, perfis modulares e motor de comissão.
Fora do escopo: telas definitivas, integrações (CRM Five, Bitrix24), IA, cobrança do SaaS e painel do CEO.

## 1. Contexto

O mockup em `mockup/` validou a experiência da APROVEC: consultor, coordenador da base e administrador, com comissões de 7% sobre a própria carteira, 2% sobre a carteira de supervisionados diretos e 1% de coordenação global. O produto será vendido como SaaS para várias empresas. A APROVEC é a primeira empresa (tenant). As regras dela servem de modelo sugerido, e outras empresas podem configurar as próprias.

Premissas:
- De 100 a 500 vendedores por empresa, além dos usuários internos.
- Um único desenvolvedor, com apoio de IA.
- Hospedagem em VPS ou cloud genérica, com a infraestrutura administrada pelo próprio desenvolvedor.
- A empresa já tem outros sistemas de backend em C#.

## 2. Stack e arquitetura

| Camada | Tecnologia | Responsabilidade |
|---|---|---|
| Frontend + BFF | Next.js (App Router) | Interface, sessão via cookie e repasse das chamadas para a API |
| API | ASP.NET Core (C#), EF Core + Npgsql | Regras de negócio, autorização, cálculo de comissão e acesso a dados |
| Banco | PostgreSQL + pgvector | Dados, RLS e embeddings futuros |
| Deploy | Docker Compose no VPS | Containers `web` (Next.js), `api` (.NET) e `db` (Postgres) |

Por que C#: tipo `decimal` nativo para valores monetários e alinhamento com os sistemas C# já existentes.

A API C# **não é exposta à internet**. Só o container `web` a acessa, pela rede interna do Docker. O navegador fala apenas com o Next.js.

Tarefas longas, como reprocessar uma competência ou gerar embeddings em lote, rodam em um worker .NET separado (`BackgroundService` ou processo agendado), fora do ciclo de uma requisição HTTP.

Valores monetários ficam em `numeric(14,2)` no banco e em `decimal` no C#. Taxas ficam em `numeric(7,4)`. Não se usa ponto flutuante em nenhuma etapa.

## 3. Multi-tenância

Cada empresa acessa por um subdomínio próprio: `{slug}.<domínio da plataforma>`.

- DNS com registro curinga (`*.<domínio>`) e certificado TLS curinga.
- O Next.js lê o `Host` e repassa o subdomínio à API em um header interno. O Next.js não resolve tenant e não acessa o banco.
- A API resolve `slug → tenant_id` pela tabela `tenants`, com cache em memória invalidado quando um tenant é alterado.
- A API confere se o `tenant_id` resolvido é igual ao `tenant_id` da sessão. Se forem diferentes, responde 403.
- Tenant inexistente ou suspenso: 404 genérico, sem revelar se o slug existe.

## 4. Autenticação e sessão

### Senhas
- Hash com **Argon2id** na API. Os parâmetros de custo ficam em configuração, para serem revisados conforme a carga.
- Um hash com parâmetros antigos é refeito com os parâmetros atuais no próximo login bem-sucedido.

### Provisionamento só por convite
1. Um usuário com permissão `usuarios.convidar` cadastra nome e e-mail. O usuário é criado com `status = convidado`.
2. A API gera um token aleatório, grava só o hash dele em `invite_tokens` com validade de 72 horas e envia o link.
3. A pessoa define a senha pelo link. O token é marcado como usado e o usuário passa a `ativo`.

A redefinição de senha usa o mesmo mecanismo, com validade de 1 hora.

### Sessões
- Token opaco de 256 bits gerado pela API. O banco guarda **só o hash** do token (`sessions.token_hash`).
- O Next.js grava o token em um cookie `httpOnly`, `Secure`, `SameSite=Lax`, restrito ao subdomínio da empresa, e o repassa à API a cada chamada.
- A sessão fica presa a um `tenant_id`. A expiração é deslizante: 12 horas sem uso, com limite absoluto de 7 dias.
- Revogação imediata: desligar um usuário ou trocar a senha apaga as sessões dele.
- Login: limite de tentativas por e-mail e por IP, e a mesma mensagem de erro para e-mail inexistente e senha errada.

### Superadmin da plataforma
- Fica na tabela `platform_admins`, separada de `users` e sem `tenant_id`.
- Faz login por um host exclusivo, `admin.<domínio>`, e suas sessões são marcadas como `platform`.
- Só essas sessões podem usar a conexão `app_superadmin` (ver seção 6).

## 5. Perfis modulares (RBAC)

O perfil de acesso define o que a pessoa vê e faz. A participação na comissão define quanto ela recebe (seção 7). Os dois conceitos são independentes.

### Catálogo global, mantido pelo superadmin
- `modules`: `carteira`, `comissoes`, `fechamento`, `estrutura`, `regras_comissao` e `usuarios`.
- `permissions`: `key` no formato `modulo.acao` (ex.: `carteira.visualizar`, `fechamento.confirmar`, `estrutura.editar`, `comissoes.exportar`, `usuarios.convidar`), `module_key` e `scoped` (booleano: a permissão aceita escopo de linhas?).
- `role_templates` + `role_template_permissions`: perfis sugeridos Consultor, Coordenador e Administrador.

### Por empresa
- `tenant_modules`: módulos contratados. Uma permissão só vale se o módulo dela estiver ativo para a empresa.
- `roles` (`tenant_id`, `name`, `source_template_id`) + `role_permissions` (`role_id`, `permission_key`, `scope`).
- `user_roles` (`user_id`, `role_id`): relação N:N. Uma pessoa pode ser Coordenador e Consultor ao mesmo tempo.
- Quando uma empresa é criada, os templates são copiados para `roles`. Depois, a empresa pode editar ou criar perfis livremente.

### Escopos das permissões com escopo
| Escopo | Linhas visíveis |
|---|---|
| `own` | as da própria pessoa |
| `direct` | as da pessoa e dos supervisionados diretos |
| `subtree` | as da pessoa e de toda a estrutura abaixo dela |
| `tenant` | todas as da empresa |

Permissão efetiva = união dos perfis da pessoa. Quando mais de um perfil concede a mesma permissão, vale o maior escopo.

### Validações do cadastro de perfis
- Toda empresa precisa manter pelo menos um usuário ativo com `usuarios.gerenciar_perfis`. A API bloqueia a alteração que deixaria a empresa sem nenhum.
- Uma pessoa não pode conceder uma permissão que ela mesma não tem.

## 6. Row Level Security

### Roles do Postgres
- `app_user`: role usada pela API em todas as operações. Não tem `BYPASSRLS` e não é dona das tabelas.
- `app_superadmin`: tem `BYPASSRLS` e usa uma connection string separada. A API só a usa depois de validar uma sessão `platform`.
- `app_owner`: dona do schema, usada apenas nas migrations.

`FORCE ROW LEVEL SECURITY` fica ativo em todas as tabelas de tenant.

### Contexto da transação
No início de cada transação, a API executa `SET LOCAL app.tenant_id` e `SET LOCAL app.user_id` com os valores da sessão validada, por meio de um interceptor do EF Core. Toda operação com banco acontece dentro de uma transação explícita. Sem contexto definido, as policies não retornam nenhuma linha.

### Tabelas lidas antes do contexto
`tenants`, `sessions`, `invite_tokens` e `platform_admins` são consultadas antes de existir usuário ou tenant na transação. Por isso, `app_user` não tem permissão direta sobre elas. O acesso acontece só por funções `SECURITY DEFINER` com propósito único:
- `app.resolve_tenant(slug)`: devolve id e status.
- `app.find_login(tenant_id, email)`: devolve id, hash e status do usuário.
- `app.create_session(...)`, `app.resolve_session(token_hash)` e `app.revoke_sessions(user_id)`.
- `app.consume_invite(token_hash, password_hash)`.

### Policies
- Isolamento, em toda tabela de tenant: `tenant_id = app.current_tenant()`.
- Visibilidade por escopo, em tabelas com dono (`boletos` pelo participante, com a permissão `carteira.visualizar`, e `users` pela própria linha, com `estrutura.visualizar`): a função `app.scope_for('<permissão>')`, marcada como `STABLE` e chamada como `(select app.scope_for(...))` para ser avaliada uma única vez por query, devolve o maior escopo do usuário. A policy filtra o dono da linha:
  - `own`: `owner_id = app.current_user_id()`.
  - `direct`: o dono está em `hierarchy_paths` com `ancestor_id = usuário` e `depth <= 1`.
  - `subtree`: o dono está em `hierarchy_paths` com `ancestor_id = usuário`.
  - `tenant`: sem filtro adicional.
- Ações que não são leitura (confirmar fechamento, exportar, editar estrutura) são autorizadas na API com `[RequirePermission("...")]`. O RLS garante a visibilidade e o isolamento. A API garante as ações. Uma camada não substitui a outra.

## 7. Motor de comissão

### Modelo
- `commission_plans`: `tenant_id`, `name`, `effective_from`, `status` (`rascunho` ou `ativo`), `source_template_id`. Plano ativo não pode ser editado. Para mudar regras, cria-se uma versão nova.
- `commission_rules`: `plan_id`, `type`, `rate` (`numeric(7,4)`, CHECK entre 0 e 1), `level` e `group_id`.
  - `own`: o dono da carteira recebe `rate` sobre o valor pago.
  - `upline`: quem está `level` níveis acima do dono da carteira recebe `rate`. É obrigatório `level >= 1` e pode haver um nível por regra no mesmo plano, sem repetir.
  - `global`: cada membro do `group_id` recebe `rate` sobre todo o valor pago na empresa.
- `commission_groups` + `commission_group_members`: grupos nomeados, como "Coordenação". As regras apontam para o grupo, então uma nova versão do plano não exige refazer a atribuição de pessoas.
- `plan_templates` (global): o plano APROVEC sugerido: `own` 7%, `upline` nível 1 com 2% e `global` Coordenação com 1%.

### Regras de cálculo
- O plano de uma competência é o último plano `ativo` com `effective_from` até o primeiro dia dela.
- Só entram boletos com status `recebido` e pagos dentro da competência. Boletos em atraso, cancelados ou a vencer geram zero.
- Os níveis acima são lidos de `hierarchy_paths` (`descendant_id = dono`, `depth = level`). Se não houver ninguém naquele nível, a regra não gera valor. O valor não é repassado a outro nível.
- O cálculo é uma função pura no domínio C#, com entrada (boletos, plano, hierarquia, grupos) e saída (lançamentos por beneficiário, regra e boleto), sem acesso a banco.
- Arredondamento: cada lançamento é arredondado para 2 casas, com arredondamento bancário (`MidpointRounding.ToEven`). Os totais são a soma dos lançamentos já arredondados.

### Comissão sem acesso à carteira alheia
João recebe 2% sobre a carteira de Maria, mas o perfil Consultor com escopo `own` não lista os boletos dela. Para calcular, o serviço de comissão lê os boletos de origem pela função `SECURITY DEFINER` `app.commission_sources(beneficiario_id, competencia)`. Ela devolve só os boletos que geram comissão para aquele beneficiário: os próprios, os da estrutura abaixo até o maior `level` do plano e, se ele for membro de um grupo `global`, os da empresa inteira.

A resposta de `comissoes.visualizar` mostra só os lançamentos (regra, pessoa de origem, base e valor). Os dados do associado (nome, placa) aparecem apenas quando o escopo de `carteira.visualizar` do usuário cobre aquele boleto.

### Competência aberta e fechada
- Competência em apuração: o cálculo é feito a cada consulta, com os boletos, o plano, a hierarquia e os grupos atuais. Nada é gravado.
- Na confirmação do fechamento, os lançamentos e as taxas usadas são gravados em `fechamento_detalhes`. A partir daí, esse mês é lido só do snapshot. Mudanças posteriores de plano, estrutura ou grupos não o alteram.

## 8. Modelo de dados

Todas as tabelas de tenant têm `id uuid`, `tenant_id uuid not null` (FK para `tenants`), `created_at` e `updated_at`.

| Tabela | Colunas principais |
|---|---|
| `tenants` | `slug` único, `name`, `status` (`ativo`, `suspenso`) |
| `platform_admins` | `email` único, `password_hash` |
| `users` | `name`, `email` (único por tenant), `password_hash`, `status` (`convidado`, `ativo`, `desligado`), `supervisor_id` (FK para `users`, nullable) |
| `hierarchy_paths` | `ancestor_id`, `descendant_id`, `depth` (0 = a própria pessoa) |
| `sessions` | `token_hash`, `user_id` ou `platform_admin_id`, `tenant_id` (nullable só em sessões `platform`), `expires_at`, `absolute_expires_at` |
| `invite_tokens` | `token_hash`, `user_id`, `purpose` (`convite`, `redefinicao`), `expires_at`, `used_at` |
| `modules`, `permissions`, `role_templates`, `role_template_permissions`, `plan_templates` | catálogo global (sem `tenant_id`) |
| `tenant_modules`, `roles`, `role_permissions`, `user_roles` | RBAC por empresa (seção 5) |
| `commission_plans`, `commission_rules`, `commission_groups`, `commission_group_members` | motor de comissão (seção 7) |
| `boletos` | `participante_id` (FK para `users`), `associado_ref`, `associado_nome`, `placa`, `valor numeric(14,2)`, `status` (`a_vencer`, `recebido`, `atraso`, `cancelado`), `vencimento`, `pago_em` |
| `fechamentos` | `competencia` (primeiro dia do mês), `status` (`apuracao`, `conferencia`, `confirmado`, `provisionado`), `confirmado_em`, `confirmado_por`; único por (`tenant_id`, `competencia`) |
| `fechamento_detalhes` | `fechamento_id`, `beneficiario_id`, `rule_type`, `level`, `rate`, `base`, `valor` |
| `audit_log` | `user_id`, `action`, `entity`, `entity_id`, `before` e `after` (jsonb); registra alterações de estrutura, perfis, planos e fechamentos |

### Hierarquia
- `supervisor_id` guarda o vínculo direto. `hierarchy_paths` é uma closure table derivada dele, mantida por trigger no mesmo commit da alteração.
- A API valida antes de gravar: supervisor da mesma empresa, sem autorreferência e sem ciclo. O ciclo é detectado quando o novo supervisor já é descendente da pessoa em `hierarchy_paths`. O trigger repete a checagem de ciclo como última defesa.
- A profundidade da estrutura é ilimitada. A profundidade da comissão depende só das regras `upline` do plano.

### pgvector
A extensão é habilitada na migration inicial. Nenhuma tabela de embeddings é criada agora.

## 9. Erros

- A API responde com Problem Details (RFC 7807) e um código estável (ex.: `hierarchy.cycle`, `role.last_admin`). O Next.js traduz o código para uma mensagem em português.
- 401: sessão ausente ou expirada. O Next.js redireciona para o login do subdomínio.
- 403: sem permissão ou tenant divergente.
- 404: recurso inexistente ou fora do escopo do usuário. A API não diferencia "não existe" de "não pode ver".
- 409: conflito de regra de negócio (ciclo, e-mail duplicado, plano ativo imutável, fechamento já confirmado).
- Erros inesperados são registrados com um id de correlação, e o usuário vê só esse id.

## 10. Testes

- **Unidade (xUnit):** motor de comissão, com os cenários de `tests/network.test.cjs` portados, incluindo o exemplo consolidado do PDF (João R$ 1.600, Maria R$ 800, Pedro R$ 350, coordenador R$ 350, total R$ 3.100) e o histórico de agosto. Também planos com 3 ou mais níveis, nível sem beneficiário e arredondamento.
- **Integração com Postgres real (Testcontainers):** para cada escopo (`own`, `direct`, `subtree`, `tenant`), conectar como `app_user` com um contexto de usuário e verificar o que ele vê e o que não vê. Também: isolamento entre dois tenants, ausência de linhas sem contexto, trigger da closure table e rejeição de ciclo.
- **API:** tenant divergente entre sessão e subdomínio, revogação de sessão, expiração do convite, bloqueio da remoção do último administrador e limite de tentativas de login.
- **E2E (Playwright):** login por convite e visão do consultor em um subdomínio de teste.

## 11. Regras em aberto para validar com a APROVEC

- Com mais de um membro no grupo Coordenação, cada um recebe 1% integral (comportamento atual do motor) ou o valor é rateado?
- D-3: dias úteis ou corridos. Data de referência financeira. Calendário de fechamento.
- Estornos, pagamentos parciais e troca de titularidade.
- Origem dos boletos: importação manual, arquivo ou integração. Isso define a próxima etapa depois da fundação.
