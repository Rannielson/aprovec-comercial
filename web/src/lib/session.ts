import 'server-only';
import { cookies } from 'next/headers';

export const SESSION_COOKIE = 'sid';

function secureCookies(): boolean {
  const configured = process.env.COOKIE_SECURE;
  return configured ? configured === 'true' : process.env.NODE_ENV === 'production';
}

export async function setSession(token: string, absoluteExpiresAt: string): Promise<void> {
  (await cookies()).set(SESSION_COOKIE, token, {
    httpOnly: true,
    secure: secureCookies(),
    sameSite: 'lax',
    path: '/',
    expires: new Date(absoluteExpiresAt),
  });
}

export async function clearSession(): Promise<void> {
  (await cookies()).delete(SESSION_COOKIE);
}
