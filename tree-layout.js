/* Top-down forest layout. Iterative traversal keeps depth independent of the JS call stack. */
window.aprovecTreeLayout = function(people,options={}) {
  const nodeWidth=226,nodeHeight=154,rowGap=240,padding=40,siblingGap=34,rootGap=88;
  const byId=new Map(people.map(p=>[p.id,p])),children=new Map(people.map(p=>[p.id,[]]));
  people.forEach(p=>{if(p.parentId&&children.has(p.parentId))children.get(p.parentId).push(p.id);});
  const roots=options.rootId&&byId.has(options.rootId)?[options.rootId]:people.filter(p=>!p.parentId).map(p=>p.id);
  const collapsed=new Set(options.collapsed||[]),order=[],stack=roots.slice().reverse();
  while(stack.length){const id=stack.pop();order.push(id);const kids=children.get(id);for(let i=kids.length-1;i>=0;i--)stack.push(kids[i]);}
  const spans=new Map(),counts=new Map();
  for(let i=order.length-1;i>=0;i--){const id=order[i],kids=children.get(id);counts.set(id,kids.reduce((sum,k)=>sum+1+counts.get(k),0));spans.set(id,collapsed.has(id)||!kids.length?nodeWidth:Math.max(nodeWidth,kids.reduce((sum,k)=>sum+spans.get(k),0)+siblingGap*(kids.length-1)));}
  const showNew=!!options.newRoot&&!options.rootId;
  const forestWidth=roots.reduce((sum,id)=>sum+spans.get(id),0)+Math.max(0,roots.length-1)*rootGap+(showNew?(roots.length?rootGap:0)+nodeWidth:0);
  const width=Math.max(940,forestWidth+padding*2),offset=(width-forestWidth)/2;
  const nodes=[],pending=[];let cursor=offset;
  roots.forEach(id=>{pending.push({id,left:cursor,depth:0});cursor+=spans.get(id)+rootGap;});
  const newRoot=showNew?{x:roots.length?cursor:offset,y:202}:null;
  while(pending.length){const {id,left,depth}=pending.pop(),p=byId.get(id),kids=children.get(id),span=spans.get(id),x=left+(span-nodeWidth)/2,y=202+depth*rowGap;
    nodes.push({id,parentId:depth?p.parentId:null,x,y,depth,descendants:counts.get(id),childCount:kids.length,collapsed:collapsed.has(id)});
    if(!collapsed.has(id)){let childLeft=left;for(const kid of kids){pending.push({id:kid,left:childLeft,depth:depth+1});childLeft+=spans.get(kid)+siblingGap;}}
  }
  nodes.sort((a,b)=>a.depth-b.depth||a.x-b.x);
  const positions=new Map(nodes.map(p=>[p.id,p]));
  const edges=nodes.filter(p=>p.parentId&&positions.has(p.parentId)).map(to=>({from:positions.get(to.parentId),to}));
  const height=nodes.reduce((max,p)=>Math.max(max,p.y+nodeHeight+82),460);
  return {nodes,edges,roots,newRoot,width,height,nodeWidth,nodeHeight,manager:{x:width/2-175,y:26,width:350,height:110}};
};
