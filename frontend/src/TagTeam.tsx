import {useCallback,useEffect,useState} from 'react';
import {Switch,useToast} from 'open-glass-ui';
import {ArrowRightLeft,Clock,FileText,Save} from 'lucide-react';
import {request,isDesktop} from './bridge';
import {Select} from './GlassSelect';
import type {GlassOption} from './GlassSelect';
import {useHarness} from './harness-store';
import type {HSession,HEvent} from './harness-store';

export interface PairSide { Key:string; Name:string; Model:string; Percent:number; Limited:boolean; ResetsUtc:string; Ran:boolean; Busy:boolean; Mode?:string; Writable?:boolean }
export interface PairInfo { Id:string; Mode:'off'|'assisted'|'automatic'; ChatMode:string; Active:'primary'|'partner'; ActiveName:string; Primary:PairSide; Partner:PairSide; HasPartner:boolean; AutoSwitch:boolean; SwitchBack:boolean; Halted:string; WaitUntilUtc:string; Preview:string; EstTokens:number; EstCost:number; Currency:string; HandoffFile:string; LastSwitchUtc:string; Switches:{TimeUtc:string;Text:string;Detail:string}[]; ReturnThreshold:number }
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
      <b>If you switch now, {other.Name} receives:</b>
      <small>{tokensText(info.EstTokens)} · about {money(info.EstCost,info.Currency)} (input only, estimated)</small>
      <pre>{info.Preview?info.Preview.slice(0,1800)+(info.Preview.length>1800?'\n…':''):'Nothing yet.'}</pre>
      <small>Kept in {info.HandoffFile} in the project folder. Nothing is sent until a switch happens.</small>
    </span>
  </span>;
}

/** One line in the chat for a switch or a wait, so the timeline of who did what is readable. */
export function SwitchRow({e}:{e:HEvent}){
  let cost='';try{const d=JSON.parse(e.Detail??'{}') as {EstTokens?:number;EstCost?:number};if(d.EstTokens)cost=` · ${tokensText(d.EstTokens)}${d.EstCost?` (~${money(d.EstCost,'USD')})`:''}`;}catch{/* plain text */}
  const wait=e.Kind==='pairwait';
  return <div className={'hmsg handoff tt-row'+(wait?' wait':'')}>{wait?<Clock size={14}/>:<ArrowRightLeft size={14}/>}<span>{e.Text}<small>{cost}</small></span><time>{clock(e.TimeUtc)}</time></div>;
}

/** Composer pills for an open chat: continuity mode, partner, and the two automatic behaviours. */
export function TagTeamPills({session}:{session:HSession}){
  const {toast}=useToast();const {harnesses,reload}=useHarness();
  const {info,load}=usePair(session.Id,(session.Continuity??'')+(session.Busy?1:0));
  const partners=harnesses.filter(h=>h.Available&&!['workflow','laica'].includes(h.Id)&&h.Id!==session.Harness);
  if(session.Harness==='workflow'||session.TeamId||!partners.length||!isDesktop)return null;
  const mode=info?.Mode??'assisted';
  const apply=(patch:Record<string,unknown>)=>request('pairSet',{Id:session.Id,...patch}).then(()=>{void load();void reload();}).catch(e=>toast({title:'Could not change that',description:(e as Error).message,duration:6500}));
  const modes:GlassOption[]=[{value:'',label:'Use my default',description:'Set under Settings > Agents > Continuity'},{value:'off',label:'Off',description:'No handoff notes for this chat'},{value:'assisted',label:'Assisted',description:'Keeps HANDOFF.md up to date; you switch by hand'},{value:'automatic',label:'Tag-team',description:'Hands over by itself when one agent runs out, and back when it resets'}];
  const label=session.Continuity==='automatic'?'Automatic':session.Continuity==='assisted'?'Assisted':session.Continuity==='off'?'Off':mode==='automatic'?'Automatic (default)':mode==='off'?'Off (default)':'Assisted (default)';
  const partnerKey=info?.Partner.Key??'';
  return <>
    <Select variant="pill" aria-label="Tag-team" value={session.Continuity??''} options={modes} onChange={e=>void apply({Mode:e.target.value})} triggerLabel={'Tag-team · '+label} menuWidth={340}/>
    {mode==='automatic'&&info?.HasPartner&&<>
      <Select variant="pill" aria-label="Tag-team partner" value={partnerKey} options={partners.map(h=>({value:h.Id,label:h.Name}))} onChange={e=>void apply({PartnerHarness:e.target.value})} triggerLabel={'Partner · '+(info.Partner.Name||'choose')} menuWidth={240}/>
      <button type="button" className="tt-toggle" aria-pressed={info.AutoSwitch} title="Switch to the partner by itself when this agent runs out of usage" onClick={()=>void apply({AutoSwitch:info.AutoSwitch?'False':'True'})}>Auto-switch on limit</button>
      <button type="button" className="tt-toggle" aria-pressed={info.SwitchBack} title="Hand the work back once the first agent has reset" onClick={()=>void apply({SwitchBack:info.SwitchBack?'False':'True'})}>Switch back when reset</button>
    </>}
  </>;
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
    {doc?.Exists?<pre className="hp-managed">{doc.Managed}</pre>:<p className="tree-note">No handoff file yet. LAICA writes one after the first finished turn in this project.</p>}
    <label className="hp-pinned">Pinned notes <small>(yours, never overwritten, every agent reads them)</small>
      <textarea className="hinput" rows={5} value={pinned} placeholder="Decisions, conventions, things no agent should touch…" onChange={e=>{setPinned(e.target.value);setDirty(true);}}/></label>
    <button type="button" className="sketch-go" disabled={!dirty} onClick={()=>{void save();}}><Save size={13}/> Save pinned notes</button>
  </div>;
}
