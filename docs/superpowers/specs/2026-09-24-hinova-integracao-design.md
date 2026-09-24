# Integração Hinova — Configurações/Integrações (Fase 3) — Design

**Specs anteriores relacionadas:**
- `docs/superpowers/specs/2026-09-23-fundacao-saas-design.md` (arquitetura geral do SaaS)
- `docs/superpowers/specs/2026-09-24-app-shell-design.md` (Fase 2a — casca fixa, sidebar/topbar)
- `docs/superpowers/specs/2026-09-24-carteira-design.md` (Fase 2b — decisão registrada de adiar a origem real dos boletos/ERP)

## Contexto e objetivo

A Fase 2b registrou como decisão em aberto de onde os boletos e a árvore comissionada virão em produção — hoje só existem via dados de desenvolvimento. Esta fase começa a resolver isso pelo lado da identidade: a Hinova (sistema SGA v2, `https://api.hinova.com.br/api/sga/v2`) é o ERP externo de onde vêm os "voluntários" (vendedores). Esta fase constrói a base administrativa — o admin do tenant configura as credenciais da API Hinova e mapeia voluntários Hinova a usuários já existentes no APROVEC — dentro de uma nova área da sidebar, "Configurações", com uma seção "Integrações".

**Fora de escopo nesta fase, deliberadamente:**
- Importação de boletos/comissões via Hinova (fase futura, ainda não desenhada).
- Endpoint `Cadastrar Voluntário` (criar/alterar voluntário na Hinova a partir do APROVEC) — decisão registrada nesta sessão: fica para depois.
- Endpoint `Buscar Voluntário` (busca por CPF/código) — investigado, mas não é usado por esta fase (ver descoberta abaixo).
- Qualquer papel de `codigo_classificacao` ou das `cooperativas[]` da Hinova na árvore comissionada — a árvore continua 100% modelada dentro do APROVEC (RBAC/`commission_plans`), sem relação com a estrutura de cooperativas da Hinova.

## Descobertas que moldam o design

Testado nesta sessão contra a API real da Hinova (ambiente de produção do cliente, com credenciais fornecidas por ele para este fim):

- **Autenticação** (`POST /usuario/autenticar`) — header `Authorization: Bearer {token estático gerado no SGA}` + corpo `{"usuario": "...", "senha": "..."}`. Devolve `{"mensagem": "OK", "token_usuario": "..."}`. Esse `token_usuario` **não expira**, segundo a documentação — mas esta fase optou por **não persisti-lo**: cada operação que fala com a Hinova reautentica primeiro. Isso mantém o conjunto de segredos por tenant em só 3 campos (usuário, senha, token estático da SGA) e evita depender de mais um segredo de longa duração guardado no banco.
- **Listar Voluntário** (`GET /listar/voluntario/:situacao/:pagina`, header `Authorization: Bearer {token_usuario}`) — 5000 registros por página, página inicia em 0. Testado com `situacao=ativo`: devolveu 240 voluntários reais. Cada item tem `codigo_voluntario`, `nome`, `cpf`, contato, `situacao`, `codigo_classificacao`, e um array `cooperativas[]` (cada voluntário pode estar em 0 a N cooperativas). **Não existe busca por nome no servidor da Hinova** — a busca por texto desta fase é feita do nosso lado, sobre o resultado já trazido da Hinova (decisão registrada: busca "ao vivo", sem persistir a lista completa).
- **Buscar Voluntário** (`GET /buscar/voluntario/:cpfOuCodigo`) — testado com um código; funciona, mas é por CPF/código, não por nome (diferente do que se supunha inicialmente). Como o fluxo de mapeamento usa a lista completa + busca local, este endpoint não é necessário nesta fase.
- **Cadastrar Voluntário** (`POST /voluntario/cadastrar`) — documentação lida (campos: nome, cpf, formato/valor de pagamento, telefones, endereço, `cooperativas[]`, `codigo_voluntario_vinculado`), mas **não testado** (é uma mutação em produção de terceiro) e fica fora do escopo desta fase.
- Este repositório **não tem hoje nenhum mecanismo de criptografia reversível** — só hash irreversível (Argon2id para senha de usuário). Introduzir a criptografia reversível para as credenciais Hinova é, portanto, uma decisão de arquitetura desta fase, não um padrão já existente para seguir.
- Também não existe hoje um padrão de "tabela de configuração por tenant" — as tabelas tenant-scoped existentes (`tenant_modules`, `roles`, `commission_*`) não servem de modelo direto para guardar credenciais; esta fase introduz esse padrão.

## Decisões de escopo (resolvidas nesta sessão)

1. **Cooperativas da Hinova não definem tenant nem hierarquia.** Um voluntário pode estar em várias cooperativas na Hinova (achado: 49 dos 240 voluntários testados estavam em 2+ cooperativas, sempre incluindo uma cooperativa "guarda-chuva" comum) — isso é ignorado pelo mapeamento. Guardamos só a identidade (`codigo_voluntario`) do voluntário, independente de quantas cooperativas ele tem na Hinova.
2. **Vínculo é 1-para-1 nos dois sentidos:** um usuário APROVEC aponta para no máximo um `codigo_voluntario`, e cada `codigo_voluntario` está vinculado a no máximo um usuário APROVEC.
3. **Reautenticar a cada chamada** em vez de cachear/persistir `token_usuario` (ver descoberta acima).
4. **Criptografia em AES-256-GCM na aplicação C#**, chave via variável de ambiente — não `pgcrypto` (evita levar uma chave de criptografia para dentro do Postgres, o que atritaria com o modelo de RLS/`security definer` já estabelecido) e não a Data Protection API do ASP.NET Core (seu key ring padrão é em arquivo local, o que não escala se a API algum dia rodar em mais de uma réplica). Mesmo padrão já usado para `Internal.Key`: variável de ambiente, nunca comitada em `appsettings.json`.

## Backend

**Nova migration:** `src/Recorrencia.Db/Scripts/0014_hinova_integracao.sql`

- Módulo e permissão RBAC novos, seguindo o padrão de `0006_rbac_catalog.sql`:
  ```sql
  insert into modules (key, name, sort_order) values
    ('integracoes', 'Integrações', 7);

  insert into permissions (key, module_key, name, scoped) values
    ('integracoes.gerenciar', 'integracoes', 'Gerenciar integrações', false);
  ```
  Não é preciso inserir em `role_template_permissions` manualmente — o catch-all já existente em `0006_rbac_catalog.sql:90-91` (`select 'administrador', key, ... from permissions`) concede automaticamente `integracoes.gerenciar` ao role `administrador`, mesmo comportamento já usado para toda nova permissão marcada `scoped=false`.

- **Tabela `hinova_credenciais`** — uma linha por tenant:
  ```sql
  create table hinova_credenciais (
    tenant_id uuid primary key references tenants (id),
    usuario_enc bytea not null,
    senha_enc bytea not null,
    token_sga_enc bytea not null,
    updated_at timestamptz not null default now(),
    updated_by uuid not null references users (id)
  );
  ```
  RLS por `tenant_id`, mesmo padrão já usado nas demais tabelas tenant-scoped. Os três campos `_enc` guardam o resultado de AES-256-GCM (nonce + ciphertext + tag concatenados em um único `bytea`); a chave de criptografia vem de uma variável de ambiente nova (`Hinova__EncryptionKey`, 32 bytes em base64), nunca persistida no banco nem comitada.

- **Tabela `hinova_voluntario_mapping`** — um vínculo por linha:
  ```sql
  create table hinova_voluntario_mapping (
    tenant_id uuid not null references tenants (id),
    user_id uuid not null references users (id),
    codigo_voluntario text not null,
    nome_hinova text not null,
    cpf_hinova text not null,
    mapped_at timestamptz not null default now(),
    mapped_by uuid not null references users (id),
    primary key (tenant_id, user_id),
    unique (tenant_id, codigo_voluntario)
  );
  ```
  `nome_hinova`/`cpf_hinova` são um retrato do momento do vínculo (para listar os vínculos já salvos sem precisar rebuscar na Hinova a cada carregamento da tela). RLS por `tenant_id`.

**Novo:** `src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs` — todos atrás de `.RequirePermission("integracoes.gerenciar")`:

| Endpoint | Descrição |
|---|---|
| `GET /integracoes/hinova/credenciais` | Devolve só `{ configurado: bool, atualizadoEm, atualizadoPor }` — nunca os valores em si. |
| `PUT /integracoes/hinova/credenciais` | Corpo `{ usuario, senha, tokenSga }`. Antes de gravar, chama `IHinovaClient.Autenticar(...)`; se a Hinova responder erro, devolve 400 e não grava. Em sucesso, criptografa e faz upsert em `hinova_credenciais`. |
| `GET /integracoes/hinova/voluntarios?query=` | Autentica com as credenciais salvas do tenant (400 se não configuradas), chama `Listar Voluntário` com `situacao=ativo`, filtra por `nome` contendo `query` (case-insensitive, mesmo estilo do filtro de `carteira`), e marca cada item com `jaVinculado: bool` + `vinculadoA` (nome do usuário, se houver) cruzando com `hinova_voluntario_mapping`. |
| `GET /integracoes/hinova/mapeamentos` | Lista os vínculos salvos do tenant (nome/código Hinova + usuário APROVEC vinculado). |
| `POST /integracoes/hinova/mapeamentos` | Corpo `{ userId, codigoVoluntario, nomeHinova, cpfHinova }`. Insere; 409 se o usuário ou o código já estiverem vinculados a outra coisa. |
| `DELETE /integracoes/hinova/mapeamentos/{userId}` | Remove o vínculo daquele usuário. |

**Novo:** `src/Recorrencia.Api/Integracoes/IHinovaClient.cs` — interface (`Task<string> Autenticar(usuario, senha, tokenSga)`, `Task<IReadOnlyList<HinovaVoluntario>> ListarVoluntarios(tokenUsuario)`) com uma implementação real (`HinovaClient`, `HttpClient` apontando para `https://api.hinova.com.br/api/sga/v2`) e uma implementação fake para os testes automatizados — nenhum teste bate na Hinova real.

**Novo:** `src/Recorrencia.Api/Security/AesGcmCipher.cs` (ou local equivalente) — `byte[] Encrypt(string plaintext)` / `string Decrypt(byte[] cipher)`, chave lida uma vez de `Hinova__EncryptionKey` via `IOptions`.

## Frontend

- **Modificado:** `web/src/app/app-shell.tsx` — novo item de navegação "Configurações" (ícone novo, a definir), com `href: '/configuracoes/integracoes'`, visível só quando `me.permissions` contém `integracoes.gerenciar` (mesmo padrão de ocultação condicional já usado para os cards de comissão em `tenant-home.tsx`).
- **Novo:** `web/src/app/configuracoes/integracoes/page.tsx` — Server Component, guarda de host (404 fora do tenant) + guarda de permissão (`integracoes.gerenciar`, senão mesmo cartão "sem acesso" já usado em `carteira/page.tsx`). Duas seções:
  - **Credenciais Hinova:** formulário (usuário, senha, token) + indicação de status (configurado desde `<data>` / não configurado). Salvar chama `PUT /integracoes/hinova/credenciais`; erro de validação da Hinova aparece como mensagem no formulário.
  - **Mapeamento de voluntários:** campo de busca por texto (formulário `GET`, mesmo padrão de recarregar a página com querystring já usado em `carteira`) → lista de resultados de `GET /integracoes/hinova/voluntarios?query=`, cada linha não vinculada com um `<select>` dos usuários do tenant + botão "Vincular"; linhas já vinculadas mostram a quem. Abaixo, lista dos vínculos já salvos com botão "Desvincular" por linha.

## Testes

- Testes de banco para `hinova_credenciais`/`hinova_voluntario_mapping`: RLS por tenant, unicidade (`codigo_voluntario` e `user_id` únicos por tenant).
- Testes de API com `IHinovaClient` fake: salvar credenciais (sucesso e rejeição pela Hinova), listar voluntários com filtro por nome e marcação de `jaVinculado`, criar/remover vínculo, 409 em vínculo duplicado, 403 sem a permissão `integracoes.gerenciar` em todos os endpoints.
- Teste unitário de `AesGcmCipher`: round-trip (encripta e decripta volta ao valor original), e que duas criptografias do mesmo texto geram `bytea` diferentes (nonce aleatório).
- E2E (`web/e2e/`): entrar como admin, abrir Configurações > Integrações pela sidebar, salvar credenciais (contra o fake), buscar um voluntário por nome, vincular a um usuário, confirmar que aparece na lista de vínculos.
- `npm run typecheck && npm run lint && npm run build` limpos.

## Riscos e decisões (resolvidos nesta spec)

1. **Nenhuma credencial real foi persistida nesta sessão.** As credenciais fornecidas pelo usuário para teste exploratório foram usadas apenas via `curl` local, para ler a estrutura de dados real da Hinova — não foram salvas em nenhum arquivo do repositório nem em nenhum banco.
2. A ausência de mecanismo de criptografia reversível no projeto até agora significa que esta fase introduz esse mecanismo do zero (AES-256-GCM em C#) — é a primeira vez que o projeto guarda um segredo que precisa ser lido de volta em texto claro.
3. `codigo_classificacao` e as `cooperativas[]` da Hinova não têm significado definido para nós e são deliberadamente ignorados — não há uma tabela de referência na documentação da Hinova para o que cada `codigo_classificacao` significa, e não é necessário para o mapeamento de identidade.
4. Divergência encontrada entre o que se esperava (busca por nome na Hinova) e o que a API real oferece (busca só por CPF/código) foi resolvida trazendo a lista completa e filtrando do nosso lado, evitando depender de um endpoint que não existe.
