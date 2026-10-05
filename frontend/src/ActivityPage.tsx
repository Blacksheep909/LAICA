import {useEffect,useMemo,useState} from 'react';
import {Button,Glass,SegmentedControl,Select,useToast} from 'open-glass-ui';
import {Activity as ActivityIcon,ArrowUpRight,Check,ChevronRight,Clock,Copy,Download,FileCode2,GitBranch,Globe,Layers,Search,Terminal,Workflow,X} from 'lucide-react';
import {isDesktop,request} from './bridge';
import type {Activity,ActivityAgent,ActivityEvent,Session,TeamSummary,WorkspaceState} from './types';
import {concreteEvent,crewFor,eventTitle,eventsFor,recordedWorking,stateLabel,timestamp,workArea,type WorkArea} from './activity-model';
import './Activity.css';

const areaNames:Record<WorkArea,string>={terminal:'Commands',files:'Files',browser:'Browser & computer',handoff:'Handoffs',update:'Updates'};
const areaIcons={terminal:Terminal,files:FileCode2,browser:Globe,handoff:GitBranch,update:ActivityIcon};
const observedState=(a:ActivityAgent)=>a.State==='running'&&!recordedWorking(a)?'stale':a.State;
function timeAgo(value?:string){const age=Math.max(0,Date.now()-timestamp(value));return !timestamp(value)?'Time not recorded':age<60000?'Updated just now':age<3600000?`Updated ${Math.floor(age/60000)}m ago`:`Updated ${Math.floor(age/3600000)}h ago`}
const shortModel=(model:string)=>model.replace(/^gpt-/,'GPT-');
type Props={state:WorkspaceState;runAction:(method:string,payload?:unknown,success?:string)=>Promise<any>};

export default function ActivityPage({state,runAction}:Props){
 const [source,setSource]=useState(state.Plan.Nodes.find(n=>n.Id==='root')?.ConnectionId==='codex'?'codex':'local');
 const [sessions,setSessions]=useState<Session[]>([]),[teams,setTeams]=useState<TeamSummary[]>([]),[session,setSession]=useState(''),[snapshot,setSnapshot]=useState<Activity|null>(null),[error,setError]=useState(''),[overviewError,setOverviewError]=useState(''),[loading,setLoading]=useState(isDesktop||import.meta.env.DEV),[view,setView]=useState('work'),[agentId,setAgentId]=useState(''),[query,setQuery]=useState(''),[area,setArea]=useState('all'),[examples,setExamples]=useState<Record<string,Activity>>({});
 const example=import.meta.env.DEV&&!isDesktop;
 useEffect(()=>{if(!example)return;let disposed=false;import('./activity-example').then(({exampleActivity,exampleTeams,exampleActivities})=>{if(disposed)return;setExamples(exampleActivities);setSessions(exampleTeams);setTeams(exampleTeams);setSession(exampleActivity.SessionId);setSnapshot(exampleActivity);setLoading(false)});return()=>{disposed=true}},[example]);
 useEffect(()=>{if(source!=='codex'||!isDesktop)return;let disposed=false,running=false;const refresh=async()=>{if(running)return;running=true;try{const [choices,overview]=await Promise.all([request<Session[]>('sessions'),request<TeamSummary[]>('teamOverview')]);if(!disposed){setSessions(choices);setTeams(overview);setSession(current=>choices.some(s=>s.Id===current)?current:choices[0]?.Id??'');setOverviewError('');setLoading(false)}}catch(err){if(!disposed){setOverviewError((err as Error).message);setLoading(false)}}finally{running=false}};refresh();const timer=setInterval(refresh,15000);return()=>{disposed=true;clearInterval(timer)}},[source]);
 useEffect(()=>{if(source!=='codex'||!session||!isDesktop)return;setSnapshot(null);setAgentId('');setError('');let disposed=false,running=false;const poll=async()=>{if(running)return;running=true;try{const result=await request<Activity>('activity',{Id:session});if(!disposed){setSnapshot(result);setError('')}}catch(err){if(!disposed)setError((err as Error).message)}finally{running=false}};poll();const timer=setInterval(poll,2500);return()=>{disposed=true;clearInterval(timer)}},[source,session]);
 const choose=(id:string)=>{setSession(id);setAgentId('');setQuery('');setArea('all');if(isDesktop)setSnapshot(null);else if(example)setSnapshot(examples[id]??null)};
 const crew=useMemo(()=>snapshot?crewFor(snapshot):null,[snapshot]);
 const selected=snapshot?.Agents.find(a=>a.Id===agentId)??crew?.crew[0];
 const all=useMemo(()=>snapshot?eventsFor(snapshot):[],[snapshot]);
 const filtered=all.filter(e=>(!agentId||e.AgentId===agentId)&&(area==='all'||workArea(e)===area)&&(!query||[e.Text,e.Command,e.Tool,e.Path,e.Purpose,e.Outcome,e.Detail].join(' ').toLowerCase().includes(query.toLowerCase())));
 const lastWork=selected&&snapshot?concreteEvent(eventsFor(snapshot,selected.Id)):undefined;
 const task=snapshot?.Task||sessions.find(s=>s.Id===session)?.Title||'Recorded team work';
 return <div className="activity-content mission-content">
  <div className="activity-controls"><SegmentedControl aria-label="Activity source" items={[{value:'codex',label:'Chat teams'},{value:'local',label:'Local runs'}]} value={source} onValueChange={setSource}/>{example&&source==='codex'&&<span className="example-label">Example data · no agents are running</span>}</div>
  {source==='local'?<LocalRun state={state} runAction={runAction}/>:<>
   <div className="fleet-heading"><div><h2>Your chat teams</h2><span>Recent chats · one supervisor and up to three workers</span></div><div className="all-chat-picker">{sessions.length>0&&<Select label="All chats" value={session} options={sessions.map(s=>({value:s.Id,label:`${s.Title||'Untitled chat'}${s.Project?' · '+s.Project:''}`}))} onChange={e=>choose(e.target.value)}/>}</div></div>
   {overviewError&&<p role="alert" className="error-text">Could not refresh teams: {overviewError}</p>}
   <div className="fleet" aria-label="Recent chat teams">{teams.map(t=><button type="button" key={t.Id} className={`fleet-card ${session===t.Id?'selected':''}`} onClick={()=>choose(t.Id)}><span className="fleet-project"><Layers size={13}/>{t.Project||'Chat'}</span><strong>{t.Title||'Untitled chat'}</strong><span className="fleet-task">{t.Task||'No task recorded'}</span><span className="fleet-state"><span className={`work-dot ${t.State}`}/>{stateLabel(t.State)}</span><small>{t.Action||timeAgo(t.ObservedUtc)}</small></button>)}</div>

   {error&&<p role="alert" className="error-text">Updates paused: {error}. The last recorded work remains below.</p>}
   {!snapshot?<div className="empty-state compact"><Workflow size={25}/><h2>{loading?'Reading chat records…':session?'Reading this team’s work…':isDesktop?'No chat teams found yet':'Open the desktop app to follow your teams'}</h2><p>{session?'Commands and outcomes will appear when they are recorded.':'Open a Codex chat, then return here.'}</p></div>:<>
    <div className="mission-heading"><div><span className="eyebrow">{snapshot.Project||'CODEX'} · {timeAgo(snapshot.UpdatedUtc)}</span><h2>{task}</h2></div><SegmentedControl aria-label="Team activity view" items={[{value:'work',label:'Work overview'},{value:'logs',label:'Recorded logs'}]} value={view} onValueChange={setView}/></div>
    {crew&&crew.overflow.length>0&&<p className="overflow-note">{crew.overflow.length} additional worker{crew.overflow.length===1?' has':'s have'} recent recorded activity. All workers remain available in Recorded logs.</p>}
    {view==='work'&&crew&&<><div className="mission-workspace">
     <div className="crew-board"><div className="crew-heading"><h3>Recently observed crew</h3><span>{crew.crew.length} shown · {crew.history.length} earlier thread{crew.history.length===1?'':'s'} in logs</span></div>
      <div className="crew-flow">{crew.crew.map((a,i)=><CrewCard key={a.Id} agent={a} event={concreteEvent(eventsFor(snapshot,a.Id))} selected={selected?.Id===a.Id} supervisor={i===0} onClick={()=>setAgentId(a.Id)}/>)}</div>
      <div className="work-areas">{(['terminal','files','browser','handoff'] as WorkArea[]).map(kind=>{const Icon=areaIcons[kind],count=all.filter(e=>workArea(e)===kind).length;return <button key={kind} onClick={()=>{setView('logs');setArea(kind);setAgentId('')}}><Icon size={16}/><span>{areaNames[kind]}</span><strong>{count}</strong><ChevronRight size={13}/></button>})}</div>
     </div>
     <Glass className="work-detail" material="regular"><div className="work-detail-heading"><span className="eyebrow">SELECTED AGENT</span><h2>{selected?.Name||'Agent'}</h2><p>{shortModel(selected?.Model??'')} · {selected?.Effort||'Reasoning not recorded'}</p></div>
      {selected?.Assignment&&<details className="work-assignment"><summary>Assignment</summary><p>{selected.Assignment}</p></details>}
      <span className="eyebrow">LATEST RECORDED WORK</span>{lastWork?<WorkEntry event={lastWork} expanded/>:<p className="field-note">No command or tool action recorded for this agent yet.</p>}
      {(selected?.Files?.length??0)>0&&<div className="mission-files"><h3>Files touched</h3>{selected?.Files?.slice(-5).map(f=><div key={f.Path}><FileCode2 size={13}/><span title={f.Path}>{f.Path}</span><small>{f.Kind}</small></div>)}</div>}
      <Button variant="quiet" size="small" onClick={()=>{setView('logs');setArea('all')}}>See this agent’s steps <ArrowUpRight size={13}/></Button>
     </Glass>
    </div><section className="recent-work"><div className="crew-heading"><h3>Recent steps across the team</h3><Button size="small" variant="quiet" onClick={()=>{setAgentId('');setArea('all');setView('logs')}}>Open logs <ArrowUpRight size={13}/></Button></div>{all.slice(0,5).map((e,i)=><div className="recent-step" key={`${e.TimeUtc}-${i}`}><span className={`work-dot ${/failed|error/.test(e.Kind)?'error':'complete'}`}/><span className="step-owner">{snapshot.Agents.find(a=>a.Id===e.AgentId)?.Name||'Agent'}</span><span className="step-description">{eventTitle(e)}</span><time>{new Date(e.TimeUtc).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'})}</time></div>)}</section></>}
    {view==='logs'&&<section className="work-log"><div className="log-filters"><Select label="Agent" value={agentId} options={[{value:'',label:'Whole team'},...snapshot.Agents.map(a=>({value:a.Id,label:`${a.Name||'Agent'} · ${stateLabel(observedState(a))}`}))]} onChange={e=>setAgentId(e.target.value)}/><Select label="Work type" value={area} options={[{value:'all',label:'All steps'},...Object.entries(areaNames).map(([value,label])=>({value,label}))]} onChange={e=>setArea(e.target.value)}/><label className="log-search"><span>Search recorded work</span><div><Search size={15}/><input value={query} onChange={e=>setQuery(e.target.value)} placeholder="Command, file, tool or result"/></div></label></div>
     <div className="log-caption"><span>{filtered.length} retained steps · newest first</span><span>Expand a step for command, output and event fields</span></div>
     {filtered.length?filtered.map((e,i)=><div className="log-row" key={`${e.TimeUtc}-${e.AgentId}-${i}`}><div className="log-identity"><time>{new Date(e.TimeUtc).toLocaleTimeString([],{hour:'2-digit',minute:'2-digit',second:'2-digit'})}</time><strong>{snapshot.Agents.find(a=>a.Id===e.AgentId)?.Name||'Agent'}</strong></div><WorkEntry event={e} raw/></div>):<div className="empty-state compact"><Search size={23}/><h2>No matching steps</h2><p>Try another agent, work type or search.</p></div>}
    </section>}
    <p className="mission-notice"><Clock size={12}/> Recorded activity can arrive late. “No recent update” means the log has been quiet for over a minute; it does not prove an agent stopped. Logs show a bounded recent history, with obvious credentials redacted.</p>
   </>}
  </>}
 </div>
}

function CrewCard({agent,event,selected,supervisor,onClick}:{agent:ActivityAgent;event?:ActivityEvent;selected:boolean;supervisor:boolean;onClick:()=>void}){
 const area=workArea(event),Icon=areaIcons[area],state=observedState(agent);
 return <button className={`crew-card ${supervisor?'crew-supervisor':''} ${selected?'selected':''}`} onClick={onClick}><div className="crew-role"><span>{supervisor?'Supervisor':agent.Role||'Worker'}</span><span className={`work-dot ${state}`}/></div><strong>{agent.Name||'Agent'}</strong><small>{shortModel(agent.Model||'Model not recorded')} · {agent.Effort||'—'}</small><div className="crew-action"><Icon size={14}/><span>{event?eventTitle(event):agent.Action||'No work recorded yet'}</span></div><div className="crew-state"><span>{stateLabel(state)}</span><span>{timeAgo(agent.UpdatedUtc).replace('Updated ','')}</span></div></button>
}
function WorkEntry({event:e,expanded=false,raw=false}:{event:ActivityEvent;expanded?:boolean;raw?:boolean}){
 const Icon=areaIcons[workArea(e)];
 return <details className="work-entry" open={expanded||undefined}><summary><Icon size={15}/><span>{eventTitle(e)}</span>{e.ExitCode!=null&&<small className={e.ExitCode===0?'exit-ok':'exit-failed'}>exit {e.ExitCode}</small>}</summary><div className="work-entry-body">
  {e.Purpose&&<p className="entry-purpose">{e.Purpose}</p>}{e.Command&&<><span className="entry-label">Recorded command</span><pre>{e.Command}</pre></>}{e.Tool&&<p className="entry-tool">Tool: <code>{e.Tool}</code></p>}{e.Path&&<p className="entry-tool">File: <code>{e.Path}</code></p>}{e.Outcome&&<p className="entry-outcome">{/failed|error/.test(e.Kind)?<X size={13}/>:<Check size={13}/ >}{e.Outcome}</p>}
  {e.Detail&&<details className="entry-output"><summary>Recorded output / tool detail</summary><pre>{e.Detail}</pre></details>}{!e.Command&&!e.Detail&&!e.Purpose&&<p className="field-note">This record contains a summary only.</p>}{raw&&<details className="entry-output"><summary>Event fields</summary><pre>{JSON.stringify(e,null,2)}</pre></details>}
 </div></details>
}
function LocalRun({state,runAction}:Props){const {toast}=useToast();const last=[...state.RunEvents].reverse().find(e=>e.AgentId==='run'&&['error','cancelled','completed'].includes(e.Kind));return <><div className="activity-summary"><div><span className="eyebrow">{state.Busy?'RUNNING':state.Answer?'FINISHED':last?.Kind==='error'?'FAILED':last?.Kind==='cancelled'?'STOPPED':'LOCAL RUNS'}</span><h2>{state.Busy?'Your team is working.':state.Answer?'Your reviewed answer.':last?.Text||'The next result starts here.'}</h2></div><div className="inline-actions">{state.Busy&&<Button onClick={()=>runAction('stop')}>Stop run</Button>}<Button size="small" disabled={!state.Answer} onClick={async()=>{try{await navigator.clipboard.writeText(state.Answer);toast({title:'Answer copied'})}catch{toast({title:'Could not copy answer'})}}}><Copy size={14}/>Copy answer</Button><Button size="small" disabled={!state.Answer} onClick={()=>runAction('saveResult',{},'Answer saved')}><Download size={14}/>Save answer</Button></div></div><div className="local-activity-layout"><Glass material="regular" className="answer-panel"><h3>Final answer</h3><pre>{state.Answer||last?.Text||'Choose a local service and start a task in Teams.'}</pre></Glass><Glass className="run-events" material="regular"><h3>Recorded steps</h3>{[...state.RunEvents].reverse().map((e,i)=><WorkEntry key={i} event={e} raw/>)}</Glass></div></>}
