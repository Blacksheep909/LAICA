import React,{useEffect,useMemo,useState} from 'react';
import {createRoot} from 'react-dom/client';
import {GlassSystemProvider} from 'open-glass-ui';
import 'open-glass-ui/styles.css';
import './workspace.css';
import './Shell.css';
import './GlassSelect.css';
import './Polish.css';
import './Liquid.css';
import './ui/ui.css';
import {installLens} from './lens';
import {TooltipProvider} from './ui/kit';
import {HarnessProvider} from './harness-store';
import RemoteGate from './RemoteGate';
import App from './App';
import {loadLook,LOOK_EVENT,type Look} from './look';

function Root(){
  const [look,setLook]=useState<Look>(loadLook);
  useEffect(()=>{const on=()=>setLook(loadLook());window.addEventListener(LOOK_EVENT,on);return()=>window.removeEventListener(LOOK_EVENT,on);},[]);
  useEffect(()=>{const r=document.documentElement;r.style.setProperty('--laica-panel',String(look.panel));r.style.setProperty('--laica-blur',look.blur+'px');r.style.setProperty('--laica-accent',look.accent);r.dataset.backdrop=look.backdrop;},[look]);
  // a stable theme object, and no key: changing the look must never remount the app (that sent people back to the new-chat page)
  const theme=useMemo(()=>({appearance:'dark' as const,theme:{accent:look.accent,contrast:look.contrast,glass:look.glass}}),[look.accent,look.contrast,look.glass]);
  return <GlassSystemProvider renderer="auto" motion="system" theme={theme} toasts={{maxVisible:2,defaultDuration:4500}}><TooltipProvider><RemoteGate><HarnessProvider><App/></HarnessProvider></RemoteGate></TooltipProvider></GlassSystemProvider>;
}
createRoot(document.getElementById('root')!).render(<React.StrictMode><Root/></React.StrictMode>);
requestAnimationFrame(()=>installLens());
