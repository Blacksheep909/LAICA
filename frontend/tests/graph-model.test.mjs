import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import ts from 'typescript';

const source = await readFile(new URL('../src/graph-model.ts', import.meta.url), 'utf8');
const js = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2022 } }).outputText;
const model = await import(`data:text/javascript;base64,${Buffer.from(js).toString('base64')}`);
const plan = () => ({ Version: 1, Goal: 'test', Nodes: [
  { Id: 'root', ParentId: null, Name: 'Root', Role: 'supervisor', Model: 'm', Effort: 'medium', Job: '', ConnectionId: '', X: 0, Y: 0 },
  { Id: 'leaf', ParentId: 'root', Name: 'Leaf', Role: 'implementer', Model: 'm', Effort: 'medium', Job: '', ConnectionId: '', X: 0, Y: 0 },
], Stages: [], HiddenStageLinks: [] });

test('default layout places stage anchors and only lays out plans without positions', () => {
  const p = model.defaultLayout(plan());
  assert.ok(p.Stages.find(s=>s.Id==='$input').X+150 < p.Nodes[0].X);
  assert.ok(p.Stages.find(s=>s.Id==='$review').X > Math.max(...p.Nodes.map(n=>n.X+model.NODE_W)));
  assert.ok(p.Stages.find(s=>s.Id==='$output').X > p.Stages.find(s=>s.Id==='$review').X+150);
  assert.equal(p.Nodes[0].X, 270);
  assert.deepEqual(model.defaultLayout(p).Nodes.map(n => [n.X, n.Y]), p.Nodes.map(n => [n.X, n.Y]));
});

test('connections respect stage routes, hierarchy, and cycle prevention', () => {
  const p = plan();
  assert.equal(model.canConnect(p, '$input', 'root').ok, true);
  assert.equal(model.canConnect(p, '$input', 'leaf').ok, false);
  assert.equal(model.canConnect(p, 'leaf', '$review').ok, true);
  assert.equal(model.canConnect(p, 'root', 'leaf').ok, true);
  assert.match(model.canConnect(p, 'leaf', 'root').reason, /supervisor/);
});

test('duplicate remaps internal parents and does not alter input plan', () => {
  const p = plan();p.Nodes.push({...p.Nodes[1],Id:'child',ParentId:'leaf',Name:'Child'}); let next = 0;
  const copy = model.remapDuplicate(p, ['root', 'leaf', 'child'], () => `copy-${++next}`);
  assert.equal(copy.Nodes.length,5);
  assert.equal(copy.Nodes[3].Id, 'copy-1');
  assert.equal(copy.Nodes[4].ParentId, 'copy-1');
  assert.equal(copy.Nodes.filter(n=>n.Id==='root').length,1);
  assert.equal(p.Nodes.length, 3);
  assert.equal(model.snapped(31, true), 40);
  assert.equal(model.snapped(31, false), 31);
});

test('sockets constrain route direction and shared sockets expose every attached edge', () => {
  const p = plan();
  assert.equal(model.sideAllows('$input', 'out', 'source'), true);
  assert.equal(model.sideAllows('$input', 'in', 'target'), false);
  assert.equal(model.sideAllows('root', 'in', 'source'), false);
  assert.equal(model.sideAllows('root', 'out', 'target'), false);
  assert.deepEqual(model.linksAt(p, 'root', 'in'), [{ from: '$input', to: 'root' }]);
  assert.deepEqual(model.linksAt(p, 'root', 'out'), [{ from: 'root', to: 'leaf' }]);
});

test('disconnect records hidden stage routes and reconnect restores them without toggling', () => {
  const p = plan();
  const removed = model.disconnectLink(p, '$input', 'root');
  assert.equal(model.hiddenLink(removed, '$input', 'root'), true);
  const restored = model.reconnectLink(removed, '$input', 'root');
  assert.equal(restored.plan && model.hiddenLink(restored.plan, '$input', 'root'), false);
  assert.equal(model.hiddenLink(p, '$input', 'root'), false);
});

test('reconnect rejects incompatible sockets and cycles without changing the source plan', () => {
  const p = plan();
  p.Nodes.push({ Id: 'grandchild', ParentId: 'leaf', Name: 'Grandchild', Role: 'implementer', Model: 'm', Effort: 'medium', Job: '', ConnectionId: '', X: 0, Y: 0 });
  assert.match(model.reconnectLink(p, 'root', 'leaf', undefined, 'in', 'in').reason, /output socket/);
  assert.match(model.reconnectLink(p, 'grandchild', 'leaf').reason, /cycle/);
  assert.equal(p.Nodes[1].ParentId, 'root');
});

test('root deletion is refused while deleting a linked worker leaves its children disconnected', () => {
  const p = plan();
  p.Nodes.push({ Id: 'grandchild', ParentId: 'leaf', Name: 'Grandchild', Role: 'implementer', Model: 'm', Effort: 'medium', Job: '', ConnectionId: '', X: 0, Y: 0 });
  assert.deepEqual(model.deleteWorker(p, 'root').Nodes.map(n => n.Id), ['root', 'leaf', 'grandchild']);
  const next = model.deleteWorker(p, 'leaf');
  assert.deepEqual(next.Nodes.map(n => n.Id), ['root', 'grandchild']);
  assert.equal(next.Nodes[1].ParentId, null);
});

test('disconnected drafts do not acquire false input or review stage links', () => {
  const p = plan();
  p.Nodes.push({ Id: 'draft', ParentId: null, Name: 'Draft', Role: 'implementer', Model: 'm', Effort: 'medium', Job: '', ConnectionId: '', X: 0, Y: 0 });
  assert.deepEqual(model.allEdges(p).filter(e => e.from === '$input'), [{ from: '$input', to: 'root' }]);
  assert.equal(model.allEdges(p).some(e => e.from === 'draft' || e.to === 'draft'), false);
});

test('reconnecting an attached wire removes its former target and preserves unrelated links',()=>{
 const p=plan();p.Nodes.push({...p.Nodes[1],Id:'other',ParentId:null,Name:'Other'});
 const moved=model.reconnectLink(p,'root','other',{from:'root',to:'leaf'}).plan;
 assert.equal(moved.Nodes.find(n=>n.Id==='leaf').ParentId,null);
 assert.equal(moved.Nodes.find(n=>n.Id==='other').ParentId,'root');
 assert.equal(p.Nodes.find(n=>n.Id==='leaf').ParentId,'root');
 const invalid=model.reconnectLink(p,'leaf','root',{from:'root',to:'leaf'});
 assert.equal(invalid.plan,undefined);assert.equal(p.Nodes[1].ParentId,'root');
});
test('malformed cyclic drafts terminate validation rather than hang',()=>{
 const p=plan();p.Nodes.push({...p.Nodes[1],Id:'a',ParentId:'b'},{...p.Nodes[1],Id:'b',ParentId:'a'},{...p.Nodes[1],Id:'c',ParentId:null});
 assert.match(model.canConnect(p,'a','c').reason,/cycle/);
});
