import {useEffect,useMemo,useState} from 'react';
import {Globe,MousePointerClick,Keyboard,Camera,Monitor,Square,Move,ArrowDownUp,Hourglass} from 'lucide-react';
import {Button} from 'open-glass-ui';
import {request} from './bridge';
import {useHarness} from './harness-store';
import type {HEvent} from './harness-store';
import {imageUrl,refsOf,Thumbs} from './Images';
import type {ImgRef} from './Images';

/** Tools that drive a browser or the computer: Playwright, Chrome DevTools, other browser MCP servers and LAICA computer use. */
export const SCREEN_TOOL=/(playwright|browser|chrome|puppeteer|laica-computer|computer[_.-]use|computer__|screenshot|navigate)/i;
const parse=(d?:string|null):Record<string,unknown>=>{if(!d||d[0]!=='{')return {};try{return JSON.parse(d) as Record<string,unknown>;}catch{return {};}};
const bare=(t:string)=>t.replace(/^mcp__[^_]*(?:_[^_]+)*?__/,'').replace(/^[^.]*\./,'');

export interface ScreenAction { time:string; kind:'shot'|'click'|'type'|'key'|'go'|'scroll'|'move'|'wait'|'other'; text:string; computer:boolean }
function describe(e:HEvent):ScreenAction{
  const name=bare(e.Text||'tool').toLowerCase(),j=parse(e.Detail),computer=/laica-computer/i.test(e.Text||'');
  const at=typeof j.x==='number'&&typeof j.y==='number'?` at ${Math.round(j.x as number)}, ${Math.round(j.y as number)}`:'';
  const url=typeof j.url==='string'?j.url:'';
  if(/screenshot|snapshot|screen_info|capture/.test(name))return {time:e.TimeUtc,kind:'shot',text:computer?'Looked at the screen':'Took a screenshot',computer};
  if(/navigate|goto|open_page|new_page|go_to/.test(name))return {time:e.TimeUtc,kind:'go',text:'Opened '+(url||'a page'),computer};
  if(/double_click/.test(name))return {time:e.TimeUtc,kind:'click',text:'Double-clicked'+at,computer};
  if(/right_click/.test(name))return {time:e.TimeUtc,kind:'click',text:'Right-clicked'+at,computer};
  if(/click|press_button|tap/.test(name))return {time:e.TimeUtc,kind:'click',text:'Clicked'+(typeof j.element==='string'?' '+j.element:at),computer};
  if(/type|fill|input/.test(name))return {time:e.TimeUtc,kind:'type',text:'Typed'+(typeof j.text==='string'&&(j.text as string).length<60?' “'+j.text+'”':''),computer};
  if(/key|press/.test(name))return {time:e.TimeUtc,kind:'key',text:'Pressed '+(typeof j.keys==='string'?j.keys:typeof j.key==='string'?j.key:'a key'),computer};
  if(/scroll/.test(name))return {time:e.TimeUtc,kind:'scroll',text:'Scrolled'+(typeof j.direction==='string'?' '+j.direction:''),computer};
  if(/move|drag|hover/.test(name))return {time:e.TimeUtc,kind:'move',text:(/drag/.test(name)?'Dragged':'Moved the pointer'),computer};
  if(/wait/.test(name))return {time:e.TimeUtc,kind:'wait',text:'Waited',computer};
  return {time:e.TimeUtc,kind:'other',text:bare(e.Text||'tool'),computer};
}
const icon=(k:ScreenAction['kind'])=>k==='shot'?<Camera size={13}/>:k==='go'?<Globe size={13}/>:k==='click'?<MousePointerClick size={13}/>:k==='type'||k==='key'?<Keyboard size={13}/>:k==='scroll'?<ArrowDownUp size={13}/>:k==='wait'?<Hourglass size={13}/>:<Move size={13}/>;

/** What the agent is doing in a browser or on the computer: the latest picture it saw, in a slim lavender frame while it is live, and the steps it took. */
export function useScreenActivity(sessionId:string){
  const {events,sessions}=useHarness();const list=events[sessionId]??[];const busy=!!sessions.find(s=>s.Id===sessionId)?.Busy;
  return useMemo(()=>{
    const acts:ScreenAction[]=[];let last:ImgRef[]=[];let any=false;
    for(const e of list){
      if(e.Kind==='tool'&&SCREEN_TOOL.test(e.Text||'')){any=true;acts.push(describe(e));}
      if((e.Kind==='tool_result'||e.Kind==='tool'||e.Kind==='image')&&e.Images?.length&&(any||SCREEN_TOOL.test(e.Text||''))){last=refsOf(e.SessionId,e.Images);any=true;}
    }
    const lastTime=acts.length?Date.parse(acts[acts.length-1].time):0;
    return {any,acts,image:last[last.length-1]??null,images:last,live:busy&&lastTime>0&&Date.now()-lastTime<120000,lastTime};
  },[list,busy]);
}

export default function ScreenPanel({sessionId}:{sessionId:string}){
  const a=useScreenActivity(sessionId);const [url,setUrl]=useState(''),[,tick]=useState(0);
  useEffect(()=>{let live=true;if(a.image)imageUrl(a.image).then(u=>{if(live)setUrl(u);}).catch(()=>undefined);else setUrl('');return()=>{live=false;};},[a.image?.file,a.image?.sid]);// eslint-disable-line react-hooks/exhaustive-deps
  useEffect(()=>{const t=setInterval(()=>tick(n=>n+1),5000);return()=>clearInterval(t);},[]);
  if(!a.any)return <div className="screen-empty"><Monitor size={26}/><h3>Nothing here yet</h3><p>When an agent uses a browser or your computer, you will see what it sees here, live, with every step it takes. LAICA computer use is switched on under Settings, Agents.</p></div>;
  const computer=a.acts.some(x=>x.computer);
  return <div className="screen-panel">
    <div className="screen-head"><span className={`screen-dot ${a.live?'live':''}`}/><b>{a.live?(computer?'LAICA is using your computer':'The agent is using a browser'):'Last used '+(a.lastTime?ago(a.lastTime):'earlier')}</b>{a.live&&<Button size="small" variant="quiet" leadingIcon={<Square size={11} fill="currentColor"/>} onClick={()=>{void request('harnessStop',{Id:sessionId});}}>Stop · Esc</Button>}</div>
    <div className={`screen-frame ${a.live?'live':''}`}>{url?<img src={url} alt="What the agent last saw" draggable={false}/>:<div className="screen-wait">Waiting for a picture…</div>}</div>
    {a.images.length>1&&<div className="screen-strip"><Thumbs images={a.images.slice(-8)} max={8}/></div>}
    <ol className="screen-steps">{a.acts.slice(-40).reverse().map((x,i)=><li key={i} className={x.computer?'is-computer':''}><span className="ss-ico">{icon(x.kind)}</span><span className="ss-text">{x.text}</span><time>{new Date(x.time).toLocaleTimeString([], {hour:'2-digit',minute:'2-digit',second:'2-digit'})}</time></li>)}</ol>
  </div>;
}
function ago(ms:number){const s=Math.round((Date.now()-ms)/1000);return s<60?'moments ago':s<3600?Math.round(s/60)+' min ago':Math.round(s/3600)+' h ago';}