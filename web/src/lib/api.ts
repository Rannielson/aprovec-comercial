import 'server-only';
import { cookies, headers } from 'next/headers';
import { buildApiHeaders } from './api-headers';
import { env } from './env';
import { parseHost, type HostInfo } from './host';
import { SESSION_COOKIE } from './session';

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
  ) {
    super(code);
    this.name = 'ApiError';
  }
}

type ApiRequest = {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
  token?: string | null;
};

export async function currentHost(): Promise<HostInfo> {
  const incoming = await headers();
  // Next.js re-renders the redirect target of a Server Action (e.g. after login) as part of
  // the action's own response, and in that inner render the raw `host` header reflects the
  // server's own bind address rather than the browser's request; `x-forwarded-host` still
  // carries the original value in that case, so prefer it when present.
  const host = incoming.get('x-forwarded-host') ?? incoming.get('host');
  return parseHost(host, env.ROOT_DOMAIN);
}

export async function apiFetch<T = void>(path: string, request: ApiRequest = {}): Promise<T> {
  const incoming = await headers();
  const host = await currentHost();
  if (host.kind === 'unknown') throw new ApiError(404, 'tenant.not_found');

  const token = request.token === undefined ? (await cookies()).get(SESSION_COOKIE)?.value : (request.token ?? undefined);

  let response: Response;
  try {
    response = await fetch(`${env.API_INTERNAL_URL}${path}`, {
      method: request.method ?? 'GET',
      headers: buildApiHeaders({
        host,
        internalKey: env.API_INTERNAL_KEY,
        token,
        forwardedFor: incoming.get('x-forwarded-for'),
        userAgent: incoming.get('user-agent'),
        hasBody: request.body !== undefined,
      }),
      body: request.body === undefined ? undefined : JSON.stringify(request.body),
      cache: 'no-store',
      signal: AbortSignal.timeout(10_000),
    });
  } catch {
    // The API is unreachable, the connection failed, or the request timed out — none of
    // these produce an HTTP response to read a `code` from, so map them to a generic
    // ApiError the same way call sites already handle every other API failure.
    throw new ApiError(503, 'internal.error');
  }

  const text = await response.text();
  let data: unknown;
  try {
    data = text ? JSON.parse(text) : undefined;
  } catch {
    // Non-JSON body (e.g. an upstream proxy error page) — same generic mapping as above.
    throw new ApiError(503, 'internal.error');
  }
  if (!response.ok) throw new ApiError(response.status, (data as { code?: string })?.code ?? 'internal.error');
  return data as T;
}
