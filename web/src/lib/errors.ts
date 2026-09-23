const messages: Record<string, string> = {
  'auth.invalid_credentials': 'E-mail ou senha incorretos.',
  'auth.too_many_attempts': 'Muitas tentativas. Aguarde alguns minutos e tente de novo.',
  'auth.weak_password': 'A senha precisa ter entre 10 e 128 caracteres.',
  'auth.forbidden': 'Você não tem permissão para esta ação.',
  'auth.unauthenticated': 'Sua sessão expirou. Entre novamente.',
  'invite.invalid': 'Este link expirou ou já foi usado. Peça um novo.',
  'tenant.not_found': 'Empresa não encontrada.',
  'tenant.invalid_slug': 'Use só letras minúsculas, números e hífen no endereço.',
  'tenant.slug_taken': 'Esse endereço já está em uso.',
  'tenant.invalid_request': 'Preencha todos os campos.',
  'users.invalid_email': 'Informe um e-mail válido.',
  'plan.invalid_effective_from': 'Escolha o mês de início da vigência.',
};

export function messageFor(code: string): string {
  return messages[code] ?? 'Não foi possível concluir. Tente novamente.';
}
