import {useCallback,useEffect,useState} from 'react';
import {Button,Glass,Dialog,useToast} from 'open-glass-ui';
import {Select} from './GlassSelect';
import {Plus,Play,Pencil,Trash2,Clock} from 'lucide-react';
import {request,subscribe,isDesktop} from './bridge';
import type {HarnessInfo} from './harness-store';
import './Harness.css';

interface Task { Id:string; Name:string; Kind:'interval'|'cron'|'once'; Prompt:string; Harness:string; Cwd:string; Mode:string; Cron:string; Minutes:number; RunAt:string; Enabled:boolean; NewSession:boolean; Isolate:boolean; SessionId:string; LastRunUtc:string }
const blank=(cwd:string,harness:string):Task=>({Id:'',Name:'',Kind:'interval',Prompt:'',Harness:harness,Cwd:cwd,Mode:'',Cron:'0 9 * * 1-5',Minutes:60,RunAt:'',Enabled:true,NewSession:true,Isolate:false,SessionId:'',LastRunUtc:''});
const describe=(t:Task)=>t.Kind==='interval'?`Every ${t.Minutes} min`:t.Kind==='cron'?`Cron ${t.Cron}`:`Once ${t.RunAt?new Date(t.RunAt).toLocaleString():''}`;

export default function TasksPage({workingDirectory}:{workingDirectory:string}){
  const {toast}=useToast();
  const [tasks,setTasks]=useState<Task[]>([]),[harnesses,setHarnesses]=useState<HarnessInfo[]>([]),[edit,setEdit]=useState<Task|null>(null);
  const fail=useCallback((title:string)=>(e:Error)=>toast({title,description:e.message,duration:6500}),[toast]);
  const reload=useCallback(()=>request<Task[]>('taskList').then(setTasks).catch(fail('Could not load tasks')),[fail]);
  useEffect(()=>{if(!isDesktop)return;reload();request<HarnessInfo[]>('harnesses').then(setHarnesses).catch(()=>{});return subscribe(m=>{if(m.Type==='harnessSessions')reload();});},[reload]);
  const save=async(t:Task)=>{try{await request('taskSave',{...t,Enabled:t.Enabled,NewSession:t.NewSession,Isolate:t.Isolate});setEdit(null);reload();}catch(e){fail('Could not save task')(e as Error);}};
  const first=harnesses.find(h=>h.Available)?.Id??'codex';
  if(!isDesktop)return <div className="empty-state compact"><h2>Scheduled tasks need the desktop app</h2></div>;
  return <div className="settings-content page-pad">
    <div className="section-heading"><div><h2>Scheduled tasks</h2><p>Run a prompt on a schedule while LAICA is open (it keeps running from the tray when the window is closed).</p></div><Button size="small" variant="primary" leadingIcon={<Plus size={15}/>} onClick={()=>setEdit(blank(workingDirectory,first))}>New task</Button></div>
    {!tasks.length&&<div className="empty-state compact"><Clock size={24}/><h2>No scheduled tasks</h2><p>Create one to run Codex or Claude Code automatically.</p></div>}
    {tasks.map(t=><Glass key={t.Id} material="regular" className="list-row"><div className="grow"><strong>{t.Name}</strong><small>{describe(t)} · {harnesses.find(h=>h.Id===t.Harness)?.Name??t.Harness} · {t.Cwd}</small><small>{t.LastRunUtc?`Last run ${new Date(t.LastRunUtc).toLocaleString()}`:'Not run yet'}</small></div>
      <label className="chk"><input type="checkbox" checked={t.Enabled} onChange={e=>save({...t,Enabled:e.target.checked})}/> On</label>
      <Button size="small" variant="quiet" leadingIcon={<Play size={14}/>} onClick={()=>request('taskRun',{Id:t.Id}).then(()=>toast({title:'Task started'})).catch(fail('Could not run task'))}>Run now</Button>
      <Button size="small" variant="quiet" aria-label="Edit" onClick={()=>setEdit(t)}><Pencil size={14}/></Button>
      <Button size="small" variant="quiet" aria-label="Delete" onClick={()=>request('taskDelete',{Id:t.Id}).then(reload).catch(fail('Could not delete'))}><Trash2 size={14}/></Button></Glass>)}
    <Dialog title={edit?.Id?'Edit task':'New task'} open={edit!==null} onOpenChange={o=>{if(!o)setEdit(null);}}>{edit&&<div className="form-grid">
      <label className="full">Name<input className="hinput" value={edit.Name} onChange={e=>setEdit({...edit,Name:e.target.value})}/></label>
      <Select aria-label="Agent" label="Agent" value={edit.Harness} options={harnesses.map(h=>({value:h.Id,label:h.Name}))} onChange={e=>setEdit({...edit,Harness:e.target.value,Mode:''})}/>
      <label>Working folder<input className="hinput" value={edit.Cwd} onChange={e=>setEdit({...edit,Cwd:e.target.value})}/></label>
      <Select aria-label="Schedule" label="Schedule" value={edit.Kind} options={[{value:'interval',label:'Every N minutes'},{value:'cron',label:'Cron expression'},{value:'once',label:'One time'}]} onChange={e=>setEdit({...edit,Kind:e.target.value as Task['Kind']})}/>
      {edit.Kind==='interval'&&<label>Minutes<input className="hinput" type="number" min={1} value={edit.Minutes} onChange={e=>setEdit({...edit,Minutes:Math.max(1,Number(e.target.value)||1)})}/></label>}
      {edit.Kind==='cron'&&<label>Cron (min hour day month weekday)<input className="hinput" value={edit.Cron} onChange={e=>setEdit({...edit,Cron:e.target.value})}/></label>}
      {edit.Kind==='once'&&<label>Run at<input className="hinput" type="datetime-local" value={edit.RunAt} onChange={e=>setEdit({...edit,RunAt:e.target.value})}/></label>}
      <label className="full">Prompt<textarea value={edit.Prompt} onChange={e=>setEdit({...edit,Prompt:e.target.value})} placeholder="What should the agent do each time?"/></label>
      <label className="full chk"><input type="checkbox" checked={edit.NewSession} onChange={e=>setEdit({...edit,NewSession:e.target.checked})}/> Start a fresh chat for every run (otherwise continue the same chat)</label>
      <label className="full chk"><input type="checkbox" checked={edit.Isolate} disabled={!edit.NewSession} onChange={e=>setEdit({...edit,Isolate:e.target.checked})}/> Run each fresh chat in its own git worktree (git repositories only)</label><div className="full dialog-actions"><Button variant="quiet" onClick={()=>setEdit(null)}>Cancel</Button><Button variant="primary" onClick={()=>save(edit)}>Save task</Button></div></div>}</Dialog>
  </div>;
}
