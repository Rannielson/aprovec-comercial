export type HostInfo =
  | { kind: 'tenant'; slug: string }
  | { kind: 'platform' }
  | { kind: 'unknown' };

export const PLATFORM_SUBDOMAIN = 'admin';

const SUBDOMAIN = /^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/;

export function parseHost(host: string | null | undefined, rootDomain: string): HostInfo {
  const normalizedHost = (host ?? '').trim().toLowerCase();
  const suffix = `.${rootDomain.trim().toLowerCase()}`;
  if (suffix === '.' || !normalizedHost.endsWith(suffix)) return { kind: 'unknown' };

  const subdomain = normalizedHost.slice(0, -suffix.length);
  if (!SUBDOMAIN.test(subdomain)) return { kind: 'unknown' };

  return subdomain === PLATFORM_SUBDOMAIN ? { kind: 'platform' } : { kind: 'tenant', slug: subdomain };
}

export function apiHostHeader(host: HostInfo): string | null {
  if (host.kind === 'tenant') return host.slug;
  if (host.kind === 'platform') return PLATFORM_SUBDOMAIN;
  return null;
}
