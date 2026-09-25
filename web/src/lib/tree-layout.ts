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
  const children = new Map<string, string[]>(people.map((p) => [p.id, [] as string[]]));
  for (const p of people) {
    if (p.parentId !== null && children.has(p.parentId)) {
      children.get(p.parentId)!.push(p.id);
    }
  }

  // An unmatched rootId falls back to the full forest instead of producing a blank tree
  // (mirrors mockup/tree-layout.js: `options.rootId && byId.has(options.rootId) ? [...] : <forest>`).
  const roots =
    options.rootId && byId.has(options.rootId)
      ? [options.rootId]
      : people.filter((p) => p.parentId === null).map((p) => p.id);

  // Iterative pre-order traversal (parent before children) via an explicit stack, so span/descendant
  // computation below never recurses the JS call stack — matches mockup/tree-layout.js's own comment:
  // "Iterative traversal keeps depth independent of the JS call stack." A real commission chain has no
  // depth limit, so unmemoized recursion here would be both a stack-overflow and an O(n^2) risk.
  const order: string[] = [];
  const traversalStack = roots.slice().reverse();
  while (traversalStack.length > 0) {
    const id = traversalStack.pop()!;
    order.push(id);
    const kids = children.get(id) ?? [];
    for (let i = kids.length - 1; i >= 0; i--) traversalStack.push(kids[i]);
  }

  // Single reverse pass over the flat traversal order accumulates spans and descendant counts
  // bottom-up in O(n), each id computed exactly once (memoized via the spans/counts maps).
  const spans = new Map<string, number>();
  const counts = new Map<string, number>();
  for (let i = order.length - 1; i >= 0; i--) {
    const id = order[i];
    const kids = children.get(id) ?? [];
    counts.set(
      id,
      kids.reduce((sum, childId) => sum + 1 + (counts.get(childId) ?? 0), 0),
    );
    spans.set(
      id,
      collapsedSet.has(id) || kids.length === 0
        ? NODE_WIDTH
        : Math.max(
            NODE_WIDTH,
            kids.reduce((sum, childId) => sum + (spans.get(childId) ?? NODE_WIDTH), 0) +
              SIBLING_GAP * (kids.length - 1),
          ),
    );
  }

  // The whole forest is centered in the canvas: one ROOT_GAP between roots (never a trailing gap
  // after the last one), and any extra canvas width beyond the content is split evenly as an offset.
  const forestWidth =
    roots.reduce((sum, id) => sum + (spans.get(id) ?? NODE_WIDTH), 0) + Math.max(0, roots.length - 1) * ROOT_GAP;
  const width = Math.max(940, forestWidth + PADDING * 2);
  const offsetX = (width - forestWidth) / 2;

  const nodes: TreeNode[] = [];
  const nodesById = new Map<string, TreeNode>();
  const pending: { id: string; depth: number; left: number }[] = [];
  let cursorX = offsetX;
  for (const rootId of roots) {
    pending.push({ id: rootId, depth: 0, left: cursorX });
    cursorX += (spans.get(rootId) ?? NODE_WIDTH) + ROOT_GAP;
  }

  while (pending.length > 0) {
    const { id, depth, left } = pending.pop()!;
    const person = byId.get(id)!;
    const kids = children.get(id) ?? [];
    const spanWidth = spans.get(id) ?? NODE_WIDTH;
    const node: TreeNode = {
      id,
      // A depth-0 node always reports parentId: null, whether it's a true forest root or the root of
      // a rootId-scoped subtree — its real parent (if any) isn't part of the returned nodes.
      parentId: depth === 0 ? null : person.parentId,
      x: left + (spanWidth - NODE_WIDTH) / 2,
      y: PADDING + depth * ROW_GAP,
      depth,
      descendants: counts.get(id) ?? 0,
      childCount: kids.length,
      collapsed: collapsedSet.has(id),
    };
    nodes.push(node);
    nodesById.set(id, node);

    if (!collapsedSet.has(id)) {
      let childLeft = left;
      for (const childId of kids) {
        pending.push({ id: childId, depth: depth + 1, left: childLeft });
        childLeft += (spans.get(childId) ?? NODE_WIDTH) + SIBLING_GAP;
      }
    }
  }

  const edges: TreeEdge[] = [];
  for (const node of nodes) {
    if (node.parentId === null) continue;
    const parent = nodesById.get(node.parentId);
    if (parent) edges.push({ from: parent, to: node });
  }

  const maxDepth = nodes.reduce((max, n) => Math.max(max, n.depth), 0);
  const height = Math.max(460, PADDING + (maxDepth + 1) * ROW_GAP + 82);

  const newRoot = options.newRoot ? { x: cursorX, y: PADDING } : null;

  return { nodes, edges, roots, newRoot, width, height, nodeWidth: NODE_WIDTH, nodeHeight: NODE_HEIGHT };
}
