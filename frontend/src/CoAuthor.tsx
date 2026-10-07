import {useCallback,useEffect,useState} from 'react';
import {Switch} from 'open-glass-ui';
import {request} from './bridge';

interface Cfg { Enabled:boolean; Name:string; Email:string; Placeholder:boolean; Trailer:string; Projects:{Cwd:string;Enabled:boolean}[] }

/** Settings card: LAICA as a co-author on commits made by agents run through it. */
export function CoAuthorCard(){
  const [cfg,setCfg]=useState<Cfg|null>(null),[email,setEmail]=useState(''),[err,setErr]=useState('');
  const apply=useCallback((c:Cfg)=>{setCfg(c);setEmail(c.Placeholder?'':c.Email);},[]);
  useEffect(()=>{void request<Cfg>('coauthorGet',{}).then(apply).catch(()=>{});},[apply]);
  const save=(p:Record<string,unknown>)=>request<Cfg>('coauthorSet',p).then(c=>{apply(c);setErr('');}).catch(e=>setErr((e as Error).message));
  if(!cfg)return null;
  return <div className="handoff-card coauthor-card">
    <div className="handoff-copy"><span className="eyebrow">COMMITS</span><h2>LAICA as a co-author</h2>
      <p>When an agent run through LAICA makes a git commit, LAICA adds itself as a co-author next to the agent's own credit (Claude, Codex, Gemini and so on), so history shows which tools did the work. Repositories keep their own commit hooks.</p></div>
    <Switch label="Add LAICA as a co-author" description="On by default. Turn it off for single projects below." checked={cfg.Enabled} onCheckedChange={v=>void save({Enabled:v})}/>
    <label className="coauthor-email">GitHub email for the trailer<input className="hinput" placeholder="1234567+laica-bot@users.noreply.github.com" value={email} onChange={e=>setEmail(e.target.value)} onBlur={()=>{if(email.trim()!==(cfg.Placeholder?'':cfg.Email))void save({Email:email.trim()});}}/></label>
    <p className="handoff-note">{cfg.Placeholder?'Using a placeholder address, so commits are credited to LAICA by name only. GitHub shows an avatar and links the contributor only when the address belongs to a real account: a dedicated LAICA bot account (use its ID+name@users.noreply.github.com address), a GitHub App (its bot noreply address), or a secondary account you own.':'Commits will carry: '+cfg.Trailer}</p>
    {err&&<p className="handoff-note bad">{err}</p>}
    {cfg.Projects.length>0&&<div className="coauthor-projects"><span className="eyebrow">PER PROJECT</span>{cfg.Projects.map(p=><Switch key={p.Cwd} label={p.Cwd.split(/[\\/]/).filter(Boolean).pop()??p.Cwd} description={p.Cwd} disabled={!cfg.Enabled} checked={p.Enabled} onCheckedChange={v=>void save({Cwd:p.Cwd,ProjectEnabled:v})}/>)}</div>}
  </div>;
}