import {useEffect,useLayoutEffect,useRef,useState} from 'react';
import {createPortal} from 'react-dom';
import {Glass} from 'open-glass-ui';
import {Minus,Square,Copy,X,ArrowLeft,ArrowRight,PanelLeft} from 'lucide-react';
import {request,subscribe} from './bridge';

export type MenuItem={label:string;shortcut?:string;onClick:()=>void;disabled?:boolean}|'separator';
export interface TopMenus { File:MenuItem[]; Edit:MenuItem[]; View:MenuItem[]; Help:MenuItem[] }
interface Props { canBack:boolean; canForward:boolean; onBack:()=>void; onForward:()=>void; sidebarHidden:boolean; onToggleSidebar:()=>void; menus:TopMenus }

const send=(Action:string)=>request<{Maximized:boolean;Fullscreen:boolean}>('windowControl',{Action}).catch(()=>undefined);

function MenuPopover({anchor,items,onClose}:{anchor:HTMLElement;items:MenuItem[];onClose:()=>void}){
  const [pos,setPos]=useState<{left:number;top:number}|null>(null),[active,setActive]=useState(-1),ref=useRef<HTMLDivElement>(null);
  useLayoutEffect(()=>{const r=anchor.getBoundingClientRect();setPos({left:Math.max(6,Math.min(r.left,window.innerWidth-250)),top:r.bottom+4});},[anchor]);
  useEffect(()=>{
    const down=(e:PointerEvent)=>{const t=e.target as Node;if(ref.current?.contains(t)||anchor.contains(t))return;onClose();};
    const key=(e:KeyboardEvent)=>{const real=items.map((it,i)=>[it,i] as const).filter(([it])=>it!=='separator'&&!it.disabled).map(([,i])=>i);
      if(e.key==='Escape'){onClose();}else if(e.key==='ArrowDown'||e.key==='ArrowUp'){e.preventDefault();const at=real.indexOf(active);const n=real.length;setActive(real[(at+(e.key==='ArrowDown'?1:-1)+n)%n]);}
      else if(e.key==='Enter'&&active>=0){const it=items[active];if(it!=='separator'){onClose();it.onClick();}}};
    document.addEventListener('pointerdown',down,true);document.addEventListener('keydown',key);return()=>{document.removeEventListener('pointerdown',down,true);document.removeEventListener('keydown',key);};
  },[anchor,items,active,onClose]);
  if(!pos)return null;
  return createPortal(<Glass material="frosted" className="gs-pop menu-pop" style={{left:pos.left,top:pos.top,minWidth:230}}><div ref={ref} role="menu" className="gs-list">
    {items.map((it,i)=>it==='separator'?<div key={i} className="menu-sep" role="separator"/>:<div key={i} role="menuitem" aria-disabled={it.disabled} className={`gs-item ${i===active?'is-active':''} ${it.disabled?'is-disabled':''}`} onPointerMove={()=>!it.disabled&&setActive(i)} onClick={()=>{if(it.disabled)return;onClose();it.onClick();}}><span className="gs-text"><span className="gs-label">{it.label}</span></span>{it.shortcut&&<span className="menu-key">{it.shortcut}</span>}</div>)}
  </div></Glass>,document.body);
}

/** Codex-style top bar: back / forward, sidebar toggle, File Edit View Help, and the window controls. Empty space drags the window. */
export default function TitleBar({canBack,canForward,onBack,onForward,sidebarHidden,onToggleSidebar,menus}:Props){
  const [maximized,setMaximized]=useState(false),[full,setFull]=useState(false),[open,setOpen]=useState<keyof TopMenus|null>(null),[anchor,setAnchor]=useState<HTMLElement|null>(null);
  useEffect(()=>{
    const off=subscribe(m=>{const w=m as unknown as {Type:string;Maximized?:boolean;Fullscreen?:boolean};if(w.Type==='window'){setMaximized(!!w.Maximized);setFull(!!w.Fullscreen);}});
    const key=(e:KeyboardEvent)=>{if(e.key==='F11'){e.preventDefault();void send('fullscreen');}};
    window.addEventListener('keydown',key);return()=>{off();window.removeEventListener('keydown',key);};
  },[]);
  const toggle=(name:keyof TopMenus,el:HTMLElement)=>{if(open===name){setOpen(null);setAnchor(null);}else{setOpen(name);setAnchor(el);}};
  const hover=(name:keyof TopMenus,el:HTMLElement)=>{if(open&&open!==name){setOpen(name);setAnchor(el);}};
  const names:(keyof TopMenus)[]=['File','Edit','View','Help'];
  return <div className="titlebar" role="presentation">
    <div className="tb-left">
      <button className="tb-icon" aria-label="Back" title="Back (Alt+Left)" disabled={!canBack} onClick={onBack}><ArrowLeft size={16}/></button>
      <button className="tb-icon" aria-label="Forward" title="Forward (Alt+Right)" disabled={!canForward} onClick={onForward}><ArrowRight size={16}/></button>
      <button className={`tb-icon ${sidebarHidden?'':'on'}`} aria-label={sidebarHidden?'Show sidebar':'Hide sidebar'} title="Toggle sidebar (Ctrl+B)" onClick={onToggleSidebar}><PanelLeft size={16}/></button>
      <nav className="tb-menus" aria-label="Application menu">{names.map(n=><button key={n} className={`tb-menu ${open===n?'open':''}`} aria-haspopup="menu" aria-expanded={open===n} onClick={e=>toggle(n,e.currentTarget)} onPointerEnter={e=>hover(n,e.currentTarget)}>{n}</button>)}</nav>
    </div>
    <div className="titlebar-drag" onPointerDown={e=>{if(e.button===0&&e.detail<2)void send('drag');}} onDoubleClick={()=>void send('maximize')}/>
    <div className="titlebar-controls">
      <button aria-label="Minimize" title="Minimize" onClick={()=>void send('minimize')}><Minus size={14}/></button>
      <button aria-label={maximized||full?'Restore':'Maximize'} title={maximized||full?'Restore':'Maximize'} onClick={()=>void send('maximize')}>{maximized||full?<Copy size={12}/>:<Square size={12}/>}</button>
      <button className="close" aria-label="Close" title="Close" onClick={()=>void send('close')}><X size={15}/></button>
    </div>
    {open&&anchor&&<MenuPopover anchor={anchor} items={menus[open]} onClose={()=>{setOpen(null);setAnchor(null);}}/>}
  </div>;
}
export {send as windowControl};
