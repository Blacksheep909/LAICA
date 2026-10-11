import {useCallback,useEffect,useState} from 'react';
import {Moon} from 'lucide-react';
import {Button,SegmentedControl,Switch} from './ui/controls';
import {request} from './bridge';

interface Awake { Platform:string; Active:boolean; Display:boolean; Lid:boolean; MinBattery:number; Remaining:number; Error:string; Supported:boolean; HasBattery:boolean; BatteryPercent:number; OnBattery:boolean; LidNote:string }
const lengths=[['30','30 min'],['60','1 hour'],['120','2 hours'],['240','4 hours'],['480','8 hours'],['720','12 hours']];
const floors=[['0','Off'],['10','10%'],['20','20%'],['30','30%']];

function left(s:number){const h=Math.floor(s/3600),m=Math.floor(s%3600/60);return h?`${h} h ${String(m).padStart(2,'0')} min`:m?`${m} min`:`${s} s`;}

/** Settings card: keep the computer awake for a set time so long agent runs are not cut off by sleep. */
export function AwakeCard(){
  const [a,setA]=useState<Awake|null>(null),[minutes,setMinutes]=useState('120'),[display,setDisplay]=useState(false),[lid,setLid]=useState(false),[floor,setFloor]=useState('20'),[busy,setBusy]=useState(false),[err,setErr]=useState(''),[now,setNow]=useState(0);
  const load=useCallback(()=>request<Awake>('awakeGet',{}).then(r=>{setA(r);setNow(r.Remaining);}).catch(()=>{}),[]);
  useEffect(()=>{void load();const t=setInterval(()=>void load(),10000);return()=>clearInterval(t);},[load]);
  useEffect(()=>{if(!a?.Active)return;const t=setInterval(()=>setNow(n=>Math.max(0,n-1)),1000);return()=>clearInterval(t);},[a?.Active]);
  const run=(method:string,payload:Record<string,unknown>={})=>{setBusy(true);setErr('');return request<Awake>(method,payload).then(r=>{setA(r);setNow(r.Remaining);}).catch(e=>setErr((e as Error).message)).finally(()=>setBusy(false));};
  if(!a)return null;
  const mac=a.Platform==='mac';
  return <div className="handoff-card awake-card">
    <div className="handoff-copy"><span className="eyebrow">INSOMNIA</span><h2>Keep this computer awake</h2>
      <p>Stops sleep from interrupting a long run. It ends by itself at the time you choose, and when you quit LAICA.</p></div>
    {a.Active
      ? <div className="awake-live"><Moon size={16}/><span><b>Awake for {left(now)}</b>{a.Lid?' · lid-closed mode on':''}{a.Display?' · screen stays on':''}</span><Button variant="secondary" size="small" disabled={busy} onClick={()=>void run('awakeStop')}>Stop</Button></div>
      : <>
        <SegmentedControl aria-label="How long" items={lengths.map(([value,label])=>({value,label}))} value={minutes} onValueChange={setMinutes}/>
        <Switch label="Keep the screen on too" description="Otherwise only the computer stays awake and the screen may turn off." checked={display} onCheckedChange={setDisplay}/>
        <Switch label="Keep running with the lid closed" description={a.LidNote} checked={lid} onCheckedChange={setLid}/>
        {a.HasBattery&&<div className="awake-floor"><span className="switch-label">Stop on battery below</span><SegmentedControl aria-label="Battery floor" items={floors.map(([value,label])=>({value,label}))} value={floor} onValueChange={setFloor}/></div>}
        {lid&&a.HasBattery&&<p className="handoff-note">A closed laptop in a bag gets hot. The battery limit above is a good idea.</p>}
        <div className="dialog-actions"><Button variant="primary" leadingIcon={<Moon size={14}/>} disabled={busy||!a.Supported} onClick={()=>void run('awakeStart',{Minutes:Number(minutes),Display:display,Lid:lid,MinBattery:a.HasBattery?Number(floor):0})}>Keep awake{mac&&lid?'…':''}</Button></div>
        {!a.Supported&&<p className="handoff-note bad">{mac?'caffeinate was not found on this Mac.':'This needs systemd (systemd-inhibit).'}</p>}
      </>}
    {(err||a.Error)&&<p className="handoff-note bad">{err||a.Error}</p>}
  </div>;
}
