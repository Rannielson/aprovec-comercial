import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';

const outbox = path.resolve(__dirname, '../../tmp/emails');

export async function latestLinkFor(email: string, timeoutMs = 10_000): Promise<string> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const files = (await readdir(outbox).catch(() => [] as string[])).sort().reverse();
    for (const file of files) {
      const content = await readFile(path.join(outbox, file), 'utf8');
      const link = content.match(/https?:\/\/\S+\/definir-senha\/[A-Za-z0-9_-]+/);
      if (content.includes(`Para: ${email}`) && link) return link[0];
    }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  throw new Error(`Nenhum e-mail com link para ${email}.`);
}
