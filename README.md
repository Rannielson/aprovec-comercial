# APROVEC — Mockup local de recorrência comercial

Abra `mockup/index.html` diretamente no navegador. Todos os arquivos necessários, incluindo a logo original e as fontes, estão em `mockup/`; não há serviços externos ou etapa de instalação.

A identidade visual segue o [site oficial da APROVEC](https://aprovecbrasil.com.br/): vermelho e preto, títulos Inter, textos Poppins e botões com cantos assimétricos. Referências em `design/direcao-visual.md`; tema em `mockup/brand.css`.

Para abrir por um servidor local:

```sh
cd mockup
python3 -m http.server 4173 --bind 127.0.0.1
```

Acesse http://127.0.0.1:4173.

## Perfis e navegação

O seletor **Explorar como**, no topo, alterna entre consultor, coordenador da base e administrador. É uma navegação demonstrativa, sem autenticação.

### Consultor · 7%

- Visão geral: comissão apurada, composição de consultor/supervisão, saúde da carteira e fechamento disponível.
- Minha carteira: 106 boletos fictícios, busca, filtros, paginação e detalhe de cada cálculo.
- Comissões: 7% sobre pagamentos da própria carteira e 2% sobre pagamentos de cada consultor supervisionado diretamente; exemplo da limitação a um nível.
- Fechamento: demonstrativo de agosto, confirmação e NF demonstrativa até o provisionamento simulado.

### Coordenador da base · 1%

- Resumo de todos os recebimentos e comissão de coordenação global de 1%.
- Boletos da operação: busca, filtros por carteira e situação, paginação e cálculo por boleto.
- Comissão por pessoa, demonstrativo CSV e acompanhamento dos fechamentos de agosto.
- Consulta da árvore e detalhe das supervisões de cada consultor.

### Administrador

- Estrutura vertical em pirâmides: coordenador acima de várias árvores, cada consultor com seus próprios consultores supervisionados.
- Botão + abaixo de cada consultor abre o cadastro com supervisor preenchido; Nova árvore cadastra um topo independente.
- Zoom, enquadramento, filtro por árvore, recolhimento de ramos e alternativa em lista.
- Cadastro e edição de participantes, e-mail e supervisor direto. A coordenação global é separada dos vínculos de supervisão.
- Edição dos percentuais de consultor, supervisão direta e coordenação, com prévia de remuneração.
- Validações de e-mail duplicado, autorreferência, ciclos e percentuais inválidos.

Cadastros e percentuais são salvos no armazenamento local deste navegador (`aprovec-network-v1`). Recarregar reinicia o perfil e as etapas de fechamento; preserva participantes e taxas. Salvar remuneração ou alterar vínculos recalcula setembro nos três perfis. Agosto mantém seu demonstrativo histórico. Novos participantes começam sem boletos ou comissões.

O painel do CEO fica para uma etapa posterior. Nenhuma ação envia mensagens, documentos ou lançamentos para sistemas externos. A exportação gera somente CSV local de dados demonstrativos.

## Valores de referência

Setembro: R$ 20.000 recebidos na carteira do consultor × 7% = R$ 1.400. R$ 10.000 recebidos na carteira de Maria × 2% de supervisão = R$ 200. Total apurado: R$ 1.600.

Carteira própria: 90 recebidos de R$ 20.000; 12 atrasados de R$ 3.600; 4 cancelados de R$ 800. Boletos não pagos geram zero de comissão apurada. A situação Inativo Jurídico permanece distinta de cancelamento.

Agosto: R$ 1.260 de consultor + R$ 180 por supervisão = R$ 1.440. Setembro ainda está em apuração.

O corte D-3 e o calendário de fechamento são ilustrativos; as pendências de regras estão acessíveis em “Regras do demonstrativo”.

## Exemplo consolidado do PDF

| Pessoa | Base própria paga | Consultor · 7% | Supervisor · 2% | Coordenador · 1% | Total |
| --- | ---: | ---: | ---: | ---: | ---: |
| João | R$ 20.000 | R$ 1.400 | R$ 200 | — | R$ 1.600 |
| Maria | R$ 10.000 | R$ 700 | R$ 100 | — | R$ 800 |
| Pedro | R$ 5.000 | R$ 350 | R$ 0 | — | R$ 350 |
| Coordenador | — | — | — | R$ 350 | R$ 350 |
| Total | R$ 35.000 | R$ 2.450 | R$ 300 | R$ 350 | R$ 3.100 |

Base completa: 204 boletos, com 165 recebidos (R$ 35.000), 21 atrasados (R$ 5.400), 10 a vencer (R$ 2.000) e 8 cancelados (R$ 1.600). Os boletos adicionais de Maria e Pedro são fictícios. O jurídico é uma situação do associado, distinta do cancelamento do boleto.

O acompanhamento histórico de agosto usa bases próprias de R$ 18 mil / R$ 9 mil / R$ 4 mil e taxas de 7% / 2% / 1%: João R$ 1.440, Maria R$ 710, Pedro R$ 280 e coordenador R$ 310. Estados de confirmação e provisionamento são demonstrativos; a situação de João acompanha a simulação do consultor.

## Verificação

Execute `node --test tests/*.test.cjs` para verificar a árvore, os cálculos do exemplo, as validações e a persistência. As novas telas também foram verificadas no navegador em desktop e celular.

## Estrutura vertical

A árvore não tem limite fixo de níveis nem o antigo teto demonstrativo de 200 participantes. O desenho usa um percurso iterativo em `mockup/tree-layout.js`; o teste de disposição verifica 1.200 níveis. A renderização no navegador e o armazenamento local continuam sujeitos aos recursos disponíveis do dispositivo.

A linha pontilhada representa a abrangência global do coordenador, sem relação de supervisão. Linhas vermelhas representam vínculos diretos; todos os consultores recebem 7% sobre a própria carteira e apenas seus consultores supervisionados diretamente geram 2%. Novos níveis não propagam comissão aos demais ancestrais.

O exemplo cadastrado no navegador inclui uma segunda árvore de Bruno Costa, com Carla Mendes abaixo, e Lucas Almeida abaixo de Pedro para demonstrar a continuidade dos níveis. Esses participantes começam sem recebimentos e não alteram os totais do PDF.

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

`docker compose up -d --build` sobe banco, migrações, API (sem porta publicada) e web em `127.0.0.1:3000`. Em produção, coloque um proxy reverso com TLS curinga (`*.seu-dominio`) na frente do `web`, defina `ROOT_DOMAIN`, `WEB_SCHEME=https` e `COOKIE_SECURE=true`, e garanta que o proxy sobrescreva `X-Forwarded-For` e `X-Forwarded-Host` (nunca repasse os valores recebidos do cliente sem verificação), já que ambos alimentam a identificação do IP do cliente e do tenant. O envio de e-mail por SMTP ainda não está implementado: até lá, os e-mails aparecem no log da API.
