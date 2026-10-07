import {useCallback,useEffect,useMemo,useRef,useState} from 'react';
import {Button,Glass,useToast} from 'open-glass-ui';
import {Gauge,AlertTriangle,ArrowRightLeft,Check,ChevronRight,Info} from 'lucide-react';
import {Select} from './GlassSelect';
import {request,isDesktop} from './bridge';
import {useHarness} from './harness-store';
import type {VendorUsage,PlanWindow} from './harness-store';
import {Progress,Tip} from './ui/kit';

const fmt=(n:number)=>n>=1e6?(n/1e6).toFixed(1)+'M':n>=1e3?Math.round(n/1e3)+'k':String(n);
const tone=(p:number)=>p>=95?'bad':p>=80?'warn':'ok';
export const back=(iso:string)=>{const t=Date.parse(iso);if(!t)return 'soon';const m=Math.max(1,Math.round((t-Date.now())/60000));return m<60?`in about ${m} min`:`around ${new Date(t).toLocaleTimeString([],{hour:'numeric',minute:'2-digit'})}`;};
/** "resets in 2h 14m" within a day, otherwise "resets Sun 3:40 pm". */
export const resetText=(iso:string)=>{
  const t=Date.parse(iso);if(!t)return '';const ms=t-Date.now();if(ms<=0)return 'just reset';
  const m=Math.round(ms/60000);if(m<60)return `resets in ${m} min`;if(m<24*60)return `resets in ${Math.floor(m/60)}h ${m%60}m`;
  const d=new Date(t);return 'resets '+d.toLocaleDateString([],{weekday:'short'})+' '+d.toLocaleTimeString([],{hour:'numeric',minute:'2-digit'});
};
const windowName=(w:PlanWindow)=>w.Label==='5-hour'?'5-hour window':w.Label==='Weekly'?'This week':w.Label;

/** Bottom-right usage meter: real plan percentages (daily window and weekly) per app, token totals and optional limits. */
export function UsageButton(){
  const {usage,reloadUsage,setBudget,clearLimit}=useHarness();
  const [open,setOpen]=useState(false);const box=useRef<HTMLDivElement>(null);
  useEffect(()=>{if(!open)return;reloadUsage();const t=setInterval(reloadUsage,15000);const off=(e:MouseEvent)=>{if(box.current&&!box.current.contains(e.target as Node))setOpen(false);};const key=(e:KeyboardEvent)=>{if(e.key==='Escape')setOpen(false);};document.addEventListener('mousedown',off);document.addEventListener('keydown',key);return()=>{clearInterval(t);document.removeEventListener('mousedown',off);document.removeEventListener('keydown',key);};},[open,reloadUsage]);
  const worst=useMemo(()=>[...usage].filter(u=>u.Available||u.Turns>0).sort((a,b)=>(b.Limited?1000:b.TopPercent)-(a.Limited?1000:a.TopPercent))[0],[usage]);
  if(!isDesktop)return null;
  const hot=worst&&(worst.Limited||worst.TopPercent>=80);
  const t=worst?.Limited?'bad':worst&&worst.TopPercent>=95?'bad':worst&&worst.TopPercent>=80?'warn':'ok';
  const label=!worst?'Usage':worst.Limited?`${worst.Name} out of usage`:hot?`${worst.Name} ${Math.round(worst.TopPercent)}%`:'Usage';
  return <div className="usage-wrap" ref={box}>
    <Tip label="Plan usage across your agents" side="top"><button type="button" className={`usage-btn is-${t}`} aria-haspopup="dialog" aria-expanded={open} onClick={()=>setOpen(o=>!o)}><Gauge size={13}/><span>{label}</span></button></Tip>
    {open&&<Glass material="regular" className="usage-pop" role="dialog" aria-label="Usage across agents">
      <div className="usage-head"><strong>Usage</strong><span>Live from each agent</span></div>
      <div className="usage-scroll">
        {!usage.length&&<p className="usage-note">Nothing used yet.</p>}
        {usage.filter(u=>u.Available||u.Turns>0||u.Windows.length>0).map(u=><UsageRow key={u.Key} u={u} onBudget={(d,w)=>setBudget(u.Key,d,w)} onClear={()=>clearLimit(u.Key)}/>)}
      </div>
    </Glass>}
  </div>;
}

function Meter({label,percent,detail,sub}:{label:string;percent:number;detail:string;sub?:string}){
  const p=Math.max(0,Math.min(100,percent));
  return <div className="meter"><div className="meter-top"><span>{label}</span><b className={`is-${tone(p)}`}>{Math.round(p)}%</b></div><Progress value={p} tone={tone(p)} aria-label={`${label}: ${Math.round(p)} percent used`}/><div className="meter-sub"><span>{detail}</span>{sub&&<span>{sub}</span>}</div></div>;
}

function UsageRow({u,onBudget,onClear}:{u:VendorUsage;onBudget:(daily?:number,weekly?:number)=>void;onClear:()=>void}){
  const [day,setDay]=useState(u.Budget?String(u.Budget):''),[week,setWeek]=useState(u.WeekBudget?String(u.WeekBudget):''),[limits,setLimits]=useState(false);
  useEffect(()=>setDay(u.Budget?String(u.Budget):''),[u.Budget]);useEffect(()=>setWeek(u.WeekBudget?String(u.WeekBudget):''),[u.WeekBudget]);
  const state=u.Limited?'bad':tone(u.TopPercent);
  const exact=u.Windows.length>0;
  const commit=()=>{const d=Number(day||0),w=Number(week||0);if(d!==u.Budget||w!==u.WeekBudget)onBudget(d,w);};
  return <div className="usage-row">
    <div className="usage-line"><span className={`usage-dot is-${state==='ok'?'ok':state}`}/><strong>{u.Name}</strong>{u.Source&&<Tip label={u.Source} side="top"><span className="usage-info"><Info size={12}/></span></Tip>}<span className="usage-sum">{(u.Replies??0)>0?`${u.Replies} replies today`:u.TurnsToday>0?`${u.TurnsToday} chat turn${u.TurnsToday===1?'':'s'} in LAICA today`:''}</span></div>
    {u.Limited&&<div className="usage-alert"><AlertTriangle size={13}/><span>Out of usage, back {back(u.LimitedUntilUtc)}.</span><button type="button" onClick={onClear}>It's back</button></div>}
    {u.Windows.map(w=><Meter key={w.Label} label={windowName(w)} percent={w.Percent} detail={w.Stale?'window renewed since the last reading':resetText(w.ResetsUtc)||'live reading'} sub={w.Source==='Claude'?'reported by Claude':undefined}/>)}
    {!exact&&u.Budget>0&&<Meter label="Today" percent={u.TokensToday*100/u.Budget} detail={`${fmt(u.TokensToday)} of ${fmt(u.Budget)} tokens`}/>}
    {!exact&&u.WeekBudget>0&&<Meter label="This week" percent={u.TokensWeek*100/u.WeekBudget} detail={`${fmt(u.TokensWeek)} of ${fmt(u.WeekBudget)} tokens`}/>}
    <div className="usage-tokens"><span>{fmt(u.TokensToday)} tokens today</span><span>{fmt(u.TokensWeek)} this week</span></div>
    {!exact&&u.Key!=='codex'&&!u.Budget&&!u.WeekBudget&&<p className="usage-hint">{u.Key==='claude'?'Claude does not share its plan percentage with other apps. Set a limit below to see one, or LAICA will show it once Claude reports it during a chat.':'Set a limit below to see a percentage for this one.'}</p>}
    <button type="button" className="usage-limits-toggle" aria-expanded={limits} onClick={()=>setLimits(l=>!l)}><ChevronRight size={12} className={limits?'is-open':''}/>{limits?'Hide':'Set'} your own limits</button>
    {limits&&<div className="usage-limits">
      <label className="usage-budget">Daily tokens<input inputMode="numeric" placeholder="none" value={day} onChange={e=>setDay(e.target.value.replace(/[^0-9]/g,''))} onBlur={commit} onKeyDown={e=>{if(e.key==='Enter')(e.target as HTMLInputElement).blur();}}/></label>
      <label className="usage-budget">Weekly tokens<input inputMode="numeric" placeholder="none" value={week} onChange={e=>setWeek(e.target.value.replace(/[^0-9]/g,''))} onBlur={commit} onKeyDown={e=>{if(e.key==='Enter')(e.target as HTMLInputElement).blur();}}/></label>
    </div>}
  </div>;
}

/** Shown in a chat when its vendor ran out of usage: carry on with another agent, or hand the request to a team. */
export function LimitBanner({sessionId,vendor,text}:{sessionId:string;vendor:string;text:string}){
  const {usage,harnesses,teams,events,continueElsewhere}=useHarness();const {toast}=useToast();
  const [pick,setPick]=useState('');const [busy,setBusy]=useState(false);
  const me=usage.find(u=>u.Key===vendor);
  const options=useMemo(()=>{
    const out:{value:string;label:string;description:string;group:string}[]=[];
    for(const h of harnesses){if(!h.Available||['laica','workflow'].includes(h.Id)||h.Id===vendor)continue;const u=usage.find(x=>x.Key===h.Id);if(u?.Limited)continue;out.push({value:'h:'+h.Id,label:h.Id==='codex'?'Codex (GPT)':h.Name,description:'Takes over with this chat\'s transcript',group:'Continue with'});}
    for(const t of teams){if(t.Running)continue;out.push({value:'t:'+t.Id,label:t.Title,description:'Runs your last request as a team',group:'Hand to a team'});}
    return out;
  },[harnesses,teams,usage,vendor]);
  const chosen=options.some(o=>o.value===pick)?pick:options[0]?.value??'';
  const go=useCallback(async()=>{
    if(!chosen)return;setBusy(true);
    try{
      if(chosen.startsWith('h:'))await continueElsewhere(sessionId,chosen.slice(2));
      else{const list=events[sessionId]??[];let goal='';for(let i=list.length-1;i>=0;i--)if(list[i].Kind==='user'){goal=list[i].Text;break;}await request('teamRun',{Id:chosen.slice(2),Goal:goal});toast({title:'Team started',description:'Follow it on the Teams page.'});}
    }catch(e){toast({title:'Could not continue',description:(e as Error).message,duration:7000});}
    setBusy(false);
  },[chosen,sessionId,events,continueElsewhere,toast]);
  if(me&&!me.Limited)return <div className="hmsg limit is-ok"><Check size={14}/> {me.Name} is available again. Send your message again to carry on here.</div>;
  return <div className="hmsg limit"><div className="limit-title"><AlertTriangle size={15}/><strong>{text||'This agent has run out of usage.'}</strong>{me?.Limited&&<span> Back {back(me.LimitedUntilUtc)}.</span>}</div>
    {options.length?<div className="limit-actions"><Select aria-label="Continue with" value={chosen} options={options} onChange={e=>setPick(e.target.value)}/><Button variant="primary" size="small" leadingIcon={<ArrowRightLeft size={14}/>} disabled={busy} onClick={go}>{chosen.startsWith('t:')?'Run on team':'Continue there'}</Button></div>
      :<p className="limit-none">No other agent has usage left right now. Wait for the reset, or add an API service under Settings, Connections.</p>}
  </div>;
}
