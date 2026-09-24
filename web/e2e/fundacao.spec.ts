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
