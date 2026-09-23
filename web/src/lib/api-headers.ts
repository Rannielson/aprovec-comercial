import { apiHostHeader, type HostInfo } from './host';

export type ApiHeaderInput = {
  host: HostInfo;
  internalKey: string;
  token?: string;
  forwardedFor?: string | null;
  userAgent?: string | null;
  hasBody: boolean;
};

export function clientIpFrom(forwardedFor: string | null | undefined): string | undefined {
  const last = forwardedFor?.split(',').map((part) => part.trim()).filter(Boolean).at(-1);
  return last || undefined;
}

export function buildApiHeaders(input: ApiHeaderInput): Record<string, string> {
  const tenantHost = apiHostHeader(input.host);
  if (!tenantHost) throw new Error('Host desconhecido.');

  const headers: Record<string, string> = {
    Accept: 'application/json',
    'X-Internal-Key': input.internalKey,
    'X-Tenant-Host': tenantHost,
  };
  const ip = clientIpFrom(input.forwardedFor);
  if (ip) headers['X-Client-Ip'] = ip;
  if (input.userAgent) headers['X-Client-User-Agent'] = input.userAgent;
  if (input.token) headers.Authorization = `Bearer ${input.token}`;
  if (input.hasBody) headers['Content-Type'] = 'application/json';
  return headers;
}
