import { NextResponse } from 'next/server';
import { apiFetch, currentHost } from '@/lib/api';
import { env } from '@/lib/env';
import { PLATFORM_SUBDOMAIN } from '@/lib/host';
import { clearSession } from '@/lib/session';

export async function POST(request: Request) {
  await apiFetch('/auth/logout', { method: 'POST' }).catch(() => undefined);
  await clearSession();
  // request.url reports the server's own bind address, not the tenant subdomain the
  // browser actually requested (Next.js does not trust the Host header for this by
  // default). Rebuild the redirect target from currentHost(), which validates the
  // requested host against ROOT_DOMAIN the same way every other route does, instead
  // of trusting the raw Host header directly (which would allow an open redirect).
  const host = await currentHost();
  const url = new URL('/login', request.url);
  if (host.kind === 'tenant') url.host = `${host.slug}.${env.ROOT_DOMAIN}`;
  else if (host.kind === 'platform') url.host = `${PLATFORM_SUBDOMAIN}.${env.ROOT_DOMAIN}`;
  return NextResponse.redirect(url, 303);
}
