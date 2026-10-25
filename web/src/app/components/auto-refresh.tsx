'use client';

import { useEffect, useTransition } from 'react';
import { useRouter } from 'next/navigation';

export function AutoRefresh() {
  const router = useRouter();
  const [pending, startTransition] = useTransition();

  useEffect(() => {
    function refresh() {
      if (pending || document.visibilityState !== 'visible' || !navigator.onLine) return;
      if (document.activeElement?.matches('input, textarea, select, [contenteditable="true"]')) return;
      startTransition(() => router.refresh());
    }

    let afterEditing: ReturnType<typeof setTimeout> | undefined;
    function resumeAfterEditing() {
      clearTimeout(afterEditing);
      afterEditing = setTimeout(refresh, 0);
    }

    const interval = setInterval(refresh, 30_000);
    document.addEventListener('visibilitychange', refresh);
    document.addEventListener('focusout', resumeAfterEditing);
    window.addEventListener('focus', refresh);
    window.addEventListener('online', refresh);
    return () => {
      clearInterval(interval);
      clearTimeout(afterEditing);
      document.removeEventListener('visibilitychange', refresh);
      document.removeEventListener('focusout', resumeAfterEditing);
      window.removeEventListener('focus', refresh);
      window.removeEventListener('online', refresh);
    };
  }, [router, pending, startTransition]);

  return null;
}
