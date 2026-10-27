import { apiFetch } from '@/lib/api';

export async function FeeReview({ month }: { month: string }) {
  const pending = await apiFetch<{ count: number }>(`/commissions/${month}/pending-fees`);
  if (!pending.count) return null;
  return (
    <p className="wallet-alert" role="status">
      {pending.count} {pending.count === 1 ? 'boleto pago está' : 'boletos pagos estão'} com tarifa bancária pendente de conferência.
      {' '}Esses pagamentos ainda não compõem a apuração líquida. Fechamentos já confirmados preservam os valores registrados.
      {' '}<a href={`/carteira?status=recebido&tarifas=pendentes&competencia=${month}`}>Conferir na carteira</a>
    </p>
  );
}
