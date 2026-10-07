import {TeamHandoffSelect,TeamContinue} from './Handoff';
import type {HandoffChoice} from './Handoff';
import {useEffect,useMemo,useState} from 'react';
import {Button,Glass,useToast} from 'open-glass-ui';
import {Select} from './GlassSelect';
import {Plus,Play,Pause,Square,Trash2,Users,MessageSquare,Pencil,GitBranch,ArrowRightLeft} from 'lucide-react';
import {request,isDesktop} from './bridge';
import {pushUndo,runUndo} from './undo';
import {Markdown} from './markdown';
import {useHarness,modeLabel} from './harness-store';
import type {Service,Model} from './types';
import './Harness.css';

interface Spec { Harness:string; Mode:string; ServiceId:string; Model:string }
interface Member extends Spec { Name:string; Role:string }
interface TaskRec { Assignee:string; Title:string; SessionId:string; Status:string; Output:string; Note?:string }
interface Team { Id:string; Title:string; Cwd:string; Isolate:boolean; Leader:Spec; Members:Member[]; Goal:string; Phase:string; Result:string; Tasks:TaskRec[]; LeaderSessionId:string; Running:boolean; Paused?:boolean; Note?:string; HandoffTo?:string; Handoff?:HandoffChoice|null }
const phases=['planning','working','reviewing','done'];
const phaseLabel:Record<string,string>={idle:'Ready',planning:'Leader is planning',working:'Teammates are working',reviewing:'Leader is reviewing',done:'Finished',stopped:'Stopped',failed:'Stopped with a problem'};

export default function TeamPage({workingDirectory,teamId,onOpened,services,models,openChat,onCancel}:{workingDirectory:string;teamId:string|null;onOpened:(id:string)=>void;services:Service[];models:Model[];openChat:(id:string)=>void;onCancel:()=>void}){
  const {toast}=useToast();
  const {teams,reloadTeams,harnesses}=useHarness();
  const team=teams.find(t=>t.Id===teamId) as unknown as Team|undefined;
  const [editing,setEditing]=useState(!teamId),[draft,setDraft]=useState<Team|null>(null),[goal,setGoal]=useState('');
  const available=harnesses.filter(h=>h.Available);
  const blank=useMemo<Team>(()=>({Id:'',Title:'',Cwd:workingDirectory,Isolate:false,Leader:{Harness:available[0]?.Id??'',Mode:'',ServiceId:'',Model:''},Members:[{Name:'Builder',Role:'writes the code',Harness:available[0]?.Id??'',Mode:'',ServiceId:'',Model:''},{Name:'Reviewer',Role:'checks the work',Harness:available[0]?.Id??'',Mode:'',ServiceId:'',Model:''}],Goal:'',Phase:'idle',Result:'',Tasks:[],LeaderSessionId:'',Running:false}),[workingDirectory,available[0]?.Id]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(()=>{if(teamId&&team){setEditing(false);setGoal(g=>g||team.Goal);}else if(!teamId){setEditing(true);setDraft(blank);}},[teamId,team?.Id]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(()=>{if(editing&&!draft)setDraft(team??blank);},[editing]); // eslint-disable-line react-hooks/exhaustive-deps
  if(!isDesktop)return <div className="empty-state compact"><h2>Team mode needs the desktop app</h2></div>;
  const fail=(title:string)=>(e:Error)=>toast({title,description:e.message,duration:7000});
  const defaults=(id:string,spec:Spec):Spec=>{const h=harnesses.find(x=>x.Id===id);const s=services[0]?.Id??'';const m=models.find(x=>x.ConnectionId===s)?.Id??'';return {...spec,Harness:id,Mode:h?.DefaultMode??'',ServiceId:id==='laica'?s:'',Model:id==='laica'?m:''};};
  const agentFields=(spec:Spec,set:(s:Spec)=>void)=><>
    <Select aria-label="Agent" label="Agent" value={spec.Harness} options={available.map(h=>({value:h.Id,label:h.Name}))} onChange={e=>set(defaults(e.target.value,spec))}/>
    <Select aria-label="Permissions" label="Permissions" value={spec.Mode||harnesses.find(h=>h.Id===spec.Harness)?.DefaultMode||''} options={(harnesses.find(h=>h.Id===spec.Harness)?.Modes??[]).map(m=>({value:m,label:modeLabel[m]??m}))} onChange={e=>set({...spec,Mode:e.target.value})}/>
    {spec.Harness==='laica'&&<><Select aria-label="Service" label="Service" value={spec.ServiceId} options={services.map(s=>({value:s.Id,label:s.Name}))} onChange={e=>set({...spec,ServiceId:e.target.value,Model:models.find(m=>m.ConnectionId===e.target.value)?.Id??''})}/><label>Model<input className="hinput" list="team-models" value={spec.Model} onChange={e=>set({...spec,Model:e.target.value})}/></label></>}
    {spec.Harness!=='laica'&&<label>Model (optional)<input className="hinput" placeholder="Agent default" value={spec.Model} onChange={e=>set({...spec,Model:e.target.value})}/></label>}
  </>;
  const save=async()=>{if(!draft)return;try{const saved=await request<Team>('teamSave',{...draft});reloadTeams();setEditing(false);onOpened(saved.Id);toast({title:'Team saved'});}catch(e){fail('Could not save team')(e as Error);}};
  if(editing&&draft){
    const setMember=(i:number,m:Member)=>setDraft({...draft,Members:draft.Members.map((x,j)=>j===i?m:x)});
    return <div className="settings-content page-pad">
      <datalist id="team-models">{models.map(m=><option key={m.Id} value={m.Id}/>)}</datalist>
      <Glass material="regular" className="setting-card"><span className="eyebrow">TEAM</span><div className="form-grid"><label>Name<input className="hinput" value={draft.Title} onChange={e=>setDraft({...draft,Title:e.target.value})} placeholder="e.g. Feature crew"/></label><label>Working folder<input className="hinput" value={draft.Cwd} onChange={e=>setDraft({...draft,Cwd:e.target.value})}/></label>
        <label className="full chk"><input type="checkbox" checked={draft.Isolate} onChange={e=>setDraft({...draft,Isolate:e.target.checked})}/><GitBranch size={13}/> Give each teammate its own git worktree (recommended for repositories, so parallel edits never collide)</label><div className="full"><TeamHandoffSelect value={draft.Handoff} onChange={c=>setDraft({...draft,Handoff:c})} teamId={draft.Id}/></div></div></Glass>
      <Glass material="regular" className="setting-card"><span className="eyebrow">LEADER</span><p>Plans the work, splits it between teammates, then reviews their reports and writes the final answer.</p><div className="form-grid" style={{marginTop:14}}>{agentFields(draft.Leader,l=>setDraft({...draft,Leader:l}))}</div></Glass>
      <div className="section-heading"><div><h2>Teammates</h2><p>They run at the same time, each in its own chat.</p></div><Button size="small" leadingIcon={<Plus size={15}/>} disabled={draft.Members.length>=8} onClick={()=>setDraft({...draft,Members:[...draft.Members,{Name:`Teammate ${draft.Members.length+1}`,Role:'',...defaults(available[0]?.Id??'',{Harness:'',Mode:'',ServiceId:'',Model:''})}]})}>Add teammate</Button></div>
      {draft.Members.map((m,i)=><Glass key={i} material="regular" className="setting-card"><div className="form-grid"><label>Name<input className="hinput" value={m.Name} onChange={e=>setMember(i,{...m,Name:e.target.value})}/></label><label>Role<input className="hinput" value={m.Role} onChange={e=>setMember(i,{...m,Role:e.target.value})} placeholder="What are they good at?"/></label>{agentFields(m,s=>setMember(i,{...m,...s}))}
        <div className="full dialog-actions" style={{marginTop:0}}><Button size="small" variant="quiet" leadingIcon={<Trash2 size={14}/>} disabled={draft.Members.length<=1} onClick={()=>setDraft({...draft,Members:draft.Members.filter((_,j)=>j!==i)})}>Remove</Button></div></div></Glass>)}
      <div className="dialog-actions"><Button variant="quiet" onClick={()=>{if(team){setEditing(false);setDraft(null);}else{setDraft(null);onCancel();}}}>Cancel</Button><Button variant="primary" disabled={!draft.Title.trim()||!draft.Members.length} onClick={save}>Save team</Button></div>
    </div>;
  }
  if(!team)return <div className="empty-state compact"><Users size={26}/><h2>Pick or create a team</h2></div>;
  const stage=phases.indexOf(team.Phase);
  return <div className="settings-content page-pad">
    <Glass material="regular" className="setting-card"><div className="team-head"><div><span className="eyebrow">TEAM</span><h2>{team.Title}</h2><p>{team.Members.length} teammates · {team.Cwd}</p></div><div className="inline-actions"><Button size="small" leadingIcon={<Pencil size={14}/>} disabled={team.Running} onClick={()=>{setDraft(team);setEditing(true);}}>Edit</Button><Button size="small" variant="quiet" leadingIcon={<Trash2 size={14}/>} disabled={team.Running} onClick={()=>{const saved={...team};request('teamDelete',{Id:team.Id}).then(()=>{reloadTeams();onOpened('');const uid=pushUndo('Deleted team '+saved.Title,async()=>{await request('teamSave',{...saved});reloadTeams();onOpened(saved.Id);});toast({title:'Deleted '+saved.Title,description:'Its chats stay in the sidebar.',actionLabel:'Undo',onAction:()=>{void runUndo(uid);},duration:9000});}).catch(fail('Could not delete'));}}>Delete</Button></div></div>
      <label className="goal-box">Goal<textarea value={goal} onChange={e=>setGoal(e.target.value)} placeholder="Describe what the team should achieve." disabled={team.Running}/></label>
      <div className="dialog-actions">{team.Running?<><Button leadingIcon={team.Paused?<Play size={14}/>:<Pause size={14}/>} onClick={()=>request(team.Paused?'teamResume':'teamPause',{Id:team.Id}).then(reloadTeams).catch(fail(team.Paused?'Could not resume':'Could not pause'))}>{team.Paused?'Resume team':'Pause team'}</Button><Button leadingIcon={<Square size={14}/>} onClick={()=>request('teamStop',{Id:team.Id}).catch(fail('Could not stop'))}>Stop team</Button></>:<Button variant="primary" leadingIcon={<Play size={14}/>} disabled={!goal.trim()} onClick={()=>request('teamRun',{Id:team.Id,Goal:goal}).then(reloadTeams).catch(fail('Could not start'))}>{team.Phase==='idle'?'Run team':'Run again'}</Button>}</div></Glass>
    <div className="phase-row">{phases.map((p,i)=><span key={p} className={`phase ${stage>=i?'on':''} ${team.Phase===p&&team.Running?'now':''}`}>{p==='planning'?'Plan':p==='working'?'Work':p==='reviewing'?'Review':'Done'}</span>)}<small>{team.Paused?'Paused':(phaseLabel[team.Phase]??team.Phase)}</small></div>{team.Note&&<p className="field-note"><ArrowRightLeft size={12}/> {team.Note}{team.HandoffTo&&<button type="button" className="link-btn" onClick={()=>window.dispatchEvent(new CustomEvent('laica-open',{detail:team.HandoffTo}))}>Open</button>}</p>}{!team.Running&&team.Phase==='failed'&&/out of usage|ran out/i.test((team.Note??'')+(team.Result??''))&&!team.HandoffTo&&<TeamContinue teamId={team.Id} onDone={reloadTeams}/>}
    {team.LeaderSessionId&&<Button size="small" variant="quiet" leadingIcon={<MessageSquare size={14}/>} onClick={()=>openChat(team.LeaderSessionId)}>Open the leader's chat</Button>}
    {team.Tasks.length>0&&<div className="grid-cards">{team.Tasks.map((t,i)=><Glass key={i} material="regular" className="acard"><span className="tag">{t.Status.toUpperCase()}</span><h3>{t.Assignee} — {t.Title}</h3>{t.Note&&<p className="field-note"><ArrowRightLeft size={12}/> {t.Note}</p>}<p>{t.Output?t.Output.slice(0,260):t.Status==='working'?'Working…':''}</p><div className="row">{t.SessionId&&<Button size="small" variant="quiet" leadingIcon={<MessageSquare size={14}/>} onClick={()=>openChat(t.SessionId)}>Open chat</Button>}</div></Glass>)}</div>}
    {team.Result&&<Glass material="regular" className="setting-card"><span className="eyebrow">RESULT</span><Markdown text={team.Result}/></Glass>}
  </div>;
}
