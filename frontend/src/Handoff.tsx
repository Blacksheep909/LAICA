import {useCallback,useEffect,useMemo,useState} from 'react';
import {useToast} from 'open-glass-ui';
import {ArrowRightLeft,Users,Bot,HandHelping} from 'lucide-react';
import {Select} from './GlassSelect';
import type {GlassOption} from './GlassSelect';
import {request,isDesktop} from './bridge';
import {useHarness} from './harness-store';

export interface HandoffChoice { Mode:'manual'|'team'|'agent'|'inherit'; TeamId?:string; Harness?:string; ServiceId?:string; Model?:string }

/** "manual", "t:<team>" or "a:<agent>": the one string a picker works with. */
export const choiceValue=(c?:HandoffChoice|null)=>!c||c.Mode==='manual'?'manual':c.Mode==='inherit'?'inherit':c.Mode==='team'?'t:'+(c.TeamId??''):'a:'+(c.Harness??'');
export const choiceOf=(v:string):HandoffChoice=>v==='inherit'?{Mode:'inherit'}:v.startsWith('t:')?{Mode:'team',TeamId:v.slice(2)}:v.startsWith('a:')?{Mode:'agent',Harness:v.slice(2)}:{Mode:'manual'};

/** The picker's options: ask me, a backup team, or one agent. A team's own vendors are shown so you can pick one that avoids the vendor likely to run out. */
export function useHandoffOptions(allowInherit=false,excludeTeam?:string):GlassOption[]{
  const {teams,harnesses}=useHarness();
  return useMemo(()=>{
    const out:GlassOption[]=[];
    if(allowInherit)out.push({value:'inherit',label:'Use my default',description:'The setting in Settings',group:'When a vendor runs out of usage',icon:<HandHelping size={15}/>});
    out.push({value:'manual',label:'Ask me',description:'Show the choice and wait',group:'When a vendor runs out of usage',icon:<HandHelping size={15}/>});
    teams.filter(t=>t.Id!==excludeTeam).forEach(t=>{const vendors=[...new Set([t.Leader,...t.Members.map(m=>m.Harness)].filter(Boolean))];out.push({value:'t:'+t.Id,label:t.Title,description:'Backup team · '+vendors.map(v=>harnesses.find(h=>h.Id===v)?.Name??v).join(', '),group:'Hand to a backup team',icon:<Users size={15}/>});});
    harnesses.filter(h=>h.Available&&!['workflow','laica'].includes(h.Id)).forEach(h=>out.push({value:'a:'+h.Id,label:h.Name,description:'One agent takes over',group:'Hand to one agent',icon:<Bot size={15}/>}));
    return out;
  },[teams,harnesses,allowInherit,excludeTeam]);
}

export function useHandoff(){
  const {toast}=useToast();const [cfg,setCfg]=useState<HandoffChoice>({Mode:'manual'});
  useEffect(()=>{if(isDesktop)request<HandoffChoice>('handoffGet').then(setCfg).catch(()=>undefined);},[]);
  const save=useCallback((c:HandoffChoice)=>{setCfg(c);request<HandoffChoice>('handoffSet',c).then(setCfg).catch(e=>{toast({title:'Could not save',description:(e as Error).message,duration:6000});request<HandoffChoice>('handoffGet').then(setCfg).catch(()=>undefined);});},[toast]);
  return {cfg,save};
}

/** Settings card: what happens when a vendor runs out of usage. */
export function HandoffCard(){
  const {cfg,save}=useHandoff();const options=useHandoffOptions();
  return <div className="handoff-card">
    <div className="handoff-copy"><span className="eyebrow">WORKFLOW HANDOFF</span><h2>When a vendor runs out of usage</h2>
      <p>Choose what happens to a chat or a team's work when Codex, Claude or another vendor hits its limit. LAICA can hand it to a backup team that doesn't rely on that vendor, or to one agent, and carries over everything done so far. Work moves at most twice, so it can never loop. (Tag-team chats, below, are different: they swap back and forth for as long as real progress is being made.)</p></div>
    <Select label="Hand off to" value={choiceValue(cfg)} options={options} onChange={e=>save(choiceOf(e.target.value))} searchable={options.length>9} menuWidth={380}/>
    <p className="handoff-note">{cfg.Mode==='manual'?'Nothing moves by itself. The chat shows a banner and you pick where to continue.':cfg.Mode==='team'?'A backup team is only used if none of its agents is the vendor that ran out.':'The agent you chose takes over with the full conversation.'}</p>
  </div>;
}

/** Compact version for the workflow designer's toolbar. */
export function HandoffPill(){
  const {cfg,save}=useHandoff();const options=useHandoffOptions();
  return <Select variant="pill" aria-label="Workflow handoff" value={choiceValue(cfg)} options={options} onChange={e=>save(choiceOf(e.target.value))} menuWidth={360}
    triggerLabel={'Handoff · '+(cfg.Mode==='manual'?'Ask me':options.find(o=>o.value===choiceValue(cfg))?.label??'Ask me')}/>;
}

/** A row in the team editor: this team's own choice, or the default. */
export function TeamHandoffSelect({value,onChange,teamId}:{value?:HandoffChoice|null;onChange:(c:HandoffChoice)=>void;teamId?:string}){
  const options=useHandoffOptions(true,teamId);
  return <Select label="If a vendor runs out of usage" value={choiceValue(value??{Mode:'inherit'})} options={options} onChange={e=>onChange(choiceOf(e.target.value))} menuWidth={380}/>;
}

/** On a team that stopped because a vendor ran out: pick where its work continues. */
export function TeamContinue({teamId,onDone}:{teamId:string;onDone:()=>void}){
  const {toast}=useToast();const options=useHandoffOptions(false,teamId).filter(o=>o.value!=='manual');const [pick,setPick]=useState('');const [busy,setBusy]=useState(false);
  const chosen=options.some(o=>o.value===pick)?pick:options[0]?.value??'';
  if(!options.length)return null;
  const go=async()=>{setBusy(true);try{await request('teamHandoff',{Id:teamId,Choice:choiceOf(chosen)});onDone();toast({title:'Work handed over',description:'Everything done so far went with it.'});}catch(e){toast({title:'Could not hand over',description:(e as Error).message,duration:8000});}setBusy(false);};
  return <div className="team-continue"><Select aria-label="Continue with" value={chosen} options={options} onChange={e=>setPick(e.target.value)} menuWidth={380}/><button type="button" className="sketch-go" disabled={busy} onClick={()=>{void go();}}><ArrowRightLeft size={14}/> Continue there</button></div>;
}
