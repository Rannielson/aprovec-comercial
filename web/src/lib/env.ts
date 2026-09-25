import 'server-only';

function required(name: string): string {
  const value = process.env[name];
  if (!value) throw new Error(`Variável de ambiente ${name} não definida.`);
  return value;
}

export const env = {
  get API_INTERNAL_URL() {
    return required('API_INTERNAL_URL');
  },
  get API_INTERNAL_KEY() {
    return required('API_INTERNAL_KEY');
  },
  get ROOT_DOMAIN() {
    return required('ROOT_DOMAIN');
  },
};
