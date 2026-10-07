import {useCallback,useEffect,useMemo,useState} from 'react';
import {Button,IconButton,useToast} from 'open-glass-ui';
import {Folder,File as FileIcon,ChevronRight,ChevronDown,RefreshCw,Save,X,Pencil,Eye} from 'lucide-react';
import {request} from './bridge';
import {Markdown} from './markdown';
import ChangesPanel from './ChangesPanel';

interface Entry { Name:string; Path:string; Dir:boolean; Size:number }
interface Sheet { Name:string; Rows:string[][]; Truncated:boolean }
interface Doc { Kind:'text'|'image'|'binary'|'toolarge'|'markdown'|'sheet'|'pdf'; Text?:string; DataUri?:string; Ext?:string; Size?:number; Sheets?:Sheet[] }
interface Tab { path:string; doc:Doc|null; draft:string|null }

function Tree({id,path,depth,open,toggle,pick,active,version,children}:{id:string;path:string;depth:number;open:Set<string>;toggle:(p:string)=>void;pick:(p:string)=>void;active:string|null;version:number;children?:never}){
  const [items,setItems]=useState<Entry[]|null>(null);
  useEffect(()=>{let live=true;request<Entry[]>('harnessFiles',{Id:id,Path:path}).then(r=>live&&setItems(r)).catch(()=>live&&setItems([]));return()=>{live=false;};},[id,path,version]);
  if(!items)return <div className="tree-note" style={{paddingLeft:10+depth*14}}>Loading…</div>;
  if(!items.length&&depth===0)return <div className="tree-note">This folder is empty.</div>;
  return <>{items.map(e=><div key={e.Path}>
    <button className={`tree-row ${active===e.Path?'on':''}`} style={{paddingLeft:8+depth*14}} onClick={()=>e.Dir?toggle(e.Path):pick(e.Path)}>
      {e.Dir?(open.has(e.Path)?<ChevronDown size={13}/>:<ChevronRight size={13}/>):<span style={{width:13}}/>}
      {e.Dir?<Folder size={14}/>:<FileIcon size={14}/>}<span>{e.Name}</span></button>
    {e.Dir&&open.has(e.Path)&&<Tree id={id} path={e.Path} depth={depth+1} open={open} toggle={toggle} pick={pick} active={active} version={version}/>}
  </div>)}</>;
}

function SheetView({sheets}:{sheets:Sheet[]}){
  const [i,setI]=useState(0);const sheet=sheets[Math.min(i,sheets.length-1)];
  if(!sheet)return <div className="tree-note">This workbook is empty.</div>;
  const width=Math.max(1,...sheet.Rows.map(r=>r.length));
  return <div className="sheet">{sheets.length>1&&<div className="sheet-tabs">{sheets.map((s,k)=><button key={k} className={k===i?'on':''} onClick={()=>setI(k)}>{s.Name}</button>)}</div>}
    <div className="sheet-scroll"><table><tbody>{sheet.Rows.map((r,y)=><tr key={y}><th>{y+1}</th>{Array.from({length:width},(_,x)=><td key={x}>{r[x]??''}</td>)}</tr>)}</tbody></table>{sheet.Truncated&&<div className="tree-note">Showing the first {sheet.Rows.length} rows.</div>}</div></div>;
}
function PdfView({dataUri,title}:{dataUri:string;title:string}){
  const url=useMemo(()=>{try{const bin=atob(dataUri.slice(dataUri.indexOf(',')+1));const bytes=new Uint8Array(bin.length);for(let k=0;k<bin.length;k++)bytes[k]=bin.charCodeAt(k);return URL.createObjectURL(new Blob([bytes],{type:'application/pdf'}));}catch{return '';}},[dataUri]);
  useEffect(()=>()=>{if(url)URL.revokeObjectURL(url);},[url]);
  return url?<iframe className="pv-html" title={title} src={url}/>:<div className="tree-note">This PDF couldn't be opened.</div>;
}
function Viewer({doc,path,draft,setDraft,editing}:{doc:Doc;path:string;draft:string|null;setDraft:(v:string)=>void;editing:boolean}){
  const ext=(path.match(/\.[^.\\/]+$/)?.[0]??'').toLowerCase();
  if(doc.Kind==='markdown')return <div className="pv-md"><Markdown text={doc.Text??''}/></div>;
  if(doc.Kind==='sheet')return <SheetView sheets={doc.Sheets??[]}/>;
  if(doc.Kind==='pdf')return <PdfView dataUri={doc.DataUri??''} title={path}/>;
  if(doc.Kind==='image')return <div className="pv-image"><img src={doc.DataUri} alt={path}/></div>;
  if(doc.Kind==='binary')return <div className="tree-note">This file isn't text, so it can't be previewed here.</div>;
  if(doc.Kind==='toolarge')return <div className="tree-note">This file is too large to preview ({Math.round((doc.Size??0)/1024)} KB).</div>;
  const text=(draft??doc.Text??'').replace(/^\uFEFF/,'');
  if(editing)return <textarea className="pv-edit" value={text} spellCheck={false} onChange={e=>setDraft(e.target.value)}/>;
  if(ext==='.md'||ext==='.markdown')return <div className="pv-md"><Markdown text={text}/></div>;
  if(ext==='.html'||ext==='.htm')return <iframe className="pv-html" title={path} sandbox="" srcDoc={text}/>;
  if(ext==='.diff'||ext==='.patch')return <pre className="pv-code">{text.split('\n').map((l,i)=><div key={i} className={l.startsWith('+')&&!l.startsWith('+++')?'add':l.startsWith('-')&&!l.startsWith('---')?'del':l.startsWith('@@')?'hunk':''}>{l||' '}</div>)}</pre>;
  const lines=text.split('\n');
  return <pre className="pv-code">{lines.map((l,i)=><div key={i}><span className="ln">{i+1}</span>{l||' '}</div>)}</pre>;
}

export default function PreviewPanel({sessionId,refreshKey,onClose}:{sessionId:string;refreshKey:number;onClose:()=>void}){
  const {toast}=useToast();
  const [open,setOpen]=useState<Set<string>>(new Set()),[tabs,setTabs]=useState<Tab[]>([]),[current,setCurrent]=useState<string|null>(null),[version,setVersion]=useState(0),[editing,setEditing]=useState(false),[view,setView]=useState<'files'|'changes'>('files');
  useEffect(()=>{const on=()=>setView('changes');window.addEventListener('laica-open-changes',on);return()=>window.removeEventListener('laica-open-changes',on);},[]);
  useEffect(()=>{setTabs([]);setCurrent(null);setOpen(new Set());setEditing(false);},[sessionId]);
  useEffect(()=>{setVersion(v=>v+1);},[refreshKey]);
  const load=useCallback((path:string)=>request<Doc>('harnessReadFile',{Id:sessionId,Path:path}).then(doc=>setTabs(t=>t.map(x=>x.path===path&&x.draft===null?{...x,doc}:x))).catch(e=>toast({title:'Could not open file',description:e.message})),[sessionId,toast]);
  useEffect(()=>{if(current&&!editing)load(current);},[refreshKey]);// eslint-disable-line react-hooks/exhaustive-deps
  const pick=(path:string)=>{setTabs(t=>t.some(x=>x.path===path)?t:[...t,{path,doc:null,draft:null}]);setCurrent(path);setEditing(false);load(path);};
  const toggle=(p:string)=>setOpen(o=>{const n=new Set(o);n.has(p)?n.delete(p):n.add(p);return n;});
  const tab=tabs.find(t=>t.path===current)??null;
  const closeTab=(path:string)=>{setTabs(t=>t.filter(x=>x.path!==path));if(current===path){const rest=tabs.filter(x=>x.path!==path);setCurrent(rest.length?rest[rest.length-1].path:null);setEditing(false);}};
  const save=async()=>{if(!tab||tab.draft===null)return;try{await request('harnessWriteFile',{Id:sessionId,Path:tab.path,Text:tab.draft});setTabs(t=>t.map(x=>x.path===tab.path?{...x,doc:{...x.doc!,Text:tab.draft!},draft:null}:x));toast({title:'Saved'});}catch(e){toast({title:'Could not save',description:(e as Error).message,duration:6000});}};
  const canEdit=tab?.doc?.Kind==='text';
  return <aside className="pv">
    <div className="pv-head"><div className="pv-switch" role="tablist"><button role="tab" aria-selected={view==='files'} className={view==='files'?'on':''} onClick={()=>setView('files')}>Files</button><button role="tab" aria-selected={view==='changes'} className={view==='changes'?'on':''} onClick={()=>setView('changes')}>Changes</button></div><div><IconButton size="small" variant="quiet" aria-label="Refresh files" onClick={()=>setVersion(v=>v+1)}><RefreshCw size={14}/></IconButton><IconButton size="small" variant="quiet" aria-label="Hide panel" onClick={onClose}><X size={14}/></IconButton></div></div>
    {view==='changes'?<ChangesPanel sessionId={sessionId} refreshKey={refreshKey}/>:<>
    <div className="pv-tree"><Tree id={sessionId} path="" depth={0} open={open} toggle={toggle} pick={pick} active={current} version={version}/></div>
    {tabs.length>0&&<div className="pv-tabs">{tabs.map(t=><button key={t.path} className={`pv-tab ${t.path===current?'on':''}`} onClick={()=>{setCurrent(t.path);setEditing(false);}}><span>{t.path.split('\\').pop()}{t.draft!==null?' •':''}</span><i role="button" aria-label="Close tab" onClick={e=>{e.stopPropagation();closeTab(t.path);}}><X size={11}/></i></button>)}</div>}
    {tab&&<div className="pv-body">
      <div className="pv-bar"><span title={tab.path}>{tab.path}</span><div>{canEdit&&<Button size="small" variant="quiet" leadingIcon={editing?<Eye size={13}/>:<Pencil size={13}/>} onClick={()=>{setEditing(!editing);if(!editing&&tab.draft===null)setTabs(ts=>ts.map(x=>x.path===tab.path?{...x,draft:x.doc?.Text??''}:x));}}>{editing?'Preview':'Edit'}</Button>}{tab.draft!==null&&<Button size="small" variant="primary" leadingIcon={<Save size={13}/>} onClick={save}>Save</Button>}</div></div>
      <div className="pv-view">{tab.doc?<Viewer doc={tab.doc} path={tab.path} draft={tab.draft} editing={editing} setDraft={v=>setTabs(ts=>ts.map(x=>x.path===tab.path?{...x,draft:v}:x))}/>:<div className="tree-note">Loading…</div>}</div>
    </div>}
    </>}
  </aside>;
}
