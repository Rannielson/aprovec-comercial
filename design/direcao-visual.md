# APROVEC — Sistema visual do mockup

## Referência oficial

Identidade observada em [aprovecbrasil.com.br](https://aprovecbrasil.com.br/) em 13/09/2026, por inspeção visual e leitura dos estilos carregados no navegador. Esta direção substitui a exploração inicial em azul-marinho, preservada nos arquivos `referencia-visual.*`.

O site usa áreas vermelhas, logo branca, títulos fortes, blocos pretos e brancos, botões com cantos opostos arredondados e formas geométricas de contorno. O painel adapta esses elementos à leitura de valores, tabelas e situações financeiras.

## Cores observadas

| Referência | Valor | Aplicação no painel |
| --- | --- | --- |
| Vermelho primário | `#AB090A` | Navegação, seleção e links |
| Vermelho secundário | `#DA0000` | Pequenos acentos |
| Preto dos botões | `#0C0C0E` | Destaque de comissão e ações principais |
| Texto preto | `#000000` | Referência para títulos e contraste |
| Branco | `#FFFFFF` | Logo, tabelas e superfícies |
| Branco suave | `#FAFAFA` | Referência para fundos claros |
| Verde de ação do site | `#3F8500` | Confirmação final da simulação |

Tons auxiliares de cinza e vermelho claro foram adaptados para o painel. Recebimentos usam verde, atrasos usam âmbar e cancelamentos usam vermelho suave. Cada estado tem texto explícito, sem depender apenas de cor.

## Tipografia e marca

Inter nos títulos e valores; Poppins nos textos e controles, conforme o site. Fontes armazenadas em `mockup/assets/fonts/`, sem requisições externas durante o uso. Logo branca original enviada pelo usuário em `mockup/assets/logo-aprovec.webp`, preservando proporções, sobre vermelho.

Moeda brasileira, datas `dd/mm/aaaa` e números com alinhamento tabular.

## Composição e componentes

Quatro áreas: Visão geral, Minha carteira, Comissões e Fechamento. Desktop com navegação lateral vermelha e conteúdo claro. Comissão total e suas duas origens compartilham um bloco preto. Tabelas brancas têm divisores discretos e valores alinhados à direita. Fechamento disponível recebe um destaque vermelho leve.

Botões principais pretos com cantos opostos arredondados reproduzem um elemento recorrente do site. Geometria de contorno aparece discretamente no bloco de comissão. No celular, navegação horizontal, conteúdo empilhado e tabelas com rolagem própria.

## Interações e limites

Mockup local com filtros, busca, detalhes, CSV e simulação de conferência/NF/provisionamento. Dados demonstrativos identificados. Setembro em apuração; agosto disponível para conferência. CEO e CRM ficam fora desta entrega. Regras e valores em `brief-recorrencia.md`.

## Arquivos de referência

- `design/site-reference/global.css`: estilos globais do site e cores oficiais.
- `design/site-reference/home.css`: estilos da página inicial.
- `mockup/brand.css`: aplicação da identidade sobre a estrutura do mockup.

Origem dos estilos: `/wp-content/uploads/elementor/css/post-191.css` e `/wp-content/uploads/elementor/css/post-295.css` no domínio oficial. Fontes obtidas dos recursos carregados pelo próprio site.

## Gestor e administração

A navegação contextual usa o seletor de perfil no cabeçalho. O gestor destaca sua comissão global em preto, com quatro situações da base e contribuição por carteira. A administração apresenta árvore em fundo pontilhado discreto, conectores vermelhos, nós brancos e detalhe lateral. A faixa preta de gestão fica separada da árvore para evitar confusão com indicação. Formulários usam painéis laterais e taxas editáveis com prévia. Componentes em `mockup/network.css`.

## Árvores em pirâmide — revisão de 14/09/2026

Estrutura vertical em área ampla, com gestor centralizado acima de várias árvores independentes. Conectores pontilhados distinguem abrangência global de vínculos diretos vermelhos. Cada cartão tem nome, taxa própria, base recebida e comissão total; um botão circular + abaixo cadastra um indicado com o vínculo preenchido. O cartão Nova árvore inicia outra ramificação. Detalhes abrem lateralmente para preservar espaço da ilustração. Zoom, enquadramento, filtro por topo e recolhimento de ramos permitem explorar uma base crescente. Componentes em `mockup/tree.css`.
