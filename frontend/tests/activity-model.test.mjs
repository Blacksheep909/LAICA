import test from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import ts from 'typescript';
const source=await readFile(new URL('../src/activity-model.ts',import.meta.url),'utf8');
const js=ts.transpileModule(source,{compilerOptions:{module:ts.ModuleKind.ESNext,target:ts.ScriptTarget.ES2022}}).outputText;
const {crewFor,recordedWorking,eventsFor,workArea,eventTitle,switchTimeline}=await import(`data:text/javascript;base64,${Buffer.from(js).toString('base64')}`);
const now=Date.now(),agent=(id,offset=0,state='running')=>({Id:id,ParentId:id==='root'?null:'root',State:state,UpdatedUtc:new Date(now-offset).toISOString(),RecentEvents:[]});
test('one supervisor and three recent workers are shown; earlier threads remain intact',()=>{
 const snapshot={SessionId:'root',Agents:[agent('old',500000,'complete'),agent('root'),agent('a',300),agent('b',200),agent('c',100)],Events:[]};
 const result=crewFor(snapshot,now);assert.deepEqual(result.crew.map(a=>a.Id),['root','c','b','a']);assert.deepEqual(result.history.map(a=>a.Id),['old']);assert.equal(snapshot.Agents.length,5);
});
test('additional recently working agents are surfaced rather than silently dropped',()=>{
 const result=crewFor({SessionId:'root',Agents:['root','a','b','c','d'].map(id=>agent(id)),Events:[]},now);assert.equal(result.crew.length,4);assert.equal(result.overflow.length,1);
});
test('quiet, complete and missing-timestamp agents do not count as recently working',()=>{
 assert.equal(recordedWorking(agent('a',61000),now),false);assert.equal(recordedWorking(agent('a',0,'complete'),now),false);assert.equal(recordedWorking({...agent('a'),UpdatedUtc:undefined},now),false);
});
test('logs retain historical workers and deduplicate events from both observer lists',()=>{
 const old={AgentId:'old',TimeUtc:new Date(now-1000).toISOString(),Kind:'command_complete',Text:'Ran test',Command:'pnpm test',ExitCode:0};
 const latest={...old,AgentId:'root',TimeUtc:new Date(now).toISOString()};
 const snapshot={Agents:[{Id:'old',RecentEvents:[old]}],Events:[latest,old]};assert.equal(eventsFor(snapshot).length,2);assert.equal(eventsFor(snapshot,'old').length,1);assert.equal(eventsFor(snapshot)[0].AgentId,'root');
});
test('concrete work categories and outcomes preserve command failures',()=>{
 assert.equal(workArea({Command:'pnpm test'}),'terminal');assert.equal(workArea({Tool:'browser.getScreenshot'}),'browser');assert.equal(workArea({Path:'x.cs'}),'files');assert.equal(workArea({Tool:'followup_task'}),'handoff');assert.match(eventTitle({Command:'pnpm check',ExitCode:2,Kind:'command_complete'}),/^Command failed/);
});
test('a completed command without an exit code is not labelled pending',()=>{
 assert.match(eventTitle({Command:'pnpm check',Kind:'command_complete'}),/^Ran:/);
 assert.match(eventTitle({Command:'pnpm check',Kind:'command_failed',ExitCode:0}),/^Command failed:/);
});
test('tag-team switches and waits form a timeline and count as handoff work',()=>{
 const ev=[{Kind:'user',Text:'go',TimeUtc:'2026-10-09T01:00:00Z'},{Kind:'switch',Text:'Switched to Claude Code because Codex ran out of usage.',Detail:JSON.stringify({From:'Codex',To:'Claude Code',Reason:'limit'}),TimeUtc:'2026-10-09T02:00:00Z'},{Kind:'pairwait',Text:'Both out of usage.',Detail:'2026-10-09T03:00:00Z',TimeUtc:'2026-10-09T02:30:00Z'}];
 const steps=switchTimeline(ev);assert.equal(steps.length,2);assert.equal(steps[0].from,'Codex');assert.equal(steps[0].to,'Claude Code');assert.equal(steps[0].reason,'limit');assert.equal(steps[1].wait,true);
 assert.equal(workArea({Kind:'switch'}),'handoff');assert.equal(workArea({Kind:'pairwait'}),'handoff');
});
