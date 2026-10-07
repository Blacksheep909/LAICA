import {useEffect,useState} from 'react';
import {ChevronRight,SquareTerminal,FileText,Pencil,Search,Globe,Wrench,LoaderCircle,AlertTriangle,FileCode2} from 'lucide-react';
import type {HEvent} from './harness-store';
import {WorkingGlyph} from './ui/kit';

/* The activity view: tool calls turned into plain sentences, grouped the way a person would describe them ("Ran 2 commands, edited a file"),
   with the exact command, edit or output one click away. Works for Claude Code, Codex, the built-in agent and imported history. */
type Kind='cmd'|'read'|'edit'|'search'|'web'|'tool';
export interface FileEdit { file:string; add:number; del:number; text:string; note?:string }
export interface Step { edits?:FileEdit[]; tool:string; kind:Kind; title:string; body:string; result?:string; failed?:boolean; running?:boolean; time?:string }
export type RowItem={type:'event';e:HEvent}|{type:'steps';items:Step[];running:boolean}|{type:'files';files:FileEdit[]}|{type:'summary';files:number;add:number;del:number}|{type:'worked';ms:number};

/** Lines added and removed by one edit: the common start and end are trimmed, the rest is what changed. */
function lineDiff(oldS:string,newS:string):{add:number;del:number;text:string}{
  const a=oldS?oldS.split('\n'):[],b=newS?newS.split('\n'):[];let p=0;while(p<a.length&&p<b.length&&a[p]===b[p])p++;
  let q=0;while(q<a.length-p&&q<b.length-p&&a[a.length-1-q]===b[b.length-1-q])q++;
  const dels=a.slice(p,a.length-q),adds=b.slice(p,b.length-q);
  return {add:adds.length,del:dels.length,text:[...dels.map(l=>'- '+l),...adds.map(l=>'+ '+l)].join('\n')};
}
const cap=(t:string)=>t.length>8000?t.slice(0,8000)+'\n...':t;
function editsOf(tool:string,detail:string|null|undefined,j:Record<string,unknown>|null):FileEdit[]{
  const out:FileEdit[]=[];
  const one=(file:string,o:string,n:string)=>{const d=lineDiff(o,n);out.push({file,add:d.add,del:d.del,text:cap(d.text)});};
  if(j){
    const file=s(j.file_path)||s(j.path)||s(j.notebook_path);
    if(Array.isArray(j.edits))for(const x of j.edits as Record<string,unknown>[])one(file,s(x.old_string),s(x.new_string));
    else if(s(j.old_string)||s(j.new_string))one(file,s(j.old_string),s(j.new_string));
    else if(s(j.content))one(file,'',s(j.content));
    else if(s(j.new_source))one(file,'',s(j.new_source));
    else if(file&&/(edit|write|patch)/i.test(tool))out.push({file,add:0,del:0,text:'',note:'Changed'});
  }else if(detail&&detail[0]==='['){
    try{for(const c of JSON.parse(detail) as Record<string,unknown>[]){const diff=s(c.diff)||s(c.unified_diff);const lines=diff.split('\n');const add=lines.filter(l=>l.startsWith('+')&&!l.startsWith('+++')).length,del=lines.filter(l=>l.startsWith('-')&&!l.startsWith('---')).length;out.push({file:s(c.path),add,del,text:cap(diff),note:diff?undefined:(typeof c.kind==='string'?c.kind:s((c.kind as Record<string,unknown>|undefined)?.type))||'Changed'});}}catch{/* not a change list */}
  }
  return out;
}

const base=(p:string)=>(p||'').split(/[\\/]/).filter(Boolean).pop()??p;
const first=(s:string,n=90)=>{const t=(s||'').trim().split('\n')[0];return t.length>n?t.slice(0,n-1)+'…':t;};
const parse=(d:string|null|undefined):Record<string,unknown>|null=>{if(!d||d[0]!=='{')return null;try{return JSON.parse(d) as Record<string,unknown>;}catch{return null;}};
const s=(v:unknown)=>typeof v==='string'?v:'';

function kindOf(tool:string):Kind{
  const t=tool.toLowerCase();
  if(/^(bash|powershell|shell|command|run)/.test(t)||t==='shell')return 'cmd';
  if(/^(read|ls|view|notebookread)/.test(t))return 'read';
  if(/(edit|write|patch|file change|multiedit|notebookedit)/.test(t))return 'edit';
  if(/^(grep|glob|search|find|toolsearch)/.test(t))return 'search';
  if(/(web|fetch|browser|navigate)/.test(t))return 'web';
  return 'tool';
}

export function stepOf(e:HEvent):Step{
  const tool=e.Text||'tool';const kind=kindOf(tool);const j=parse(e.Detail);const raw=j?'':(e.Detail??'');
  const desc=s(j?.description),cmd=s(j?.command)||(kind==='cmd'?raw:''),file=s(j?.file_path)||s(j?.path)||s(j?.notebook_path)||(kind==='read'||kind==='edit'?raw:'');
  let title=desc;
  if(!title){
    if(kind==='cmd')title='Ran '+first(cmd||tool,80);
    else if(kind==='read')title='Read '+(base(file)||'a file');
    else if(kind==='edit')title=(/write/i.test(tool)?'Wrote ':'Edited ')+(base(file)||'files');
    else if(kind==='search')title='Searched '+(s(j?.pattern)?`for “${first(s(j?.pattern),50)}”`:first(raw,50)||'the project');
    else if(kind==='web')title=s(j?.query)?`Searched the web for “${first(s(j?.query),50)}”`:'Fetched '+(()=>{try{return new URL(s(j?.url)||raw).hostname;}catch{return first(raw,40)||'a page';}})();
    else if(/^skill$/i.test(tool))title='Used the '+(s(j?.skill)||'a')+' skill';
    else if(/^(task|agent)$/i.test(tool))title='Delegated: '+first(s(j?.description)||s(j?.prompt),70);
    else if(tool.includes('.'))title='Used '+tool.split('.').slice(1).join('.')+' ('+tool.split('.')[0]+')';
    else title='Used '+tool;
  }
  let body='';
  if(kind==='cmd')body=cmd||raw;
  else if(kind==='edit'&&j&&(s(j.old_string)||s(j.new_string)))body=(s(j.old_string)?s(j.old_string).split('\n').map(l=>'- '+l).join('\n')+'\n':'')+s(j.new_string).split('\n').map(l=>'+ '+l).join('\n');
  else if(kind==='edit'&&j&&s(j.content))body=s(j.content);
  else if(j)body=JSON.stringify(j,null,2);else body=raw;
  const edits=kind==='edit'?editsOf(tool,e.Detail,j):undefined;
  if(kind==='edit'&&(!desc)&&edits&&edits.length===1&&edits[0].file)title=(/write/i.test(tool)?'Wrote ':'Edited ')+base(edits[0].file);
  return {tool,kind,title,body:body.length>6000?body.slice(0,6000)+'\n...':body,time:e.TimeUtc,edits};
}

const failedText=(t:string)=>/^(error|fatal|exception|command failed)\b/i.test(t.trim())||/\bexit code:?\s*[1-9]\d*\b/i.test(t)||/\bexited with code [1-9]/i.test(t);

/** Folds consecutive tool calls and their outputs into groups, leaving every other event as it is. */
export function groupRows(events:HEvent[],busy:boolean):RowItem[]{
  const out:RowItem[]=[];let cur:Step[]|null=null;const turn=new Map<string,{add:number;del:number}>();
  const flush=()=>{
    if(cur&&cur.length){
      out.push({type:'steps',items:cur,running:false});
      const merged=new Map<string,FileEdit>();
      for(const st of cur)for(const ed of st.edits??[]){const key=ed.file||'(file)';const m=merged.get(key);if(m){m.add+=ed.add;m.del+=ed.del;m.text=(m.text?m.text+'\n':'')+ed.text;}else merged.set(key,{...ed});const t=turn.get(key)??{add:0,del:0};t.add+=ed.add;t.del+=ed.del;turn.set(key,t);}
      if(merged.size)out.push({type:'files',files:[...merged.values()]});
    }
    cur=null;
  };
  const summarize=()=>{if(!turn.size)return;let add=0,del=0;turn.forEach(v=>{add+=v.add;del+=v.del;});out.push({type:'summary',files:turn.size,add,del});turn.clear();};
  for(const e of events){
    if(e.Kind==='tool'){(cur??=[]).push(stepOf(e));}
    else if(e.Kind==='tool_result'&&cur){const open=cur.find(x=>x.result===undefined);if(open){open.result=e.Text||'';open.failed=e.Detail==='error'||failedText(e.Text||'');}else{(cur as Step[]).push({tool:'Result',kind:'tool',title:'Output',body:'',result:e.Text||'',failed:e.Detail==='error'});}}
    else if(e.Kind==='done'){
      flush();
      let started=0;for(let i=events.indexOf(e)-1;i>=0;i--){if(events[i].Kind==='user'){started=Date.parse(events[i].TimeUtc)||0;break;}}
      const ms=started?Date.parse(e.TimeUtc)-started:0;if(ms>=8000)out.push({type:'worked',ms});
      summarize();
    }
    else{flush();if(e.Kind==='user')summarize();out.push({type:'event',e});}
  }
  flush();summarize();
  if(busy&&out.length){const last=out[out.length-1];if(last.type==='steps'){last.running=true;for(const x of last.items)if(x.result===undefined)x.running=true;}}
  return out;
}

const icon=(k:Kind,size=14)=>k==='cmd'?<SquareTerminal size={size}/>:k==='read'?<FileText size={size}/>:k==='edit'?<Pencil size={size}/>:k==='search'?<Search size={size}/>:k==='web'?<Globe size={size}/>:<Wrench size={size}/>;
const plural=(n:number,one:string,many:string)=>n===1?`1 ${one}`:`${n} ${many}`;

function summary(items:Step[]):string{
  const c={cmd:0,read:0,edit:0,search:0,web:0,tool:0};items.forEach(i=>{c[i.kind]++;});
  const parts:string[]=[];
  if(c.cmd)parts.push('ran '+plural(c.cmd,'command','commands'));
  if(c.read)parts.push('read '+plural(c.read,'file','files'));
  if(c.edit)parts.push('edited '+plural(c.edit,'file','files'));
  if(c.search)parts.push('searched '+plural(c.search,'time','times'));
  if(c.web)parts.push('browsed '+plural(c.web,'page','pages'));
  if(c.tool)parts.push('used '+plural(c.tool,'tool','tools'));
  const text=parts.join(', ');const failed=items.filter(i=>i.failed).length;
  return text.charAt(0).toUpperCase()+text.slice(1)+(failed?` (${failed} failed)`:'');
}

function StepRow({step}:{step:Step}){
  const has=!!step.body||!!step.result;
  return <details className={`step ${step.failed?'is-failed':''} ${step.running?'is-running':''}`}>
    <summary><span className="step-ico">{step.running?<LoaderCircle size={14} className="ui-spin"/>:step.failed?<AlertTriangle size={14}/>:icon(step.kind)}</span><span className="step-title">{step.title}</span>{step.failed&&<span className="step-bad">failed</span>}{has&&<ChevronRight size={14} className="step-chev"/>}</summary>
    {step.body&&<pre className="step-code">{step.body}</pre>}
    {step.result!==undefined&&step.result!==''&&<pre className={`step-out ${step.failed?'is-bad':''}`}>{step.result.length>6000?step.result.slice(0,6000)+'\n…':step.result}</pre>}
    {step.result==='' &&<p className="step-empty">No output.</p>}
  </details>;
}

export function Steps({items,running}:{items:Step[];running:boolean}){
  if(items.length===1)return <div className="steps single"><StepRow step={items[0]}/></div>;
  const failed=items.some(i=>i.failed);
  const live=items.find(i=>i.running);
  return <details className={`steps ${failed?'has-failed':''}`} open={running&&!!live}>
    <summary><span className="step-ico">{live?<LoaderCircle size={14} className="ui-spin"/>:icon(items[0].kind)}</span><span className="step-title">{live?live.title:summary(items)}</span><ChevronRight size={14} className="step-chev"/></summary>
    <div className="steps-list">{items.map((it,i)=><StepRow key={i} step={it}/>)}</div>
  </details>;
}

export const clock=(ms:number)=>{const t=Math.max(0,Math.round(ms/1000));const h=Math.floor(t/3600),m=Math.floor(t%3600/60),sec=t%60;return h?`${h}h ${m}m`:m?`${m}m ${sec}s`:`${sec}s`;};
/** "Working  27m 55s · Look for rate-limit data…": what the agent is doing right now and for how long. */
export function WorkingLine({events,paused}:{events:HEvent[];paused?:boolean}){
  const [now,setNow]=useState(Date.now());
  useEffect(()=>{if(paused)return;const t=setInterval(()=>setNow(Date.now()),1000);return()=>clearInterval(t);},[paused]);
  let start=0,label='';
  for(let i=events.length-1;i>=0;i--){const e=events[i];if(!label&&e.Kind==='tool'){label=stepOf(e).title;}if(e.Kind==='user'){start=Date.parse(e.TimeUtc)||0;break;}}
  return <div className="hmsg working"><WorkingGlyph size={14} paused={paused}/><span>{paused?'Paused':'Working'}</span>{start>0&&<span className="working-time">{clock(now-start)}</span>}{label&&<span className="working-what">{label}</span>}</div>;
}

const DiffText=({text}:{text:string})=><pre className="step-code diff">{text.split('\n').map((l,i)=><span key={i} className={l.startsWith('+')?'dl-add':l.startsWith('-')?'dl-del':'dl-ctx'}>{l+'\n'}</span>)}</pre>;
const dirOf=(p:string)=>{const parts=p.split(/[\\/]/).filter(Boolean);return parts.length>1?parts.slice(-3,-1).join('/'):'';};

/** One card per edited file, with how many lines were added and removed; open it to read the change. */
export function FileCards({files}:{files:FileEdit[]}){
  return <div className="file-cards">{files.map(f=><details key={f.file} className="file-card">
    <summary><FileCode2 size={15} className="fc-ico"/><span className="fc-name">{base(f.file)||'file'}</span>{dirOf(f.file)&&<span className="fc-dir">{dirOf(f.file)}</span>}
      <span className="fc-stats">{f.add>0&&<b className="fc-add">+{f.add}</b>}{f.del>0&&<b className="fc-del">−{f.del}</b>}{f.add===0&&f.del===0&&<em>{f.note??'Changed'}</em>}</span><ChevronRight size={14} className="step-chev"/></summary>
    {f.text?<DiffText text={f.text}/>:<p className="step-empty">The change itself was not recorded.</p>}
  </details>)}</div>;
}

/** "10 files changed +221 -23" at the end of a turn; click to open the Changes panel. */
export function TurnSummary({files,add,del,onOpen}:{files:number;add:number;del:number;onOpen:()=>void}){
  return <button type="button" className="turn-summary" onClick={onOpen} title="Review everything that changed"><span>{files} file{files===1?'':'s'} changed</span>{add>0&&<b className="fc-add">+{add}</b>}{del>0&&<b className="fc-del">−{del}</b>}<ChevronRight size={13}/></button>;
}
/** A ticking "4m 12s" for something that started at `since` (an ISO time). */
export function Elapsed({since,className}:{since?:string;className?:string}){
  const [now,setNow]=useState(Date.now());
  useEffect(()=>{const t=setInterval(()=>setNow(Date.now()),1000);return()=>clearInterval(t);},[]);
  const start=since?Date.parse(since):0;if(!start)return null;
  return <span className={className??'elapsed'}>{clock(now-start)}</span>;
}
/** "Worked for 40m 48s" once a turn is finished. */
export const WorkedLine=({ms}:{ms:number})=><div className="worked-line"><span>Worked for {clock(ms)}</span></div>;