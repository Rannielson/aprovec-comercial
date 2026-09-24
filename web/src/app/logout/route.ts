import { NextResponse } from 'next/server';
import { apiFetch } from '@/lib/api';
import { clearSession } from '@/lib/session';

export async function POST(request: Request) {
  await apiFetch('/auth/logout', { method: 'POST' }).catch(() => undefined);
  await clearSession();
  // request.url reports the server's own bind address, not the tenant subdomain the
  // browser actually requested (Next.js does not trust the Host header for this by
  // default). Rebuild the redirect target from the Host header, same as currentHost()
  // does in `@/lib/api`, so multi-tenant subdomains redirect back to themselves.
  const url = new URL('/login', request.url);
  const host = request.headers.get('host');
  if (host) url.host = host;
  return NextResponse.redirect(url, 303);
}
