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
