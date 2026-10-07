import {useEffect} from 'react';
import {useToast} from 'open-glass-ui';
import {request} from './bridge';
import {useHarness} from './harness-store';
import {runUndo,undoDepth} from './undo';

/** App-wide keyboard shortcuts. Typing fields keep their own undo; everywhere else Ctrl+Z takes back the last destructive action. */
export default function Shortcuts({go,openChat}:{go:(page:string)=>void;openChat:(id:string)=>void}){
  const {toast}=useToast();
  const {sessions,active}=useHarness();
  useEffect(()=>{
    const typing=(t:EventTarget|null)=>{const e=t as HTMLElement|null;return !!e&&(e.isContentEditable||e.tagName==='INPUT'||e.tagName==='TEXTAREA'||e.tagName==='SELECT');};
    const overlayOpen=()=>!!document.querySelector('[role=dialog],[role=menu],[data-radix-popper-content-wrapper]');
    const key=async(e:KeyboardEvent)=>{
      const mod=e.ctrlKey||e.metaKey,k=e.key.toLowerCase();
      if(mod&&!e.shiftKey&&!e.altKey&&k==='z'){
        const t=e.target as HTMLInputElement|HTMLTextAreaElement|null;
        if(typing(e.target)&&t&&'value' in t&&(t.value??'')!=='')return;            // a field with text: let the browser undo typing
        if(!undoDepth())return;e.preventDefault();
        try{const label=await runUndo();if(label)toast({title:'Undone',description:label,duration:4000});}catch(err){toast({title:'Could not undo',description:(err as Error).message,duration:6000});}
        return;
      }
      if(mod&&k===','){e.preventDefault();go('settings');return;}
      if(mod&&k==='l'){e.preventDefault();(document.querySelector('textarea[aria-label=Message]') as HTMLElement|null)?.focus();return;}
      if(mod&&k==='f'&&!e.shiftKey){const s=document.querySelector('.side-search input') as HTMLElement|null;if(s){e.preventDefault();s.focus();}return;}
      if(mod&&!e.shiftKey&&!e.altKey&&/^[1-9]$/.test(e.key)){
        const recent=[...sessions].sort((a,b)=>Number(b.Busy)-Number(a.Busy));const s=recent[Number(e.key)-1];
        if(s){e.preventDefault();openChat(s.Id);}return;
      }
      if(e.key==='Escape'&&!overlayOpen()){
        const cur=sessions.find(s=>s.Id===active);
        if(cur?.Busy&&(!typing(e.target)||(e.target as HTMLElement).closest('.harness-compose'))){e.preventDefault();void request('harnessStop',{Id:cur.Id}).catch(()=>undefined);toast({title:'Stopped',description:cur.Title,duration:2500});}
      }
    };
    document.addEventListener('keydown',key);return()=>document.removeEventListener('keydown',key);
  },[sessions,active,go,openChat,toast]);
  return null;
}
