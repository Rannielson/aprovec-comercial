# Gestor e administração da recorrência

Objetivo: ampliar o mockup local com gestão consolidada e cadastro da árvore remunerada, preservando a identidade APROVEC e o exemplo da página 5 do PDF.

## Escopo e decisões
- Seletor de perfis demonstrativos: vendedor, gestor e administrador. Não representa autenticação.
- Base inicial: João → Maria → Pedro, recebimentos de R$ 20 mil / R$ 10 mil / R$ 5 mil. Gestor com R$ 350; remuneração total de R$ 3.100.
- Gestão global é um vínculo distinto da indicação, válida apenas no primeiro nível.
- Admin cadastra e edita participantes e indicador direto, consulta árvore/lista e configura 7% / 2% / 1%.
- Alterações simuladas recalculam setembro; agosto é um demonstrativo histórico fixo. Cadastros e taxas persistem somente neste navegador.
- Novos participantes começam sem recebimentos. Impedir autorreferência, ciclos, e-mail duplicado e taxas fora de 0–100 ou cuja soma ultrapasse 100.

## Implementação
1. `mockup/network.js`: modelo independente para participantes, boletos, taxas, cálculos e validações. `tests/network.test.cjs` verifica exemplo do PDF, limite de um nível, 1% global sem duplicar base, validações e persistência.
2. `mockup/network-ui.js`: páginas do gestor (resumo, boletos, comissões por pessoa, árvore) e admin (árvore, participantes, remuneração), formulários e detalhes.
3. `mockup/network.css`: componentes e gráfico responsivo na identidade existente. `mockup/app.js` recebe seletor de perfil, navegação contextual e cálculos atuais do vendedor.
4. Conferir navegador desktop/celular, cadastro/edição/ciclos, taxas, persistência, troca de perfis e regressão do vendedor. Atualizar documentação.
