import {Thumbs,refsOf} from './Images';
import {useCallback,useEffect,useMemo,useRef,useState} from 'react';
import {Button,Glass,IconButton,useToast} from 'open-glass-ui';
import {Bot,Send,Square,Wrench,TerminalSquare,Brain,AlertTriangle,PanelRightOpen,ArrowDown,ArrowRightLeft,Pencil,Paperclip,GitBranch,Info,ShieldQuestion,Check,X,Pause,Play,Download,Clock} from 'lucide-react';
import {useAttachments,useDropZone,AttachChips,DropOverlay,MicButton,CopyBtn,FlagChips,usePasteFiles,composePrompt,splitPrompt,noFlags} from './Composer';
import type {Flags} from './Composer';
import PlusMenu from './PlusMenu';
import ChatPills from './ChatPills';
import type {Model} from './types';
import {Steps,WorkingLine,FileCards,TurnSummary,WorkedLine,groupRows} from './Steps';
import {request,isRemote} from './bridge';
import {pushUndo} from './undo';
import {Markdown} from './markdown';
import PreviewPanel from './PreviewPanel';
import SendButton from './SendButton';
import {LimitBanner} from './UsageUI';
import {TagTeamChip,SwitchRow} from './TagTeam';
import {ProviderChip,providerOfSession} from './provider';
import {useHarness,modeLabel,effortLabel} from './harness-store';
import type {HEvent} from './harness-store';

export default function ChatView({models}:{models:Model[]}){
  const {toast}=useToast();
  const {sessions,active,events,refreshKey,assistants,send,stop,rename,nameOf}=useHarness();
  const [draft,setDraftRaw]=useState(''),[panel,setPanel]=useState(true),[renaming,setRenaming]=useState(false),[queue,setQueue]=useState<string[]>([]);
  const target=useCallback(()=>({Id:active??undefined}),[active]);
  const files=useAttachments(target);const [flags,setFlags]=useState<Flags>(noFlags);usePasteFiles(files.addFiles);
  const drop=useDropZone(f=>{void files.addFiles(f);});
  const endRef=useRef<HTMLDivElement>(null),logRef=useRef<HTMLDivElement>(null),stick=useRef(true),drafts=useRef(new Map<string,string>());
  const [atBottom,setAtBottom]=useState(true);
  // each chat keeps its own unsent draft, so switching away and back never loses what you were typing
  const setDraft=useCallback((v:string|((d:string)=>string))=>{setDraftRaw(cur=>{const nv=typeof v==='function'?v(cur):v;drafts.current.set(active??'',nv);return nv;});},[active]);
  useEffect(()=>{setDraftRaw(drafts.current.get(active??'')??'');},[active]);
  const onLogScroll=()=>{const el=logRef.current;if(!el)return;const near=el.scrollHeight-el.scrollTop-el.clientHeight<180;stick.current=near;setAtBottom(near);};
  const jumpDown=()=>{stick.current=true;setAtBottom(true);endRef.current?.scrollIntoView({block:'end',behavior:'smooth'});};
  const current=sessions.find(s=>s.Id===active);const list=events[active??'']??[];
  useEffect(()=>{if(stick.current)endRef.current?.scrollIntoView({block:'end'});},[list.length]);
  useEffect(()=>{stick.current=true;setAtBottom(true);endRef.current?.scrollIntoView({block:'end'});},[active]);
  useEffect(()=>{setQueue([]);},[active]);
  useEffect(()=>{if(current&&!current.Busy&&queue.length){const [next,...rest]=queue;setQueue(rest);send(current.Id,next).catch(e=>toast({title:'Could not send the queued message',description:(e as Error).message,duration:7000}));}},[current?.Busy,queue]); // eslint-disable-line react-hooks/exhaustive-deps
  const resolved=useMemo(()=>{const m=new Map<string,string>();for(const e of list)if(e.Kind==='approval_result')m.set(e.Detail??'',e.Text);return m;},[list]);
  const rows=useMemo(()=>{const out:HEvent[]=[];for(const e of list){if(e.Kind==='approval_result')continue;const prev=out[out.length-1];if(e.Kind==='assistant'&&e.Detail==='append'&&prev&&prev.Kind==='assistant'&&prev.Detail==='append')out[out.length-1]={...prev,Text:prev.Text+'\n'+e.Text};else out.push(e);}return out;},[list]);
  const items=useMemo(()=>groupRows(rows,current?.Busy??false),[rows,current?.Busy]);
  const openChanges=()=>{setPanel(true);setTimeout(()=>window.dispatchEvent(new Event('laica-open-changes')),60);};
  if(!current)return <div className="empty-state compact"><Bot size={26}/><h2>Pick a conversation</h2><p>Choose one from the sidebar, or start a new chat.</p></div>;
  const assistant=assistants.find(a=>a.Id===current.AssistantId);
  const doSend=async()=>{
    if(!draft.trim()&&!files.items.length&&!flags.plan&&!flags.goal)return;
    const text=composePrompt(draft,flags,files.items);const keepDraft=draft,keepFiles=files.items,keepFlags=flags;setDraft('');files.clear();setFlags(noFlags);
    if(current.Busy){setQueue(q=>[...q,text]);return;}
    try{await send(current.Id,text);}catch(e){setDraft(keepDraft);files.restore(keepFiles);setFlags(keepFlags);toast({title:'Could not send',description:(e as Error).message,duration:6500});}
  };
  const togglePause=()=>request(current.Paused?'harnessResume':'harnessPause',{Id:current.Id}).catch(e=>toast({title:current.Paused?'Could not resume':'Could not pause',description:(e as Error).message,duration:6000}));
  const exportChat=async()=>{try{const text=await request<string>('harnessExport',{Id:current.Id});const name=current.Title.replace(/[^\w .-]+/g,'').trim()||'chat';
    if(isRemote){const a=document.createElement('a');a.href=URL.createObjectURL(new Blob([text],{type:'text/markdown'}));a.download=name+'.md';a.click();URL.revokeObjectURL(a.href);}
    else{const r=await request<{Message:string}|null>('saveText',{Name:name+'.md',Text:text});if(r)toast({title:'Chat saved',description:r.Message});}}catch(e){toast({title:'Could not export',description:(e as Error).message,duration:6000});}};
    const answer=(requestId:string,allow:boolean,always=false)=>request('harnessApprove',{Id:current.Id,RequestId:requestId,Allow:allow,Always:always}).catch(e=>toast({title:'Could not answer',description:(e as Error).message}));
  return <div className={`chat ${panel?'with-panel':''}`}>
    <Glass material="regular" className={`harness-stream ${drop.dragging?'is-dragging':''}`} {...drop.bind}><DropOverlay show={drop.dragging}/>
      <div className="harness-meta">{renaming?<input className="hinput" autoFocus defaultValue={current.Title} onBlur={e=>{setRenaming(false);rename(current.Id,e.target.value);}} onKeyDown={e=>{if(e.key==='Enter')(e.target as HTMLInputElement).blur();if(e.key==='Escape')setRenaming(false);}}/>:<b onDoubleClick={()=>setRenaming(true)}>{current.Title}</b>}
        <ProviderChip p={providerOfSession(current,nameOf)}/><TagTeamChip session={current}/><span className="hsub">{nameOf(current.Harness)} · {modeLabel[current.Mode]??current.Mode}{current.Effort?` · ${effortLabel[current.Effort]??current.Effort} effort`:''}{assistant?` · ${assistant.Name}`:''}</span>
        {current.Isolated&&<span className="pill ok" title={`Isolated worktree on branch ${current.Branch}`}><GitBranch size={11}/> {current.Branch}</span>}
        <span className="hcwd" title={current.Cwd}>{current.Cwd}</span>
        {current.Paused&&<span className="pill warn"><Pause size={11}/> Paused</span>}<IconButton size="small" variant="quiet" aria-label="Rename chat" onClick={()=>setRenaming(true)}><Pencil size={14}/></IconButton><IconButton size="small" variant="quiet" aria-label="Save chat as Markdown" title="Save chat as Markdown" onClick={()=>{void exportChat();}}><Download size={14}/></IconButton>{!panel&&<IconButton size="small" variant="quiet" aria-label="Show workspace panel" onClick={()=>setPanel(true)}><PanelRightOpen size={15}/></IconButton>}</div>
      <div className="harness-log" aria-live="polite" ref={logRef} onScroll={onLogScroll}>{items.map((r,i)=>r.type==='steps'?<Steps key={i} items={r.items} running={r.running}/>:r.type==='files'?<FileCards key={i} files={r.files}/>:r.type==='worked'?<WorkedLine key={i} ms={r.ms}/>:r.type==='summary'?<TurnSummary key={i} files={r.files} add={r.add} del={r.del} onOpen={openChanges}/>:<Row key={i} e={r.e} resolved={resolved} answer={answer}/>)}{queue.map((q,i)=><div key={i} className="hmsg queued"><Clock size={12}/><span>Queued: {splitPrompt(q).body.slice(0,120)}</span><button type="button" aria-label="Remove queued message" onClick={()=>{const item=q;setQueue(cur=>cur.filter((_,k)=>k!==i));pushUndo('Removed a queued message',()=>setQueue(cur=>[...cur,item]));}}><X size={11}/></button></div>)}{current.Busy&&<WorkingLine events={list} paused={current.Paused}/>}<div ref={endRef}/></div>
      {!atBottom&&<button type="button" className="to-bottom" aria-label="Jump to the latest message" onClick={jumpDown}><ArrowDown size={16}/></button>}
      <div className="harness-compose">
        <FlagChips flags={flags} setFlags={setFlags}/><AttachChips items={files.items} busy={files.busy} onRemove={files.remove}/>
        <div className="compose-field"><textarea aria-label="Message" rows={2} value={draft} placeholder={current.Busy?`${nameOf(current.Harness)} is working — type to queue your next message`:`Message ${nameOf(current.Harness)} — Enter to send, Shift+Enter for a new line, drop files to attach`} onChange={e=>setDraft(e.target.value)} onKeyDown={e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();void doSend();}else if(e.key==='ArrowUp'&&!draft&&!files.items.length){const last=[...list].reverse().find(x=>x.Kind==='user');if(last){e.preventDefault();setDraft(splitPrompt(last.Text).body);}}}}/></div>
        <div className="compose-bar"><div className="compose-left"><PlusMenu onPick={()=>{void files.pick();}} onFiles={f=>{void files.addFiles(f);}} flags={flags} setFlags={setFlags} insert={x=>setDraft(d=>d+(d&&!/\s$/.test(d)?' ':'')+x)}/><ChatPills session={current} models={models}/></div><div className="compose-right"><MicButton onText={text=>setDraft(d=>d+(d&&!/\s$/.test(d)?' ':'')+text)}/>{current.Busy&&current.Harness!=='workflow'&&<IconButton size="small" variant="quiet" className={`pause-btn ${current.Paused?'is-paused':''}`} aria-label={current.Paused?'Resume':'Pause'} title={current.Paused?'Resume the work':'Pause the work (nothing is lost)'} onClick={()=>{void togglePause();}}>{current.Paused?<Play size={16} fill="currentColor"/>:<Pause size={16} fill="currentColor"/>}</IconButton>}<SendButton busy={current.Busy&&!draft.trim()&&!files.items.length} disabled={!draft.trim()&&!files.items.length&&!flags.plan&&!flags.goal} label={current.Busy?'Queue message':'Send'} onSend={()=>{void doSend();}} onStop={()=>stop(current.Id)}/></div></div>
      </div>
    </Glass>
    {panel&&<Glass material="regular" className="hpanel"><PreviewPanel sessionId={current.Id} refreshKey={refreshKey} onClose={()=>setPanel(false)}/></Glass>}
  </div>;
}

function Row({e,resolved,answer}:{e:HEvent;resolved:Map<string,string>;answer:(id:string,allow:boolean,always?:boolean)=>void}){
  if(e.Kind==='paused'||e.Kind==='resumed')return <div className="hmsg log">{e.Kind==='paused'?<Pause size={12}/>:<Play size={12}/>} {e.Text}</div>;
    if(e.Kind==='user'){const p=splitPrompt(e.Text);return <div className="hmsg user">{p.tags.length>0&&<div className="msg-tags">{p.tags.map(t=><span key={t} className="ui-badge is-accent">{t}</span>)}</div>}{p.body}{p.files.length>0&&<div className="msg-files">{p.files.map(f=><span key={f.path} className="file-chip" title={f.path}><Paperclip size={11}/>{f.name}</span>)}</div>}<CopyBtn text={p.body}/></div>;}
  if(e.Kind==='assistant')return <div className="hmsg assistant"><Markdown text={e.Text}/><CopyBtn text={e.Text}/></div>;
  if(e.Kind==='image')return <div className="hmsg image-row"><Thumbs images={refsOf(e.SessionId,e.Images)} max={6} big/></div>;
  if(e.Kind==='thinking')return <details className="hmsg thinking"><summary><Brain size={13}/> Thinking</summary>{e.Text}</details>;
  if(e.Kind==='tool')return <details className="hmsg tool"><summary><Wrench size={13}/> {e.Text}</summary><pre>{e.Detail}</pre></details>;
  if(e.Kind==='tool_result')return <details className="hmsg tool"><summary><TerminalSquare size={13}/> {e.Detail?`Output of ${e.Detail.slice(0,80)}`:'Result'}</summary><pre>{e.Text||'(no output)'}</pre></details>;
  if(e.Kind==='notice')return <details className="hmsg notice" open><summary><Info size={13}/> Changes while you were away</summary><pre>{e.Text}</pre></details>;
  if(e.Kind==='approval'){let id='',input='';try{const d=JSON.parse(e.Detail??'{}');id=d.RequestId;input=d.Input;}catch{/* malformed */}const state=resolved.get(id);
    return <div className={`hmsg approval ${state??''}`}><div className="ap-head"><ShieldQuestion size={15}/> <b>Allow {e.Text}?</b></div><pre>{input}</pre>
      {state?<small>{state==='allowed'?'Allowed':'Denied'}</small>:<div className="ap-actions"><Button size="small" variant="primary" leadingIcon={<Check size={13}/>} onClick={()=>answer(id,true)}>Allow</Button><Button size="small" onClick={()=>answer(id,true,true)}>Always allow {e.Text}</Button><Button size="small" variant="quiet" leadingIcon={<X size={13}/>} onClick={()=>answer(id,false)}>Deny</Button></div>}</div>;}
  if(e.Kind==='switch'||e.Kind==='pairwait')return <SwitchRow e={e}/>;
  if(e.Kind==='pairnote')return <div className="hmsg log tt-note"><Info size={12}/> {e.Text}</div>;
  if(e.Kind==='limit')return <LimitBanner sessionId={e.SessionId} vendor={e.Detail??''} text={e.Text}/>;
    if(e.Kind==='handoff')return <div className="hmsg handoff"><ArrowRightLeft size={14}/><span>{e.Text}</span>{e.Detail&&<button type="button" onClick={()=>window.dispatchEvent(new CustomEvent('laica-open',{detail:e.Detail}))}>Open</button>}</div>;
    if(e.Kind==='error')return <div className="hmsg error"><AlertTriangle size={14}/> {e.Text}</div>;
  return <div className="hmsg log">{e.Text}</div>;
}
