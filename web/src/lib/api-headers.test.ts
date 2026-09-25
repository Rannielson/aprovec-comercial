import { describe, expect, it } from 'vitest';
import { buildApiHeaders, clientIpFrom } from './api-headers';

describe('clientIpFrom', () => {
  it('usa o último endereço, que é o acrescentado pelo proxy', () => {
    expect(clientIpFrom('1.1.1.1, 203.0.113.9')).toBe('203.0.113.9');
    expect(clientIpFrom('203.0.113.9')).toBe('203.0.113.9');
    expect(clientIpFrom('')).toBeUndefined();
    expect(clientIpFrom(null)).toBeUndefined();
  });
});

describe('buildApiHeaders', () => {
  it('monta os cabeçalhos da empresa com sessão e corpo', () => {
    expect(
      buildApiHeaders({
        host: { kind: 'tenant', slug: 'aprovec' },
        internalKey: 'chave',
        token: 'tok',
        forwardedFor: '203.0.113.9',
        userAgent: 'Navegador',
        hasBody: true,
      }),
    ).toEqual({
      Accept: 'application/json',
      'X-Internal-Key': 'chave',
      'X-Tenant-Host': 'aprovec',
      'X-Client-Ip': '203.0.113.9',
      'X-Client-User-Agent': 'Navegador',
      Authorization: 'Bearer tok',
      'Content-Type': 'application/json',
    });
  });

  it('omite o que não existe', () => {
    expect(buildApiHeaders({ host: { kind: 'platform' }, internalKey: 'chave', hasBody: false })).toEqual({
      Accept: 'application/json',
      'X-Internal-Key': 'chave',
      'X-Tenant-Host': 'admin',
    });
  });

  it('recusa host desconhecido', () => {
    expect(() => buildApiHeaders({ host: { kind: 'unknown' }, internalKey: 'chave', hasBody: false })).toThrow();
  });
});
