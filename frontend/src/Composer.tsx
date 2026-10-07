import {useCallback,useEffect,useRef,useState} from 'react';
import type {DragEvent} from 'react';
import {IconButton,useToast} from 'open-glass-ui';
import {Plus,Mic,Square,Paperclip,Image as ImageIcon,Folder,X,Copy,Check,LoaderCircle,Lightbulb,Target} from 'lucide-react';
import {request,subscribe,isDesktop,isRemote,resolveDroppedFile} from './bridge';
import {pushUndo} from './undo';

export interface Attachment { Name:string; Path:string; Size:number; Kind:'image'|'file'|'folder'; Thumb?:string }
export interface Flags { plan:boolean; goal:boolean }
export const noFlags:Flags={plan:false,goal:false};
const PLAN_TEXT='[Plan mode] Do not change any files or run anything that modifies the project. First write a clear step-by-step plan, then stop and wait for my approval.';
const GOAL_TEXT='[Goal] Keep working on this on your own until it is fully achieved. Before you stop, check your work against the goal and tell me what you verified.';
/** The message as the agent receives it: the user's words, plan/goal instructions, then the attached file paths. */
export const composePrompt=(text:string,flags:Flags,items:Attachment[])=>(flags.plan?PLAN_TEXT+'\n\n':'')+(text.trim()||'See the attached files.')+(flags.goal?'\n\n'+GOAL_TEXT:'')+attachmentBlock(items);
/** Strips those instructions again for display, returning small tags instead. */
export function splitPrompt(text:string):{body:string;tags:string[];files:{name:string;path:string}[]}{
  let t=text;const tags:string[]=[];
  if(t.startsWith(PLAN_TEXT)){tags.push('Plan mode');t=t.slice(PLAN_TEXT.length).replace(/^\n+/,'');}
  const a=parseAttachments(t);t=a.body;
  if(t.endsWith(GOAL_TEXT)){tags.push('Goal');t=t.slice(0,-GOAL_TEXT.length).replace(/\n+$/,'');}
  return {body:t,tags,files:a.files};
}

const CHUNK=2*1024*1024;
const sizeLabel=(n:number)=>n>=1048576?(n/1048576).toFixed(1)+' MB':n>=1024?Math.round(n/1024)+' KB':n+' B';
const imageExt=/\.(png|jpe?g|gif|webp|bmp)$/i;

function toBase64(blob:Blob):Promise<string>{
  return new Promise((resolve,reject)=>{const r=new FileReader();r.onload=()=>resolve(String(r.result).split(',')[1]??'');r.onerror=()=>reject(r.error??new Error('Could not read the file.'));r.readAsDataURL(blob);});
}

/** The paths a message carries, appended so any agent can open them. The chat strips this block again when it shows the message. */
export const attachmentBlock=(items:Attachment[])=>items.length?`\n\n[Attached files]\n${items.map(a=>`- ${a.Path}`).join('\n')}`:'';
export function parseAttachments(text:string):{body:string;files:{name:string;path:string}[]}{
  const m=/\n\n\[Attached files\]\n((?:- .*(?:\n|$))+)\s*$/.exec(text);
  if(!m)return {body:text,files:[]};
  const files=m[1].split('\n').filter(l=>l.startsWith('- ')).map(l=>{const path=l.slice(2).trim();return {path,name:path.split(/[\\/]/).filter(Boolean).pop()??path};});
  return {body:text.slice(0,m.index),files};
}

/** Files for the next message: picked with the + button, dropped onto the composer, or pasted. */
export function useAttachments(target:()=>{Id?:string;Cwd?:string}){
  const {toast}=useToast();
  const [items,setItems]=useState<Attachment[]>([]),[busy,setBusy]=useState(0);
  const fail=useCallback((e:unknown)=>toast({title:'Could not attach',description:(e as Error).message,duration:7000}),[toast]);
  const thumbs=useRef(new Map<string,string>());
  const giveThumbs=useCallback(()=>setItems(cur=>cur.map(i=>{if(i.Thumb||i.Kind!=='image')return i;const n=i.Name.toLowerCase();let url=thumbs.current.get(n);if(!url){const stem=n.replace(/ \(\d+\)(\.[^.]*)?$/,'$1');url=thumbs.current.get(stem);}return url?{...i,Thumb:url}:i;})),[]);

  const stage=useCallback(async(paths:string[])=>{
    if(!paths.length)return;
    const t=target();const out=await request<Attachment[]>('attachStage',{Id:t.Id??'',Cwd:t.Cwd??'',Paths:paths});
    setItems(cur=>{const seen=new Set(cur.map(c=>c.Path.toLowerCase()));return [...cur,...out.filter(o=>!seen.has(o.Path.toLowerCase()))];});giveThumbs();
  },[target,giveThumbs]);

  const upload=useCallback(async(file:File)=>{
    const t=target();let tgt='';let info:Attachment|null=null;
    for(let at=0;at<Math.max(file.size,1);at+=CHUNK){
      const data=await toBase64(file.slice(at,at+CHUNK));
      info=await request<Attachment>('attachSave',{Id:t.Id??'',Cwd:t.Cwd??'',Name:file.name||'pasted-file',Data:data,Target:tgt,Append:at>0});tgt=info.Path;
    }
    if(info){setItems(cur=>[...cur,info!]);giveThumbs();}
  },[target,giveThumbs]);

  const addFiles=useCallback(async(files:File[])=>{
    if(!files.length)return;setBusy(b=>b+1);
    for(const f of files)if(/^image\//.test(f.type)||imageExt.test(f.name))thumbs.current.set((f.name||'image.png').toLowerCase(),URL.createObjectURL(f));
    try{
      const loose:File[]=[];const paths:string[]=[];
      for(const f of files){
        if(isDesktop&&!isRemote){const got=await resolveDroppedFile(f);if(got.length){paths.push(...got);continue;}}
        loose.push(f);
      }
      await stage(paths);
      for(const f of loose){if(f.size>200*1024*1024)throw new Error(`${f.name} is larger than 200 MB.`);await upload(f);}
    }catch(e){fail(e);}finally{setBusy(b=>b-1);}
  },[stage,upload,fail]);

  const pick=useCallback(async()=>{
    try{
      if(isDesktop&&!isRemote){setBusy(b=>b+1);try{const paths=await request<string[]|null>('pickFiles');if(paths?.length)await stage(paths);}finally{setBusy(b=>b-1);}return;}
      const input=document.createElement('input');input.type='file';input.multiple=true;input.onchange=()=>{void addFiles(Array.from(input.files??[]));};input.click();
    }catch(e){fail(e);}
  },[stage,addFiles,fail]);

  const itemsRef=useRef<Attachment[]>([]);itemsRef.current=items;
  const remove=useCallback((path:string)=>{const it=itemsRef.current.find(c=>c.Path===path);if(it)pushUndo('Removed '+it.Name,()=>setItems(cur=>cur.some(c=>c.Path===it.Path)?cur:[...cur,it]));setItems(cur=>cur.filter(c=>c.Path!==path));},[]);
  const clear=useCallback(()=>setItems([]),[]);
  const restore=useCallback((back:Attachment[])=>setItems(back),[]);
  return {items,busy:busy>0,addFiles,pick,remove,clear,restore};
}

/** Drag-and-drop for any element: spread `bind` on it and show a hint while `dragging`. */
export function useDropZone(onFiles:(files:File[])=>void){
  const [dragging,setDragging]=useState(false);const depth=useRef(0);
  const has=(e:DragEvent)=>Array.from(e.dataTransfer?.types??[]).includes('Files');
  const bind={
    onDragEnter:(e:DragEvent)=>{if(!has(e))return;e.preventDefault();depth.current++;setDragging(true);},
    onDragOver:(e:DragEvent)=>{if(!has(e))return;e.preventDefault();e.dataTransfer.dropEffect='copy';},
    onDragLeave:(e:DragEvent)=>{if(!has(e))return;depth.current=Math.max(0,depth.current-1);if(!depth.current)setDragging(false);},
    onDrop:(e:DragEvent)=>{if(!has(e))return;e.preventDefault();depth.current=0;setDragging(false);onFiles(Array.from(e.dataTransfer.files));}
  };
  return {dragging,bind};
}
export const DropOverlay=({show,label='Drop files to attach'}:{show:boolean;label?:string})=>show?<div className="drop-overlay" aria-hidden="true"><Paperclip size={22}/><b>{label}</b><span>They are added to your next message</span></div>:null;

export function AttachChips({items,busy,onRemove}:{items:Attachment[];busy:boolean;onRemove:(path:string)=>void}){
  if(!items.length&&!busy)return null;
  return <div className="attach-row">{items.map(a=><span key={a.Path} className={`file-chip is-${a.Kind} ${a.Thumb?'has-thumb':''}`} title={a.Path}>{a.Thumb?<img className="file-thumb" src={a.Thumb} alt=""/>:a.Kind==='image'?<ImageIcon size={12}/>:a.Kind==='folder'?<Folder size={12}/>:<Paperclip size={12}/>}<span className="file-name">{a.Name}</span>{a.Size>0&&<small>{sizeLabel(a.Size)}</small>}<button type="button" aria-label={`Remove ${a.Name}`} onClick={()=>onRemove(a.Path)}><X size={11}/></button></span>)}
    {busy&&<span className="file-chip is-busy"><LoaderCircle size={12} className="spin"/>Adding…</span>}</div>;
}

export const PlusButton=({onClick,disabled}:{onClick:()=>void;disabled?:boolean})=><IconButton size="small" variant="quiet" aria-label="Add files" title="Add files or photos (or drop them here)" disabled={disabled} onClick={onClick}><Plus size={17}/></IconButton>;

/** Small copy button shown on hover over a message. */
export function CopyBtn({text}:{text:string}){
  const [done,setDone]=useState(false);
  return <button type="button" className="copy-btn" aria-label="Copy message" title="Copy" onClick={()=>{void navigator.clipboard?.writeText(text).then(()=>{setDone(true);setTimeout(()=>setDone(false),1400);}).catch(()=>{});}}>{done?<Check size={13}/>:<Copy size={13}/>}</button>;
}

export type DictationMode='auto'|'windows'|'cloud';
export const getDictationMode=():DictationMode=>{try{const v=localStorage.getItem('laica-dictation');return v==='windows'||v==='cloud'?v:'auto';}catch{return 'auto';}};
export const setDictationMode=(m:DictationMode)=>{try{localStorage.setItem('laica-dictation',m);}catch{/* storage unavailable */}};

/**
 * Dictation, like the mic in ChatGPT: tap to talk, tap again to finish. With an OpenAI or Groq service it records and transcribes in the
 * cloud (most accurate); otherwise it listens through Windows' own offline speech recognition. Words are added to the message as you pause.
 */
export function MicButton({onText,disabled}:{onText:(text:string)=>void;disabled?:boolean}){
  const {toast}=useToast();
  const [state,setState]=useState<'idle'|'listening'|'transcribing'>('idle'),[interim,setInterim]=useState(''),[cloud,setCloud]=useState(false);
  const how=useRef<'windows'|'cloud'>('windows');const recorder=useRef<MediaRecorder|null>(null);const off=useRef<(()=>void)|null>(null);const timer=useRef<ReturnType<typeof setTimeout>|null>(null);
  useEffect(()=>{if(isDesktop)request<{Cloud:boolean}>('speechStatus').then(s=>setCloud(!!s.Cloud)).catch(()=>{});},[]);
  useEffect(()=>()=>{off.current?.();if(timer.current)clearTimeout(timer.current);try{recorder.current?.stop();}catch{/* already stopped */}if(isDesktop&&!isRemote)void request('dictateStop').catch(()=>{});},[]);

  const finish=useCallback(async()=>{
    if(timer.current){clearTimeout(timer.current);timer.current=null;}
    if(how.current==='cloud'){try{recorder.current?.stop();}catch{setState('idle');}return;}
    off.current?.();off.current=null;setInterim('');setState('idle');try{await request('dictateStop');}catch{/* host already stopped */}
  },[]);

  const startWindows=async()=>{
    how.current='windows';
    const r=await request<{Ok:boolean;Error?:string}>('dictateStart');
    if(!r.Ok){toast({title:'Dictation unavailable',description:r.Error,duration:8000});return;}
    off.current=subscribe(m=>{if(m.Type!=='dictation')return;if(m.Final){setInterim('');if(m.Text)onText(m.Text);}else setInterim(m.Text??'');});
    setState('listening');timer.current=setTimeout(()=>{void finish();},10*60*1000);
  };
  const startCloud=async()=>{
    how.current='cloud';
    const stream=await navigator.mediaDevices.getUserMedia({audio:true});
    const mime=typeof MediaRecorder!=='undefined'&&MediaRecorder.isTypeSupported('audio/webm;codecs=opus')?'audio/webm;codecs=opus':'';
    const rec=new MediaRecorder(stream,mime?{mimeType:mime}:undefined);const chunks:Blob[]=[];
    rec.ondataavailable=e=>{if(e.data.size)chunks.push(e.data);};
    rec.onstop=async()=>{
      stream.getTracks().forEach(t=>t.stop());setState('transcribing');
      try{const blob=new Blob(chunks,{type:rec.mimeType||'audio/webm'});const text=await request<string>('transcribe',{Data:await toBase64(blob),Mime:blob.type});if(text)onText(text);else toast({title:'Nothing heard',description:'Try again a little closer to the microphone.'});}
      catch(e){toast({title:'Could not transcribe',description:(e as Error).message,duration:8000});}
      finally{setState('idle');}
    };
    recorder.current=rec;rec.start();setState('listening');timer.current=setTimeout(()=>{void finish();},5*60*1000);
  };
  const toggle=async()=>{
    if(state==='transcribing')return;
    if(state==='listening'){await finish();return;}
    try{
      const pref=getDictationMode();const useCloud=pref==='cloud'||(pref==='auto'&&cloud);
      if(useCloud&&!cloud)throw new Error('Add an OpenAI or Groq service in Settings to dictate with the cloud.');
      if(useCloud)await startCloud();else if(isDesktop&&!isRemote)await startWindows();else throw new Error('Dictation needs the LAICA desktop app, or an OpenAI or Groq service for browser use.');
    }catch(e){setState('idle');toast({title:'Dictation unavailable',description:(e as Error).message,duration:8000});}
  };
  const remoteWithoutCloud=isRemote&&!cloud;
  if(!isDesktop||remoteWithoutCloud)return null;
  return <>
    {state==='listening'&&<div className="mic-caption" role="status"><span className="mic-bars" aria-hidden="true"><i/><i/><i/><i/><i/></span>{interim||'Listening…'}</div>}
    <button type="button" className={`mic-btn is-${state}`} disabled={disabled||state==='transcribing'} aria-label={state==='listening'?'Stop dictation':'Dictate'} aria-pressed={state==='listening'} title={state==='listening'?'Tap to finish':'Dictate (speak your message)'} onClick={()=>{void toggle();}}>
      {state==='listening'?<Square size={12} fill="currentColor" strokeWidth={0}/>:state==='transcribing'?<LoaderCircle size={16} className="spin"/>:<Mic size={17}/>}
    </button>
  </>;
}

/** Ctrl+V anywhere in the chat: an image (or file) on the clipboard becomes an attachment. */
export function usePasteFiles(addFiles:(files:File[])=>Promise<void>|void,enabled=true){
  useEffect(()=>{
    if(!enabled)return;
    const on=(e:ClipboardEvent)=>{
      const f=Array.from(e.clipboardData?.files??[]);if(!f.length)return;
      const t=e.target as HTMLElement|null;if(t&&(t.isContentEditable||(['INPUT','TEXTAREA'].includes(t.tagName)&&!t.closest('.composer,.harness-compose'))))return;
      e.preventDefault();void addFiles(f);
    };
    document.addEventListener('paste',on);return()=>document.removeEventListener('paste',on);
  },[addFiles,enabled]);
}
export function FlagChips({flags,setFlags}:{flags:Flags;setFlags:(f:Flags)=>void}){
  if(!flags.plan&&!flags.goal)return null;
  return <div className="attach-row">{flags.plan&&<span className="file-chip is-flag"><Lightbulb size={12}/><span className="file-name">Plan mode: plan first, change nothing</span><button type="button" aria-label="Turn off plan mode" onClick={()=>setFlags({...flags,plan:false})}><X size={11}/></button></span>}
    {flags.goal&&<span className="file-chip is-flag"><Target size={12}/><span className="file-name">Goal: keep going until it is done</span><button type="button" aria-label="Turn off goal" onClick={()=>setFlags({...flags,goal:false})}><X size={11}/></button></span>}</div>;
}