import {useCallback,useEffect,useMemo,useRef,useState} from 'react';
import {Switch,useToast} from 'open-glass-ui';
import {ArrowRightLeft,ChevronDown,Clock,FileText,Save} from 'lucide-react';
import {request,isDesktop} from './bridge';
import {Select} from './GlassSelect';
import type {GlassOption} from './GlassSelect';
import {useHarness,effortLabel} from './harness-store';
import type {HSession,HEvent} from './harness-store';
import {useClaudeModels} from './ClaudeModels';
import {Markdown} from './markdown';
import {FileLink} from './fileLinks';
import type {Model} from './types';

export interface PairSide { Key:string; Name:string; Model:string; Effort?:string; Percent:number; Limited:boolean; ResetsUtc:string; Ran:boolean; Busy:boolean; Mode?:string; Writable?:boolean }
export interface PairInfo { Id:string; Mode:'off'|'assisted'|'automatic'; ChatMode:string; Active:'primary'|'partner'; ActiveName:string; Primary:PairSide; Partner:PairSide; HasPartner:boolean; AutoSwitch:boolean; SwitchBack:boolean; Halted:string; WaitUntilUtc:string; Preview:string; EstTokens:number; EstCost:number; Currency:string; HandoffFile:string; LastSwitchUtc:string; Switches:{TimeUtc:string;Text:string;Detail:string}[]; ReturnThreshold:number; Summary?:PairSummary|null }
export interface PairSummary { Target:string; From:string; Fresh:boolean; Request:string; Unfinished:boolean; LastReply:string; Turns:number; Files:string[]; FilesTotal:number; Verify:string[]; Commands:string[]; Problems:string[]; Stage:string }
export interface Continuity { Mode:'off'|'assisted'|'automatic'; PartnerHarness:string; PartnerService:string; PartnerModel:string; AutoSwitch:boolean; SwitchBack:boolean; MinGapMinutes:number; ThrashSwitches:number; ThrashMinutes:number; ReturnThreshold:number; ReturnAtBoundary:boolean; HandoffPath:string; AgentNote:boolean; Harnesses:{Id:string;Name:string}[] }

export const clock=(iso?:string)=>{if(!iso)return '';const d=new Date(iso);return Number.isNaN(d.getTime())?'':d.toLocaleTimeString([],{hour:'numeric',minute:'2-digit'}).toLowerCase();};
const money=(n:number,c:string)=>(c==='USD'?'$':c+' ')+(n<0.01?n.toFixed(4):n.toFixed(2));
const tokensText=(n:number)=>n>=1000?'~'+(n/1000).toFixed(n>=10000?0:1)+'k tokens':'~'+n+' tokens';
export const countdown=(iso:string,now:number)=>{const ms=Date.parse(iso)-now;if(!Number.isFinite(ms)||ms<=0)return 'any moment now';const m=Math.round(ms/60000);return m>=60?Math.floor(m/60)+'h '+(m%60)+'m':m+' min';};

/** The pair's state for one chat: who holds the work, usage and reset times of both agents, and what the next switch would send. */
export function usePair(id:string|undefined,watch:string|number){
  const [info,setInfo]=useState<PairInfo|null>(null);
  const load=useCallback(()=>{if(!id||!isDesktop)return Promise.resolve();return request<PairInfo>('pairGet',{Id:id}).then(setInfo).catch(()=>setInfo(null));},[id]);
  useEffect(()=>{void load();},[load,watch]);
  useEffect(()=>{const t=setInterval(()=>{void load();},20000);return()=>clearInterval(t);},[load]);
  return {info,load};
}

/** Header chip: who is working, when the other agent resets, and a Switch now button. Hover shows what a switch would send. */
export function TagTeamChip({session}:{session:HSession}){
  const {toast}=useToast();const {events,reload}=useHarness();
  const evCount=(events[session.Id]??[]).length;
  const {info,load}=usePair(session.Id,evCount+(session.Busy?1:0)+(session.Waiting?1:0));
  const [now,setNow]=useState(Date.now());
  useEffect(()=>{const t=setInterval(()=>setNow(Date.now()),30000);return()=>clearInterval(t);},[]);
  if(!info||info.Mode==='off'||!info.HasPartner)return null;
  const waiting=!!info.WaitUntilUtc;
  if(!(info.Mode==='automatic'||info.Active==='partner'||waiting))return null;
  const mine=info.Active==='partner'?info.Partner:info.Primary,other=info.Active==='partner'?info.Primary:info.Partner;
  const warn=!waiting&&mine.Percent>=80;
  const otherText=other.Limited||other.ResetsUtc?`${other.Name} resets ${clock(other.ResetsUtc)||'later'}`:`${other.Name} ready`;
  const label=waiting?`Waiting · both out of usage · back ${clock(info.WaitUntilUtc)} (${countdown(info.WaitUntilUtc,now)})`:`${info.ActiveName||mine.Name} active · ${otherText}`;
  const go=()=>{request<PairInfo>('pairSwitch',{Id:session.Id}).then(()=>{void load();void reload();}).catch(e=>toast({title:'Could not switch',description:(e as Error).message,duration:7000}));};
  return <span className={'tt-chip'+(warn?' warn':'')+(waiting?' wait':'')+(info.Halted?' halted':'')} tabIndex={0} aria-label={label}>
    {waiting?<Clock size={12}/>:<ArrowRightLeft size={12}/>}<span className="tt-label">{label}</span>
    {warn&&<b className="tt-pct">{Math.round(mine.Percent)}% used</b>}
    {info.Halted&&<b className="tt-halt">auto-switching paused</b>}
    <button type="button" className="tt-go" disabled={session.Busy||waiting} onClick={go}>Switch now</button>
    <span className="tt-pop" role="tooltip">
      <RunDown info={info} target={other.Name}/>
    </span>

  </span>;
}

/** The plain-language rundown: where the next agent picks up, as a short list, with the exact text one click away. */
function RunDown({info,target}:{info:PairInfo;target:string}){
  const s=info.Summary;
  const short=(t:string,n=170)=>{const x=(t||'').replace(/\s+/g,' ').trim();return x.length>n?x.slice(0,n-1)+'…':x;};
  return <div className="tt-run">
    <b>{target} takes over from here</b>
    <small>{tokensText(info.EstTokens)} · about {money(info.EstCost,info.Currency)} to send (input only, estimated){s?.Fresh?' · first time it joins, so it gets the full briefing':''}</small>
    {s?<ul>
      <li><i>{s.Unfinished?'Carries on with':'Next request'}</i><span>{short(s.Request)||'Waiting for your next message'}</span></li>
      {s.LastReply&&<li><i>{s.From} last said</i><span>{short(s.LastReply)}</span></li>}
      {s.FilesTotal>0&&<li><i>Files changed ({s.FilesTotal})</i><span>{s.Files.map((f,i)=><span key={f}>{i>0&&', '}<FileLink path={f.replace(/ \((?:[^)]*)\)$/,'')}>{f.replace(/ \((?:[^)]*)\)$/,'')}</FileLink></span>)}{s.FilesTotal>s.Files.length?' …':''}</span></li>}
      {s.Verify.length>0&&<li className="warn"><i>Check first</i><span>{s.Verify.map((f,i)=><span key={f}>{i>0&&', '}<FileLink path={f}>{f}</FileLink></span>)} (may be half-written)</span></li>}
      {s.Commands.length>0&&<li><i>Recent commands</i><span>{s.Commands.map(c=>short(c,70)).join(' · ')}</span></li>}
      {s.Problems.length>0&&<li className="warn"><i>Problems</i><span>{s.Problems.map(c=>short(c,100)).join(' · ')}</span></li>}
      <li><i>Background</i><span>{s.Turns} earlier turn{s.Turns===1?'':'s'} summarised in <button type="button" className="tt-link" onClick={e=>{e.stopPropagation();window.dispatchEvent(new Event('laica-open-handoff'));}}>{info.HandoffFile}</button> <small>(click to read it)</small></span></li>
    </ul>:<p className="tt-none">Nothing to hand over yet.</p>}
    <details><summary>Show the exact text it receives</summary><pre>{info.Preview?info.Preview.slice(0,6000)+(info.Preview.length>6000?'\n…':''):'Nothing yet.'}</pre></details>
  </div>;
}
/** One line in the chat for a switch or a wait, so the timeline of who did what is readable. */
export function SwitchRow({e}:{e:HEvent}){
  let cost='';try{const d=JSON.parse(e.Detail??'{}') as {EstTokens?:number;EstCost?:number};if(d.EstTokens)cost=` · ${tokensText(d.EstTokens)}${d.EstCost?` (~${money(d.EstCost,'USD')})`:''}`;}catch{/* plain text */}
  const wait=e.Kind==='pairwait';
  return <div className={'hmsg handoff tt-row'+(wait?' wait':'')}>{wait?<Clock size={14}/>:<ArrowRightLeft size={14}/>}<span>{e.Text}<small>{cost}</small></span><time>{clock(e.TimeUtc)}</time></div>;
}

/** Models the partner can be, from every installed agent except the chat's own. Values look like "claude:opus" or "codex:default". */
export function usePartnerOptions(exclude:string,models:Model[]):GlassOption[]{
  const {harnesses}=useHarness();const claudeList=useClaudeModels();
  return useMemo(()=>{
    const out:GlassOption[]=[];
    harnesses.filter(h=>h.Available&&!['laica','workflow'].includes(h.Id)&&h.Id!==exclude).forEach(h=>{
      if(h.Id==='codex'){const list=models.filter(m=>m.ConnectionId==='codex');(list.length?list:[{Id:'default',Name:'Codex default'}]).forEach(m=>out.push({value:'codex:'+m.Id,label:m.Name,group:'Codex'}));}
      else if(h.Id==='claude')claudeList.forEach(c=>out.push({value:'claude:'+c.value,label:c.label,description:c.description,group:'Claude Code'}));
      else out.push({value:h.Id+':default',label:h.Name,group:'Other agents'});
    });
    return out;
  },[harnesses,models,claudeList,exclude]);
}
export const splitPartner=(v:string)=>{const i=v.indexOf(':');return {harness:v.slice(0,i),model:v.slice(i+1)==='default'?'':v.slice(i+1)};};
export type PairRules='both'|'limit'|'manual';
export const rulesOptions:GlassOption[]=[
  {value:'both',label:'Switch on limit, return on reset',description:'Hands over when one runs out, and back when it resets'},
  {value:'limit',label:'Switch on limit only',description:'Hands over when one runs out; you hand back'},
  {value:'manual',label:'Only when I say',description:'Keeps the notes; you press Switch now'}];
export const rulesPatch=(r:PairRules)=>({AutoSwitch:r==='manual'?'False':'True',SwitchBack:r==='both'?'True':'False'});
export const rulesOf=(auto:boolean,back:boolean):PairRules=>!auto?'manual':back?'both':'limit';

export interface AgentAuth { Id:string; Name:string; SignedIn:boolean|null }
/** Whether Claude Code and Codex are signed in on this computer (asked of the agents themselves; polled while something needs it). */
export function useAgentAuth(active:boolean){
  const [list,setList]=useState<AgentAuth[]>([]);
  const load=useCallback(()=>{if(isDesktop)void request<AgentAuth[]>('agentAuth',{}).then(setList).catch(()=>undefined);},[]);
  useEffect(()=>{if(!active)return;load();const t=setInterval(load,6000);return()=>clearInterval(t);},[active,load]);
  return {list,load};
}
/** Starts the agent's own sign-in (it opens your browser; LAICA never sees the login). */
export function SignInButton({harness,onStarted}:{harness:string;onStarted?:()=>void}){
  const {toast}=useToast();
  if(harness!=='claude'&&harness!=='codex')return null;
  return <button type="button" className="tt-signin" onClick={e=>{e.stopPropagation();request('agentSignIn',{Harness:harness}).then(()=>{toast({title:'Finish signing in',description:'A sign-in window opened. When it says you are signed in, come back here.',duration:9000});onStarted?.();}).catch(err=>toast({title:'Could not start sign-in',description:(err as Error).message,duration:7000}));}}>Sign in</button>;
}
/** One pill, one small panel: who the partner is, how hard it thinks, and when the work moves. */
export function PartnerPicker({options,models,value,onPick,effort,onEffort,rules,onRules}:{options:GlassOption[];models:Model[];value:string;onPick:(v:string)=>void;effort:string;onEffort:(e:string)=>void;rules:PairRules;onRules:(r:PairRules)=>void}){
  const [open,setOpen]=useState(false);const box=useRef<HTMLSpanElement>(null);
  useEffect(()=>{if(!open)return;const away=(e:PointerEvent)=>{const el=e.target as HTMLElement;if(box.current?.contains(el)||el.closest('.gs-pop,[role=listbox],[role=option]'))return;setOpen(false);};const esc=(e:KeyboardEvent)=>{if(e.key==='Escape')setOpen(false);};document.addEventListener('pointerdown',away);document.addEventListener('keydown',esc);return()=>{document.removeEventListener('pointerdown',away);document.removeEventListener('keydown',esc);};},[open]);
  const p=value?splitPartner(value):null;const auth=useAgentAuth(open);const unsigned=p?auth.list.find(a=>a.Id===p.harness&&a.SignedIn===false):undefined;
  const levels=useMemo<string[]>(()=>{if(!p)return [];if(p.harness==='claude')return ['low','medium','high','xhigh','max'];if(p.harness==='codex')return (models.find(m=>m.ConnectionId==='codex'&&m.Id===(p.model||''))?.Efforts??models.find(m=>m.ConnectionId==='codex')?.Efforts??[]).filter(l=>l!=='default'&&effortLabel[l]);return [];},[p?.harness,p?.model,models]); // eslint-disable-line react-hooks/exhaustive-deps
  const name=value?options.find(o=>o.value===value)?.label??p!.harness:'';
  const label=value?'Tag-team · '+name+(effort&&levels.includes(effort)?' · '+(effortLabel[effort]??effort):''):'Tag-team · Off';
  return <span className="tt-picker" ref={box}>
    <button type="button" className={'tt-pill'+(value?' on':'')} aria-haspopup="dialog" aria-expanded={open} onClick={()=>setOpen(v=>!v)}><ArrowRightLeft size={13}/><span>{label}</span><ChevronDown size={13}/></button>
    {open&&<div className="tt-panel" role="dialog" aria-label="Tag-team">
      <p className="tt-help">A second agent takes over when the first runs out of usage, and hands back when it resets.</p>
      <Select label="Partner" value={value} options={[{value:'',label:'Off',description:'One agent works alone'},...options]} onChange={e=>onPick(e.target.value)} searchable={options.length>9} menuWidth={320}/>
      {unsigned&&<p className="tt-warn">{unsigned.Name} isn't signed in on this computer, so it can't take over yet. <SignInButton harness={unsigned.Id} onStarted={auth.load}/></p>}
      {value&&levels.length>0&&<Select label="Reasoning effort" value={levels.includes(effort)?effort:''} options={[{value:'',label:'Default',description:'Let the model decide'},...levels.map(l=>({value:l,label:effortLabel[l]??l}))]} onChange={e=>onEffort(e.target.value)} menuWidth={260}/>}
      {value&&<Select label="When to switch" value={rules} options={rulesOptions} onChange={e=>onRules(e.target.value as PairRules)} menuWidth={340}/>}
    </div>}
  </span>;
}
/** Composer pills for an open chat. */
export function TagTeamPills({session,models}:{session:HSession;models:Model[]}){
  const {toast}=useToast();const {reload}=useHarness();
  const {info,load}=usePair(session.Id,(session.Continuity??'')+(session.Busy?1:0));
  const options=usePartnerOptions(session.Harness,models);
  if(session.Harness==='workflow'||session.TeamId||!options.length||!isDesktop)return null;
  const on=!!info&&info.Mode==='automatic'&&info.HasPartner;
  const value=on?info!.Partner.Key+':'+(info!.Partner.Model||'default'):'';
  const apply=(patch:Record<string,unknown>)=>request('pairSet',{Id:session.Id,...patch}).then(()=>{void load();void reload();}).catch(e=>toast({title:'Could not change that',description:(e as Error).message,duration:6500}));
  const pick=(v:string)=>{if(!v){void apply({Mode:'assisted'});return;}const p=splitPartner(v);void apply({Mode:'automatic',PartnerHarness:p.harness,PartnerModel:p.model||'default'});};
  return <PartnerPicker options={options} models={models} value={value} onPick={pick} effort={info?.Partner.Effort??''} onEffort={e=>void apply({PartnerEffort:e||'default'})} rules={rulesOf(info?.AutoSwitch??true,info?.SwitchBack??true)} onRules={r=>void apply(rulesPatch(r))}/>;
}
const num=(v:string,d:number)=>{const n=parseInt(v,10);return Number.isFinite(n)?n:d;};

/** Settings > Agents > Continuity. */
export function ContinuityCard(){
  const {toast}=useToast();const [cfg,setCfg]=useState<Continuity|null>(null),[path,setPath]=useState('');
  const apply=useCallback((c:Continuity)=>{setCfg(c);setPath(c.HandoffPath);},[]);
  useEffect(()=>{if(isDesktop)void request<Continuity>('continuityGet',{}).then(apply).catch(()=>{});},[apply]);
  const save=(p:Record<string,unknown>)=>request<Continuity>('continuitySet',p).then(apply).catch(e=>toast({title:'Could not save',description:(e as Error).message,duration:6500}));
  if(!cfg)return null;
  const modes:GlassOption[]=[{value:'off',label:'Off',description:'LAICA writes no handoff file'},{value:'assisted',label:'Assisted (default)',description:'Keeps HANDOFF.md up to date after each turn; you hand over by hand'},{value:'automatic',label:'Automatic tag-team',description:'Two agents take turns: hands over when one runs out, and back when it resets'}];
  const partners:GlassOption[]=[{value:'',label:'Pick for me',description:'The other installed agent'},...cfg.Harnesses.map(h=>({value:h.Id,label:h.Name}))];
  const numberField=(label:string,k:'MinGapMinutes'|'ThrashSwitches'|'ThrashMinutes'|'ReturnThreshold',unit:string,help:string)=><label className="coauthor-email">{label} ({unit})<input className="hinput" inputMode="numeric" defaultValue={String(cfg[k])} key={k+cfg[k]} onBlur={e=>{const v=num(e.target.value,cfg[k]);if(v!==cfg[k])void save({[k]:v});}}/><small>{help}</small></label>;
  return <div className="handoff-card continuity-card">
    <div className="handoff-copy"><span className="eyebrow">CONTINUITY</span><h2>Tag-team handoff between agents</h2>
      <p>Pick two agents (say Codex and Claude Code) and let them take turns on the same project. When one runs out of usage, LAICA hands the work to the other with only what it hasn't seen, and hands it back when the first has reset, as many times as it takes. LAICA writes the handoff notes itself from the chat and your folder, so it works even when an agent has no usage left to write them. Every approval still comes to you.</p></div>
    <Select label="Default for new chats" value={cfg.Mode} options={modes} onChange={e=>void save({Mode:e.target.value})} menuWidth={380}/>
    <Select label="Partner agent" value={cfg.PartnerHarness} options={partners} onChange={e=>void save({PartnerHarness:e.target.value})} menuWidth={300}/>
    <Switch label="Switch by itself when an agent runs out" description="Tag-team chats only." checked={cfg.AutoSwitch} onCheckedChange={v=>void save({AutoSwitch:v})}/>
    <Switch label="Switch back when the first agent has reset" checked={cfg.SwitchBack} onCheckedChange={v=>void save({SwitchBack:v})}/>
    <Switch label="Also switch back at the next task boundary" description="Return as soon as the first agent is usable, not only when the second is nearly out." checked={cfg.ReturnAtBoundary} onCheckedChange={v=>void save({ReturnAtBoundary:v})}/>
    <div className="tt-grid">
      {numberField('Return threshold','ReturnThreshold','% used','Hand over early when the working agent passes this share of its limit and the other is fresher.')}
      {numberField('Minimum gap between switches','MinGapMinutes','minutes','Optional switches wait this long. A usage limit is never delayed.')}
      {numberField('Thrash guard: switches','ThrashSwitches','count','Stop auto-switching after this many switches...')}
      {numberField('Thrash guard: window','ThrashMinutes','minutes','...inside this window, when no file has changed.')}
    </div>
    <label className="coauthor-email">Handoff file in each project<input className="hinput" value={path} onChange={e=>setPath(e.target.value)} onBlur={()=>{if(path.trim()&&path.trim()!==cfg.HandoffPath)void save({HandoffPath:path.trim()});}}/><small>Relative to the project folder. The previous copy is kept as HANDOFF.prev.md. Your Pinned notes at the bottom are never overwritten.</small></label>
    <p className="handoff-note">Nothing here makes network calls. If you work in a folder without git, LAICA follows file times instead and never runs git init. Two agents never edit at once: one holds the work at a time. Note for Fusion: cloud designs aren't isolated by worktrees, so let only one agent drive Fusion at a time.</p>
  </div>;
}

/** The "Handoff" tab of the workspace panel: view HANDOFF.md and edit the Pinned notes. */
export function HandoffPanel({sessionId,refreshKey}:{sessionId:string;refreshKey:number}){
  const {toast}=useToast();const {sessions}=useHarness();const cwd=sessions.find(s=>s.Id===sessionId)?.Cwd??'';
  const [doc,setDoc]=useState<{Path:string;Exists:boolean;Managed:string;Pinned:string;UpdatedBy:string;UpdatedUtc:string}|null>(null),[pinned,setPinned]=useState(''),[dirty,setDirty]=useState(false);
  const load=useCallback(()=>{if(!cwd)return;void request<NonNullable<typeof doc>>('handoffFileGet',{Cwd:cwd}).then(d=>{setDoc(d);setPinned(p=>dirty?p:d.Pinned);}).catch(()=>setDoc(null));},[cwd,dirty]);
  useEffect(()=>{load();},[load,refreshKey]);
  const save=()=>request<NonNullable<typeof doc>>('handoffFileSet',{Cwd:cwd,Pinned:pinned}).then(d=>{setDoc(d);setPinned(d.Pinned);setDirty(false);toast({title:'Pinned notes saved'});}).catch(e=>toast({title:'Could not save',description:(e as Error).message,duration:6500}));
  return <div className="handoff-panel">
    <div className="hp-head"><FileText size={14}/><b>Project handoff</b>{doc?.Exists&&<small>last updated by {doc.UpdatedBy||'LAICA'}{doc.UpdatedUtc?' at '+new Date(doc.UpdatedUtc).toLocaleString([],{month:'short',day:'numeric',hour:'numeric',minute:'2-digit'}):''}</small>}</div>
    {doc?.Exists?<div className="hp-managed"><Markdown text={doc.Managed}/></div>:<p className="tree-note">No handoff file yet. LAICA writes one after the first finished turn in this project.</p>}
    <label className="hp-pinned">Pinned notes <small>(yours, never overwritten, every agent reads them)</small>
      <textarea className="hinput" rows={5} value={pinned} placeholder="Decisions, conventions, things no agent should touch…" onChange={e=>{setPinned(e.target.value);setDirty(true);}}/></label>
    <button type="button" className="sketch-go" disabled={!dirty} onClick={()=>{void save();}}><Save size={13}/> Save pinned notes</button>
  </div>;
}
