import { describe, expect, it } from 'vitest';
import { messageFor } from './errors';

describe('messageFor', () => {
  it('traduz códigos conhecidos', () => {
    expect(messageFor('auth.invalid_credentials')).toBe('E-mail ou senha incorretos.');
    expect(messageFor('invite.invalid')).toBe('Este link expirou ou já foi usado. Peça um novo.');
  });

  it('tem uma mensagem genérica para o resto', () => {
    expect(messageFor('qualquer.coisa')).toBe('Não foi possível concluir. Tente novamente.');
  });
});
