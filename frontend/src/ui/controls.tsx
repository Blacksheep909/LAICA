import * as React from 'react';
import {createPortal} from 'react-dom';
import {X} from 'lucide-react';
import {cx,Dialog,DialogContent,DialogDescription,DialogTitle} from './kit';

/* The app's own controls. One look, written once in controls.css: flat translucent fills and a crisp hairline, no blur on small things
   (blur belongs to big surfaces), a clear focus ring, and a press that dips. */

/* ---------- Button ---------- */
export type ButtonVariant='primary'|'secondary'|'quiet'|'danger'|'pill';
export interface ButtonProps extends React.ButtonHTMLAttributes<HTMLButtonElement>{variant?:ButtonVariant;size?:'small'|'medium';leadingIcon?:React.ReactNode;trailingIcon?:React.ReactNode}
export const Button=React.forwardRef<HTMLButtonElement,ButtonProps>(function Button({variant='secondary',size='medium',leadingIcon,trailingIcon,className,children,type='button',...p},ref){
  return <button ref={ref} type={type} className={cx('btn',`btn-${variant==='pill'?'secondary':variant}`,size==='small'&&'btn-sm',className)} {...p}>
    {leadingIcon&&<span className="btn-ico">{leadingIcon}</span>}{children!=null&&children!==false&&<span className="btn-label">{children}</span>}{trailingIcon&&<span className="btn-ico">{trailingIcon}</span>}
  </button>;
});
export const IconButton=React.forwardRef<HTMLButtonElement,Omit<ButtonProps,'leadingIcon'|'trailingIcon'>>(function IconButton({variant='quiet',size='medium',className,children,type='button',...p},ref){
  return <button ref={ref} type={type} className={cx('btn',`btn-${variant==='pill'?'secondary':variant}`,'btn-icon',size==='small'&&'btn-sm',className)} {...p}>{children}</button>;
});

/* ---------- Segmented control ---------- */
export interface SegmentItem{value:string;label:React.ReactNode;disabled?:boolean}
export function SegmentedControl({items,value,onValueChange,className,'aria-label':label}:{items:SegmentItem[];value:string;onValueChange:(v:string)=>void;className?:string;'aria-label':string}){
  return <div role="radiogroup" aria-label={label} className={cx('segments',className)}>
    {items.map(i=><button key={i.value} type="button" role="radio" aria-checked={i.value===value} disabled={i.disabled} className="segment" onClick={()=>onValueChange(i.value)}>{i.label}</button>)}
  </div>;
}

/* ---------- Switch: a labelled on/off row ---------- */
export interface SwitchProps extends Omit<React.InputHTMLAttributes<HTMLInputElement>,'onChange'|'type'|'role'>{label:React.ReactNode;description?:React.ReactNode;onCheckedChange?:(checked:boolean)=>void}
export function Switch({label,description,className,onCheckedChange,id,...p}:SwitchProps){
  const auto=React.useId(),key=id??auto;
  return <label className={cx('switch',p.disabled&&'is-disabled',className)} htmlFor={key}>
    <span className="switch-copy"><span className="switch-label">{label}</span>{description&&<span className="switch-desc">{description}</span>}</span>
    <input {...p} id={key} type="checkbox" role="switch" onChange={e=>onCheckedChange?.(e.currentTarget.checked)}/>
    <span className="switch-track" aria-hidden="true"><span className="switch-thumb"/></span>
  </label>;
}

/* ---------- Labelled text area ---------- */
export function Textarea({label,className,id,...p}:React.TextareaHTMLAttributes<HTMLTextAreaElement>&{label:React.ReactNode}){
  const auto=React.useId(),key=id??auto;
  return <label className={cx('field',className)} htmlFor={key}><span>{label}</span><textarea {...p} id={key}/></label>;
}

/* ---------- Modal: a titled dialog ---------- */
export function Modal({title,description,open,onOpenChange,children,className}:{title:React.ReactNode;description?:React.ReactNode;open:boolean;onOpenChange:(open:boolean)=>void;children:React.ReactNode;className?:string}){
  return <Dialog open={open} onOpenChange={onOpenChange}>
    <DialogContent className={cx('modal',className)} aria-describedby={description?undefined:undefined}>
      <header className="modal-head"><DialogTitle>{title}</DialogTitle>{description&&<DialogDescription>{description}</DialogDescription>}</header>
      <div className="modal-body">{children}</div>
    </DialogContent>
  </Dialog>;
}

/* ---------- Toasts ---------- */
export interface ToastOptions{title:React.ReactNode;description?:React.ReactNode;duration?:number;actionLabel?:string;onAction?:()=>void}
interface Notice extends ToastOptions{id:number}
interface ToastApi{toast:(o:ToastOptions)=>number;dismiss:(id:number)=>void}
const ToastContext=React.createContext<ToastApi|null>(null);
export function useToast():ToastApi{const c=React.useContext(ToastContext);if(!c)throw new Error('useToast needs a ToastProvider');return c;}

function ToastCard({n,onClose,fallback}:{n:Notice;onClose:()=>void;fallback:number}){
  const [paused,setPaused]=React.useState(false);const left=React.useRef(n.duration??fallback);
  React.useEffect(()=>{if(paused||left.current<=0)return;const start=Date.now();const t=window.setTimeout(onClose,left.current);return()=>{window.clearTimeout(t);left.current-=Date.now()-start;};},[paused,onClose]);
  return <div className="toast" role="status" onMouseEnter={()=>setPaused(true)} onMouseLeave={()=>setPaused(false)} onFocus={()=>setPaused(true)} onBlur={()=>setPaused(false)} onKeyDown={e=>{if(e.key==='Escape')onClose();}}>
    <div className="toast-text"><strong>{n.title}</strong>{n.description&&<p>{n.description}</p>}</div>
    {n.actionLabel&&<button type="button" className="toast-action" onClick={()=>{n.onAction?.();onClose();}}>{n.actionLabel}</button>}
    <button type="button" className="toast-x" aria-label="Dismiss" onClick={onClose}><X size={13}/></button>
  </div>;
}
export function ToastProvider({children,maxVisible=3,defaultDuration=5000}:{children:React.ReactNode;maxVisible?:number;defaultDuration?:number}){
  const [notices,setNotices]=React.useState<Notice[]>([]);const next=React.useRef(0);
  const dismiss=React.useCallback((id:number)=>setNotices(c=>c.filter(n=>n.id!==id)),[]);
  const toast=React.useCallback((o:ToastOptions)=>{const id=++next.current;setNotices(c=>[...c,{...o,id}].slice(-maxVisible));return id;},[maxVisible]);
  const api=React.useMemo(()=>({toast,dismiss}),[toast,dismiss]);
  return <ToastContext.Provider value={api}>{children}{createPortal(<section className="toasts" aria-label="Notifications">{notices.map(n=><ToastCard key={n.id} n={n} fallback={defaultDuration} onClose={()=>dismiss(n.id)}/>)}</section>,document.body)}</ToastContext.Provider>;
}
