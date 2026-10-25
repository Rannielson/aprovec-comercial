import { cookies } from 'next/headers';
import { redirect } from 'next/navigation';
import { ApiError } from '@/lib/api';
import { PLATFORM_SESSION_COOKIE } from '@/lib/session';
import { PlatformHome } from '../platform-home';

export default async function AdminPage() {
  if (!(await cookies()).has(PLATFORM_SESSION_COOKIE)) redirect('/admin/login');
  try {
    return await PlatformHome({ adminPath: true });
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) redirect('/admin/login');
    throw error;
  }
}
