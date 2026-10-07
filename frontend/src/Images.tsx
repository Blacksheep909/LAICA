import {useEffect,useState,useCallback} from 'react';
import {createPortal} from 'react-dom';
import {X,ChevronLeft,ChevronRight} from 'lucide-react';
import {request} from './bridge';

/** A picture the agent saw or made (a screenshot, a generated image), stored with the chat by the backend. */
export interface ImgRef { sid:string; file:string }
const cache=new Map<string,Promise<string>>();
export function imageUrl(r:ImgRef):Promise<string>{
  const key=r.sid+'/'+r.file;let p=cache.get(key);
  if(!p){p=request<{Mime:string;Data:string}>('imageGet',{Id:r.sid,File:r.file}).then(v=>`data:${v.Mime};base64,${v.Data}`);p.catch(()=>cache.delete(key));cache.set(key,p);}
  return p;
}
export const refsOf=(sid:string,files?:string[]|null):ImgRef[]=>(files??[]).map(file=>({sid,file}));

function Thumb({r,onOpen,big}:{r:ImgRef;onOpen:()=>void;big?:boolean}){
  const [url,setUrl]=useState(''),[bad,setBad]=useState(false);
  useEffect(()=>{let live=true;imageUrl(r).then(u=>{if(live)setUrl(u);}).catch(()=>{if(live)setBad(true);});return()=>{live=false;};},[r.sid,r.file]);// eslint-disable-line react-hooks/exhaustive-deps
  return <button type="button" className={`img-thumb ${big?'big':''} ${bad?'is-bad':''}`} aria-label="Open picture" onClick={e=>{e.preventDefault();e.stopPropagation();onOpen();}}>{url?<img src={url} alt="" draggable={false}/>:<span className="img-wait"/>}</button>;
}

/** Full-size viewer: arrows or keys move between the pictures, Esc closes. */
function Lightbox({images,start,onClose}:{images:ImgRef[];start:number;onClose:()=>void}){
  const [i,setI]=useState(start),[url,setUrl]=useState('');
  const step=useCallback((d:number)=>setI(v=>(v+d+images.length)%images.length),[images.length]);
  useEffect(()=>{let live=true;setUrl('');imageUrl(images[i]).then(u=>{if(live)setUrl(u);}).catch(()=>{});return()=>{live=false;};},[images,i]);
  useEffect(()=>{const k=(e:KeyboardEvent)=>{if(e.key==='Escape'){e.stopPropagation();onClose();}else if(e.key==='ArrowRight')step(1);else if(e.key==='ArrowLeft')step(-1);};window.addEventListener('keydown',k,true);return()=>window.removeEventListener('keydown',k,true);},[onClose,step]);
  return createPortal(<div className="lightbox" role="dialog" aria-modal="true" aria-label="Picture" onClick={onClose}>
    {url?<img src={url} alt="" onClick={e=>e.stopPropagation()}/>:<span className="img-wait big"/>}
    <button type="button" className="lb-close" aria-label="Close picture" onClick={e=>{e.stopPropagation();onClose();}}><X size={18}/></button>
    {images.length>1&&<><button type="button" className="lb-nav prev" aria-label="Previous picture" onClick={e=>{e.stopPropagation();step(-1);}}><ChevronLeft size={22}/></button><button type="button" className="lb-nav next" aria-label="Next picture" onClick={e=>{e.stopPropagation();step(1);}}><ChevronRight size={22}/></button><span className="lb-count">{i+1} / {images.length}</span></>}
  </div>,document.body);
}

/** A row of small previews (the first few, then "+N") that open the viewer. */
export function Thumbs({images,max=3,big}:{images:ImgRef[];max?:number;big?:boolean}){
  const [open,setOpen]=useState<number|null>(null);
  if(!images.length)return null;
  const shown=images.slice(0,max);
  return <span className="img-row" onClick={e=>e.stopPropagation()}>
    {images.length>max&&<span className="img-more">+{images.length-max}</span>}
    {shown.map((r,i)=><Thumb key={r.file} r={r} big={big} onOpen={()=>setOpen(i)}/>)}
    {open!==null&&<Lightbox images={images} start={open} onClose={()=>setOpen(null)}/>}
  </span>;
}