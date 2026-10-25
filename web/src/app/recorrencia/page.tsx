import { cookies } from 'next/headers';
import { notFound, redirect } from 'next/navigation';
import { ApiError, currentHost } from '@/lib/api';
import { SESSION_COOKIE } from '@/lib/session';
import { TenantHome } from '../tenant-home';

export default async function RecorrenciaPage({ searchParams }: { searchParams: Promise<{ competencia?: string }> }) {
  const host = await currentHost();
  if (host.kind !== 'tenant') notFound();
  if (!(await cookies()).has(SESSION_COOKIE)) redirect('/login');

  const { competencia } = await searchParams;
  try {
    return await TenantHome({ competencia });
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) redirect('/login');
    if (error instanceof ApiError && error.status === 404) notFound();
    throw error;
  }
}
