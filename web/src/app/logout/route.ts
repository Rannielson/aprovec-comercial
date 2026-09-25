import { apiFetch, currentHost } from '@/lib/api';
import { env } from '@/lib/env';
import { PLATFORM_SUBDOMAIN } from '@/lib/host';
import { clearSession } from '@/lib/session';

export async function POST(request: Request) {
  // Reject cross-site logout POSTs (e.g. an auto-submitting form on another site forcing a
  // visitor's session to be logged out). Browsers always send Origin on a cross-site POST;
  // same-origin requests from this app may omit it, so only reject when an Origin is present
  // and it doesn't match the (validated) host the request claims to be for.
  const origin = request.headers.get('origin');
  if (origin) {
    const host = await currentHost();
    const expectedHost =
      host.kind === 'tenant'
        ? `${host.slug}.${env.ROOT_DOMAIN}`
        : host.kind === 'platform'
          ? `${PLATFORM_SUBDOMAIN}.${env.ROOT_DOMAIN}`
          : null;
    let originHost: string | null;
    try {
      originHost = new URL(origin).host.toLowerCase();
    } catch {
      originHost = null;
    }
    if (!expectedHost || originHost !== expectedHost.toLowerCase()) {
      return new Response(null, { status: 400 });
    }
  }

  await apiFetch('/auth/logout', { method: 'POST' }).catch(() => undefined);
  await clearSession();
  // A relative Location is resolved by the browser against whatever host it actually used,
  // so there's no host-building logic needed here (this also removes the open-redirect
  // surface entirely, superseding the previous host-reconstruction approach).
  return new Response(null, { status: 303, headers: { Location: '/login' } });
}
