# APROVEC — Mockup de recorrência comercial

## Entrega e escopo
Mockup navegável para debate, em português do Brasil. Visões do vendedor, gestor da base e administrador. CEO, CRM Five, Bitrix24 e IA ficam fora desta entrega. Dados fictícios identificados no produto. Nenhuma operação financeira real, integração ou envio externo.

## Regras extraídas do resumo da call
- Venda própria: 7% do valor efetivamente pago.
- Indicação direta: 2% do valor pago na carteira do vendedor indicado, e não 2% da comissão dele.
- Profundidade de indicação: um nível. João indica Maria, Maria indica Pedro. João recebe sobre Maria, mas não sobre Pedro.
- A comissão de gestor de 1% incide sobre toda a operação, separada da indicação direta.
- Saúde da base e comissões vêm dos mesmos boletos.
- Atualização diária D-3, com corte visível.
- Fechamento mensal: apuração, conferência, confirmação, NF e provisionamento financeiro.
- Provisionado não significa pago.
- Régua de associado: em dia, inadimplente até 30 dias, inadimplente 31–60 dias, inativo jurídico a partir de 61 dias. Inativo jurídico não é automaticamente cancelamento.

## Dados demonstrativos reconciliados
Usuário: João Silva, consultor comercial. Data de referência 13/09/2026, corte ilustrativo até 10/09/2026. Competência selecionada: setembro de 2026, ainda em apuração.

Carteira própria: 90 boletos recebidos totalizam R$ 20.000,00 e geram R$ 1.400,00 (7%). Doze boletos em atraso totalizam R$ 3.600,00; quatro boletos cancelados totalizam R$ 800,00. Total: 106 boletos. Os filtros e contagens devem reconciliar, sem inventar linhas que contradigam os totais.

Composição possível para dados navegáveis: 70 recebidos de R$ 200 + 20 recebidos de R$ 300; 12 atrasados de R$ 300; 4 cancelados de R$ 200. Em recebidos, comissões de R$ 14 e R$ 21 respectivamente. Atrasados e cancelados não geram comissão apurada. Separe situação do boleto e do associado.

Maria Oliveira, indicação direta: R$ 10.000,00 recebidos na carteira dela, 2% = R$ 200,00 para João. Comissão total de setembro: R$ 1.600,00. Pedro é segundo nível, fora da base comissionável de João. Não somar os R$ 10.000 da carteira de Maria aos R$ 20.000 da carteira própria.

Agosto de 2026: fechamento demonstrativo disponível para conferência. R$ 18.000 próprios × 7% = R$ 1.260; R$ 9.000 da indicada × 2% = R$ 180; total R$ 1.440. Fechamento de setembro ainda não liberado.

## Quatro áreas navegáveis
1. Visão geral: comissão apurada, composição própria/indicação, saúde da carteira, recentes recebimentos e ação para conferir agosto. Links e cartões levam ao respectivo detalhe.
2. Minha carteira: tabela por boleto com associado, placa, telefone fictício, vencimento, valor, status e comissão. Busca, filtros recebidos/atrasados/cancelados, paginação e detalhe lateral. Exibir intervalo da amostra e total real do filtro. Atraso até 30, 31–60 e jurídico distinguíveis. Sem botão que envie mensagens externas.
3. Comissões: separação 7% próprias e 2% indicação direta; demonstrativo por origem; explicação curta de um nível. Registro abre cálculo. A árvore pode ser consultada em detalhe, mas a tabela de origem deve prevalecer na leitura diária.
4. Fechamento: competência agosto, demonstrativo R$ 1.440, etapas de apuração/conferência/NF/provisionamento. Confirmar demonstrativo, escolher NF demonstrativa e simular envio devem mudar estado local sem sugerir operação real. Status provisionado diferente de pago. Setembro permanece em apuração.

## Regras ainda abertas — não inventar decisão definitiva
Dias úteis versus corridos em D-3, data de referência financeira, calendário de fechamento (inclusive dia 1 segunda-feira), estornos, pagamentos parciais e troca de titularidade. Na interface, detalhe discreto 'Regras do demonstrativo' informa que o corte D-3 é ilustrativo e o calendário final será validado. Não colocar jargão técnico ou avisos extensos na experiência principal.

## Identidade
Referência visual: [site oficial da APROVEC](https://aprovecbrasil.com.br/). Vermelho, preto e branco; títulos Inter e textos Poppins. Logo oficial enviada, sem substituições, em `mockup/assets/logo-aprovec.webp`, branca sobre vermelho. Fontes e imagem disponíveis localmente. Ver `direcao-visual.md` para as referências e sua aplicação.

## Verificação do mockup
Conferir visual no navegador, logo real, textos portugueses, aritmética, navegação, filtro, detalhe de comissão e fluxo de fechamento simulado. Não confundir leitura do HTML com inspeção visual.

## Ampliação: gestor e administrador

O gestor acompanha 204 boletos da operação, saúde consolidada, comissões por pessoa, árvore e estados demonstrativos de fechamento/provisionamento. O exemplo da página 5 do PDF resulta em R$ 350 para o gestor sobre R$ 35.000 pagos; o custo total de comissões é R$ 3.100.

O administrador cadastra participantes e indicador direto, edita vínculos sem permitir ciclos e configura os percentuais globais (7% / 2% / 1% inicialmente). O nível de indicação permanece limitado a um. A árvore permite seleção com detalhes e alternativa em lista. Cadastros e taxas persistem localmente; alterações recalculam setembro e preservam o histórico de agosto. Novos participantes entram com base zero.
