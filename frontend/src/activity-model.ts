import type {Activity,ActivityAgent,ActivityEvent} from './types';

export type WorkArea='terminal'|'files'|'browser'|'handoff'|'update';
export const timestamp=(value?:string)=>{const n=Date.parse(value??'');return Number.isFinite(n)?n:0};
export function recordedWorking(agent:ActivityAgent,now=Date.now()){
 const age=now-timestamp(agent.UpdatedUtc);
 return agent.State==='running'&&age>=-5000&&age<60000;
}
export function crewFor(snapshot:Activity,now=Date.now()){
 const root=snapshot.Agents.find(a=>!a.ParentId||a.Id===snapshot.SessionId);
 const workers=snapshot.Agents.filter(a=>a!==root).sort((a,b)=>Number(recordedWorking(b,now))-Number(recordedWorking(a,now))||timestamp(b.UpdatedUtc)-timestamp(a.UpdatedUtc)||a.Id.localeCompare(b.Id));
 return {crew:[...(root?[root]:[]),...workers.slice(0,3)],history:workers.slice(3),overflow:workers.slice(3).filter(a=>recordedWorking(a,now))};
}
export function workArea(event?:ActivityEvent):WorkArea{
 if(!event)return 'update';
 const tool=event.Tool??'',kind=event.Kind??'';
 if(event.Command||/command|exec_command|write_stdin/i.test(tool+' '+kind))return 'terminal';
 if(event.Path||/file_change|edit|read|search/.test(kind)||/apply_patch|view_image/.test(tool))return 'files';
 if(/cua|sky|browser|web__|web\.run|web\.search/i.test(tool))return 'browser';
 if(/spawn_agent|followup_task|send_message|wait_agent|handoff/.test(tool+' '+kind))return 'handoff';
 return 'update';
}
export function eventsFor(snapshot:Activity,agentId?:string){
 const entries=[...snapshot.Events,...snapshot.Agents.flatMap(a=>a.RecentEvents??[])];
 const seen=new Set<string>();
 return entries.filter(e=>{if(agentId&&e.AgentId!==agentId)return false;const key=[e.AgentId,e.TimeUtc,e.Kind,e.CallId,e.Text].join('\0');if(seen.has(key))return false;seen.add(key);return true}).sort((a,b)=>timestamp(b.TimeUtc)-timestamp(a.TimeUtc));
}
export const concreteEvent=(events:ActivityEvent[])=>events.find(e=>e.Command||e.Tool||e.Path||/command_|file_change_|tool_|read|search|edit/.test(e.Kind));
export function stateLabel(state:string){return ({running:'Working · recorded',complete:'Finished',completed:'Finished',stale:'No recent update',idle:'Idle',error:'Needs attention',unavailable:'Unavailable',cancelled:'Stopped'} as Record<string,string>)[state]??state??'Unknown'};
export function eventTitle(e:ActivityEvent){
 if(e.Command)return `${/failed|error/.test(e.Kind)||e.ExitCode!=null&&e.ExitCode!==0?'Command failed':e.ExitCode!=null||/complete/.test(e.Kind)?'Ran':'Command requested'}: ${e.Command.split(/\r?\n/)[0]}`;
 return e.Text||e.Tool||e.Path||'Recorded step';
}
