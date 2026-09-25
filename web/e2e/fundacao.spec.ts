import { expect, test, type Page } from '@playwright/test';
import { latestLinkFor } from './emails';

const tenant = 'http://aprovec.localhost:3000';
const platform = 'http://admin.localhost:3000';
const password = 'senha-dev-123';

async function login(page: Page, baseUrl: string, email: string, secret = password) {
  await page.goto(`${baseUrl}/login`);
  await page.getByLabel('E-mail', { exact: true }).fill(email);
  await page.getByLabel('Senha', { exact: true }).fill(secret);
  await page.getByRole('button', { name: 'Entrar' }).click();
  // The button submits a server action that either redirects to `/` (success) or re-renders the
  // form with an inline error (failure). Wait for whichever happens before the caller moves on,
  // instead of racing the pending submission with the next step.
  await Promise.race([
    page.waitForURL(`${baseUrl}/`).catch(() => {}),
    page.locator('p.error[role="alert"]').waitFor({ state: 'visible' }).catch(() => {}),
  ]);
}

test('consultor entra, vê a comissão de setembro e sai', async ({ page }) => {
  await page.goto(`${tenant}/`);
  await expect(page).toHaveURL(`${tenant}/login`);

  await login(page, tenant, 'joao@aprovec.local');
  await expect(page.getByRole('complementary').getByText('João Silva')).toBeVisible();

  await page.goto(`${tenant}/?competencia=2026-09`);
  await expect(page.locator('.hero-number')).toHaveText(/R\$\s1\.600,00/);
  await expect(page.getByRole('cell', { name: 'Carteira própria' })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Supervisão · 1º nível' })).toBeVisible();

  await page.getByRole('button', { name: 'Sair' }).click();
  await expect(page).toHaveURL(`${tenant}/login`);
});

test('coordenação vê seu próprio total pessoal na faixa de comissão', async ({ page }) => {
  await login(page, tenant, 'coordenacao@aprovec.local');
  await page.goto(`${tenant}/?competencia=2026-09`);
  // A faixa mostra o total PESSOAL de quem está logado (own.total), não o agregado da equipe
  // (commissions.total, que era R$ 3.100,00). Como coordenadora ela só participa via a regra
  // global "Coordenação" (1% sobre a base de setembro de todos: R$ 35.000,00 => R$ 350,00) —
  // valor confirmado empiricamente contra a tabela detalhada da própria página.
  await expect(page.locator('.hero-number')).toHaveText(/R\$\s350,00/);
});

test('senha errada mostra a mensagem de erro', async ({ page }) => {
  await login(page, tenant, 'pedro@aprovec.local', 'senha-errada-123');
  // Scoped to the form's own error paragraph: Next.js also renders a hidden
  // `role="alert"` route announcer, which would otherwise make `getByRole('alert')` ambiguous.
  await expect(page.locator('p.error[role="alert"]')).toHaveText('E-mail ou senha incorretos.');
});

test('pedido de redefinição mostra sempre a mesma confirmação', async ({ page }) => {
  await page.goto(`${tenant}/redefinir-senha`);
  await page.getByLabel('E-mail', { exact: true }).fill('ninguem@aprovec.local');
  await page.getByRole('button', { name: 'Enviar link' }).click();
  await expect(page.getByRole('status')).toContainText('Se o e-mail estiver cadastrado');
});

test('plataforma cria empresa e a administradora define a senha pelo convite', async ({ page }) => {
  const slug = `e2e-${Date.now()}`;
  const adminEmail = `dona@${slug}.local`;

  await login(page, platform, 'admin@plataforma.local');
  await expect(page.getByRole('heading', { name: 'Empresas' })).toBeVisible();

  await page.getByLabel('Nome da empresa').fill('Associação E2E');
  await page.getByLabel('Endereço (subdomínio)').fill(slug);
  await page.getByLabel('Nome do administrador').fill('Dona E2E');
  await page.getByLabel('E-mail do administrador').fill(adminEmail);
  await page.getByRole('button', { name: 'Criar empresa' }).click();
  await expect(page.getByRole('status')).toContainText('Empresa criada');
  await expect(page.getByRole('cell', { name: slug })).toBeVisible();

  await page.goto(await latestLinkFor(adminEmail));
  await page.getByLabel('Nova senha').fill('senha-da-dona-1');
  await page.getByLabel('Confirme a senha').fill('senha-da-dona-1');
  await page.getByRole('button', { name: 'Salvar senha' }).click();

  await expect(page.getByRole('complementary').getByText('Dona E2E')).toBeVisible();
  // The tenant home page no longer displays the company name anywhere (the
  // app shell dropped that eyebrow in favor of a static page heading), so
  // confirm we landed on the new tenant's own subdomain instead.
  await expect(page).toHaveURL(`http://${slug}.localhost:3000/`);
});

test('vendedor abre a carteira pela sidebar e filtra por situação', async ({ page }) => {
  await login(page, tenant, 'joao@aprovec.local');
  await page.getByRole('link', { name: 'Minha carteira' }).click();
  await expect(page).toHaveURL(`${tenant}/carteira`);
  await expect(page.getByRole('heading', { name: 'Minha carteira' })).toBeVisible();

  // O rótulo e a contagem são elementos adjacentes sem espaço no texto-fonte (mesma
  // estrutura do mockup) — o nome acessível concatenado não tem espaço entre eles, então
  // usamos `hasText` (substring) em vez de `getByRole(... { name })` (nome exato).
  await page.locator('.filter-tab', { hasText: 'Cancelados' }).click();
  // A página 1 nunca aparece na URL (o código só inclui `page` quando é diferente de 1).
  await expect(page).toHaveURL(`${tenant}/carteira?status=cancelado`);
  await expect(page.getByText('4 boletos no filtro')).toBeVisible();
});

test('admin configura a Hinova e vincula um voluntário', async ({ page }) => {
  await login(page, tenant, 'admin@aprovec.local');
  await page.getByRole('link', { name: 'Configurações' }).click();
  await expect(page).toHaveURL(`${tenant}/configuracoes/integracoes`);

  await page.getByLabel('Usuário', { exact: true }).fill('usuario-dev');
  await page.getByLabel('Senha').fill('senha-dev');
  await page.getByLabel('Token da SGA').fill('token-dev');
  await page.getByRole('button', { name: 'Salvar credenciais' }).click();
  await expect(page.getByText('Credenciais salvas.')).toBeVisible();

  await page.getByLabel('Buscar voluntário por nome').fill('Ana Paula');
  await page.getByLabel('Buscar voluntário por nome').press('Enter');
  await expect(page.getByRole('cell', { name: 'Ana Paula Ferreira' })).toBeVisible();

  await page.getByLabel('Usuário APROVEC').selectOption({ label: 'João Silva' });
  await page.getByRole('button', { name: 'Vincular' }).click();
  await expect(page.getByText('Voluntário vinculado.')).toBeVisible();
  const vinculo = page.getByRole('row', { name: /João Silva.*Ana Paula Ferreira/ });
  await expect(vinculo).toBeVisible();

  // Desvincula no final para deixar o voluntário seed livre de novo: o teste roda contra
  // um banco de dev persistente (prepare.mjs só semeia uma vez), então sem isso uma segunda
  // execução encontraria "Ana Paula Ferreira" duplicada (na busca e em Vínculos atuais) e
  // falharia por violação do modo estrito — o mesmo motivo pelo qual o teste de "plataforma
  // cria empresa" usa um slug único por execução.
  await vinculo.getByRole('button', { name: 'Desvincular' }).click();
  await expect(vinculo).not.toBeVisible();
});

test('admin monta uma árvore comissionada buscando um voluntário na Hinova', async ({ page }) => {
  await login(page, tenant, 'admin@aprovec.local');

  // Garante que as credenciais da Hinova estão salvas: o teste anterior já as salva nesta mesma
  // execução, mas salvar de novo aqui é idempotente (mesmas credenciais de dev) e deixa este teste
  // independente da ordem de execução do arquivo.
  await page.getByRole('link', { name: 'Configurações' }).click();
  await expect(page).toHaveURL(`${tenant}/configuracoes/integracoes`);
  await page.getByLabel('Usuário', { exact: true }).fill('usuario-dev');
  await page.getByLabel('Senha').fill('senha-dev');
  await page.getByLabel('Token da SGA').fill('token-dev');
  await page.getByRole('button', { name: 'Salvar credenciais' }).click();
  await expect(page.getByText('Credenciais salvas.')).toBeVisible();

  await page.getByRole('link', { name: 'Árvore comissionada' }).click();
  await expect(page).toHaveURL(`${tenant}/administracao/arvore`);

  await page.getByRole('link', { name: 'Nova árvore' }).click();
  await expect(page).toHaveURL(`${tenant}/administracao/arvore?adicionar=nova`);

  await page.getByLabel('Buscar voluntário por nome').fill('Bruno');
  await page.getByLabel('Buscar voluntário por nome').press('Enter');
  // Escopado à classe do botão de resultado da busca (não a role/name genérico): sem isso, numa
  // segunda execução o "Bruno Costa Lima" já criado no round anterior também aparece como um nó
  // da pirâmide com um botão "Adicionar indicado a Bruno Costa Lima" (aria-label contém o mesmo
  // texto), e o locator por nome vira ambíguo (strict mode).
  await page.locator('.participante-option', { hasText: 'Bruno Costa Lima' }).click();

  // O formulário da Hinova nem sempre traz e-mail: o admin sempre digita um, aqui como lá.
  // E-mail único por execução: sem isso, uma segunda rodada contra o mesmo banco de dev
  // persistente encontraria o e-mail já cadastrado (POST /users volta 409 users.email_taken,
  // já que não existe exclusão de usuário, só desativação) — mesmo motivo pelo qual o teste de
  // "plataforma cria empresa" usa um slug único por execução.
  const brunoEmail = `bruno-${Date.now()}@e2e.aprovec.local`;
  await page.getByLabel('E-mail').fill(brunoEmail);
  await page.getByRole('button', { name: 'Confirmar' }).click();
  await expect(page.getByText('Participante adicionado.')).toBeVisible();
  // `.last()`: como não existe exclusão de usuário, uma execução anterior pode ter deixado outro
  // cartão "Bruno Costa Lima" na árvore (só o vínculo com a Hinova é desfeito ao final, não o
  // participante) — o recém-criado é o que importa aqui, e é o mais novo.
  await expect(page.locator('.pyramid-node', { hasText: 'Bruno Costa Lima' }).last()).toBeVisible();

  await page.getByRole('link', { name: 'Participantes' }).click();
  await expect(page).toHaveURL(`${tenant}/administracao/participantes`);
  // Por e-mail (único nesta execução), não por nome: outra execução pode ter deixado uma linha
  // "Bruno Costa Lima" anterior na tabela, e o nome sozinho seria ambíguo.
  const linha = page.getByRole('row', { name: brunoEmail });
  await expect(linha).toBeVisible();
  // Era uma nova raiz (sem indicador), então a coluna Supervisor mostra o rótulo de "sem vínculo".
  await expect(linha).toContainText('Sem indicador');

  // Desvincula o voluntário da Hinova no final para deixar o código 102 livre de novo: o e-mail
  // único acima evita o 409 de e-mail duplicado, mas sozinho ainda deixaria "Bruno Costa Lima"
  // marcado como já vinculado na busca (botão desabilitado) numa segunda execução — mesmo padrão
  // de limpeza do teste "admin configura a Hinova e vincula um voluntário", acima.
  await page.getByRole('link', { name: 'Configurações' }).click();
  await expect(page).toHaveURL(`${tenant}/configuracoes/integracoes`);
  // Escopado à seção "Vínculos atuais": sem query digitada, a seção "Mapeamento de voluntários"
  // desta mesma página sempre lista os 3 voluntários fixos da Hinova (Ana Paula, Bruno, Carla),
  // então uma linha "Bruno Costa Lima" também aparece ali — sem o escopo, o locator por nome vira
  // ambíguo entre as duas seções.
  const vinculosAtuais = page.locator('section', { has: page.getByRole('heading', { name: 'Vínculos atuais' }) });
  const vinculo = vinculosAtuais.getByRole('row', { name: /Bruno Costa Lima/ });
  await expect(vinculo).toBeVisible();
  await vinculo.getByRole('button', { name: 'Desvincular' }).click();
  await expect(vinculo).not.toBeVisible();
});

test('indicação: gerar link, preencher formulário, aprovar e logar', async ({ page }) => {
  const cpf = `1112223${Date.now() % 10000}`;
  const email = `indicado-${Date.now()}@e2e.aprovec.local`;

  // Vincula Pedro Santos ao voluntário 103 da Hinova (fake de dev) para ele poder gerar um link.
  await login(page, tenant, 'admin@aprovec.local');
  await page.getByRole('link', { name: 'Configurações' }).click();
  await page.getByLabel('Usuário', { exact: true }).fill('usuario-dev');
  await page.getByLabel('Senha').fill('senha-dev');
  await page.getByLabel('Token da SGA').fill('token-dev');
  await page.getByRole('button', { name: 'Salvar credenciais' }).click();
  await expect(page.getByText('Credenciais salvas.')).toBeVisible();
  await page.getByLabel('Buscar voluntário por nome').fill('Carla');
  await page.getByLabel('Buscar voluntário por nome').press('Enter');
  await page.getByLabel('Usuário APROVEC').selectOption({ label: 'Pedro Santos' });
  await page.getByRole('button', { name: 'Vincular' }).click();
  await expect(page.getByText('Voluntário vinculado.')).toBeVisible();

  // Pedro pega o próprio link de indicação.
  await login(page, tenant, 'pedro@aprovec.local');
  await expect(page.getByLabel('Seu link de indicação')).toBeVisible();
  const link = await page.getByLabel('Seu link de indicação').inputValue();

  // Um visitante (sem sessão) abre o link e preenche o formulário.
  await page.goto(link);
  await expect(page.getByRole('heading', { name: 'Você foi indicado por Pedro Santos' })).toBeVisible();
  await page.getByLabel('Nome completo').fill('Indicado E2E');
  await page.getByLabel('CPF').fill(cpf);
  await page.getByLabel('Celular').fill('11999990000');
  await page.getByLabel('E-mail').fill(email);
  await page.getByLabel('CEP').fill('01310-100');
  await page.getByLabel('Número').fill('100');
  await page.getByLabel('Bairro').fill('Bela Vista');
  await page.getByLabel('Cidade').fill('São Paulo');
  await page.getByLabel('Estado').fill('SP');
  await page.getByRole('button', { name: 'Solicitar cadastro' }).click();
  await expect(page.getByText('Solicitação enviada!')).toBeVisible();

  // O administrador aprova.
  await login(page, tenant, 'admin@aprovec.local');
  await page.getByRole('link', { name: 'Convites' }).click();
  await expect(page).toHaveURL(`${tenant}/administracao/convites`);
  const linha = page.getByRole('row', { name: new RegExp(cpf) });
  await expect(linha).toBeVisible();
  await expect(linha).toContainText('Pedro Santos');
  await linha.getByRole('button', { name: 'Aprovar' }).click();
  // `SolicitacaoActions` (administracao/convites/solicitacao-actions.tsx) só renderiza
  // `approveState.error`, nunca `approveState.message` -- diferente de todos os outros
  // formulários de ação deste app (credenciais-form, mapeamento-form, indicacao-form, etc.),
  // que sempre mostram `state.message` em caso de sucesso. Não há nenhum texto de confirmação
  // visível após aprovar; o sinal observável de sucesso é a linha sumir da fila (a action chama
  // `revalidatePath('/administracao/convites')`, que remove a solicitação já aprovada da lista).
  await expect(linha).not.toBeVisible();

  // O novo participante recebe o e-mail e consegue definir a senha e logar.
  await page.goto(await latestLinkFor(email));
  await page.getByLabel('Nova senha').fill('senha-do-indicado-1');
  await page.getByLabel('Confirme a senha').fill('senha-do-indicado-1');
  await page.getByRole('button', { name: 'Salvar senha' }).click();
  await expect(page.getByRole('complementary').getByText('Indicado E2E')).toBeVisible();

  // Limpeza: desvincula Pedro da Hinova para deixar o voluntário 103 livre de novo (mesmo padrão
  // de limpeza dos outros testes deste arquivo que usam o banco de dev persistente).
  await page.goto(`${tenant}/configuracoes/integracoes`);
  const vinculosAtuaisPedro = page.locator('section', { has: page.getByRole('heading', { name: 'Vínculos atuais' }) });
  const vinculoPedro = vinculosAtuaisPedro.getByRole('row', { name: /Pedro Santos/ });
  await vinculoPedro.getByRole('button', { name: 'Desvincular' }).click();
  await expect(vinculoPedro).not.toBeVisible();
});
