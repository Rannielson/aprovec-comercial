import { cookies } from 'next/headers';
import { redirect } from 'next/navigation';
import { SESSION_COOKIE } from '@/lib/session';

export default async function AdministracaoLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  if (!(await cookies()).has(SESSION_COOKIE)) redirect('/login');
  return children;
}
