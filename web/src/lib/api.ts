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
  return parseHost((await headers()).get('host'), env.ROOT_DOMAIN);
}

export async function apiFetch<T = void>(path: string, request: ApiRequest = {}): Promise<T> {
  const incoming = await headers();
  const host = parseHost(incoming.get('host'), env.ROOT_DOMAIN);
  if (host.kind === 'unknown') throw new ApiError(404, 'tenant.not_found');

  const token = request.token === undefined ? (await cookies()).get(SESSION_COOKIE)?.value : (request.token ?? undefined);
  const response = await fetch(`${env.API_INTERNAL_URL}${path}`, {
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
  });

  const text = await response.text();
  const data = text ? JSON.parse(text) : undefined;
  if (!response.ok) throw new ApiError(response.status, data?.code ?? 'internal.error');
  return data as T;
}
