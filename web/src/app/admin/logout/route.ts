import { currentHost, platformApiFetch } from '@/lib/api';
import { env } from '@/lib/env';
import { clearPlatformSession } from '@/lib/session';

export async function POST(request: Request) {
  const host = await currentHost();
  if (host.kind !== 'tenant' || host.slug !== 'sales') return new Response(null, { status: 404 });

  const origin = request.headers.get('origin');
  if (origin) {
    try {
      if (new URL(origin).host.toLowerCase() !== `sales.${env.ROOT_DOMAIN}`.toLowerCase()) {
        return new Response(null, { status: 400 });
      }
    } catch {
      return new Response(null, { status: 400 });
    }
  }

  await platformApiFetch('/auth/logout', { method: 'POST' }).catch(() => undefined);
  await clearPlatformSession();
  return new Response(null, { status: 303, headers: { Location: '/admin/login' } });
}
