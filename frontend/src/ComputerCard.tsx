import {useCallback,useEffect,useState} from 'react';
import {Switch,Button,useToast} from 'open-glass-ui';
import {request} from './bridge';

interface State { Enabled:boolean; Stopped:boolean; ServerFound:boolean; InstalledOn:string[]; Claude:boolean; Codex:boolean; Problems?:string }

/** Settings card: let agents see the screen and use the mouse and keyboard, with a lavender outline while they do and Esc to stop them. */
export function ComputerCard(){
  const {toast}=useToast();const [st,setSt]=useState<State|null>(null),[busy,setBusy]=useState(false);
  const load=useCallback(()=>request<State>('computerGet',{}).then(setSt).catch(()=>undefined),[]);
  useEffect(()=>{void load();const t=setInterval(()=>{void load();},3000);return()=>clearInterval(t);},[load]);
  const set=(enabled:boolean)=>{setBusy(true);return request<State>('computerSet',{Enabled:enabled}).then(s=>{setSt(s);if(s.Problems)toast({title:'Some agents could not be set up',description:s.Problems,duration:8000});}).catch(e=>toast({title:'Could not change computer use',description:(e as Error).message,duration:7000})).finally(()=>setBusy(false));};
  if(!st)return null;
  const where=[st.Claude&&'Claude Code',st.Codex&&'Codex'].filter(Boolean).join(' and ')||'no installed agent';
  return <div className="handoff-card computer-card">
    <div className="handoff-copy"><span className="eyebrow">COMPUTER USE</span><h2>Let agents use your computer</h2>
      <p>When this is on, Claude Code and Codex get LAICA's computer tools: they can take screenshots of your main screen and use the mouse and keyboard. While an agent is acting, a slim lavender outline is drawn around your screen and the chat's Screen tab shows what it sees. <b>Press Esc at any time to stop it.</b> It is then blocked until you switch this on again.</p></div>
    <Switch label="Allow agents to use my computer" description={`Off by default. Turning it on sets it up for ${where}.`} checked={st.Enabled} disabled={busy||(!st.ServerFound)} onCheckedChange={v=>void set(v)}/>
    {!st.ServerFound&&<p className="handoff-note bad">LAICA.Computer.exe was not found next to LAICA. Reinstall LAICA to get it.</p>}
    {st.Enabled&&st.Stopped&&<div className="computer-stopped"><span>You pressed Esc, so agents are blocked.</span><Button size="small" variant="primary" disabled={busy} onClick={()=>void set(true)}>Allow again</Button></div>}
    {st.Enabled&&!st.Stopped&&<p className="handoff-note">On{st.InstalledOn.length?` for ${st.InstalledOn.map(x=>x==='claude'?'Claude Code':'Codex').join(' and ')}`:''}. Agents see only your main screen. Ask the agent to use your computer in a normal chat; it will take a screenshot first.</p>}
    <p className="handoff-note">Agents can click and type anywhere you can, including in apps that are signed in. Only switch this on when you are watching, and keep sensitive windows closed.</p>
  </div>;
}