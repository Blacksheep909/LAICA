import type { AgentNode, AgentPlan } from './types';

export const NODE_W = 200;
export const NODE_H = 104;
export const GRID = 20;
export const MAX_NODES = 100;
export const STAGE_IDS = ['$input', '$review', '$output'] as const;
export type StageId = typeof STAGE_IDS[number];
export type EndpointSide = 'in' | 'out';

export function clonePlan(plan: AgentPlan): AgentPlan {
  return { ...plan, Nodes: plan.Nodes.map(n => ({ ...n })), Stages: (plan.Stages??[]).map(s => ({ ...s })), HiddenStageLinks: (plan.HiddenStageLinks??[]).map(l => ({ ...l })) };
}
export function stagePoint(plan: AgentPlan, id: StageId): { x: number; y: number } {
  const root=plan.Nodes.find(n=>n.Id==='root')??plan.Nodes[0];
  const right=Math.max(...plan.Nodes.map(n=>n.X+NODE_W),root?.X??0)+90;
  const fallback = id === '$input' ? { x:(root?.X??270)-260,y:root?.Y??220 } : {x:right+(id==='$output'?230:0),y:root?.Y??220};
  const s = (plan.Stages??[]).find(x => x.Id === id);
  return s ? { x: s.X, y: s.Y } : fallback;
}
export function nodePoint(n: AgentNode): { x: number; y: number } { return { x: n.X, y: n.Y }; }
export function snapped(value: number, enabled: boolean): number { return enabled ? Math.round(value / GRID) * GRID : value; }
export function linkKey(a: string, b: string): string { return `${a}\u0000${b}`; }
export function hiddenLink(plan: AgentPlan, from: string, to: string): boolean {
  return (plan.HiddenStageLinks??[]).some(l => l.FromId === from && l.ToId === to);
}
export function sideAllows(id:string,side:EndpointSide,direction:'source'|'target'):boolean {
  if(id==='$input') return side==='out'&&direction==='source';
  if(id==='$output') return side==='in'&&direction==='target';
  if(id==='$review') return direction==='source'?side==='out':side==='in';
  return direction==='source'?side==='out':side==='in';
}
export function linksAt(plan:AgentPlan,id:string,side:EndpointSide):{from:string;to:string}[] {
  return allEdges(plan).filter(e=>side==='out'?e.from===id:e.to===id);
}
export function allEdges(plan:AgentPlan):{from:string;to:string}[] {
  const edges:{from:string;to:string}[]=[];
  if(!plan.Nodes.length)return [{from:'$review',to:'$output'}];
  const root=plan.Nodes.find(n=>n.Id==='root');
  const reachable=new Set<string>();if(root){reachable.add(root.Id);let changed=true;while(changed){changed=false;for(const n of plan.Nodes)if(n.ParentId&&reachable.has(n.ParentId)&&!reachable.has(n.Id)){reachable.add(n.Id);changed=true;}}edges.push({from:'$input',to:root.Id});}
  for(const n of plan.Nodes){if(!reachable.has(n.Id))continue;if(n.ParentId)edges.push({from:n.ParentId,to:n.Id});if(!plan.Nodes.some(c=>reachable.has(c.Id)&&c.ParentId===n.Id))edges.push({from:n.Id,to:'$review'});}
  edges.push({from:'$review',to:'$output'});
  return edges.filter(e=>!hiddenLink(plan,e.from,e.to));
}
export function disconnectLink(plan:AgentPlan,from:string,to:string):AgentPlan {
  const p=clonePlan(plan);
  if(isStage(from)||isStage(to)){if(!hiddenLink(p,from,to))p.HiddenStageLinks.push({FromId:from,ToId:to});}
  else {const n=p.Nodes.find(n=>n.Id===to);if(n)n.ParentId=null;}
  return p;
}
export function deleteWorker(plan:AgentPlan,id:string):AgentPlan {
  const root=plan.Nodes.find(n=>n.Id==='root');
  if(id===root?.Id||!plan.Nodes.some(n=>n.Id===id))return clonePlan(plan);
  const p=clonePlan(plan);p.Nodes=p.Nodes.filter(n=>n.Id!==id);p.Nodes.forEach(n=>{if(n.ParentId===id)n.ParentId=null;});return p;
}
export function reconnectLink(plan:AgentPlan,from:string,to:string,attached?:{from:string;to:string},fromSide:EndpointSide='out',toSide:EndpointSide='in'):{plan?:AgentPlan;reason?:string} {
  if(!sideAllows(from,fromSide,'source')||!sideAllows(to,toSide,'target'))return {reason:'Use an output socket to start and an input socket to finish the connection.'};
  const p=clonePlan(plan);
  if(attached&&(isStage(attached.from)||isStage(attached.to))&&!hiddenLink(p,attached.from,attached.to))p.HiddenStageLinks.push({FromId:attached.from,ToId:attached.to});
  else if(attached){const n=p.Nodes.find(n=>n.Id===attached.to);if(n)n.ParentId=null;}
  const check=canConnect(p,from,to);if(!check.ok)return {reason:check.reason};
  if(isStage(from)||isStage(to))p.HiddenStageLinks=p.HiddenStageLinks.filter(l=>!(l.FromId===from&&l.ToId===to));
  else {const n=p.Nodes.find(n=>n.Id===to);if(n)n.ParentId=from;}
  return {plan:p};
}
export function isStage(id: string): id is StageId { return (STAGE_IDS as readonly string[]).includes(id); }
export function canConnect(plan: AgentPlan, from: string, to: string): { ok: boolean; reason?: string } {
  if (from === to) return { ok: false, reason: 'A node cannot connect to itself.' };
  if (!isStage(from) && !plan.Nodes.some(n => n.Id === from) || !isStage(to) && !plan.Nodes.some(n => n.Id === to)) return { ok: false, reason: 'That connection endpoint no longer exists.' };
  const root=plan.Nodes.find(n=>n.Id==='root');
  const reachable=new Set<string>();if(root){reachable.add(root.Id);let changed=true;while(changed){changed=false;for(const n of plan.Nodes)if(n.ParentId&&reachable.has(n.ParentId)&&!reachable.has(n.Id)){reachable.add(n.Id);changed=true;}}}
  const validRoute = from === '$input' ? root?.Id===to : to === '$review' ? !isStage(from) && reachable.has(from) && plan.Nodes.some(n => n.Id === from && !plan.Nodes.some(child => reachable.has(child.Id)&&child.ParentId === from)) : from === '$review' && to === '$output' || !isStage(from) && !isStage(to);
  if (!validRoute) return { ok: false, reason: 'That connection does not match the plan stage route.' };
  if (isStage(from) || isStage(to)) return { ok: true };
  if(to==='root')return {ok:false,reason:'The supervisor cannot report to a worker.'};
  let cursor: string | null = from;const visited=new Set<string>();
  while (cursor) { if (cursor === to || visited.has(cursor)) return { ok: false, reason: 'That connection would create a cycle.' }; visited.add(cursor);cursor = plan.Nodes.find(n => n.Id === cursor)?.ParentId ?? null; }
  return { ok: true };
}
export function defaultLayout(plan: AgentPlan): AgentPlan {
  const out = clonePlan(plan); const roots = out.Nodes.filter(n => !n.ParentId); const hasPositions = out.Nodes.some(n => n.X !== 0 || n.Y !== 0);
  if (!hasPositions && out.Nodes.length) {
    const root = roots.find(n => n.Id === 'root') ?? roots[0];
    if (root) { root.X = 270; root.Y = 220; }
    const rest = out.Nodes.filter(n => n !== root);
    rest.forEach((n, i) => { n.X = 540; n.Y = 40 + i * 140; });
  }
  for (const id of STAGE_IDS) if (!out.Stages.some(s => s.Id === id)) {const point=stagePoint(out,id);out.Stages.push({Id:id,X:point.x,Y:point.y});}
  return out;
}
export function remapDuplicate(plan: AgentPlan, ids: string[], makeId: () => string): AgentPlan {
  const out = clonePlan(plan); if (out.Nodes.length + ids.length > MAX_NODES) return out;
  const selected = new Set(ids); const originals = out.Nodes.filter(n => n.Id!=='root'&&selected.has(n.Id)); const map = new Map(originals.map(n => [n.Id, makeId()]));
  originals.forEach(n => out.Nodes.push({ ...n, Id: map.get(n.Id)!, ParentId: n.ParentId && map.has(n.ParentId) ? map.get(n.ParentId)! : n.ParentId, Name: `${n.Name.slice(0,75)} copy`, X: n.X + 40, Y: n.Y + 40 }));
  return out;
}
