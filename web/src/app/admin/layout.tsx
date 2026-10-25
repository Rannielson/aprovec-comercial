import { notFound } from 'next/navigation';
import { currentHost } from '@/lib/api';

export default async function AdminLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  const host = await currentHost();
  if (host.kind !== 'tenant' || host.slug !== 'sales') notFound();
  return children;
}
