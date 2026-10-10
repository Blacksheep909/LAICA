import {useEffect,useMemo,useRef,useState} from 'react';
import {ArrowLeft,ArrowRight,RotateCw,ExternalLink,Globe} from 'lucide-react';
import {IconButton} from 'open-glass-ui';
import {request,isRemote} from './bridge';
import {useHarness} from './harness-store';

/** Addresses of sites running on this computer: the "Local: http://localhost:5173/" a dev server prints, or a page the agent opened. */
const LOCAL=/https?:\/\/(?:localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\])(?::\d{2,5})?(?:\/[^\s'"`)<>\]]*)?/gi;
const clean=(u:string)=>u.replace(/[.,;:!?]+$/,'').replace('0.0.0.0','localhost').replace(/\/$/,'');
/** The local addresses seen in a chat, newest last. Works for every agent, because it reads only what they printed or said. */
export function useLocalUrls(sessionId:string):string[]{
  const {events}=useHarness();const list=events[sessionId]??[];
  return useMemo(()=>{
    const seen:string[]=[];
    for(const e of list){
      if(e.Kind!=='tool_result'&&e.Kind!=='assistant'&&e.Kind!=='tool')continue;
      const m=((e.Text||'')+' '+(e.Kind==='tool'?e.Detail||'':'')).match(LOCAL);
      if(m)for(const raw of m){const u=clean(raw);const i=seen.indexOf(u);if(i>=0)seen.splice(i,1);seen.push(u);}
    }
    return seen.slice(-6);
  },[list]);
}

const normalise=(v:string)=>{const t=v.trim();if(!t)return '';if(/^[a-z][a-z0-9+.-]*:\/\//i.test(t))return t;if(/^(localhost|127\.0\.0\.1|\[::1\]|\d{1,3}(\.\d{1,3}){3})(:\d+)?(\/|$)/i.test(t))return 'http://'+t;if(/^\d{2,5}$/.test(t))return 'http://localhost:'+t;return 'https://'+t;};
const show=(u:string)=>u.replace(/^https?:\/\//,'').replace(/\/$/,'');

/** A real browser view inside the chat: type an address or pick a dev server the agent started, and use the page here. */
export default function BrowserPanel({sessionId}:{sessionId:string}){
  const urls=useLocalUrls(sessionId);
  const [stack,setStack]=useState<string[]>([]),[at,setAt]=useState(-1),[field,setField]=useState(''),[nonce,setNonce]=useState(0),[loading,setLoading]=useState(false);
  const current=at>=0?stack[at]:'';const typed=useRef(false);
  const go=(raw:string)=>{const u=normalise(raw);if(!u)return;typed.current=true;setStack(s=>[...s.slice(0,at+1),u]);setAt(a=>a+1);setField(show(u));setLoading(true);};
  useEffect(()=>{setStack([]);setAt(-1);setField('');typed.current=false;},[sessionId]);
  // the first dev server an agent starts opens by itself; after that the address is only suggested
  useEffect(()=>{if(!typed.current&&at<0&&urls.length)go(urls[urls.length-1]);},[urls.length]);// eslint-disable-line react-hooks/exhaustive-deps
  const back=()=>{if(at>0){setAt(at-1);setField(show(stack[at-1]));setLoading(true);}},fwd=()=>{if(at<stack.length-1){setAt(at+1);setField(show(stack[at+1]));setLoading(true);}};
  const external=()=>{if(current)void request('openLink',{Url:current}).catch(()=>window.open(current,'_blank'));};
  const remoteLocal=isRemote&&/^https?:\/\/(localhost|127\.|\[::1\])/i.test(current);
  return <div className="bp">
    <form className="bp-bar" onSubmit={e=>{e.preventDefault();go(field);}}>
      <IconButton size="small" variant="quiet" aria-label="Back" disabled={at<1} onClick={back}><ArrowLeft size={14}/></IconButton>
      <IconButton size="small" variant="quiet" aria-label="Forward" disabled={at>=stack.length-1} onClick={fwd}><ArrowRight size={14}/></IconButton>
      <IconButton size="small" variant="quiet" aria-label="Reload" disabled={!current} onClick={()=>{setLoading(true);setNonce(n=>n+1);}}><RotateCw size={14} className={loading?'ui-spin':''}/></IconButton>
      <input className="bp-url" value={field} onChange={e=>setField(e.target.value)} onFocus={e=>e.target.select()} placeholder="Type an address, e.g. localhost:3000" aria-label="Address" spellCheck={false} autoCapitalize="off" autoCorrect="off"/>
      <IconButton size="small" variant="quiet" aria-label="Open in your browser" disabled={!current} onClick={external}><ExternalLink size={14}/></IconButton>
    </form>
    {urls.length>0&&<div className="bp-chips">{urls.slice().reverse().map(u=><button key={u} type="button" className={`bp-chip ${u===current?'on':''}`} onClick={()=>go(u)} title={u}><Globe size={11}/>{show(u)}</button>)}</div>}
    <div className={`bp-view ${current&&!remoteLocal?'has-page':''}`}>
      {remoteLocal?<div className="screen-empty"><Globe size={26}/><h3>This page runs on the computer with LAICA</h3><p>An address like {show(current)} points at the device you are using now, not at the PC running LAICA. Open LAICA on that PC to see it here.</p></div>
      :current?<iframe key={current+nonce} className="bp-frame" src={current} title="Page" onLoad={()=>setLoading(false)} allow="clipboard-read; clipboard-write; fullscreen" sandbox="allow-scripts allow-same-origin allow-forms allow-popups allow-modals allow-downloads allow-pointer-lock"/>
      :<div className="screen-empty"><Globe size={26}/><h3>Open a page</h3><p>Type an address above. When an agent starts a dev server, such as npm run dev, its address shows up here and opens by itself.</p></div>}
    </div>
  </div>;
}
