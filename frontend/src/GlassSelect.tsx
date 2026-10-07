import {useCallback,useEffect,useId,useLayoutEffect,useMemo,useRef,useState} from 'react';
import type {ReactNode} from 'react';
import {createPortal} from 'react-dom';
import {Glass} from 'open-glass-ui';
import {Check,ChevronDown,Search} from 'lucide-react';

export interface GlassOption { value:string; label:string; description?:string; group?:string; icon?:ReactNode; disabled?:boolean }
export interface GlassSelectProps {
  label?:string; 'aria-label'?:string; value:string; options:GlassOption[]; onChange:(e:{target:{value:string}})=>void;
  disabled?:boolean; placeholder?:string; className?:string; variant?:'field'|'pill'; searchable?:boolean; triggerLabel?:string; menuWidth?:number; footer?:ReactNode; emptyText?:string;
}
interface Pos { left:number; width:number; top?:number; bottom?:number; maxH:number }

/** A frosted-glass dropdown that replaces the browser's native list: grouped sections, descriptions, search, keyboard control. */
export function Select({label,'aria-label':aria,value,options,onChange,disabled,placeholder,className,variant='field',searchable,triggerLabel,menuWidth,footer,emptyText}:GlassSelectProps){
  const [open,setOpen]=useState(false),[query,setQuery]=useState(''),[active,setActive]=useState(0),[pos,setPos]=useState<Pos|null>(null);
  const trigger=useRef<HTMLButtonElement>(null),pop=useRef<HTMLDivElement>(null),list=useRef<HTMLDivElement>(null),uid=useId();
  const showSearch=searchable??options.length>9;
  const selected=options.find(o=>o.value===value);
  const filtered=useMemo(()=>{const q=query.trim().toLowerCase();return q?options.filter(o=>(o.label+' '+(o.description??'')+' '+(o.group??'')).toLowerCase().includes(q)):options;},[options,query]);
  const close=useCallback((refocus=true)=>{setOpen(false);setQuery('');if(refocus)trigger.current?.focus();},[]);
  const choose=useCallback((o:GlassOption)=>{if(o.disabled)return;onChange({target:{value:o.value}});close();},[onChange,close]);

  useLayoutEffect(()=>{
    if(!open||!trigger.current){setPos(null);return;}
    const calc=()=>{const r=trigger.current!.getBoundingClientRect(),below=window.innerHeight-r.bottom-14,above=r.top-14,up=below<240&&above>below,maxH=Math.max(180,Math.min(420,up?above:below)),width=Math.max(r.width,menuWidth??0,200),left=Math.min(Math.max(8,r.left),Math.max(8,window.innerWidth-width-8));
      setPos(up?{left,width,bottom:window.innerHeight-r.top+6,maxH}:{left,width,top:r.bottom+6,maxH});};
    calc();window.addEventListener('resize',calc);window.addEventListener('scroll',calc,true);
    return()=>{window.removeEventListener('resize',calc);window.removeEventListener('scroll',calc,true);};
  },[open,menuWidth]);
  useEffect(()=>{
    if(!open)return;const down=(e:PointerEvent)=>{const t=e.target as Node;if(pop.current?.contains(t)||trigger.current?.contains(t))return;close(false);};
    document.addEventListener('pointerdown',down,true);return()=>document.removeEventListener('pointerdown',down,true);
  },[open,close]);
  useEffect(()=>{if(open){const i=Math.max(0,filtered.findIndex(o=>o.value===value));setActive(i);}},[open]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(()=>{if(open)setActive(a=>Math.min(a,Math.max(0,filtered.length-1)));},[filtered.length,open]);
  useEffect(()=>{if(!open)return;list.current?.querySelector<HTMLElement>(`[data-i="${active}"]`)?.scrollIntoView({block:'nearest'});},[active,open]);
  useEffect(()=>{if(open&&showSearch)requestAnimationFrame(()=>pop.current?.querySelector<HTMLInputElement>('input')?.focus());},[open,showSearch]);

  const step=(d:number)=>{if(!filtered.length)return;let i=active;for(let n=0;n<filtered.length;n++){i=(i+d+filtered.length)%filtered.length;if(!filtered[i].disabled)break;}setActive(i);};
  const key=(e:React.KeyboardEvent)=>{
    if(e.key==='ArrowDown'){e.preventDefault();step(1);}else if(e.key==='ArrowUp'){e.preventDefault();step(-1);}
    else if(e.key==='Home'){e.preventDefault();setActive(0);}else if(e.key==='End'){e.preventDefault();setActive(filtered.length-1);}
    else if(e.key==='Enter'){e.preventDefault();const o=filtered[active];if(o)choose(o);}
    else if(e.key==='Escape'){e.preventDefault();e.stopPropagation();close();}
    else if(e.key==='Tab')close(false);
  };
  const onTriggerKey=(e:React.KeyboardEvent)=>{if(!open&&(e.key==='ArrowDown'||e.key==='ArrowUp'||e.key==='Enter'||e.key===' ')){e.preventDefault();setOpen(true);}else if(open)key(e);};

  let lastGroup:string|undefined;
  const menu=open&&pos?createPortal(
    <Glass material="frosted" className="gs-pop" style={{left:pos.left,width:pos.width,top:pos.top,bottom:pos.bottom,maxHeight:pos.maxH}} >
      <div ref={pop} className="gs-inner" onKeyDown={key}>
        {showSearch&&<label className="gs-search"><Search size={14}/><input value={query} placeholder="Search" aria-label="Search options" onChange={e=>setQuery(e.target.value)}/></label>}
        <div ref={list} role="listbox" id={uid} aria-label={label??aria} className="gs-list" style={{maxHeight:pos.maxH-(showSearch?58:20)-(footer?44:0)}}>
          {filtered.length===0&&<div className="gs-empty">{emptyText??'Nothing matches.'}</div>}
          {filtered.map((o,i)=>{const header=o.group&&o.group!==lastGroup?<div className="gs-group" key={'g'+o.group+i}>{o.group}</div>:null;lastGroup=o.group;
            return <div key={o.value+i}>{header}<div role="option" aria-selected={o.value===value} aria-disabled={o.disabled} data-i={i} className={`gs-item ${i===active?'is-active':''} ${o.value===value?'is-selected':''} ${o.disabled?'is-disabled':''}`} onPointerMove={()=>setActive(i)} onClick={()=>choose(o)}>
              {o.icon&&<span className="gs-icon">{o.icon}</span>}<span className="gs-text"><span className="gs-label">{o.label}</span>{o.description&&<span className="gs-desc">{o.description}</span>}</span>{o.value===value&&<Check size={15} className="gs-check"/>}</div></div>;})}
        </div>
        {footer&&<div className="gs-footer">{footer}</div>}
      </div>
    </Glass>,document.body):null;

  const shown=triggerLabel??selected?.label??placeholder??'Choose…';
  return <div className={`gs-field gs-${variant} ${className??''}`}>
    {label&&variant==='field'&&<span className="gs-label-top">{label}</span>}
    <button ref={trigger} type="button" className="gs-trigger" disabled={disabled} aria-haspopup="listbox" aria-expanded={open} aria-controls={open?uid:undefined} aria-label={aria??label} onClick={()=>setOpen(o=>!o)} onKeyDown={onTriggerKey}>
      {selected?.icon&&<span className="gs-icon">{selected.icon}</span>}<span className={`gs-value ${selected||triggerLabel?'':'is-placeholder'}`}>{shown}</span><ChevronDown size={14} className="gs-caret"/>
    </button>{menu}
  </div>;
}
