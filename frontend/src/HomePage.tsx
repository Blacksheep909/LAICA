import {useCallback,useEffect,useMemo,useRef,useState} from 'react';
import {Button,Glass,useToast} from 'open-glass-ui';
import SendButton from './SendButton';
import {useAttachments,useDropZone,AttachChips,DropOverlay,MicButton,FlagChips,usePasteFiles,composePrompt,noFlags} from './Composer';
import type {Flags} from './Composer';
import PlusMenu from './PlusMenu';
import {FolderOpen,FolderPlus,Pencil,GitBranch,X,Bot,Users,Workflow as WorkflowIcon } from 'lucide-react';
import {request,isDesktop} from './bridge';
import {useHarness,modeLabel,effortLabel} from './harness-store';
import {buildProjects} from './projects';
import {Select} from './GlassSelect';
import type {GlassOption} from './GlassSelect';
import type {Assistant} from './assistants-data';
import type {Service,Model,Profile} from './types';

import {useClaudeModels} from './ClaudeModels';
import {PartnerPicker,usePartnerOptions,splitPartner,rulesPatch} from './TagTeam';
import type {PairRules} from './TagTeam';
const PICK_KEY='laica-last-pick';
const badge=(t:string)=><span>{t.slice(0,1).toUpperCase()}</span>;

export default function HomePage({workingDirectory,onOpened,onOpenTeam,services,models,profiles}:{workingDirectory:string;onOpened:()=>void;onOpenTeam:(id:string)=>void;services:Service[];models:Model[];profiles:Profile[]}){
  const {toast}=useToast();
  const {harnesses,assistants,sessions,teams,project,create,send,savedProjects,history,addProject,codexProjects}=useHarness();const claudeList=useClaudeModels();
  const [pick,setPick]=useState(()=>{try{return localStorage.getItem(PICK_KEY)??'';}catch{return '';}}),[mode,setMode]=useState(''),[cwd,setCwd]=useState(()=>{try{return localStorage.getItem('laica-last-cwd')||workingDirectory;}catch{return workingDirectory;}}),[prompt,setPrompt]=useState(''),[assistant,setAssistant]=useState<Assistant|null>(null),[isolate,setIsolate]=useState(false),[repo,setRepo]=useState<{IsRepo:boolean;Exists?:boolean;Branch?:string;Dirty?:number}|null>(null),[editFolder,setEditFolder]=useState(false),[busy,setBusy]=useState(false),[effort,setEffort]=useState(''),[partner,setPartner]=useState(''),[partnerEffort,setPartnerEffort]=useState(''),[pairRules,setPairRules]=useState<PairRules>('both');
  const box=useRef<HTMLTextAreaElement>(null);
  useEffect(()=>{setCwd(c=>c||workingDirectory);},[workingDirectory]);
  useEffect(()=>{if(project)setCwd(project);},[project]);
  useEffect(()=>{if(!isDesktop||!cwd){setRepo(null);return;}const t=setTimeout(()=>{request<{IsRepo:boolean;Exists?:boolean;Branch?:string;Dirty?:number}>('gitInfo',{Cwd:cwd}).then(setRepo).catch(()=>setRepo(null));},350);return()=>clearTimeout(t);},[cwd]);
  useEffect(()=>{if(!repo?.IsRepo)setIsolate(false);},[repo]);

  const options=useMemo<GlassOption[]>(()=>{
    const out:GlassOption[]=[];
    profiles.forEach(p=>out.push({value:'w:'+p.Id,label:p.Name,description:`Workflow team · ${p.Plan.Nodes.length} agent${p.Plan.Nodes.length===1?'':'s'}`,group:'Your teams',icon:<WorkflowIcon size={12}/>}));
    teams.forEach(t=>out.push({value:'t:'+t.Id,label:t.Title,description:`Agent team · ${t.Members.length} teammates`,group:'Your teams',icon:<Users size={12}/>}));
    harnesses.filter(h=>h.Available&&h.Id!=='laica'&&h.Id!=='workflow').forEach(h=>{
      if(h.Id==='codex'){const list=models.filter(m=>m.ConnectionId==='codex');(list.length?list:[{Id:'default',Name:'Codex default',ConnectionId:'codex',Efforts:[]}]).forEach(m=>out.push({value:`m:codex:${m.Id}`,label:m.Name,description:m.Id==='default'?'Uses your Codex setting':undefined,group:'Codex',icon:badge('C')}));}
      else if(h.Id==='claude')claudeList.forEach(c=>out.push({value:`m:claude:${c.value}`,label:c.label,description:c.description,group:'Claude Code',icon:badge('C')}));
      else out.push({value:`m:${h.Id}:default`,label:h.Name,description:h.Custom?'Custom agent':'Command-line agent',group:'Other agents',icon:badge(h.Name)});
    });
    services.forEach(s=>{const list=models.filter(m=>m.ConnectionId===s.Id);
      if(!list.length)out.push({value:'x:'+s.Id,label:'No models loaded yet',description:'Open Services and refresh models',group:`${s.Name} (API key)`,disabled:true,icon:badge(s.Name)});
      list.forEach(m=>out.push({value:`m:laica:${s.Id}:${m.Id}`,label:m.Name||m.Id,group:`${s.Name} (API key)`,icon:badge(s.Name)}));});
    return out;
  },[profiles,teams,harnesses,models,services,claudeList]);
  const current=options.find(o=>o.value===pick&&!o.disabled)??options.find(o=>!o.disabled);
  const kind=current?.value.split(':')[0]??'';
  const harnessId=kind==='m'?current!.value.split(':')[1]:'';
  const info=harnesses.find(h=>h.Id===harnessId);
  const modelId=kind==='m'?current!.value.split(':').slice(2).join(':'):'';
  const levels=useMemo<string[]>(()=>{if(kind!=='m')return [];if(harnessId==='claude')return ['low','medium','high','xhigh','max'];if(harnessId==='codex')return (models.find(m=>m.ConnectionId==='codex'&&m.Id===modelId)?.Efforts??[]).filter(l=>l!=='default'&&effortLabel[l]);return [];},[kind,harnessId,modelId,models]);
  useEffect(()=>{if(effort&&!levels.includes(effort))setEffort('');},[levels,effort]);
  const partnerOptions=usePartnerOptions(harnessId,models);const partnerValue=partnerOptions.some(o=>o.value===partner)?partner:'';
  const choose=(v:string)=>{setPick(v);setMode('');try{localStorage.setItem(PICK_KEY,v);}catch{/* storage unavailable */}};
  const projectList=useMemo(()=>buildProjects(savedProjects,sessions,history,codexProjects),[savedProjects,sessions,history,codexProjects]);
  const projectOptions=useMemo<GlassOption[]>(()=>{
    const out:GlassOption[]=projectList.map(p=>({value:p.path,label:p.name,description:p.path.replace(/^C:\\Users\\[^\\]+\\/i,'~\\'),group:'Projects',icon:<FolderOpen size={12}/>}));
    if(cwd&&!projectList.some(p=>p.path.toLowerCase()===cwd.toLowerCase()))out.unshift({value:cwd,label:cwd.split(/[\\/]/).filter(Boolean).pop()||cwd,description:cwd,group:'Current folder',icon:<FolderOpen size={12}/>});
    out.push({value:'__browse',label:'Browse for a folder',group:'Other',icon:<FolderPlus size={12}/>});
    out.push({value:'__type',label:'Type a path',group:'Other',icon:<Pencil size={12}/>});
    return out;
  },[projectList,cwd]);
  const chooseProject=async(v:string)=>{
    if(v==='__type'){setEditFolder(true);return;}
    if(v==='__browse'){try{const p=await request<string|null>('pickFolder');if(p){addProject(p);setCwd(p);}}catch{toast({title:'Folder chooser unavailable',description:'Pick a project from the list or type a path.'});}return;}
    setCwd(v);
  };

  const target=useCallback(()=>({Cwd:cwd}),[cwd]);
  const files=useAttachments(target);
  const [flags,setFlags]=useState<Flags>(noFlags);usePasteFiles(files.addFiles);
  const drop=useDropZone(f=>{void files.addFiles(f);});
  const start=async()=>{
    if(!current||(!prompt.trim()&&!files.items.length&&!flags.plan&&!flags.goal)||busy)return;setBusy(true);const full=composePrompt(prompt,flags,files.items);
    try{
      const parts=current.value.split(':');
      if(kind==='t'){await request('teamRun',{Id:parts[1],Goal:full});setPrompt('');files.clear();setFlags(noFlags);onOpenTeam(parts[1]);return;}
      let s;
      if(kind==='w')s=await create({Harness:'workflow',Cwd:cwd,ServiceId:parts[1],Model:current.label,Title:current.label});
      else if(harnessId==='laica')s=await create({Harness:'laica',Cwd:cwd,Mode:mode||undefined,Assistant:assistant??undefined,Isolate:isolate,ServiceId:parts[2],Model:parts.slice(3).join(':')});
      else {const model=parts.slice(2).join(':');s=await create({Harness:harnessId,Cwd:cwd,Mode:mode||info?.DefaultMode,Assistant:assistant??undefined,Isolate:isolate,Model:model==='default'?undefined:model,Effort:effort||undefined});if(partner&&harnessId!=='laica'){const pp=splitPartner(partner);try{await request('pairSet',{Id:s.Id,Mode:'automatic',PartnerHarness:pp.harness,PartnerModel:pp.model||'default',PartnerEffort:partnerEffort||'default',...rulesPatch(pairRules)});}catch(e){toast({title:'Tag-team not set up',description:(e as Error).message,duration:7000});}}}
      await send(s.Id,full);files.clear();setFlags(noFlags);try{localStorage.setItem('laica-last-cwd',cwd);}catch{/* storage unavailable */}setPrompt('');setAssistant(null);onOpened();
    }catch(e){toast({title:'Could not start',description:(e as Error).message,duration:8000});}finally{setBusy(false);}
  };
  if(!isDesktop)return <div className="empty-state"><Bot size={26}/><h2>Open the desktop app</h2><p>Chats with Codex, Claude Code and other agents run in LAICA.exe.</p></div>;
  const folderName=cwd.split('\\').filter(Boolean).pop()||cwd;
  const agentInfo=kind==='m'?harnesses.find(h=>h.Id===harnessId):undefined;
  const modes=agentInfo?.Modes??[];const defaultMode=agentInfo?.DefaultMode??'';
  const triggerLabel=current?(kind==='m'&&current.group&&current.group!=='Claude Code'&&current.group!=='Other agents'?`${current.group.replace(' (API key)','')} · ${current.label}`:current.label):undefined;
  return <div className="home">
    <div className="home-inner">
      <h1 className="home-title">Hi, what's your plan for today?</h1>
      <Glass material="regular" className={`composer ${drop.dragging?'is-dragging':''}`} {...drop.bind}><DropOverlay show={drop.dragging}/>
        {assistant&&kind==='m'&&<div className="chip-row"><span className="chip">{assistant.Name}<button aria-label="Remove assistant" onClick={()=>setAssistant(null)}><X size={11}/></button></span></div>}
        <FlagChips flags={flags} setFlags={setFlags}/><AttachChips items={files.items} busy={files.busy} onRemove={files.remove}/>
        <textarea ref={box} aria-label="Message" rows={3} value={prompt} placeholder={kind==='t'?'Describe the goal for the team…':'Send a message, or describe the task. Pick a folder below to work inside a project.'} onChange={e=>setPrompt(e.target.value)} onKeyDown={e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();start();}}}/>
        <div className="composer-bar">
          <div className="composer-left">
            <PlusMenu onPick={()=>{void files.pick();}} onFiles={f=>{void files.addFiles(f);}} flags={flags} setFlags={setFlags} insert={x=>setPrompt(d=>d+(d&&!/\s$/.test(d)?' ':'')+x)} disabled={!cwd}/>
            <Select variant="pill" aria-label="Model or team" value={current?.value??''} options={options} onChange={e=>choose(e.target.value)} searchable menuWidth={360} placeholder="Choose a model" triggerLabel={triggerLabel} emptyText="No agents or teams found yet." footer={<>Add API keys under <b>Services</b> and design teams in the <b>Workflow designer</b>.</>}/>
            {levels.length>0&&<Select variant="pill" aria-label="Reasoning effort" value={effort} options={[{value:'',label:'Default',description:'Let the model decide'},...levels.map(l=>({value:l,label:effortLabel[l]}))]} onChange={e=>setEffort(e.target.value)} triggerLabel={'Effort · '+(effort?effortLabel[effort]:'Default')} menuWidth={220}/>}
            {modes.length>0&&<Select variant="pill" aria-label="Permissions" value={mode||defaultMode} options={modes.map(m=>({value:m,label:modeLabel[m]??m}))} onChange={e=>setMode(e.target.value)}/>}
            {kind==='m'&&harnessId!=='laica'&&partnerOptions.length>0&&<PartnerPicker options={partnerOptions} models={models} value={partnerValue} onPick={setPartner} effort={partnerEffort} onEffort={setPartnerEffort} rules={pairRules} onRules={setPairRules}/>}
          </div>
          <div className="composer-right"><MicButton onText={text=>setPrompt(d=>d+(d&&!/\s$/.test(d)?' ':'')+text)}/><SendButton label="Start chat" disabled={!current||(!prompt.trim()&&!files.items.length&&!flags.plan&&!flags.goal)||busy||(kind!=='t'&&(!cwd||repo?.Exists===false))} onSend={start}/></div>
        </div>
      </Glass>
      <div className="project-row">
        {editFolder?<><FolderOpen size={14}/><input className="hinput project-input" autoFocus value={cwd} placeholder="C:\path\to\project" onChange={e=>setCwd(e.target.value)} onBlur={()=>{setEditFolder(false);if(cwd.trim())addProject(cwd);}} onKeyDown={e=>{if(e.key==='Enter')(e.target as HTMLInputElement).blur();if(e.key==='Escape')setEditFolder(false);}}/></>:<Select variant="pill" aria-label="Project folder" value={cwd} options={projectOptions} onChange={e=>{void chooseProject(e.target.value);}} searchable menuWidth={380} triggerLabel={'Work in '+(projectList.find(p=>p.path.toLowerCase()===cwd.toLowerCase())?.name??(cwd.split(/[\\/]/).filter(Boolean).pop()||'choose a project'))} emptyText="No projects yet. Browse for a folder."/>}
        {repo?.IsRepo&&kind==='m'&&<label className="chk iso" title="Give this chat its own git worktree so it can't disturb your main checkout and returns to the same files later"><input type="checkbox" checked={isolate} onChange={e=>setIsolate(e.target.checked)}/><GitBranch size={13}/> Isolate in worktree <small>({repo.Branch})</small></label>}
        {repo&&repo.Exists===false&&<small className="warn">This folder no longer exists on this computer. Pick another project.</small>}{repo?.IsRepo&&!!repo.Dirty&&isolate&&<small className="warn">Worktrees start from the last commit; {repo.Dirty} uncommitted change{repo.Dirty>1?'s':''} won't be included.</small>}
      </div>
      {kind==='m'&&<><p className="home-hint">Select an assistant to start a task</p>
      <div className="assistant-grid">{assistants.slice(0,9).map(a=><button key={a.Id} className={`assistant-card ${assistant?.Id===a.Id?'on':''}`} onClick={()=>{setAssistant(assistant?.Id===a.Id?null:a);box.current?.focus();}}><b>{a.Name}</b><span>{a.Description}</span></button>)}</div></>}
    </div>
  </div>;
}
