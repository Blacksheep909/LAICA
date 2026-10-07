import {useCallback,useEffect,useMemo,useState} from 'react';
import {Button,Glass,Dialog,useToast} from 'open-glass-ui';
import {Wrench,Bot,Sparkles,Code2,Globe,Palette,NotebookPen,Database,BookOpen,Search,Check,X,Download,Wand2,Monitor,TriangleAlert,Folder,FileText} from 'lucide-react';
import type {ReactNode} from 'react';
import {request,isDesktop,isRemote} from './bridge';

interface Input { Key:string; Label:string; Hint:string; Kind:'folder'|'file'|'text'|'env'; Secret:boolean; Required:boolean }
interface Plugin { Id:string; Kind:'mcp'|'skill'|'agent'|'runtime'; Name:string; Summary:string; Category:string; Home:string; Needs:string[]; Suggested:boolean; Because:string; Inputs:Input[]; Preview:string; InstalledOn:string[]; AgentInstalled:boolean }
interface Library { Items:Plugin[]; Programs:string[]; Targets:{claude:boolean;codex:boolean} }

const icons:Record<string,ReactNode>={Agents:<Bot size={20}/>,Essentials:<Sparkles size={20}/>,Developer:<Code2 size={20}/>,Browser:<Globe size={20}/>,Creative:<Palette size={20}/>,Productivity:<NotebookPen size={20}/>,Data:<Database size={20}/>,Skills:<BookOpen size={20}/>,Runtimes:<Wrench size={20}/>};
const targetName:Record<string,string>={claude:'Claude Code',codex:'Codex',shared:'Shared skills'};
const filters=['All','Suggested','Installed','Agents','Essentials','Developer','Browser','Creative','Productivity','Data','Skills','Runtimes'];

/** The plugin library: click Install to add MCP servers, skills and agents. Programs already on this computer are suggested first. */
export default function PluginsPage(){
  const {toast}=useToast();
  const [lib,setLib]=useState<Library|null>(null),[error,setError]=useState(''),[q,setQ]=useState(''),[filter,setFilter]=useState('All');
  const [open,setOpen]=useState<Plugin|null>(null);
  const load=useCallback(()=>{request<Library>('pluginLibrary').then(l=>{setLib(l);setError('');}).catch(e=>setError((e as Error).message));},[]);
  useEffect(()=>{if(isDesktop)load();},[load]);
  const tool=(p:Plugin)=>p.Kind==='agent'||p.Kind==='runtime';
  const isInstalled=(p:Plugin)=>tool(p)?p.AgentInstalled:p.InstalledOn.length>0;
  const shown=useMemo(()=>{
    if(!lib)return [];const words=q.trim().toLowerCase().split(/\s+/).filter(Boolean);
    return lib.Items.filter(p=>{
      if(filter==='Suggested'&&!p.Suggested)return false;if(filter==='Installed'&&!isInstalled(p))return false;
      if(!['All','Suggested','Installed'].includes(filter)&&p.Category!==filter)return false;
      return words.every(w=>(p.Name+' '+p.Summary+' '+p.Category+' '+p.Because).toLowerCase().includes(w));
    });
  },[lib,q,filter]);
  const suggested=useMemo(()=>filter==='All'&&!q?shown.filter(p=>p.Suggested):[],[shown,filter,q]);
  const rest=useMemo(()=>shown.filter(p=>!suggested.includes(p)),[shown,suggested]);
  const remove=async(p:Plugin,t:string)=>{try{await request('pluginRemove',{Id:p.Id,Target:t});toast({title:`${p.Name} removed from ${targetName[t]}`});load();}catch(e){toast({title:'Could not remove',description:(e as Error).message,duration:7000});}};
  if(!isDesktop)return <div className="empty-state"><Bot size={26}/><h2>Open the desktop app</h2><p>The plugin library installs tools for the agents on this computer.</p></div>;

  const card=(p:Plugin)=><Glass key={p.Id} material="regular" className={`plugin-card ${isInstalled(p)?'is-installed':''}`}>
    <div className="plugin-top"><span className="plugin-icon">{icons[p.Category]??<Wand2 size={20}/>}</span><div className="plugin-title"><b>{p.Name}</b><small>{p.Kind==='mcp'?'MCP server':p.Kind==='skill'?'Skill':p.Kind==='runtime'?'Runtime':'Agent'} · {p.Category}</small></div></div>
    <p className="plugin-sum">{p.Summary}</p>
    <div className="plugin-tags">
      {p.Because&&!isInstalled(p)&&<span className="ptag is-fit"><Monitor size={11}/>{p.Because}</span>}
      {p.InstalledOn.map(t=><span key={t} className="ptag is-ok"><Check size={11}/>{targetName[t]}<button type="button" aria-label={`Remove ${p.Name} from ${targetName[t]}`} title={`Remove from ${targetName[t]}`} onClick={()=>{void remove(p,t);}}><X size={10}/></button></span>)}
      {tool(p)&&p.AgentInstalled&&<span className="ptag is-ok"><Check size={11}/>Installed</span>}
      {p.Needs.length>0&&<span className="ptag is-warn"><TriangleAlert size={11}/>Needs {p.Needs.join(' and ')}</span>}
    </div>
    <div className="plugin-foot">{tool(p)&&p.AgentInstalled?<span className="plugin-done">{p.Kind==='runtime'?'Installed on this computer':'Ready to use in chats and teams'}</span>:<Button size="small" variant={isInstalled(p)?undefined:'primary'} leadingIcon={<Download size={14}/>} disabled={p.Needs.length>0&&p.Kind!=='skill'} onClick={()=>setOpen(p)}>{isInstalled(p)?'Add to another':'Install'}</Button>}</div>
  </Glass>;

  return <div className="plugins-page">
    <div className="plugins-tools">
      <label className="palette-search plugins-search"><Search size={15}/><input value={q} onChange={e=>setQ(e.target.value)} placeholder="Search plugins, tools and agents" aria-label="Search plugins"/></label>
      <div className="plugins-filters" role="tablist" aria-label="Filter plugins">{filters.map(f=><button key={f} type="button" role="tab" aria-selected={filter===f} className={`pfilter ${filter===f?'on':''}`} onClick={()=>setFilter(f)}>{f}</button>)}</div>
    </div>
    {lib&&lib.Programs.length>0&&<p className="plugins-found"><Monitor size={13}/> Found on this computer: {lib.Programs.join(', ')}</p>}
    {error&&<p className="error-text">{error}</p>}
    {!lib&&!error&&<p className="plugins-found">Looking at what is installed on this computer…</p>}
    {suggested.length>0&&<><h2 className="plugins-h">Suggested for this computer</h2><div className="plugins-grid">{suggested.map(card)}</div></>}
    {rest.length>0&&<>{suggested.length>0&&<h2 className="plugins-h">Everything else</h2>}<div className="plugins-grid">{rest.map(card)}</div></>}
    {lib&&!shown.length&&<p className="plugins-found">Nothing matches. Try another word or filter.</p>}
    {open&&lib&&<InstallDialog plugin={open} lib={lib} onClose={()=>setOpen(null)} onDone={()=>{setOpen(null);load();}}/>}
  </div>;
}

function InstallDialog({plugin,lib,onClose,onDone}:{plugin:Plugin;lib:Library;onClose:()=>void;onDone:()=>void}){
  const {toast}=useToast();
  const choices=plugin.Kind==='mcp'?[['claude','Claude Code',lib.Targets.claude],['codex','Codex',lib.Targets.codex]]:plugin.Kind==='skill'?[['claude','Claude Code',true],['codex','Codex',true],['shared','Shared (works with both)',true]]:[];
  const [targets,setTargets]=useState<string[]>(()=>choices.filter(c=>c[2]&&!plugin.InstalledOn.includes(c[0] as string)).map(c=>c[0] as string).slice(0,plugin.Kind==='skill'?1:2));
  const [values,setValues]=useState<Record<string,string>>({}),[busy,setBusy]=useState(false);
  const set=(k:string,v:string)=>setValues(cur=>({...cur,[k]:v}));
  const browse=async(i:Input)=>{try{if(isRemote)return;if(i.Kind==='folder'){const p=await request<string|null>('pickFolder');if(p)set(i.Key,p);}else{const p=await request<string[]|null>('pickFiles');if(p?.[0])set(i.Key,p[0]);}}catch(e){toast({title:'Could not browse',description:(e as Error).message});}};
  const ready=plugin.Kind==='agent'||plugin.Kind==='runtime'||(targets.length>0&&plugin.Inputs.every(i=>!i.Required||(values[i.Key]??'').trim()));
  const preview=useMemo(()=>plugin.Preview.replace(/\{([A-Za-z_]+)\}/g,(_m,k:string)=>{const i=plugin.Inputs.find(x=>x.Key===k);const v=values[k];return i?.Secret?'••••••':v?(v.includes(' ')?`"${v}"`:v):`<${i?.Label.toLowerCase()??k}>`;}),[plugin,values]);
  const install=async()=>{
    setBusy(true);
    try{
      const r=await request<{Installed:string[];Output?:string}>('pluginInstall',{Id:plugin.Id,Targets:targets,Values:values});
      toast({title:`${plugin.Name} installed`,description:plugin.Kind==='runtime'?'Tools that needed it can now be installed.':plugin.Kind==='agent'?'It now appears when you choose a model and in teams.':plugin.Kind==='mcp'?`Added to ${r.Installed.map(t=>targetName[t]).join(' and ')}. New chats will have it.`:`Added to ${r.Installed.map(t=>targetName[t]).join(' and ')}.`,duration:7000});
      onDone();
    }catch(e){toast({title:'Could not install',description:(e as Error).message,duration:12000});setBusy(false);}
  };
  return <Dialog title={`Install ${plugin.Name}`} open onOpenChange={o=>{if(!o&&!busy)onClose();}}>
    <div className="plugin-dialog">
      <p>{plugin.Summary}</p>
      {choices.length>0&&<fieldset className="plugin-targets"><legend>Install for</legend>{choices.map(([id,label,ok])=><label key={id as string} className={`chk ${ok?'':'is-off'}`}><input type="checkbox" disabled={!ok||busy} checked={targets.includes(id as string)} onChange={e=>setTargets(cur=>e.target.checked?[...cur,id as string]:cur.filter(x=>x!==id))}/>{label as string}{!ok&&<small> (not installed)</small>}{plugin.InstalledOn.includes(id as string)&&<small> (already added)</small>}</label>)}</fieldset>}
      {plugin.Inputs.map(i=><label key={i.Key} className="plugin-field">{i.Label}
        <span className="plugin-field-row"><input className="hinput" type={i.Secret?'password':'text'} autoComplete="off" spellCheck={false} placeholder={i.Hint} value={values[i.Key]??''} onChange={e=>set(i.Key,e.target.value)} disabled={busy}/>{(i.Kind==='folder'||i.Kind==='file')&&!isRemote&&<Button size="small" onClick={()=>{void browse(i);}} leadingIcon={i.Kind==='folder'?<Folder size={14}/>:<FileText size={14}/>}>Browse</Button>}</span>
        {i.Secret&&<small>Stored in the agent's own settings on this computer, never sent anywhere else.</small>}</label>)}
      <div className="plugin-run"><small>This will run</small><code>{preview}</code></div>
      {(plugin.Kind==='agent'||plugin.Kind==='runtime')&&<p className="plugin-note">{plugin.Preview.startsWith('winget')?'Windows downloads it with the Windows Package Manager and may ask for your permission. This can take a few minutes.':'Installing downloads the package from '+(plugin.Preview.startsWith('npm')?'npm':'PyPI')+' and can take a minute or two.'}</p>}
      <div className="dialog-actions"><Button variant="quiet" onClick={onClose} disabled={busy}>Cancel</Button><Button variant="primary" leadingIcon={<Download size={14}/>} disabled={!ready||busy} onClick={()=>{void install();}}>{busy?'Installing…':'Install'}</Button></div>
    </div>
  </Dialog>;
}
