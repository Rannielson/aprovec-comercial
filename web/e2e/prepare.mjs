import { execSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');

function loadDotEnv() {
  const file = [path.join(root, '.env'), path.join(root, '.env.example')].find((f) => existsSync(f));
  return Object.fromEntries(
    readFileSync(file, 'utf8')
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line && !line.startsWith('#') && line.includes('='))
      .map((line) => [line.slice(0, line.indexOf('=')), line.slice(line.indexOf('=') + 1)]),
  );
}

const env = { ...process.env, ...loadDotEnv() };
const run = (command, cwd = root) => execSync(command, { cwd, stdio: 'inherit', env });

run('docker compose up -d --wait db');
run('dotnet run --project src/Recorrencia.Db -- bootstrap');
run('dotnet run --project src/Recorrencia.Db -- migrate');
run('dotnet build src/Recorrencia.Api');
run('dotnet run --no-build --launch-profile http -- seed-dev', path.join(root, 'src/Recorrencia.Api'));
