import {useMemo,useState} from 'react';
import {Button,Glass} from 'open-glass-ui';
import {Pin,PinOff,Pencil,Download,FolderSearch,Trash2,Store,Plus,SquarePen,Search,CalendarClock,Sparkles,Workflow,Activity as ActivityIcon,FolderOpen,Folder,MessageSquare,X,Settings,Plug,BookOpen,GitBranch,ChevronDown,ChevronRight,Minus,Users,RefreshCw,FolderPlus,History as HistoryIcon} from 'lucide-react';
import {useHarness} from './harness-store';
import type {TeamRun} from './harness-store';
import {request,isRemote} from './bridge';
import {ContextMenu,ContextMenuTrigger,ContextMenuContent,ContextMenuItem,ContextMenuSeparator,Dialog,DialogContent,DialogTitle,DialogDescription,Tip,WorkingGlyph} from './ui/kit';
import {Elapsed} from './Steps';
import AnalyticsButton from './Analytics';
import {buildProjects,projectOf} from './projects';
import {ProviderChip,providerOfSession,providerOfHistory} from './provider';
import type {Provider} from './provider';

const ago=(when:number)=>{const s=Math.max(0,(Date.now()-when)/1000);if(s<90)return 'now';if(s<3600)return Math.round(s/60)+'m';if(s<86400)return Math.round(s/3600)+'h';if(s<86400*30)return Math.round(s/86400)+'d';return Math.round(s/86400/30)+'mo';};

interface Row { since?:string; paused?:boolean; sid?:string; key:string; title:string; provider:Provider; when:number; project:string; active:boolean; busy?:boolean; branch?:string; past:boolean; open:()=>void; close?:()=>void }

export function MobileBar({page,go}:{page:string;go:(p:string)=>void}){
  const {sessions,active,setActive}=useHarness();
  const b=(id:string,text:string)=><Button key={id} size="small" variant="quiet" className={page===id?'active':''} onClick={()=>go(id)}>{text}</Button>;
  return <div className="mobile-bar" role="navigation" aria-label="Workspace"><Button size="small" variant="primary" onClick={()=>{setActive(null);go('home');}}>New</Button>{sessions.length>0&&<select aria-label="Open a chat" className="hinput" style={{width:150,height:30}} value={page==='chat'&&active?active:''} onChange={e=>{if(e.target.value){setActive(e.target.value);go('chat');}}}><option value="">Chats…</option>{sessions.map(s=><option key={s.Id} value={s.Id}>{s.Title}</option>)}</select>}{b('tasks','Tasks')}{b('assistants','Assistants')}{b('team','Teams')}{b('settings','Settings')}</div>;
}

export default function Sidebar({page,go,version,onGuide,teams,activeTeam,openTeam,newTeam,workingDirectory}:{page:string;go:(p:string)=>void;version:string;onGuide:()=>void;teams:TeamRun[];activeTeam:string|null;openTeam:(id:string)=>void;newTeam:()=>void;workingDirectory:string}){
  const {sessions,active,setActive,close,nameOf,project,setProject,savedProjects,addProject,removeProject,codexProjects,history,historyLoading,reloadHistory,openHistory,pinned,togglePin,rename}=useHarness();
  const [renameFor,setRenameFor]=useState<{id:string;title:string}|null>(null),[query,setQuery]=useState(''),[adding,setAdding]=useState(false),[newPath,setNewPath]=useState(''),[open,setOpen]=useState<Record<string,boolean>>({}),[more,setMore]=useState<Record<string,number>>({});
  const q=query.trim().toLowerCase();
  const [fold,setFold]=useState<Record<string,boolean>>(()=>{try{return JSON.parse(localStorage.getItem('laica.sidebar.fold')||'{}');}catch{return {};}});
  const folded=(id:string)=>!q&&!!fold[id];
  const flip=(id:string)=>setFold(f=>{const n={...f,[id]:!f[id]};try{localStorage.setItem('laica.sidebar.fold',JSON.stringify(n));}catch{/* private mode */}return n;});
  const fb=(id:string,label:string)=><button type="button" className="side-fold" aria-expanded={!folded(id)} aria-label={(folded(id)?'Expand ':'Collapse ')+label} title={(folded(id)?'Expand ':'Collapse ')+label} onClick={()=>flip(id)}>{folded(id)?<ChevronRight size={12}/>:<Minus size={12}/>}</button>;

  const projects=useMemo(()=>buildProjects(savedProjects,sessions,history,codexProjects),[savedProjects,sessions,history,codexProjects]);
  const rows=useMemo<Row[]>(()=>{
    const out:Row[]=[];
    sessions.forEach(s=>{
      const name=s.TeamId?'':projectOf(s.Project||s.Cwd,codexProjects,savedProjects)||(projects.find(p=>p.path.toLowerCase()===(s.Project||s.Cwd).toLowerCase())?.name??'');
      out.push({sid:s.Id,key:'s'+s.Id,title:s.Title,provider:providerOfSession(s,nameOf),when:Date.now()-(s.Busy?0:1000),project:name,active:page==='chat'&&s.Id===active,busy:s.Busy,since:s.BusySince,paused:s.Paused,branch:s.Isolated?s.Branch:undefined,past:false,open:()=>{setActive(s.Id);go('chat');},close:()=>close(s.Id)});
    });
    history.filter(h=>!h.SessionId).forEach(h=>out.push({key:'h'+h.Source+h.ExternalId,title:h.Title,provider:providerOfHistory(h),when:new Date(h.UpdatedUtc).getTime(),project:h.ProjectName||'',active:false,past:true,open:()=>{void openHistory(h,workingDirectory).then(()=>go('chat'));}}));
    return out;
  },[sessions,history,codexProjects,savedProjects,projects,page,active,nameOf,setActive,go,close,openHistory,workingDirectory]);
  const matches=(r:Row)=>!q||(r.title+' '+r.provider.label+' '+r.project).toLowerCase().includes(q);
  const byProject=useMemo(()=>{const m=new Map<string,Row[]>();rows.filter(r=>matches(r)&&!pinned.includes(r.key)).sort((a,b)=>b.when-a.when).forEach(r=>{const k=r.project.toLowerCase();(m.get(k)??m.set(k,[]).get(k)!).push(r);});return m;},[rows,q,pinned]); // eslint-disable-line react-hooks/exhaustive-deps
  const pinnedRows=useMemo(()=>pinned.map(k=>rows.find(r=>r.key===k)).filter((r):r is Row=>!!r&&matches(r)),[pinned,rows,q]); // eslint-disable-line react-hooks/exhaustive-deps
  const selectedName=project?(projectOf(project,codexProjects,savedProjects)||projects.find(p=>p.path.toLowerCase()===project.toLowerCase())?.name||'').toLowerCase():'';
  const isOpen=(name:string,i:number)=>q?true:open[name]??(i<3||name.toLowerCase()===selectedName);
  const toggle=(name:string,i:number)=>setOpen(o=>({...o,[name]:!isOpen(name,i)}));
  const unfiled=byProject.get('')??[];
  const shownProjects=projects.filter(p=>!q||byProject.has(p.name.toLowerCase())||p.name.toLowerCase().includes(q));

  const item=(id:string,title:string,Icon:typeof Plus)=><Button key={id} variant="quiet" className={`nav-item ${page===id?'active':''}`} onClick={()=>go(id)} aria-current={page===id?'page':undefined} leadingIcon={<Icon size={16}/>}>{title}</Button>;
  const browse=async()=>{try{const p=await request<string|null>('pickFolder');if(p){addProject(p);setAdding(false);setNewPath('');}}catch{/* cancelled */}};
  const submit=()=>{if(newPath.trim()){addProject(newPath);setAdding(false);setNewPath('');}};
  const exportChat=async(id:string,title:string)=>{try{const text=await request<string>('harnessExport',{Id:id});const name=title.replace(/[^\w .-]+/g,'').trim()||'chat';
    if(isRemote){const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([text],{type:'text/markdown'}));a.download=name+'.md';a.click();URL.revokeObjectURL(a.href);}else await request('saveText',{Name:name+'.md',Text:text});}catch{/* cancelled or unavailable */}};
  const threadRow=(r:Row,nested:boolean)=><ContextMenu key={r.key}><ContextMenuTrigger asChild><div role="button" tabIndex={0} className={`side-item thread ${nested?'nested':''} ${r.active?'on':''}`} title={`${r.title}\n${r.provider.detail}${r.past?' (from history)':''}`} onClick={r.open} onKeyDown={e=>{if(e.key==='Enter')r.open();}}>
    {r.busy?<WorkingGlyph size={13} paused={r.paused}/>:pinned.includes(r.key)?<Pin size={11} className="thread-pin"/>:r.past?<HistoryIcon size={12} className="thread-ico"/>:<MessageSquare size={12} className="thread-ico"/>}
    <span className="side-title">{r.title}</span>{r.busy&&<Elapsed since={r.since} className="side-elapsed"/>}{r.branch&&<span className="side-branch" title={`Worktree ${r.branch}`}><GitBranch size={11}/></span>}
    <ProviderChip p={r.provider}/><small className="thread-age">{ago(r.when)}</small>
    {r.close&&<button className="side-x" aria-label="Close chat" onClick={e=>{e.stopPropagation();r.close!();}}><X size={12}/></button>}</div></ContextMenuTrigger>
    <ContextMenuContent>
      <ContextMenuItem onSelect={r.open}><MessageSquare size={15}/>Open</ContextMenuItem>
      <ContextMenuItem onSelect={()=>togglePin(r.key,r.title)}>{pinned.includes(r.key)?<PinOff size={15}/>:<Pin size={15}/>}{pinned.includes(r.key)?'Unpin':'Pin to the top'}</ContextMenuItem>
      {r.sid&&<ContextMenuItem onSelect={()=>setRenameFor({id:r.sid!,title:r.title})}><Pencil size={15}/>Rename</ContextMenuItem>}
      {r.sid&&<ContextMenuItem onSelect={()=>{void exportChat(r.sid!,r.title);}}><Download size={15}/>Save as Markdown</ContextMenuItem>}
      {r.close&&<><ContextMenuSeparator/><ContextMenuItem onSelect={r.close}><Trash2 size={15}/>Close chat</ContextMenuItem></>}
    </ContextMenuContent></ContextMenu>;  const limit=(key:string)=>more[key]??6;

  return <Glass as="aside" material="frosted" className="navigation sidebar">
    <div className="brand"><span className="brand-mark"><img src="./LAICA.ico" alt=""/></span><div><strong>LAICA</strong><span>Agent workspace</span></div></div>
    <button className="new-chat-btn" title="New chat (Ctrl+N)" onClick={()=>{setActive(null);go('home');}}><SquarePen size={16}/><span>New chat</span><span className="nc-plus" aria-hidden="true"><Plus size={12}/></span></button>
    <label className="side-search"><Search size={14}/><input aria-label="Search chats" placeholder="Search" value={query} onChange={e=>setQuery(e.target.value)}/>{query&&<button aria-label="Clear search" onClick={()=>setQuery('')}><X size={12}/></button>}</label>
    <nav aria-label="Workspace" className="side-nav">{item('tasks','Scheduled tasks',CalendarClock)}{item('assistants','Assistants',Sparkles)}{item('plugins','Plugins',Store)}{item('teams','Workflow designer',Workflow)}{item('activity','Activity',ActivityIcon)}</nav>
    <div className="side-scroll">
      <div className="side-section">{fb('teams','Teams')}<span>Teams</span><button aria-label="New team" title="New team" onClick={newTeam}><Plus size={13}/></button></div>
      {!folded('teams')&&!teams.length&&<p className="side-empty">Team mode runs a leader and teammates in parallel.</p>}
      {!folded('teams')&&teams.map(t=><button key={t.Id} className={`side-item ${page==='team'&&activeTeam===t.Id?'on':''}`} onClick={()=>openTeam(t.Id)}><Users size={14}/><span className="side-title">{t.Title}</span><ProviderChip p={{kind:'team',label:'Team',detail:'Agent team'}}/>{t.Running&&<WorkingGlyph size={13} paused={t.Paused}/>}</button>)}
      {pinnedRows.length>0&&<><div className="side-section">{fb('pinned','Pinned')}<span>Pinned</span></div>{!folded('pinned')&&pinnedRows.map(r=>threadRow(r,false))}</>}
      <div className="side-section">{fb('projects','Projects')}<span>Projects</span><span className="side-actions"><button aria-label="Re-scan Codex and Claude Code history" title="Re-scan Codex and Claude Code history" onClick={()=>reloadHistory(true)}><RefreshCw size={12} className={historyLoading?'spin':''}/></button><button aria-label="Add project" title="Add a project folder" onClick={()=>setAdding(a=>!a)}><Plus size={13}/></button></span></div>
      {adding&&!folded('projects')&&<div className="side-add"><input aria-label="Project folder" autoFocus placeholder="Folder path, e.g. C:\code\my-app" value={newPath} onChange={e=>setNewPath(e.target.value)} onKeyDown={e=>{if(e.key==='Enter')submit();if(e.key==='Escape')setAdding(false);}}/><div>{!isRemote&&<Button size="small" variant="quiet" leadingIcon={<FolderPlus size={14}/>} onClick={browse}>Browse…</Button>}<Button size="small" variant="primary" disabled={!newPath.trim()} onClick={submit}>Add</Button></div></div>}
      {!folded('projects')&&!shownProjects.length&&!adding&&<p className="side-empty">{historyLoading?'Reading your Codex and Claude Code projects…':'Add a folder with + to organise chats by project.'}</p>}
      {!folded('projects')&&shownProjects.map((p,i)=>{const list=byProject.get(p.name.toLowerCase())??[];const expanded=isOpen(p.name,i);const lim=limit(p.name);const sel=selectedName===p.name.toLowerCase();
        return <div key={p.name} className="side-group">
          <ContextMenu><ContextMenuTrigger asChild><div role="button" tabIndex={0} className={`side-item project ${sel?'on':''}`} title={p.path} onClick={()=>{toggle(p.name,i);setProject(p.path);}} onKeyDown={e=>{if(e.key==='Enter'){toggle(p.name,i);setProject(p.path);}}}>
            {expanded?<ChevronDown size={12} className="side-chev"/>:<ChevronRight size={12} className="side-chev"/>}{sel||expanded?<FolderOpen size={14}/>:<Folder size={14}/>}<span className="side-title">{p.name}</span>{list.some(r=>r.busy)&&<WorkingGlyph size={12}/>}{list.length>0&&<small>{list.length}</small>}
            <button className="side-x side-new" aria-label={`New chat in ${p.name}`} title="New chat in this project" onClick={e=>{e.stopPropagation();setProject(p.path);setActive(null);go('home');}}><Plus size={12}/></button>
            {p.saved&&!p.codex&&<button className="side-x" aria-label={`Remove ${p.name} from the list`} title="Remove from list" onClick={e=>{e.stopPropagation();removeProject(p.path);}}><X size={12}/></button>}</div></ContextMenuTrigger>
            <ContextMenuContent>
              <ContextMenuItem onSelect={()=>{setProject(p.path);setActive(null);go('home');}}><SquarePen size={15}/>New chat here</ContextMenuItem>
              {!isRemote&&p.path&&<ContextMenuItem onSelect={()=>{void request('revealPath',{Path:p.path}).catch(()=>undefined);}}><FolderSearch size={15}/>Show in File Explorer</ContextMenuItem>}
              {p.saved&&!p.codex&&<><ContextMenuSeparator/><ContextMenuItem onSelect={()=>removeProject(p.path)}><Trash2 size={15}/>Remove from list</ContextMenuItem></>}
            </ContextMenuContent></ContextMenu>
          {expanded&&(list.length===0?<p className="side-empty nested">No conversations yet.</p>:<>{list.slice(0,lim).map(r=>threadRow(r,true))}{list.length>lim&&<button className="side-more nested" onClick={()=>setMore(m=>({...m,[p.name]:lim+10}))}>Show more ({list.length-lim})</button>}</>)}
        </div>;})}
      {(unfiled.length>0||!q)&&<div className="side-section">{fb('chats','Chats')}<span>Chats</span></div>}
      {!folded('chats')&&!unfiled.length&&!q&&<p className="side-empty">Conversations that don't belong to a project appear here.</p>}
      {!folded('chats')&&unfiled.slice(0,limit('')).map(r=>threadRow(r,false))}{!folded('chats')&&unfiled.length>limit('')&&<button className="side-more" onClick={()=>setMore(m=>({...m,'':limit('')+12}))}>Show more ({unfiled.length-limit('')})</button>}
    </div>
    <div className="side-foot">
      <div className="side-icons"><AnalyticsButton/><Tip label="Settings" shortcut="Ctrl ,"><Button variant="quiet" size="small" aria-label="Settings" className={page==='settings'?'active':''} onClick={()=>go('settings')}><Settings size={15}/></Button></Tip><Tip label="Services"><Button variant="quiet" size="small" aria-label="Services" onClick={()=>go('services')}><Plug size={15}/></Button></Tip><Tip label="Guide and shortcuts"><Button variant="quiet" size="small" aria-label="Guide" onClick={onGuide}><BookOpen size={15}/></Button></Tip></div>
      <div className="version">v{version}</div>
    </div>
    <Dialog open={!!renameFor} onOpenChange={o=>{if(!o)setRenameFor(null);}}><DialogContent aria-describedby={undefined}><DialogTitle>Rename chat</DialogTitle><DialogDescription>Give it a name you will recognise.</DialogDescription>
      <input className="hinput rename-input" autoFocus value={renameFor?.title??''} onChange={e=>setRenameFor(r=>r?{...r,title:e.target.value}:r)} onKeyDown={e=>{if(e.key==='Enter'&&renameFor){rename(renameFor.id,renameFor.title);setRenameFor(null);}}} aria-label="Chat name"/>
      <div className="dialog-actions"><Button variant="quiet" onClick={()=>setRenameFor(null)}>Cancel</Button><Button variant="primary" onClick={()=>{if(renameFor)rename(renameFor.id,renameFor.title);setRenameFor(null);}}>Rename</Button></div></DialogContent></Dialog>
  </Glass>;
}
