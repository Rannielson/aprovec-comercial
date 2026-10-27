import { ApiError, apiFetch } from '@/lib/api';

export async function GET(_request: Request, { params }: { params: Promise<{ id: string }> }) {
  const { id } = await params;
  try {
    const { telefone } = await apiFetch<{ telefone: string }>(`/carteira/${encodeURIComponent(id)}/whatsapp`, { timeoutMs: 25_000 });
    if (!/^55\d{10,11}$/.test(telefone)) throw new Error('Contato inválido da Hinova');
    return new Response(null, {
      status: 302,
      headers: { Location: `https://wa.me/${telefone}`, 'Cache-Control': 'no-store' },
    });
  } catch (error) {
    if (error instanceof ApiError && error.status === 401)
      return new Response(null, { status: 303, headers: { Location: '/login' } });
    if (!(error instanceof ApiError) && !(error instanceof Error)) throw error;
    const aviso = error instanceof ApiError && error.status === 409 ? 'cobranca-bloqueada' : 'verificacao-indisponivel';
    return new Response(null, { status: 303, headers: { Location: `/carteira?aviso=${aviso}`, 'Cache-Control': 'no-store' } });
  }
}
