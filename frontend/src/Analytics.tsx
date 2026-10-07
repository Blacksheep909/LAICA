import {useCallback,useEffect,useMemo,useState} from 'react';
import {BarChart3} from 'lucide-react';
import {request,isDesktop} from './bridge';
import {Popover,PopoverTrigger,PopoverContent,ToggleGroup,ToggleGroupItem,Progress,Tip} from './ui/kit';

interface Model { Name:string; Vendor:string; Messages:number; Tokens:number; Cost:number; Share:number }
interface Price { Class:string; Input:number; Output:number; CacheRead:number; CacheWrite:number; Default:number[] }
type Day=[string,number,number,number,number];   // day, messages, codex tokens, claude tokens
interface Data {
  Range:string; Vendor:string; Indexing:{Done:number;Total:number;Running:boolean}; Sessions:number; Messages:number; Tokens:number; Cost:number; ActiveDays:number; PeakHour:number; FavoriteModel:string;
  CurrentStreak:number; LongestStreak:number; BusiestDay:string; BusiestTokens:number; AvgTokensPerMessage:number; LongestSessionMinutes:number;
  Grid:[string,number,number][]; Daily:Day[]; Weekdays:number[]; Hours:number[]; Models:Model[]; Vendors:{Name:string;Messages:number;Tokens:number;Sessions:number;Cost:number}[]; Tools:{Name:string;Count:number}[]; Fun:string;
}

const compact=(n:number)=>n>=1e9?(n/1e9).toFixed(1)+'B':n>=1e6?(n/1e6).toFixed(1)+'M':n>=1e4?Math.round(n/1e3)+'k':n>=1e3?(n/1e3).toFixed(1)+'k':String(Math.round(n));
const whole=(n:number)=>n.toLocaleString();
const money=(n:number)=>n>=1000?'$'+Math.round(n).toLocaleString():n>=10?'$'+n.toFixed(0):'$'+n.toFixed(2);
const hourName=(h:number)=>h<0?'–':`${h%12===0?12:h%12} ${h<12?'AM':'PM'}`;
const span=(m:number)=>m>=1440?`${Math.round(m/1440)} d`:m>=60?`${Math.floor(m/60)}h ${Math.round(m%60)}m`:`${Math.round(m)} min`;
const dayName=(iso:string)=>{if(!iso)return '–';const d=new Date(iso+'T12:00:00');return d.toLocaleDateString([],{month:'short',day:'numeric'});};
const COLORS=['#9b86f5','#58d68d','#ff9d66','#6cb6ff','#ff8fb0','#e9d36c'];
const vendorClass=(name:string)=>/claude/i.test(name)?'vc-claude':'vc-codex';

function Tile({label,value}:{label:string;value:string}){return <div className="an-tile"><span>{label}</span><b title={value}>{value}</b></div>;}

/** Heatmap of activity: one square per day, weeks across. */
function Heat({grid}:{grid:[string,number,number][]}){
  const levels=useMemo(()=>{const vals=grid.map(g=>g[2]||g[1]).filter(v=>v>0).sort((a,b)=>a-b);const q=(p:number)=>vals.length?vals[Math.min(vals.length-1,Math.floor(vals.length*p))]:1;return [q(.25),q(.5),q(.75)];},[grid]);
  const level=(v:number)=>v<=0?0:v<=levels[0]?1:v<=levels[1]?2:v<=levels[2]?3:4;
  const weeks:[string,number,number][][]=[];for(let i=0;i<grid.length;i+=7)weeks.push(grid.slice(i,i+7));
  return <div className="an-heat" role="img" aria-label="Activity over the last 26 weeks">{weeks.map((w,i)=><div key={i} className="an-week">{w.map(([day,msgs,tokens])=><i key={day} className={`an-cell l${level(tokens||msgs)}`} title={`${dayName(day)}: ${whole(msgs)} messages, ${compact(tokens)} tokens`}/>)}</div>)}</div>;
}

/** Tokens per day, stacked by app. */
function DailyBars({daily}:{daily:Day[]}){
  const W=520,H=132,B=18;const max=Math.max(1,...daily.map(d=>d[2]+d[3]));const bw=W/Math.max(1,daily.length);
  return <svg className="an-svg" viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Tokens per day" preserveAspectRatio="none">
    {[0.5,1].map(f=><line key={f} x1="0" x2={W} y1={(H-B)*(1-f)+2} y2={(H-B)*(1-f)+2} className="an-grid"/>)}
    {daily.map((d,i)=>{const hk=d[3]/max*(H-B-4),hc=d[2]/max*(H-B-4),x=i*bw+Math.min(1.5,bw*.15),w=Math.max(1.5,bw-Math.min(3,bw*.3));
      return <g key={d[0]}><title>{`${dayName(d[0])}: ${compact(d[2]+d[3])} tokens (Codex ${compact(d[2])}, Claude ${compact(d[3])}), ${whole(d[1])} messages`}</title>
        {hk>0&&<rect x={x} y={H-B-hk} width={w} height={hk} rx="2" className="vc-claude-f"/>}{hc>0&&<rect x={x} y={H-B-hk-hc} width={w} height={hc} rx="2" className="vc-codex-f"/>}<rect x={x} y="0" width={w} height={H-B} fill="transparent"/></g>;})}
    <text x="0" y="10" className="an-axis">{compact(max)}</text><text x="0" y={H-3} className="an-axis">{dayName(daily[0]?.[0]??'')}</text><text x={W} y={H-3} textAnchor="end" className="an-axis">{dayName(daily[daily.length-1]?.[0]??'')}</text>
  </svg>;
}

/** Estimated cost per day. */
function CostBars({daily}:{daily:Day[]}){
  const W=520,H=110,B=18;const max=Math.max(0.01,...daily.map(d=>d[4]));const bw=W/Math.max(1,daily.length);
  return <svg className="an-svg" viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Estimated cost per day" preserveAspectRatio="none">
    {[0.5,1].map(f=><line key={f} x1="0" x2={W} y1={(H-B)*(1-f)+2} y2={(H-B)*(1-f)+2} className="an-grid"/>)}
    {daily.map((d,i)=>{const h=d[4]/max*(H-B-4),x=i*bw+Math.min(1.5,bw*.15),w=Math.max(1.5,bw-Math.min(3,bw*.3));return <g key={d[0]}><title>{`${dayName(d[0])}: ${money(d[4])}`}</title>{h>0&&<rect x={x} y={H-B-h} width={w} height={h} rx="2" fill="#e9d36c"/>}<rect x={x} y="0" width={w} height={H-B} fill="transparent"/></g>;})}
    <text x="0" y="10" className="an-axis">{money(max)}</text><text x="0" y={H-3} className="an-axis">{dayName(daily[0]?.[0]??'')}</text><text x={W} y={H-3} textAnchor="end" className="an-axis">{dayName(daily[daily.length-1]?.[0]??'')}</text>
  </svg>;
}

/** Editable price table: dollars per million tokens by model family. */
function PriceEditor({onSaved}:{onSaved:()=>void}){
  const [rows,setRows]=useState<Price[]>([]),[busy,setBusy]=useState(false);
  useEffect(()=>{void request<Price[]>('pricesGet',{}).then(setRows).catch(()=>{});},[]);
  const edit=(i:number,k:keyof Price,v:string)=>setRows(r=>r.map((x,j)=>j===i?{...x,[k]:v as unknown as number}:x));
  const save=(list:Price[])=>{setBusy(true);void request<Price[]>('pricesSet',{Rows:list}).then(r=>{setRows(r);onSaved();}).finally(()=>setBusy(false));};
  const reset=()=>save(rows.map(r=>({...r,Input:r.Default[0],Output:r.Default[1],CacheRead:r.Default[2],CacheWrite:r.Default[3]})));
  return <div className="an-prices"><p className="an-note">US dollars per million tokens. Defaults are public list prices by model family; change them to match your plan or provider.</p>
    <table><thead><tr><th>Model family</th><th>Input</th><th>Output</th><th>Cache read</th><th>Cache write</th></tr></thead><tbody>
      {rows.map((r,i)=><tr key={r.Class}><td>{r.Class}</td>{((['Input','Output','CacheRead','CacheWrite']) as (keyof Price)[]).map(k=><td key={k}><input type="number" min="0" step="0.01" value={String(r[k])} onChange={e=>edit(i,k,e.target.value)} aria-label={`${r.Class} ${k}`}/></td>)}</tr>)}
    </tbody></table>
    <div className="an-price-actions"><button type="button" className="ui-btn" disabled={busy} onClick={()=>save(rows)}>Save prices</button><button type="button" className="ui-btn" disabled={busy} onClick={reset}>Reset to defaults</button></div></div>;
}

interface TMember { Name:string; Role:string; Harness:string; Model:string; Runs:number; Seconds:number; Tokens:number; Cost:number; Share:number; Failed:number }
interface TTeam { TeamId:string; Title:string; Runs:number; Succeeded:number; Failed:number; Stopped:number; SuccessRate:number; AvgSeconds:number; AvgCost:number; TotalCost:number; TotalTokens:number; Speedup:number; LeaderShare:number; CostPerTask:number; TaskSuccess:number; Swaps:number; Vendors:string[]; Members:TMember[]; Recent:{Goal:string;StartUtc:string;Seconds:number;Outcome:string;Cost:number;Swaps:number}[] }
const dur=(s:number)=>s>=3600?`${Math.floor(s/3600)}h ${Math.round(s%3600/60)}m`:s>=60?`${Math.floor(s/60)}m ${Math.round(s%60)}s`:`${Math.round(s)}s`;
const vendorLabel=(h:string)=>h==='claude'?'Claude':h==='codex'?'Codex':h==='gemini'?'Gemini':h;

/** How well each team design performs: success, time, cost, how parallel it really is, where the money goes, and whether it needed agent swaps. */
function TeamsView({range}:{range:string}){
  const [data,setData]=useState<{Runs:number;Cost:number;Teams:TTeam[]}|null>(null),[err,setErr]=useState('');
  useEffect(()=>{void request<{Runs:number;Cost:number;Teams:TTeam[]}>('teamAnalytics',{Range:range}).then(d=>{setData(d);setErr('');}).catch(e=>setErr((e as Error).message));},[range]);
  if(err)return <p className="an-note">{err}</p>;
  if(!data)return <p className="an-note">Loading team runs…</p>;
  if(!data.Teams.length)return <p className="an-note">No team runs yet. Run a team from the Teams page and its results, speed and cost will show up here so you can compare designs.</p>;
  const best=(f:(t:TTeam)=>number,low=false)=>{const c=data.Teams.filter(t=>t.Runs>0);return c.length<2?'':c.slice().sort((a,b)=>low?f(a)-f(b):f(b)-f(a))[0].TeamId;};
  const fastest=best(t=>t.AvgSeconds,true),cheapest=best(t=>t.AvgCost,true),reliable=best(t=>t.SuccessRate);
  return <div className="an-teams">
    <div className="an-tiles"><Tile label="Team runs" value={whole(data.Runs)}/><Tile label="Teams" value={whole(data.Teams.length)}/><Tile label="Est. cost" value={money(data.Cost)}/></div>
    {data.Teams.map(t=><div key={t.TeamId} className="an-team">
      <div className="an-team-top"><b title={t.Title}>{t.Title}</b>{t.Vendors.length>1&&<span className="ui-badge">{t.Vendors.map(vendorLabel).join(' + ')}</span>}
        {t.TeamId===reliable&&<span className="ui-badge good">most reliable</span>}{t.TeamId===fastest&&<span className="ui-badge good">fastest</span>}{t.TeamId===cheapest&&<span className="ui-badge good">cheapest</span>}</div>
      <div className="an-team-stats">
        <span><b>{t.SuccessRate}%</b>success<small>{t.Succeeded} of {t.Runs} runs</small></span>
        <span><b>{dur(t.AvgSeconds)}</b>per run</span>
        <span><b>{money(t.AvgCost)}</b>per run<small>{t.CostPerTask>0?money(t.CostPerTask)+' per task':''}</small></span>
        <span><b>{t.Speedup.toFixed(1)}x</b>parallel<small>work time vs clock time</small></span>
        <span><b>{t.LeaderShare}%</b>leader cost<small>planning and review</small></span>
        <span><b>{t.Swaps}</b>agent swaps<small>out-of-usage fallbacks</small></span>
      </div>
      <div className="an-label">Where the cost goes</div>
      <div className="an-split" role="img" aria-label="Cost share by teammate">{t.Members.map((m,i)=><i key={m.Name} style={{width:Math.max(2,m.Share)+'%',background:COLORS[i%COLORS.length]}} title={`${m.Name}: ${m.Share}% of cost`}/>)}</div>
      <table className="an-member-table"><thead><tr><th>Teammate</th><th>Agent</th><th>Avg time</th><th>Tokens</th><th>Cost</th><th>Share</th></tr></thead><tbody>
        {t.Members.map((m,i)=><tr key={m.Name}><td><i className="an-dot" style={{background:COLORS[i%COLORS.length]}}/>{m.Name}{m.Role&&<small> · {m.Role}</small>}{m.Failed>0&&<small className="bad"> · {m.Failed} failed</small>}</td><td title={m.Model}>{vendorLabel(m.Harness)}{m.Model&&m.Model!==m.Harness?<small> {m.Model}</small>:null}</td><td>{dur(m.Seconds)}</td><td>{compact(m.Tokens)}</td><td>{money(m.Cost)}</td><td>{m.Share}%</td></tr>)}
      </tbody></table>
      {t.Recent.length>0&&<div className="an-recent">{t.Recent.map((r,i)=><div key={i} className="an-run"><span className={'an-out '+r.Outcome}>{r.Outcome}</span><span className="an-goal" title={r.Goal}>{r.Goal}</span><em>{dur(r.Seconds)} · {money(r.Cost)}{r.Swaps>0?` · ${r.Swaps} swap${r.Swaps>1?'s':''}`:''}</em></div>)}</div>}
    </div>)}
  </div>;
}

/** Running total of tokens over the same days. */
function Cumulative({daily}:{daily:Day[]}){
  const W=520,H=96;let run=0;const pts=daily.map(d=>{run+=d[2]+d[3];return run;});const max=Math.max(1,run);
  const xy=pts.map((v,i)=>[pts.length<2?0:i*W/(pts.length-1),H-4-v/max*(H-12)] as [number,number]);
  const line=xy.map((p,i)=>(i?'L':'M')+p[0].toFixed(1)+' '+p[1].toFixed(1)).join(' ');
  return <svg className="an-svg" viewBox={`0 0 ${W} ${H}`} role="img" aria-label="Total tokens over time" preserveAspectRatio="none">
    <defs><linearGradient id="an-area" x1="0" x2="0" y1="0" y2="1"><stop offset="0" stopColor="#b5a6ff" stopOpacity=".45"/><stop offset="1" stopColor="#b5a6ff" stopOpacity="0"/></linearGradient></defs>
    {xy.length>1&&<><path d={`${line} L${W} ${H} L0 ${H} Z`} fill="url(#an-area)"/><path d={line} className="an-line"/></>}
    <text x="0" y="10" className="an-axis">{compact(run)}</text>
  </svg>;
}

function Bars({values,label,names}:{values:number[];label:string;names:(i:number)=>string}){
  const max=Math.max(1,...values);
  return <div className="an-hours" role="img" aria-label={label}>{values.map((v,i)=><span key={i} className="an-hour" title={`${names(i)}: ${whole(v)} messages`}><i style={{height:Math.max(3,Math.round(v*100/max))+'%'}}/><em>{names(i).replace(' ','')}</em></span>)}</div>;
}

function Donut({models}:{models:Model[]}){
  const top=models.slice(0,6);const total=Math.max(1,top.reduce((a,m)=>a+m.Messages,0));const R=40,C=2*Math.PI*R;let off=0;
  return <div className="an-donut"><svg viewBox="0 0 110 110" role="img" aria-label="Share of messages by model"><g transform="rotate(-90 55 55)">{top.map((m,i)=>{const len=m.Messages/total*C;const el=<circle key={m.Name} cx="55" cy="55" r={R} fill="none" stroke={COLORS[i%COLORS.length]} strokeWidth="13" strokeDasharray={`${Math.max(0,len-1.5)} ${C-len+1.5}`} strokeDashoffset={-off}><title>{`${m.Name}: ${m.Share}%`}</title></circle>;off+=len;return el;})}</g></svg>
    <ul>{top.map((m,i)=><li key={m.Name}><i style={{background:COLORS[i%COLORS.length]}}/><span title={m.Name}>{m.Name}</span><b>{m.Share}%</b></li>)}</ul></div>;
}

/** Analytics: opened from the chart icon at the bottom left of the sidebar. Tabs for each app, or all of them combined. */
export default function AnalyticsButton(){
  const [open,setOpen]=useState(false),[view,setView]=useState<'overview'|'charts'|'models'|'teams'|'prices'>('overview'),[range,setRange]=useState<'all'|'30d'|'7d'>('all'),[vendor,setVendor]=useState<''|'codex'|'claude'>(''),[data,setData]=useState<Data|null>(null),[error,setError]=useState('');
  const load=useCallback(()=>request<Data>('analytics',{Range:range,Vendor:vendor}).then(d=>{setData(d);setError('');}).catch(e=>setError((e as Error).message)),[range,vendor]);
  useEffect(()=>{if(!open)return;void load();},[open,load]);
  useEffect(()=>{if(!open||!data?.Indexing.Running)return;const t=setInterval(()=>{void load();},1800);return()=>clearInterval(t);},[open,data?.Indexing.Running,load]);
  if(!isDesktop)return null;
  const ix=data?.Indexing;const pct=ix&&ix.Total?Math.round(ix.Done*100/ix.Total):0;
  const vendorTotal=Math.max(1,(data?.Vendors??[]).reduce((a,v)=>a+v.Tokens,0));
  const weekdayNames=['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];
  return <Popover open={open} onOpenChange={setOpen}>
    <Tip label="Analytics" side="top"><PopoverTrigger asChild><button type="button" className="an-btn" aria-label="Analytics"><BarChart3 size={15}/></button></PopoverTrigger></Tip>
    <PopoverContent className="an-pop" side="top" align="start" sideOffset={12} onOpenAutoFocus={e=>e.preventDefault()}>
      <div className="an-head">
        <ToggleGroup type="single" value={vendor||'all'} onValueChange={v=>{if(v)setVendor(v==='all'?'':v as 'codex'|'claude');}} aria-label="App"><ToggleGroupItem value="all">All</ToggleGroupItem><ToggleGroupItem value="codex">Codex</ToggleGroupItem><ToggleGroupItem value="claude">Claude</ToggleGroupItem></ToggleGroup>
        <ToggleGroup type="single" value={range} onValueChange={v=>{if(v)setRange(v as 'all'|'30d'|'7d');}} aria-label="Range"><ToggleGroupItem value="all">All</ToggleGroupItem><ToggleGroupItem value="30d">30d</ToggleGroupItem><ToggleGroupItem value="7d">7d</ToggleGroupItem></ToggleGroup>
      </div>
      <div className="an-views" role="tablist">{(['overview','charts','models','teams','prices'] as const).map(v=><button key={v} type="button" role="tab" aria-selected={view===v} className={view===v?'on':''} onClick={()=>setView(v)}>{v[0].toUpperCase()+v.slice(1)}</button>)}</div>
      {error&&<p className="an-note">{error}</p>}
      {!data&&!error&&<p className="an-note">Reading your history…</p>}
      {ix?.Running&&<div className="an-index"><span>Reading your history · {ix.Done} of {ix.Total} sessions</span><Progress value={pct} aria-label="Indexing progress"/></div>}
      {data&&view==='overview'&&<>
        <div className="an-tiles">
          <Tile label="Sessions" value={whole(data.Sessions)}/><Tile label="Messages" value={whole(data.Messages)}/><Tile label="Total tokens" value={compact(data.Tokens)}/><Tile label="Est. cost" value={money(data.Cost)}/>
          <Tile label="Active days" value={whole(data.ActiveDays)}/><Tile label="Peak hour" value={hourName(data.PeakHour)}/><Tile label="Favorite model" value={data.FavoriteModel||'–'}/>
        </div>
        <p className="an-note an-cost-note">Est. cost is what these tokens would cost at API list prices. On a flat plan you pay your subscription instead, so treat it as the compute you got. <button type="button" className="an-link" onClick={()=>setView('prices')}>Edit prices</button></p>
        <Heat grid={data.Grid}/>
        <div className="an-row">
          <div className="an-mini"><span>Streak</span><b>{data.CurrentStreak} day{data.CurrentStreak===1?'':'s'}</b><small>best {data.LongestStreak}</small></div>
          <div className="an-mini"><span>Busiest day</span><b>{dayName(data.BusiestDay)}</b><small>{compact(data.BusiestTokens)} tokens</small></div>
          <div className="an-mini"><span>Per message</span><b>{compact(data.AvgTokensPerMessage)}</b><small>tokens</small></div>
          <div className="an-mini"><span>Longest session</span><b>{span(data.LongestSessionMinutes)}</b></div>
        </div>
        {!vendor&&data.Vendors.length>0&&<div className="an-section"><div className="an-split" role="img" aria-label="Tokens by app">{data.Vendors.map(v=><i key={v.Name} className={vendorClass(v.Name)+'-f'} style={{width:Math.max(2,v.Tokens*100/vendorTotal)+'%'}} title={`${v.Name}: ${compact(v.Tokens)} tokens`}/>)}</div>
          <div className="an-legend">{data.Vendors.map(v=><span key={v.Name}><i className={vendorClass(v.Name)+'-f'}/>{v.Name}<b>{compact(v.Tokens)}</b><small>{money(v.Cost)} · {whole(v.Sessions)} sessions</small></span>)}</div></div>}
        {data.Tools.length>0&&<div className="an-section"><div className="an-label">Most used tools</div><div className="an-tools">{data.Tools.slice(0,8).map(t=><span key={t.Name} className="ui-badge">{t.Name}<b>{compact(t.Count)}</b></span>)}</div></div>}
        {data.Fun&&<p className="an-fun">{data.Fun}</p>}
      </>}
      {data&&view==='charts'&&<div className="an-charts">
        <div className="an-section"><div className="an-label">Tokens per day <span className="an-key"><i className="vc-codex-f"/>Codex<i className="vc-claude-f"/>Claude</span></div><DailyBars daily={data.Daily}/></div>
        <div className="an-section"><div className="an-label">Estimated cost per day</div><CostBars daily={data.Daily}/></div>
        <div className="an-section"><div className="an-label">Total tokens over time</div><Cumulative daily={data.Daily}/></div>
        <div className="an-pair">
          <div className="an-section"><div className="an-label">Messages by weekday</div><Bars values={data.Weekdays} label="Messages by weekday" names={i=>weekdayNames[i]}/></div>
          <div className="an-section"><div className="an-label">Share of messages by model</div>{data.Models.length?<Donut models={data.Models}/>:<p className="an-note">No models yet.</p>}</div>
        </div>
        <div className="an-section"><div className="an-label">Messages by hour of day</div><Bars values={data.Hours} label="Messages by hour" names={hourName}/></div>
      </div>}
      {data&&view==='models'&&<div className="an-models">
        {!data.Models.length&&<p className="an-note">No model activity in this range.</p>}
        {data.Models.map(m=><div key={m.Name} className="an-model"><div className="an-model-top"><b title={m.Name}>{m.Name}</b><span className="ui-badge">{m.Vendor}</span><em>{m.Share}%</em></div><Progress value={m.Share} aria-label={`${m.Name} share of messages`}/><div className="an-model-sub"><span>{whole(m.Messages)} messages</span><span>{compact(m.Tokens)} tokens</span><span>{money(m.Cost)}</span></div></div>)}
      </div>}
      {view==='teams'&&<TeamsView range={range}/>}
      {view==='prices'&&<PriceEditor onSaved={()=>{void load();}}/>}
    </PopoverContent>
  </Popover>;
}
