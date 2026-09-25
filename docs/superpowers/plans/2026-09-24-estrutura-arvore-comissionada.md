# Estrutura — Árvore comissionada Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a tenant admin build the commissioned tree (who supervises whom) manually, adding people by searching the existing Hinova voluntário list — matching the approved mockup's "Árvore comissionada" and "Participantes" screens.

**Architecture:** Almost entirely frontend. `POST /users` gains three optional fields so creating a participant and linking their Hinova code happens atomically in one transaction. The tree's visual layout is a pure TypeScript port of `mockup/tree-layout.js`. Two new pages combine three already-existing, already-tested endpoints (`GET /users`, `GET /commissions/{competencia}`, `GET /integracoes/hinova/voluntarios`).

**Tech Stack:** C# ASP.NET Core minimal APIs, Dapper/Npgsql, Next.js App Router (Server Components + Server Actions), xUnit, Vitest.

**Spec:** `docs/superpowers/specs/2026-09-24-estrutura-arvore-comissionada-design.md`

## Global Constraints

- No migration in this plan — every table already exists.
- The tree has no depth limit. Commission stays limited to the direct supervisor (already correct, unchanged).
- Adding someone to the tree is always "search the Hinova voluntário list, pick a result" for name/CPF/código — never a typed name form. The Hinova voluntário list frequently has no email on file (confirmed against real data), so email is the one field the admin always types at this step; without a real email the invite link has nowhere to go.
- `POST /users` requires `estrutura.editar` when `supervisorId` is sent (existing rule, unchanged) and additionally requires `integracoes.gerenciar` when the new Hinova fields are sent.
- The sidebar has no nesting support today (confirmed: flat list only) — the three new nav items (Árvore comissionada, Participantes, Remuneração) are three more flat entries in `NAV_ITEMS`, exactly like `settings` was added in the previous phase. Do not build a grouped/collapsible nav — out of scope.
- "Remuneração" is a disabled ("Em breve") nav item only — no page, no endpoint, in this plan.

---

## Task 1: `POST /users` — atomic Hinova linking

**Files:**
- Modify: `src/Recorrencia.Api/Users/UserEndpoints.cs`
- Modify: `tests/Recorrencia.Api.Tests/UserTests.cs`

**Interfaces:**
- Produces: `InviteUserRequest` gains `CodigoVoluntario`/`NomeHinova`/`CpfHinova` (all `string?`, all-or-nothing — if one is sent, all three must be sent). Consumed by Task 4's Server Action.
- Consumes: `hinova_voluntario_mapping` table (Fase 3, already exists), `PostgresErrorCodes.UniqueViolation` handling pattern already used in `src/Recorrencia.Api/Integracoes/HinovaEndpoints.cs`'s `CriarMapeamentoAsync`.

- [ ] **Step 1: Write the failing tests**

Read the current `tests/Recorrencia.Api.Tests/UserTests.cs` first — it already has a `LoginAsync` helper and a `CreatedDto` record you'll reuse. Add these four tests (append near the other `POST /users` tests):

```csharp
[Fact]
public async Task Creating_with_hinova_fields_links_the_mapping_in_the_same_request()
{
    var s = await api.SeedAsync();
    var admin = await LoginAsync(s, "admin");

    var response = await admin.PostAsync("/users", new
    {
        name = "Ana Paula Ferreira",
        email = $"ana@{s.Slug}.local",
        supervisorId = s.Joao,
        codigoVoluntario = "101",
        nomeHinova = "Ana Paula Ferreira",
        cpfHinova = "11122233344",
    });
    await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
    var id = (await response.Content.ReadFromJsonAsync<CreatedDto>(ApiClient.Json))!.Id;

    var mapped = await api.SqlScalarAsync<string>(
        "select codigo_voluntario from hinova_voluntario_mapping where tenant_id = @tenant and user_id = @id",
        new { tenant = s.TenantId, id });
    Assert.Equal("101", mapped);
}

[Fact]
public async Task Duplicate_codigo_voluntario_returns_conflict_and_creates_nothing()
{
    var s = await api.SeedAsync();
    var admin = await LoginAsync(s, "admin");
    await ApiClient.ExpectAsync(await admin.PostAsync("/users", new
    {
        name = "Ana Paula Ferreira",
        email = $"ana@{s.Slug}.local",
        codigoVoluntario = "101",
        nomeHinova = "Ana Paula Ferreira",
        cpfHinova = "11122233344",
    }), HttpStatusCode.Created);

    var response = await admin.PostAsync("/users", new
    {
        name = "Outra Pessoa",
        email = $"outra@{s.Slug}.local",
        codigoVoluntario = "101",
        nomeHinova = "Ana Paula Ferreira",
        cpfHinova = "11122233344",
    });

    Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    Assert.Equal("hinova.vinculo_duplicado", await ApiClient.CodeAsync(response));
    var count = await api.SqlScalarAsync<int>(
        "select count(*)::int from users where tenant_id = @tenant and email = @email",
        new { tenant = s.TenantId, email = $"outra@{s.Slug}.local" });
    Assert.Equal(0, count);
}

[Fact]
public async Task Hinova_fields_without_integracoes_gerenciar_are_forbidden()
{
    var s = await api.SeedAsync();
    var recrutador = Guid.NewGuid();
    await api.SqlAsync(
        """
        insert into roles (id, tenant_id, name) values (@recrutador, @tenant, 'Recrutador');
        insert into role_permissions (tenant_id, role_id, permission_key, scope) values (@tenant, @recrutador, 'usuarios.convidar', null);
        insert into user_roles (tenant_id, user_id, role_id) values (@tenant, @maria, @recrutador);
        """,
        new { recrutador, tenant = s.TenantId, maria = s.Maria });

    var response = await (await LoginAsync(s, "maria")).PostAsync("/users", new
    {
        name = "Nova",
        email = $"nova@{s.Slug}.local",
        codigoVoluntario = "101",
        nomeHinova = "Nova",
        cpfHinova = "11122233344",
    });

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.Equal("auth.forbidden", await ApiClient.CodeAsync(response));
}

[Fact]
public async Task Creating_without_hinova_fields_still_works_exactly_as_before()
{
    var s = await api.SeedAsync();
    var admin = await LoginAsync(s, "admin");

    var response = await admin.PostAsync("/users", new { name = "Sem Hinova", email = $"semhinova@{s.Slug}.local" });

    await ApiClient.ExpectAsync(response, HttpStatusCode.Created);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter UserTests`
Expected: the 3 new Hinova-related tests FAIL (the fields don't exist yet on the request record, so they'll be silently ignored by JSON binding and the mapping never gets created / the 403 never fires). The 4th test should already PASS (it exercises the existing, unmodified path).

- [ ] **Step 3: Extend `InviteUserRequest` and `InviteAsync`**

In `src/Recorrencia.Api/Users/UserEndpoints.cs`, change the record (line ~15):

```csharp
public sealed record InviteUserRequest(string? Name, string? Email, Guid? SupervisorId, Guid[]? RoleIds,
    string? CodigoVoluntario, string? NomeHinova, string? CpfHinova);
```

In `InviteAsync`, after the existing permission checks (`roleIds`/`SupervisorId` blocks) and before `var id = Guid.CreateVersion7();`, add:

```csharp
var hasHinovaFields = body.CodigoVoluntario is not null || body.NomeHinova is not null || body.CpfHinova is not null;
if (hasHinovaFields && !mine.Has("integracoes.gerenciar"))
    throw new ApiProblem(StatusCodes.Status403Forbidden, "auth.forbidden");
```

Then, inside the existing `await db.InTenantAsync(tenant, actor, async tx => { ... }, ct);` lambda, right after the `foreach (var roleId in roleIds.Distinct())` loop and before the `await tx.ExecuteAsync("select app.create_invite(...)` line, add:

```csharp
if (hasHinovaFields)
{
    try
    {
        await tx.ExecuteAsync(
            """
            insert into hinova_voluntario_mapping (tenant_id, user_id, codigo_voluntario, nome_hinova, cpf_hinova, mapped_by)
            values (@tenant, @id, @codigo, @nome, @cpf, @actor)
            """,
            new { tenant, actor, id, codigo = body.CodigoVoluntario, nome = body.NomeHinova, cpf = body.CpfHinova });
    }
    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
    {
        throw new ApiProblem(StatusCodes.Status409Conflict, "hinova.vinculo_duplicado");
    }
}
```

This throw happens inside the same `db.InTenantAsync` transaction as the `insert into users` above it, so a duplicate `codigo_voluntario` rolls back the whole transaction — the user row is never committed. This is why Step 1's `Duplicate_codigo_voluntario_returns_conflict_and_creates_nothing` test asserts `count == 0`, not just the 409 status.

Also extend the existing audit call a few lines below (`await WriteAsync(tx, tenant, actor, "users.invite", "users", id, null, new { name, email = address, body.SupervisorId, roleIds });`) so the Hinova link is traceable in the same audit entry instead of being silently undocumented — add the three new fields to that anonymous object:

```csharp
await WriteAsync(tx, tenant, actor, "users.invite", "users", id, null,
    new { name, email = address, body.SupervisorId, roleIds, body.CodigoVoluntario, body.NomeHinova, body.CpfHinova });
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Recorrencia.Api.Tests --filter UserTests`
Expected: all pass (the 4 new ones, plus every pre-existing `UserTests` test — no regressions, since every new field is optional and every new code path is gated behind `hasHinovaFields`).

- [ ] **Step 5: Run the full API suite for regressions**

Run: `dotnet test tests/Recorrencia.Api.Tests`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/Recorrencia.Api/Users/UserEndpoints.cs tests/Recorrencia.Api.Tests/UserTests.cs
git commit -m "feat(api): let POST /users link a Hinova voluntário atomically"
```

---

## Task 2: Frontend types, icons, and sidebar entries

**Files:**
- Modify: `web/src/lib/types.ts`
- Modify: `web/src/app/components/app-icon.tsx`
- Modify: `web/src/app/app-shell.tsx`

**Interfaces:**
- Produces: `UserNode` type (consumed by Tasks 3, 5, 6); `IconName` gains `'people' | 'shield' | 'plus'`; 3 new flat `NAV_ITEMS` entries.
- Consumes: nothing new.

- [ ] **Step 1: Add `UserNode`**

In `web/src/lib/types.ts`, append:

```ts
export type UserNode = { id: string; name: string; email: string; status: string; supervisorId: string | null; roleIds: string[] };
```

- [ ] **Step 2: Add the three new icons**

In `web/src/app/components/app-icon.tsx`, widen the union:

```tsx
export type IconName = 'grid' | 'wallet' | 'calendar' | 'logout' | 'search' | 'back' | 'chevron' | 'info' | 'settings' | 'link' | 'trash' | 'people' | 'shield' | 'plus';
```

Add three cases (after the existing `'trash'` case), using the exact path data from the mockup (`mockup/app.js`'s `paths` object and `network-ui.js`'s `plusIcon()`):

```tsx
    case 'people':
      return (
        <svg className={cls} {...svgProps}>
          <circle cx={9} cy={7} r={3} />
          <path d="M3 21v-3a6 6 0 0 1 12 0v3M16 4a3 3 0 0 1 0 6M18 14a5 5 0 0 1 3 4v3" />
        </svg>
      );
    case 'shield':
      return (
        <svg className={cls} {...svgProps}>
          <path d="m12 3 8 3v6c0 5-8 9-8 9s-8-4-8-9V6l8-3" />
          <path d="m8 12 3 3 5-6" />
        </svg>
      );
    case 'plus':
      return (
        <svg className={cls} {...svgProps}>
          <path d="M12 5v14M5 12h14" />
        </svg>
      );
```

- [ ] **Step 3: Add the three nav items**

In `web/src/app/app-shell.tsx`, widen `ActivePage`:

```ts
export type ActivePage = 'overview' | 'wallet' | 'closing' | 'settings' | 'arvore' | 'participantes' | 'remuneracao';
```

Add three entries to `NAV_ITEMS`, after the existing `'settings'` entry (same array, same shape — no grouping, matching the file's existing flat-list convention exactly):

```ts
  { id: 'arvore', icon: 'people', label: 'Árvore comissionada', href: '/administracao/arvore', permission: 'estrutura.visualizar' },
  { id: 'participantes', icon: 'people', label: 'Participantes', href: '/administracao/participantes', permission: 'estrutura.visualizar' },
  { id: 'remuneracao', icon: 'shield', label: 'Remuneração', href: null },
```

(`href: null` on `remuneracao` renders it disabled with "Em breve" automatically — same mechanism already used for `closing`. It has no `permission` field, matching `closing`'s existing convention: disabled items are visible to everyone, same as the mockup always showing all four seller nav items regardless of state.)

- [ ] **Step 4: Typecheck**

Run: `cd web && npm run typecheck`
Expected: no errors (the two new routes don't exist yet — Tasks 5-6 create them — but a string literal `href` doesn't require the route to exist for typechecking).

- [ ] **Step 5: Commit**

```bash
git add web/src/lib/types.ts web/src/app/components/app-icon.tsx web/src/app/app-shell.tsx
git commit -m "feat(web): add UserNode type, people/shield/plus icons, and Administração nav items"
```

---

## Task 3: Tree layout algorithm (pure function)

**Files:**
- Create: `web/src/lib/tree-layout.ts`
- Create: `web/src/lib/tree-layout.test.ts`

**Interfaces:**
- Produces: `treeLayout(people, options?) -> TreeLayout` (consumed by Task 5's visual tree component).
- Consumes: nothing — pure function, no I/O.

This is a straight TypeScript port of `mockup/tree-layout.js`'s `window.aprovecTreeLayout` — read that file first, it is short (26 lines) and this task reproduces its exact algorithm with types, not a redesign.

- [ ] **Step 1: Write the failing tests**

Create `web/src/lib/tree-layout.test.ts`:

```ts
import { describe, expect, it } from 'vitest';
import { treeLayout } from './tree-layout';

describe('treeLayout', () => {
  it('places a single root at the origin row', () => {
    const result = treeLayout([{ id: 'a', parentId: null }]);
    const a = result.nodes.find((n) => n.id === 'a')!;
    expect(a.depth).toBe(0);
    expect(a.parentId).toBeNull();
  });

  it('supports chains deeper than one level', () => {
    const result = treeLayout([
      { id: 'a', parentId: null },
      { id: 'b', parentId: 'a' },
      { id: 'c', parentId: 'b' },
    ]);
    const byId = Object.fromEntries(result.nodes.map((n) => [n.id, n]));
    expect(byId.a.depth).toBe(0);
    expect(byId.b.depth).toBe(1);
    expect(byId.c.depth).toBe(2);
    // Each row sits strictly below the previous one.
    expect(byId.b.y).toBeGreaterThan(byId.a.y);
    expect(byId.c.y).toBeGreaterThan(byId.b.y);
  });

  it('supports multiple independent roots, laid out side by side', () => {
    const result = treeLayout([
      { id: 'a', parentId: null },
      { id: 'b', parentId: null },
    ]);
    const byId = Object.fromEntries(result.nodes.map((n) => [n.id, n]));
    expect(byId.a.y).toBe(byId.b.y);
    expect(byId.a.x).not.toBe(byId.b.x);
    expect(result.roots).toEqual(expect.arrayContaining(['a', 'b']));
  });

  it('produces one edge per parent-child pair', () => {
    const result = treeLayout([
      { id: 'a', parentId: null },
      { id: 'b', parentId: 'a' },
      { id: 'c', parentId: 'a' },
    ]);
    expect(result.edges).toHaveLength(2);
    expect(result.edges.map((e) => e.to.id).sort()).toEqual(['b', 'c']);
  });

  it('restricts the layout to one root subtree when rootId is given', () => {
    const result = treeLayout(
      [
        { id: 'a', parentId: null },
        { id: 'b', parentId: null },
        { id: 'c', parentId: 'a' },
      ],
      { rootId: 'a' },
    );
    expect(result.nodes.map((n) => n.id).sort()).toEqual(['a', 'c']);
  });

  it('hides descendants of a collapsed node but keeps the node itself', () => {
    const result = treeLayout(
      [
        { id: 'a', parentId: null },
        { id: 'b', parentId: 'a' },
        { id: 'c', parentId: 'b' },
      ],
      { collapsed: ['b'] },
    );
    expect(result.nodes.map((n) => n.id).sort()).toEqual(['a', 'b']);
    expect(result.nodes.find((n) => n.id === 'b')!.collapsed).toBe(true);
  });

  it('reserves a newRoot placeholder position only when requested', () => {
    const withPlaceholder = treeLayout([{ id: 'a', parentId: null }], { newRoot: true });
    const without = treeLayout([{ id: 'a', parentId: null }]);
    expect(withPlaceholder.newRoot).not.toBeNull();
    expect(without.newRoot).toBeNull();
  });
});
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `cd web && npm test -- tree-layout`
Expected: FAIL — `web/src/lib/tree-layout.ts` doesn't exist yet.

- [ ] **Step 3: Implement `treeLayout`**

Create `web/src/lib/tree-layout.ts`. Read `mockup/tree-layout.js` first and port its exact algorithm (span computation bottom-up, position assignment top-down via an explicit stack — not recursion) into this typed version:

```ts
export type TreePerson = { id: string; parentId: string | null };

export type TreeLayoutOptions = {
  rootId?: string | null;
  collapsed?: string[];
  newRoot?: boolean;
};

export type TreeNode = {
  id: string;
  parentId: string | null;
  x: number;
  y: number;
  depth: number;
  descendants: number;
  childCount: number;
  collapsed: boolean;
};

export type TreeEdge = { from: TreeNode; to: TreeNode };

export type TreeLayout = {
  nodes: TreeNode[];
  edges: TreeEdge[];
  roots: string[];
  newRoot: { x: number; y: number } | null;
  width: number;
  height: number;
  nodeWidth: number;
  nodeHeight: number;
};

const NODE_WIDTH = 226;
const NODE_HEIGHT = 154;
const ROW_GAP = 240;
const PADDING = 40;
const SIBLING_GAP = 34;
const ROOT_GAP = 88;

export function treeLayout(people: TreePerson[], options: TreeLayoutOptions = {}): TreeLayout {
  const collapsedSet = new Set(options.collapsed ?? []);
  const byId = new Map(people.map((p) => [p.id, p]));
  const children = new Map<string, string[]>();
  for (const p of people) {
    if (p.parentId === null) continue;
    if (!children.has(p.parentId)) children.set(p.parentId, []);
    children.get(p.parentId)!.push(p.id);
  }

  const roots = options.rootId
    ? [options.rootId].filter((id) => byId.has(id))
    : people.filter((p) => p.parentId === null).map((p) => p.id);

  function descendantCount(id: string): number {
    const kids = children.get(id) ?? [];
    return kids.reduce((sum, childId) => sum + 1 + descendantCount(childId), 0);
  }

  function visibleChildren(id: string): string[] {
    return collapsedSet.has(id) ? [] : (children.get(id) ?? []);
  }

  function span(id: string): number {
    const kids = visibleChildren(id);
    if (kids.length === 0) return NODE_WIDTH;
    const total = kids.reduce((sum, childId) => sum + span(childId), 0) + SIBLING_GAP * (kids.length - 1);
    return Math.max(NODE_WIDTH, total);
  }

  const nodes: TreeNode[] = [];
  const nodesById = new Map<string, TreeNode>();
  let cursorX = PADDING;

  for (const rootId of roots) {
    const rootSpan = span(rootId);
    const stack: { id: string; depth: number; left: number; width: number }[] = [
      { id: rootId, depth: 0, left: cursorX, width: rootSpan },
    ];
    while (stack.length > 0) {
      const { id, depth, left, width } = stack.pop()!;
      const person = byId.get(id)!;
      const node: TreeNode = {
        id,
        parentId: person.parentId,
        x: left + width / 2 - NODE_WIDTH / 2,
        y: PADDING + depth * ROW_GAP,
        depth,
        descendants: descendantCount(id),
        childCount: (children.get(id) ?? []).length,
        collapsed: collapsedSet.has(id),
      };
      nodes.push(node);
      nodesById.set(id, node);

      const kids = visibleChildren(id);
      let childLeft = left + width / 2 - (kids.reduce((sum, childId) => sum + span(childId), 0) + SIBLING_GAP * (kids.length - 1)) / 2;
      for (const childId of kids) {
        const childSpan = span(childId);
        stack.push({ id: childId, depth: depth + 1, left: childLeft, width: childSpan });
        childLeft += childSpan + SIBLING_GAP;
      }
    }
    cursorX += rootSpan + ROOT_GAP;
  }

  const edges: TreeEdge[] = [];
  for (const node of nodes) {
    if (node.parentId === null) continue;
    const parent = nodesById.get(node.parentId);
    if (parent) edges.push({ from: parent, to: node });
  }

  const maxDepth = nodes.reduce((max, n) => Math.max(max, n.depth), 0);
  const width = Math.max(940, cursorX - SIBLING_GAP + PADDING);
  const height = Math.max(460, PADDING + (maxDepth + 1) * ROW_GAP + 82);

  const newRoot = options.newRoot ? { x: cursorX, y: PADDING } : null;

  return { nodes, edges, roots, newRoot, width, height, nodeWidth: NODE_WIDTH, nodeHeight: NODE_HEIGHT };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `cd web && npm test -- tree-layout`
Expected: all 7 tests PASS. If the span/position math doesn't match a specific assertion, adjust the implementation (not the test) to match `mockup/tree-layout.js`'s actual behavior — re-read that file's `span`/position-assignment logic closely if anything disagrees.

- [ ] **Step 5: Commit**

```bash
git add web/src/lib/tree-layout.ts web/src/lib/tree-layout.test.ts
git commit -m "feat(web): port the mockup's tree positioning algorithm to TypeScript"
```

---

## Task 4: "Buscar e adicionar participante" — shared search-and-create flow

**Files:**
- Create: `web/src/app/administracao/actions.ts`
- Create: `web/src/app/administracao/participante-search.tsx`
- Modify: `web/src/lib/errors.ts`

**Interfaces:**
- Produces: `criarParticipante` Server Action; `ParticipanteSearch` client component (props: `voluntarios: HinovaVoluntario[]`, `supervisorId: string | null`, `supervisorLabel: string`). Consumed by Task 5 (tree "+"/"Nova árvore") and Task 6 (Participantes "Novo participante").
- Consumes: `GET /integracoes/hinova/voluntarios?query=` (Fase 3, unchanged), `POST /users` (Task 1's extended version).

This task's UI pattern is a direct adaptation of `web/src/app/configuracoes/integracoes/mapeamento-form.tsx` and `actions.ts` — read both first. The key difference: instead of picking an existing APROVEC user from a `<select>` to link to a Hinova voluntário (Fase 3's flow), this creates a brand-new user FROM the chosen Hinova voluntário.

- [ ] **Step 1: Add the error message**

In `web/src/lib/errors.ts`, the `hinova.vinculo_duplicado` entry already exists (from Fase 3) — no addition needed there. Confirm this by reading the file; if for any reason it's missing, add it back exactly as it was: `'hinova.vinculo_duplicado': 'Esse usuário ou esse voluntário já está vinculado a outro registro.'`.

- [ ] **Step 2: Write the Server Action**

Create `web/src/app/administracao/actions.ts`:

```ts
'use server';

import { revalidatePath } from 'next/cache';
import { ApiError, apiFetch } from '@/lib/api';
import { messageFor } from '@/lib/errors';
import type { FormState } from '@/lib/form-state';

export async function criarParticipante(_: FormState, formData: FormData): Promise<FormState> {
  const supervisorId = String(formData.get('supervisorId') ?? '');
  try {
    await apiFetch('/users', {
      method: 'POST',
      body: {
        name: String(formData.get('nomeHinova') ?? ''),
        email: String(formData.get('email') ?? ''),
        supervisorId: supervisorId || undefined,
        codigoVoluntario: String(formData.get('codigoVoluntario') ?? ''),
        nomeHinova: String(formData.get('nomeHinova') ?? ''),
        cpfHinova: String(formData.get('cpfHinova') ?? ''),
      },
    });
  } catch (error) {
    if (error instanceof ApiError) return { error: messageFor(error.code) };
    throw error;
  }
  revalidatePath('/administracao/arvore');
  revalidatePath('/administracao/participantes');
  return { message: 'Participante adicionado.' };
}
```

Note: `email` is a real, required field the admin TYPES after picking a search result — never a placeholder. A parallel investigation this session confirmed real Hinova voluntário records frequently have no email on file (`HinovaVoluntario` doesn't even carry the field — confirm by reading `web/src/lib/types.ts`: only `codigo`, `nome`, `cpf`, `jaVinculado`, `vinculadoA`), so a synthetic placeholder email would mean the invited consultant never receives a real set-password link and can never log in. `POST /users` already requires a valid, non-empty email (`users.invalid_email` otherwise) — this task relies on that existing validation, no new validation needed here.

- [ ] **Step 3: Write the shared search component**

Create `web/src/app/administracao/participante-search.tsx`:

```tsx
'use client';

import { useActionState, useState } from 'react';
import type { FormState } from '@/lib/form-state';
import type { HinovaVoluntario } from '@/lib/types';
import { criarParticipante } from './actions';

export function ParticipanteSearch({
  voluntarios,
  supervisorId,
  supervisorLabel,
}: {
  voluntarios: HinovaVoluntario[];
  supervisorId: string | null;
  supervisorLabel: string;
}) {
  const [state, formAction, pending] = useActionState<FormState, FormData>(criarParticipante, {});
  const [selected, setSelected] = useState<HinovaVoluntario | null>(null);

  if (state.message) {
    return <p className="success">{state.message}</p>;
  }

  return (
    <div className="participante-search">
      <p className="muted">{supervisorLabel}</p>
      {!selected ? (
        <form method="get" className="search-field">
          <input type="search" name="buscarParticipante" placeholder="Buscar por nome" aria-label="Buscar voluntário por nome" />
        </form>
      ) : null}
      {!selected &&
        voluntarios.map((v) => (
          <button
            key={v.codigo}
            type="button"
            className="participante-option"
            disabled={v.jaVinculado}
            onClick={() => setSelected(v)}
          >
            <strong>{v.nome}</strong>
            {v.jaVinculado ? <span className="badge neutral">Vinculado a {v.vinculadoA}</span> : <span className="muted">{v.cpf}</span>}
          </button>
        ))}
      {selected && (
        <form action={formAction} className="form">
          <input type="hidden" name="supervisorId" value={supervisorId ?? ''} />
          <input type="hidden" name="codigoVoluntario" value={selected.codigo} />
          <input type="hidden" name="nomeHinova" value={selected.nome} />
          <input type="hidden" name="cpfHinova" value={selected.cpf} />
          <p>
            Adicionar <strong>{selected.nome}</strong>?
          </p>
          <label>
            E-mail
            <input type="email" name="email" required autoComplete="off" />
          </label>
          <p className="muted">A Hinova nem sempre tem e-mail cadastrado — confirme o e-mail correto, é para onde vai o convite de acesso.</p>
          {state.error && <p role="alert" className="error">{state.error}</p>}
          <div className="inline">
            <button type="submit" disabled={pending}>{pending ? 'Adicionando…' : 'Confirmar'}</button>
            <button type="button" className="secondary" onClick={() => setSelected(null)}>Cancelar</button>
          </div>
        </form>
      )}
    </div>
  );
}
```

The `buscarParticipante` search form submits `GET`, reloading the parent Server Component page with `?buscarParticipante=...` in the querystring — Tasks 5 and 6's pages read that param server-side and pass the filtered `voluntarios` list as this component's prop (same pattern as `web/src/app/configuracoes/integracoes/page.tsx`'s `query` param). This component does not fetch `/integracoes/hinova/voluntarios` itself.

- [ ] **Step 4: Typecheck and lint**

Run: `cd web && npm run typecheck && npm run lint`
Expected: clean. (`npm run build` will still fail until Tasks 5-6 create the pages that import this component — that's expected, do not attempt a full build yet.)

- [ ] **Step 5: Commit**

```bash
git add web/src/app/administracao/actions.ts web/src/app/administracao/participante-search.tsx
git commit -m "feat(web): add the search-Hinova-and-create-participant flow shared by both admin pages"
```

---

## Task 5: Árvore comissionada page

**Files:**
- Create: `web/src/app/administracao/arvore/page.tsx`
- Create: `web/src/app/administracao/arvore/pyramid.tsx`
- Create: `web/src/app/administracao/arvore/pyramid.css` (or append to `web/src/app/globals.css` — see Step 3)

**Interfaces:**
- Consumes: `GET /users`, `GET /commissions/{competencia}`, `GET /integracoes/hinova/voluntarios?query=` (only when a search is active), `treeLayout` (Task 3), `ParticipanteSearch` (Task 4).
- Produces: the page at `/administracao/arvore`.

This task's visual fidelity target is `mockup/network-ui.js`'s `graph()` function and `mockup/network.css`/`mockup/tree.css` — read all three before writing the component. Port the CSS class names and visual structure closely (card layout, connector lines, zoom/pan behavior); the exact pixel values already came from Task 3's `treeLayout` output, so this task is about rendering that output faithfully, not re-deriving positions.

- [ ] **Step 1: Read the mockup source for this page**

Read `mockup/network-ui.js` (the `graph()` function, roughly lines 166–200, and the zoom/fit-tree handlers around lines 206–218 and 472–491) and `mockup/network.css` + `mockup/tree.css` in full before writing any code. Note especially: nodes are absolutely positioned `<div>`s using `treeLayout`'s `x`/`y` directly as CSS `left`/`top` inside a canvas that's scaled via `transform: scale(zoom)`; connector lines are drawn as SVG `<path>` elements sized to the same canvas.

- [ ] **Step 2: Write `page.tsx` (Server Component)**

Create `web/src/app/administracao/arvore/page.tsx`. Guard structure matches `web/src/app/carteira/page.tsx` and `web/src/app/configuracoes/integracoes/page.tsx` (host guard, `notFound()` outside tenant; permission guard on `estrutura.visualizar`, "sem acesso" card otherwise). Fetch `GET /users` and, for the current competência (reuse `currentCompetencia()` from `web/src/lib/format.ts`, same as `tenant-home.tsx` does), `GET /commissions/{competencia}`. Build a lookup from `beneficiaryId` to `{ recebido, comissao, rate }`:

- For a node whose `byRule` includes a `ruleType === 'global'` entry: that person belongs in the fixed "gestão global" band, not the pyramid body — `recebido` = that entry's `base`, `comissao` = that entry's `amount`, `rate` = that entry's `rate`.
- For every other node: `recebido`/`comissao`/`rate` come from the `ruleType === 'own'` entry if present, else zero (a brand-new participant with no paid boletos yet has no `own` entry at all in `GET /commissions/{competencia}`'s response — handle that as zeros, not as an error).

Partition `GET /users`' result into `gestores` (anyone with a `global` commission entry this competência) and `sellers` (everyone else, `status !== 'desligado'`). Pass `sellers` (mapped to `{ id, parentId: supervisorId }`) into `treeLayout`. Read `?root=` (selected root id or empty for "all trees"), `?modo=` (`'arvore' | 'lista'`, default `'arvore'`), and `?buscarParticipante=` (forwarded to `ParticipanteSearch` — when non-empty, fetch `GET /integracoes/hinova/voluntarios?query=` and pass the results down) from `searchParams`.

When `modo === 'lista'`, render the same table structure as Task 6's Participantes page (consider extracting a shared `<ParticipantesTable>` component used by both this page's list mode and Task 6's page, rather than duplicating the table markup — your call based on how much actually ends up shared once both are written).

- [ ] **Step 3: Write the pyramid client component**

Create `web/src/app/administracao/arvore/pyramid.tsx` — `'use client'`. Props: `layout: TreeLayout` (Task 3's output), a lookup of per-node display data (name/rate/recebido/comissao), the `gestores` list for the fixed band, and callbacks/hrefs for zoom in/out/fit (state lives in the URL querystring via `?zoom=`, matching this app's established "state in the URL, not client state" convention used by `carteira`'s pagination — though zoom is more naturally client-only UI state; use your judgment, `useState` for zoom/pan is acceptable here since it's pure presentation, not data the server needs to know about). Render: an SVG layer for connector lines (`edges` from the layout, plus lines from the fixed gestor band down to each root), and one absolutely-positioned card per node using `layout.nodeWidth`/`layout.nodeHeight` and each node's `x`/`y`. Each card shows: avatar initials, name, rate badge, "Recebidos na carteira" value, "Comissão total" value, and — when the node has no children yet or per your judgment always — a `+` button that expands inline into `ParticipanteSearch` (Task 4) with `supervisorId` set to that node's id. A "+ Nova árvore" placeholder card (using `layout.newRoot`'s position, only rendered when `layout.newRoot` is non-null — pass `{ newRoot: true }` in the `treeLayout` call from `page.tsx`) opens the same `ParticipanteSearch` with `supervisorId: null`.

CSS: add a new file `web/src/app/administracao/arvore/pyramid.css` imported from this component, OR append the relevant classes to `web/src/app/globals.css` (existing convention for this project — check which approach the rest of the codebase uses; per `web/src/app/globals.css`'s existing size and the fact every other phase added its CSS there, prefer appending to `globals.css` for consistency unless it's become unwieldy, in which case a scoped file is fine — your call, document which you picked in the commit message).

- [ ] **Step 4: Build and typecheck**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: clean.

- [ ] **Step 5: Commit**

```bash
git add web/src/app/administracao/arvore
git commit -m "feat(web): add the Árvore comissionada page"
```

(If CSS was appended to `globals.css` instead of a new file, `git add web/src/app/globals.css` too, and skip creating `pyramid.css`.)

---

## Task 6: Participantes page

**Files:**
- Create: `web/src/app/administracao/participantes/page.tsx`

**Interfaces:**
- Consumes: `GET /users`, `GET /integracoes/hinova/voluntarios?query=`, `ParticipanteSearch` (Task 4).
- Produces: the page at `/administracao/participantes`.

- [ ] **Step 1: Write the page**

Create `web/src/app/administracao/participantes/page.tsx` — same guard structure as Task 5. Fetch `GET /users`, filter by `?query=` (name/email substring match, done client-side in this Server Component since `GET /users` has no server-side filter param — confirm this by re-checking `UserEndpoints.ListAsync`; if it turns out there IS a query param already, use it instead of filtering in the component). Render: summary counts (total participantes, quantos com papel `consultor`, quantos com papel `coordenador` — derive from `roleIds` cross-referenced with role template ids, matching the mockup's "4 participantes · 3 consultores · 1 coordenador de toda a base" line), a table (nome, e-mail, perfil, supervisor — resolve `supervisorId` to the supervisor's name via a lookup built from the same `GET /users` response), and a "Novo participante" button that reveals `ParticipanteSearch` with `supervisorId: null` (same component Task 5 uses, imported from `../participante-search`).

- [ ] **Step 2: Build and typecheck**

Run: `cd web && npm run typecheck && npm run lint && npm run build`
Expected: clean, and this time the full build should succeed end-to-end (both new routes now exist).

- [ ] **Step 3: Commit**

```bash
git add web/src/app/administracao/participantes
git commit -m "feat(web): add the Participantes page"
```

---

## Task 7: E2E scenario

**Files:**
- Modify: `web/e2e/fundacao.spec.ts`

**Interfaces:**
- Consumes: the full flow built across Tasks 1-6, running against the dev-fake Hinova client (already wired, `Hinova:UseFake=true` in `appsettings.Development.json`).

- [ ] **Step 1: Read the current file**

Read `web/e2e/fundacao.spec.ts` in full — reuse its `login()` helper and its existing style exactly, same as the Hinova phase's E2E task did.

- [ ] **Step 2: Add the scenario**

Add a new `test(...)` after the existing "admin configura a Hinova e vincula um voluntário" test. It needs Hinova credentials configured first (reuse the same save-credentials steps from that existing test, or extract a small helper if the file's style supports it — your call). Then: navigate to Árvore comissionada, click "+ Nova árvore", search "Bruno" (the `DevFakeHinovaClient` fixture has "Bruno Costa Lima", codigo 102 — confirm this against `src/Recorrencia.Api/Integracoes/DevFakeHinovaClient.cs`), select the result, fill the required E-mail field (e.g. `bruno@e2e.aprovec.local`) — Task 4's form requires this, the admin always types it — confirm, assert "Participante adicionado." appears and the new card shows "Bruno Costa Lima" in the pyramid. Then navigate to Participantes, confirm the same person appears in the table with "Sem supervisor".

- [ ] **Step 3: Run the E2E suite**

Run: `cd web && npm run e2e`
Expected: PASS, including the new scenario and all pre-existing ones (7 before this task). If the dev Postgres volume already has a "Bruno Costa Lima" participant from a previous run (persistent dev DB, same issue the Hinova phase's E2E hit), follow the same fix: use a distinct/unique voluntário or clean up at the end of the test, matching the established pattern in that same file (`f2678de`'s desvincular-at-the-end fix).

- [ ] **Step 4: Commit**

```bash
git add web/e2e/fundacao.spec.ts
git commit -m "test(e2e): cover building the commissioned tree by searching a Hinova voluntário"
```

---

## Final Verification

- [ ] `dotnet test` (whole solution) passes.
- [ ] `cd web && npm run typecheck && npm run lint && npm run build && npm test && npm run e2e` all pass.
- [ ] Manually verify against the local docker-compose stack: as `admin@aprovec.local`, open "Árvore comissionada", add a new root via search, add a child under it via its "+", switch to "Lista" mode and confirm both appear, open "Participantes" and confirm the same two rows with correct supervisor. Confirm "Remuneração" shows "Em breve" and is not clickable. Log in as a non-admin and confirm none of the three new nav items appear.
