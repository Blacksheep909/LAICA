import React,{useEffect,useState} from 'react';
import {createRoot} from 'react-dom/client';
import './styles.css';
import {installLens} from './lens';
import {TooltipProvider} from './ui/kit';
import {ToastProvider} from './ui/controls';
import {HarnessProvider} from './harness-store';
import RemoteGate from './RemoteGate';
import App from './App';
import {loadLook,LOOK_EVENT,type Look} from './look';

function Root(){
  const [look,setLook]=useState<Look>(loadLook);
  useEffect(()=>{const on=()=>setLook(loadLook());window.addEventListener(LOOK_EVENT,on);return()=>window.removeEventListener(LOOK_EVENT,on);},[]);
  useEffect(()=>{const r=document.documentElement;r.style.setProperty('--laica-panel',String(look.panel));r.style.setProperty('--laica-blur',look.blur+'px');r.style.setProperty('--laica-accent',look.accent);r.style.setProperty('--accent',look.accent);r.dataset.contrast=look.contrast;r.dataset.backdrop=look.backdrop;},[look]);
  return <ToastProvider maxVisible={2} defaultDuration={4500}><TooltipProvider><RemoteGate><HarnessProvider><App/></HarnessProvider></RemoteGate></TooltipProvider></ToastProvider>;
}
createRoot(document.getElementById('root')!).render(<React.StrictMode><Root/></React.StrictMode>);
requestAnimationFrame(()=>installLens());
