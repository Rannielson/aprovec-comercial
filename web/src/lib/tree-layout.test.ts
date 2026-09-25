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
