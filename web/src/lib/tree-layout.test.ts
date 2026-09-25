import { describe, expect, it } from 'vitest';
import { treeLayout, type TreePerson } from './tree-layout';

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

  it('nulls parentId at the root of a rootId-scoped subtree', () => {
    const result = treeLayout(
      [
        { id: 'a', parentId: null },
        { id: 'b', parentId: 'a' },
        { id: 'c', parentId: 'b' },
      ],
      { rootId: 'b' },
    );
    const b = result.nodes.find((n) => n.id === 'b')!;
    expect(b.depth).toBe(0);
    expect(b.parentId).toBeNull();
    expect(result.nodes.map((n) => n.id).sort()).toEqual(['b', 'c']);
    // No edge should ever point "from" a node outside the returned nodes.
    expect(result.edges.every((e) => result.nodes.includes(e.from))).toBe(true);
  });

  it('falls back to the full forest when rootId matches no one', () => {
    const result = treeLayout(
      [
        { id: 'a', parentId: null },
        { id: 'b', parentId: null },
      ],
      { rootId: 'does-not-exist' },
    );
    expect(result.nodes.map((n) => n.id).sort()).toEqual(['a', 'b']);
    expect(result.roots).toEqual(expect.arrayContaining(['a', 'b']));
  });

  it('centers a small multi-root forest that hits the 940px width floor', () => {
    // Two single-node roots: forestWidth = 226 + 226 + 88 (rootGap) = 540, well under the 940 floor,
    // so the leftover canvas width must be split evenly as a centering offset, not left flush at padding.
    const result = treeLayout([
      { id: 'a', parentId: null },
      { id: 'b', parentId: null },
    ]);
    const byId = Object.fromEntries(result.nodes.map((n) => [n.id, n]));
    expect(result.width).toBe(940);
    const forestWidth = 226 + 226 + 88;
    const expectedOffset = (940 - forestWidth) / 2;
    expect(byId.a.x).toBe(expectedOffset);
    expect(byId.b.x).toBe(expectedOffset + 226 + 88);
  });

  it('spaces a wide multi-root forest by one rootGap between roots (no trailing gap) and sizes width to match', () => {
    const nodeWidth = 226;
    const siblingGap = 34;
    const rootGap = 88;
    const padding = 40;

    const makeRootWithChildren = (rootId: string, childPrefix: string) => [
      { id: rootId, parentId: null },
      { id: `${childPrefix}1`, parentId: rootId },
      { id: `${childPrefix}2`, parentId: rootId },
      { id: `${childPrefix}3`, parentId: rootId },
      { id: `${childPrefix}4`, parentId: rootId },
    ];

    const people = [...makeRootWithChildren('a', 'ac'), ...makeRootWithChildren('b', 'bc')];
    const result = treeLayout(people);

    // Each root's span is driven by its 4 children: 4 * nodeWidth + 3 * siblingGap.
    const rootSpan = nodeWidth * 4 + siblingGap * 3;
    const forestWidth = rootSpan * 2 + rootGap; // exactly one gap BETWEEN the roots, no trailing gap
    const expectedWidth = forestWidth + padding * 2;

    expect(result.width).toBe(expectedWidth);

    const byId = Object.fromEntries(result.nodes.map((n) => [n.id, n]));
    // The shared centering offset cancels out of the difference, leaving exactly span + rootGap.
    expect(byId.b.x - byId.a.x).toBe(rootSpan + rootGap);
    // The forest is centered: with the canvas sized to content + 2*padding (floor not hit here),
    // the leftmost child sits exactly `padding` from the canvas edge.
    const leftmostChildX = Math.min(...['ac1', 'ac2', 'ac3', 'ac4'].map((id) => byId[id].x));
    expect(leftmostChildX).toBe(padding);
  });

  it('starts the first row below the gestão global band, like the mockup', () => {
    const result = treeLayout([{ id: 'a', parentId: null }]);
    const a = result.nodes[0];
    expect(result.manager).toEqual({ x: 940 / 2 - 175, y: 26, width: 350, height: 110 });
    expect(a.y).toBe(202);
    expect(a.y).toBeGreaterThan(result.manager.y + result.manager.height);
    expect(result.height).toBe(Math.max(460, 202 + 154 + 82));
  });

  it('keeps the newRoot placeholder inside the canvas for a wide forest', () => {
    const people: TreePerson[] = [];
    for (const root of ['a', 'b']) {
      people.push({ id: root, parentId: null });
      for (let i = 1; i <= 4; i++) people.push({ id: `${root}${i}`, parentId: root });
    }
    const result = treeLayout(people, { newRoot: true });
    expect(result.newRoot).not.toBeNull();
    const rightmost = Math.max(...result.nodes.map((n) => n.x));
    // One rootGap to the right of the last tree's rightmost card.
    expect(result.newRoot!.x).toBe(rightmost + 226 + 88);
    expect(result.newRoot!.x + result.nodeWidth).toBe(result.width - 40);
  });

  it('centers the newRoot placeholder alone when there are no roots yet', () => {
    const result = treeLayout([], { newRoot: true });
    expect(result.newRoot).toEqual({ x: (940 - 226) / 2, y: 202 });
  });

  it('suppresses the newRoot placeholder in a rootId-scoped view', () => {
    const result = treeLayout(
      [
        { id: 'a', parentId: null },
        { id: 'b', parentId: null },
      ],
      { rootId: 'a', newRoot: true },
    );
    expect(result.newRoot).toBeNull();
  });

  it('handles a very deep chain without recursing (no stack overflow)', () => {
    const depth = 3000;
    const people: TreePerson[] = [{ id: 'p0', parentId: null }];
    for (let i = 1; i < depth; i++) {
      people.push({ id: `p${i}`, parentId: `p${i - 1}` });
    }
    expect(() => treeLayout(people)).not.toThrow();
    const result = treeLayout(people);
    expect(result.nodes).toHaveLength(depth);
    const deepest = result.nodes.find((n) => n.id === `p${depth - 1}`)!;
    expect(deepest.depth).toBe(depth - 1);
  });
});
