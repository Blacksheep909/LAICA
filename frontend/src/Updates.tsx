import {useCallback,useEffect,useState} from 'react';
import {Switch,Button} from 'open-glass-ui';
import {ArrowUpCircle} from 'lucide-react';
import {request,isRemote} from './bridge';

export interface UpdateInfo { Auto:boolean; Current:string; Latest:string; Available:boolean; Notes:string; Page:string; CheckedUtc:string; Error:string; CanInstall:boolean; McpCheckedUtc:string; McpUpdated:string[] }

function useUpdates(){
  const [info,setInfo]=useState<UpdateInfo|null>(null),[busy,setBusy]=useState(false),[err,setErr]=useState('');
  const load=useCallback(()=>request<UpdateInfo>('updatesGet',{}).then(setInfo).catch(()=>{}),[]);
  useEffect(()=>{void load();const t=setInterval(()=>{void load();},20*60*1000);return()=>clearInterval(t);},[load]);
  const check=()=>{setBusy(true);setErr('');return request<UpdateInfo>('updatesCheck',{}).then(setInfo).catch(e=>setErr((e as Error).message)).finally(()=>setBusy(false));};
  const setAuto=(v:boolean)=>request<UpdateInfo>('updatesSet',{Auto:v}).then(setInfo).catch(e=>setErr((e as Error).message));
  const install=()=>{setBusy(true);setErr('');return request('updateInstall',{}).catch(e=>{setErr((e as Error).message);setBusy(false);});};
  return {info,busy,err,check,setAuto,install};
}

const when=(iso:string)=>{if(!iso)return 'never';const m=Math.round((Date.now()-new Date(iso).getTime())/60000);return m<1?'just now':m<60?`${m} min ago`:m<1440?`${Math.round(m/60)} h ago`:`${Math.round(m/1440)} d ago`;};

/** Small pill in the sidebar footer when a newer LAICA is available. */
export function UpdatePill(){
  const {info,busy,err,install}=useUpdates();
  if(!info||!info.Available||isRemote||!info.CanInstall)return null;
  return <div className="update-pill"><ArrowUpCircle size={14}/><span>LAICA {info.Latest} is ready</span><button type="button" disabled={busy} title={err||`Install ${info.Latest} and restart`} onClick={()=>void install()}>{busy?'Updating…':'Update'}</button>{err&&<small className="bad">{err}</small>}</div>;
}

/** Settings card: keep LAICA and MCP servers up to date. */
export function UpdatesCard(){
  const {info,busy,err,check,setAuto,install}=useUpdates();
  if(!info)return null;
  return <div className="handoff-card updates-card">
    <div className="handoff-copy"><span className="eyebrow">UPDATES</span><h2>Keep LAICA and your MCP servers current</h2>
      <p>LAICA checks GitHub for a newer release every few hours and tells you when one is ready; nothing installs until you click Update. Installed MCP servers that start with npx or uvx are set to fetch their latest version each time they launch. Servers pinned to a specific version are left alone, and the original config files are backed up first.</p></div>
    <Switch label="Check for updates automatically" description="LAICA and MCP servers. Turn off to only check when you click the button." checked={info.Auto} onCheckedChange={v=>void setAuto(v)}/>
    <div className="update-row"><div><b>LAICA {info.Current}</b><small>{info.Available?`Version ${info.Latest} is available.`:info.Error?`Couldn't check: ${info.Error}`:`Up to date. Last checked ${when(info.CheckedUtc)}.`}</small></div>
      <div className="update-actions"><Button size="small" variant="quiet" disabled={busy} onClick={()=>void check()}>{busy?'Checking…':'Check now'}</Button>{info.Available&&info.CanInstall&&!isRemote&&<Button size="small" variant="primary" disabled={busy} onClick={()=>void install()}>Update to {info.Latest}</Button>}{info.Available&&!info.CanInstall&&info.Page&&<small>Download it from the release page.</small>}</div></div>
    <p className="handoff-note">MCP servers: checked {when(info.McpCheckedUtc)}{info.McpUpdated.length>0?`. Now on latest: ${info.McpUpdated.join(', ')}.`:'. Nothing needed changing.'}</p>
    {err&&<p className="handoff-note bad">{err}</p>}
  </div>;
}