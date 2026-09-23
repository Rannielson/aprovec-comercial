import { describe, expect, it } from 'vitest';
import { apiHostHeader, parseHost } from './host';

describe('parseHost', () => {
  it('reconhece a empresa pelo subdomínio', () => {
    expect(parseHost('aprovec.localhost:3000', 'localhost:3000')).toEqual({ kind: 'tenant', slug: 'aprovec' });
    expect(parseHost('Aprovec.Plataforma.com.br', 'plataforma.com.br')).toEqual({ kind: 'tenant', slug: 'aprovec' });
  });

  it('reconhece a plataforma', () => {
    expect(parseHost('admin.localhost:3000', 'localhost:3000')).toEqual({ kind: 'platform' });
  });

  it.each([
    ['localhost:3000'],
    ['a.b.localhost:3000'],
    ['aprovec.outrodominio.com'],
    ['-x.localhost:3000'],
    [''],
    [null],
  ])('trata %s como desconhecido', (host) => {
    expect(parseHost(host, 'localhost:3000')).toEqual({ kind: 'unknown' });
  });
});

describe('apiHostHeader', () => {
  it('envia o slug ou admin', () => {
    expect(apiHostHeader({ kind: 'tenant', slug: 'aprovec' })).toBe('aprovec');
    expect(apiHostHeader({ kind: 'platform' })).toBe('admin');
    expect(apiHostHeader({ kind: 'unknown' })).toBeNull();
  });
});
