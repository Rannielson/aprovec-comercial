import { cookies } from 'next/headers';
import { notFound, redirect } from 'next/navigation';
import { ApiError, currentHost } from '@/lib/api';
import { SESSION_COOKIE } from '@/lib/session';
import { PlatformHome } from './platform-home';
import { TenantHome } from './tenant-home';

export default async function HomePage({ searchParams }: { searchParams: Promise<{ competencia?: string }> }) {
  const host = await currentHost();
  if (host.kind === 'unknown') notFound();
  if (!(await cookies()).has(SESSION_COOKIE)) redirect('/login');

  const { competencia } = await searchParams;
  let content: React.ReactNode;
  try {
    content = host.kind === 'platform' ? await PlatformHome() : await TenantHome({ competencia });
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) redirect('/login');
    throw error;
  }
  return content;
}
