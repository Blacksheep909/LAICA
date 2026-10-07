import {useCallback,useEffect,useState} from 'react';
import {Button,useToast} from 'open-glass-ui';
import {GitBranch,GitMerge,Trash2,GitCommitHorizontal} from 'lucide-react';
import {request} from './bridge';

interface Change { Status:string; Untracked:boolean; Path:string }
interface Info { IsRepo:boolean; Isolated?:boolean; Branch?:string; BaseBranch?:string; Files:Change[]; Ahead?:number; BaseMoved?:number }
const label:Record<string,string>={M:'Modified',A:'Added',D:'Deleted',R:'Renamed',C:'Copied',U:'Conflict'};

export default function ChangesPanel({sessionId,refreshKey}:{sessionId:string;refreshKey:number}){
  const {toast}=useToast();
  const [info,setInfo]=useState<Info|null>(null),[diff,setDiff]=useState<{path:string;text:string}|null>(null),[message,setMessage]=useState(''),[busy,setBusy]=useState(false);
  const fail=useCallback((title:string)=>(e:Error)=>toast({title,description:e.message,duration:8000}),[toast]);
  const load=useCallback(()=>request<Info>('harnessChanges',{Id:sessionId}).then(setInfo).catch(fail('Could not read changes')),[sessionId,fail]);
  useEffect(()=>{setDiff(null);load();},[load,refreshKey]);
  const show=(path:string)=>request<string>('harnessDiff',{Id:sessionId,Path:path}).then(text=>setDiff({path,text})).catch(fail('Could not read diff'));
  const act=async(fn:()=>Promise<unknown>,done:string)=>{setBusy(true);try{await fn();toast({title:done});setDiff(null);await load();}catch(e){fail('Could not finish')(e as Error);}finally{setBusy(false);}};
  if(!info)return <div className="tree-note">Loading…</div>;
  if(!info.IsRepo)return <div className="tree-note">This folder isn't a git repository, so there are no tracked changes. LAICA still tells the agent what changed here while you were away.</div>;
  return <div className="chg">
    <div className="chg-head"><GitBranch size={14}/><b>{info.Branch}</b>{info.Isolated&&<span className="pill ok">worktree</span>}{!!info.Ahead&&<span className="pill">{info.Ahead} commit{info.Ahead>1?'s':''} ahead</span>}{!!info.BaseMoved&&<span className="pill no" title={`${info.BaseBranch} has moved on since this chat started`}>{info.BaseMoved} new on {info.BaseBranch}</span>}</div>
    <div className="chg-list">{info.Files.length===0?<div className="tree-note">No uncommitted changes.</div>:info.Files.map(f=><button key={f.Path} className={`tree-row ${diff?.path===f.Path?'on':''}`} onClick={()=>show(f.Path)}><span className={`chg-s s${f.Status}`} title={label[f.Status]??f.Status}>{f.Status}</span><span>{f.Path}</span></button>)}</div>
    {diff&&<pre className="pv-code chg-diff">{diff.text.split('\n').map((l,i)=><div key={i} className={l.startsWith('+')&&!l.startsWith('+++')?'add':l.startsWith('-')&&!l.startsWith('---')?'del':l.startsWith('@@')?'hunk':''}>{l||' '}</div>)}</pre>}
    <div className="chg-actions">
      <div className="chg-commit"><input className="hinput" placeholder="Commit message" value={message} onChange={e=>setMessage(e.target.value)}/><Button size="small" leadingIcon={<GitCommitHorizontal size={14}/>} disabled={busy||!message.trim()||!info.Files.length} onClick={()=>act(async()=>{await request('harnessCommit',{Id:sessionId,Message:message});setMessage('');},'Committed')}>Commit</Button></div>
      {info.Isolated&&<div className="chg-row"><Button size="small" variant="primary" leadingIcon={<GitMerge size={14}/>} disabled={busy||!info.Ahead} onClick={()=>act(()=>request('harnessMerge',{Id:sessionId}),`Applied to ${info.BaseBranch}`)}>Apply to {info.BaseBranch}</Button>
        <Button size="small" variant="quiet" leadingIcon={<Trash2 size={14}/>} disabled={busy} onClick={()=>{if(window.confirm(info.Files.length||info.Ahead?'This worktree has changes that are not applied to the main checkout. Remove it and its branch anyway? Unapplied work will be lost.':'Remove this worktree and its branch?'))act(()=>request('harnessRemoveWorktree',{Id:sessionId,DeleteBranch:!info.Ahead&&!info.Files.length}),'Worktree removed');}}>Remove worktree</Button></div>}
    </div>
  </div>;
}
