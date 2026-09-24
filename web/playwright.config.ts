import { defineConfig, devices } from '@playwright/test';

const webEnv = {
  API_INTERNAL_URL: 'http://127.0.0.1:5080',
  API_INTERNAL_KEY: 'chave-interna-de-desenvolvimento-32+',
  ROOT_DOMAIN: 'localhost:3000',
  COOKIE_SECURE: 'false',
};

export default defineConfig({
  testDir: './e2e',
  workers: 1,
  expect: { timeout: 15_000 },
  use: { ...devices['Desktop Chrome'], trace: 'retain-on-failure' },
  webServer: [
    {
      command: 'dotnet run --no-build --launch-profile http',
      cwd: '../src/Recorrencia.Api',
      url: 'http://127.0.0.1:5080/health',
      reuseExistingServer: true,
      timeout: 120_000,
    },
    {
      command: 'npm run dev',
      url: 'http://127.0.0.1:3000/api/health',
      reuseExistingServer: true,
      timeout: 120_000,
      env: webEnv,
    },
  ],
});
