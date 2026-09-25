/**
 * `?adicionar=` value (and open-panel state) for the "Nova árvore" placeholder.
 *
 * Lives in its own plain module (no `'use client'`) so both `page.tsx` (a Server Component) and
 * `pyramid.tsx` (a Client Component) can import the same string safely. Exporting it from
 * `pyramid.tsx` instead makes it a client reference once a Server Component imports it: template
 * interpolation and `===` comparisons against it then break at runtime (confirmed against the
 * "Nova árvore" link, whose href became the RSC error text instead of `adicionar=nova`).
 */
export const NOVA_ARVORE = 'nova';
