import {useEffect,useState} from 'react';
import {Button,Glass,Dialog,useToast} from 'open-glass-ui';
import {Select} from './GlassSelect';
import {Plus,Play,Pencil,Trash2} from 'lucide-react';
import {request,isDesktop} from './bridge';
import {builtinAssistants} from './assistants-data';
import type {Assistant} from './assistants-data';
import type {HarnessInfo} from './harness-store';
import './Harness.css';

export default function AssistantsPage({workingDirectory,onStarted}:{workingDirectory:string;onStarted:(id:string)=>void}){
  const {toast}=useToast();
  const [custom,setCustom]=useState<Assistant[]>([]),[harnesses,setHarnesses]=useState<HarnessInfo[]>([]),[harness,setHarness]=useState('codex'),[cwd,setCwd]=useState(workingDirectory),[edit,setEdit]=useState<Assistant|null>(null);
  useEffect(()=>{if(!isDesktop)return;request<Assistant[]|null>('storeGet',{Name:'assistants'}).then(a=>a&&setCustom(a)).catch(()=>{});request<HarnessInfo[]>('harnesses').then(h=>{setHarnesses(h);const f=h.find(x=>x.Available);if(f)setHarness(f.Id);}).catch(()=>{});},[]);
  const persist=async(next:Assistant[])=>{setCustom(next);try{await request('storeSet',{Name:'assistants',Value:next});}catch(e){toast({title:'Could not save assistants',description:(e as Error).message});}};
  const start=async(a:Assistant)=>{try{const s=await request<{Id:string}>('harnessCreate',{Harness:harness,Cwd:cwd,Rules:a.Rules,AssistantId:a.Id,Title:a.Name});onStarted(s.Id);}catch(e){toast({title:'Could not start chat',description:(e as Error).message,duration:6500});}};
  const saveEdit=()=>{if(!edit||!edit.Name.trim()||!edit.Rules.trim()){toast({title:'Add a name and rules'});return;}const rec={...edit,Id:edit.Id||'custom-'+Date.now().toString(36)};persist(custom.some(c=>c.Id===rec.Id)?custom.map(c=>c.Id===rec.Id?rec:c):[...custom,rec]);setEdit(null);};
  if(!isDesktop)return <div className="empty-state compact"><h2>Assistants need the desktop app</h2></div>;
  const card=(a:Assistant)=><Glass key={a.Id} material="regular" className="acard"><span className="tag">{a.Builtin?'BUILT-IN':'CUSTOM'}</span><h3>{a.Name}</h3><p>{a.Description||a.Rules.slice(0,120)}</p><div className="row">{!a.Builtin&&<><Button size="small" variant="quiet" aria-label="Edit" onClick={()=>setEdit(a)}><Pencil size={14}/></Button><Button size="small" variant="quiet" aria-label="Delete" onClick={()=>persist(custom.filter(c=>c.Id!==a.Id))}><Trash2 size={14}/></Button></>}<Button size="small" variant="primary" leadingIcon={<Play size={14}/>} onClick={()=>start(a)}>Start chat</Button></div></Glass>;
  return <div className="settings-content page-pad">
    <Glass material="regular" className="setting-card"><span className="eyebrow">START WITH</span><div className="form-grid"><Select aria-label="Agent" label="Agent" value={harness} options={harnesses.map(h=>({value:h.Id,label:h.Name+(h.Available?'':' (not found)')}))} onChange={e=>setHarness(e.target.value)}/><label>Working folder<input className="hinput" value={cwd} onChange={e=>setCwd(e.target.value)}/></label></div></Glass>
    <div className="section-heading"><div><h2>Your assistants</h2><p>Reusable rules that are sent with a chat's first message.</p></div><Button size="small" leadingIcon={<Plus size={15}/>} onClick={()=>setEdit({Id:'',Name:'',Description:'',Rules:''})}>New assistant</Button></div>
    {custom.length>0&&<div className="grid-cards">{custom.map(card)}</div>}
    <div className="section-heading"><div><h2>Built-in assistants</h2></div></div>
    <div className="grid-cards">{builtinAssistants.map(card)}</div>
    <Dialog title={edit?.Id?'Edit assistant':'New assistant'} open={edit!==null} onOpenChange={o=>{if(!o)setEdit(null);}}>{edit&&<div className="form-grid">
      <label className="full">Name<input className="hinput" value={edit.Name} onChange={e=>setEdit({...edit,Name:e.target.value})}/></label>
      <label className="full">Short description<input className="hinput" value={edit.Description} onChange={e=>setEdit({...edit,Description:e.target.value})}/></label>
      <label className="full">Rules<textarea value={edit.Rules} onChange={e=>setEdit({...edit,Rules:e.target.value})} placeholder="How should this assistant behave?"/></label>
      <div className="full dialog-actions"><Button variant="quiet" onClick={()=>setEdit(null)}>Cancel</Button><Button variant="primary" onClick={saveEdit}>Save</Button></div></div>}</Dialog>
  </div>;
}
