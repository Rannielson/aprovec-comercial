import { cookies } from 'next/headers';
import { notFound, redirect } from 'next/navigation';
import { ApiError, currentHost } from '@/lib/api';
import { SESSION_COOKIE } from '@/lib/session';
import { PlatformHome } from './platform-home';

export default async function HomePage() {
  const host = await currentHost();
  if (host.kind === 'unknown') notFound();
  if (!(await cookies()).has(SESSION_COOKIE)) redirect('/login');
  if (host.kind === 'tenant') redirect('/fechamento');

  try {
    return await PlatformHome();
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) redirect('/login');
    if (error instanceof ApiError && error.status === 404) notFound();
    throw error;
  }
}
