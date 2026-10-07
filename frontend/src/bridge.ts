import type { WorkspaceState } from './types';
type HostMessage = { Type:string; State?:WorkspaceState; Page?:string; Id?:string; Result?:unknown; Error?:string; Text?:string; Final?:boolean };
declare global { interface Window { __LAICA_REMOTE__?:boolean; chrome?:{webview?:{postMessage:(value:unknown)=>void;postMessageWithAdditionalObjects?:(value:unknown,objects:ArrayLike<unknown>)=>void;addEventListener:(name:string,fn:(event:{data:HostMessage})=>void)=>void}} } }

/** True when served by LAICA's own remote-access server (a browser on another device) instead of the desktop window. */
export const isRemote = !!window.__LAICA_REMOTE__;
/** True whenever a real LAICA backend is behind the page (desktop window or remote browser). */
export const isDesktop = !!window.chrome?.webview || isRemote;
const listeners = new Set<(message:HostMessage)=>void>();
const pending = new Map<string,{resolve:(result:any)=>void;reject:(error:Error)=>void;timer:ReturnType<typeof setTimeout>}>();

const TOKEN_KEY='laica-remote-token';
const readToken=()=>{try{return sessionStorage.getItem(TOKEN_KEY);}catch{return null;}};
let token:string|null=isRemote?readToken():null;
export const hasToken=()=>!!token;
function dropToken(){token=null;try{sessionStorage.removeItem(TOKEN_KEY);}catch{/* storage unavailable */}window.dispatchEvent(new Event('laica-auth'));}
export async function remoteLogin(password:string){
  const response=await fetch('./api/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({password})});
  const body=await response.json().catch(()=>({}));
  if(!response.ok)throw new Error(body.Error||'Could not sign in.');
  token=body.token;try{sessionStorage.setItem(TOKEN_KEY,token!);}catch{/* storage unavailable */}
  void startEvents();
}

let eventsRunning=false;
async function startEvents(){
  if(!isRemote||eventsRunning||!token)return;eventsRunning=true;
  try{
    while(token){
      try{
        const response=await fetch('./api/events',{headers:{Authorization:'Bearer '+token}});
        if(response.status===401){dropToken();break;}
        if(!response.ok||!response.body)throw new Error('events unavailable');
        const reader=response.body.getReader();const decoder=new TextDecoder();let buffer='';
        for(;;){const {done,value}=await reader.read();if(done)break;buffer+=decoder.decode(value,{stream:true});
          let at:number;while((at=buffer.indexOf('\n\n'))>=0){const block=buffer.slice(0,at);buffer=buffer.slice(at+2);
            const line=block.split('\n').find(l=>l.startsWith('data: '));if(line){try{const message=JSON.parse(line.slice(6)) as HostMessage;listeners.forEach(l=>l(message));}catch{/* ignore malformed event */}}}}
      }catch{/* reconnect below */}
      await new Promise(r=>setTimeout(r,2000));
    }
  }finally{eventsRunning=false;}
}
if(isRemote&&token)void startEvents();

window.chrome?.webview?.addEventListener('message',event=>{
  const message=event.data;
  if(message.Type==='response' && message.Id){const request=pending.get(message.Id);if(request){clearTimeout(request.timer);pending.delete(message.Id);message.Error?request.reject(new Error(message.Error)):request.resolve(message.Result);}}
  else listeners.forEach(listener=>listener(message));
});
export function subscribe(listener:(message:HostMessage)=>void){listeners.add(listener);return ()=>{listeners.delete(listener);};}
export function request<T=any>(Method:string,Payload:unknown={}):Promise<T>{
  if(isRemote){
    if(!token)return Promise.reject(new Error('Sign in again.'));
    return fetch('./api/rpc',{method:'POST',headers:{'Content-Type':'application/json',Authorization:'Bearer '+token},body:JSON.stringify({Method,Payload})}).then(async response=>{
      const body=await response.json().catch(()=>({}));
      if(response.status===401){dropToken();throw new Error(body.Error||'Sign in again.');}
      if(!response.ok)throw new Error(body.Error||'LAICA could not finish this request.');
      return body.Result as T;
    });
  }
  if(!isDesktop)return Promise.reject(new Error('This is a design preview. Open the desktop app to use your services and saved profiles.'));
  const Id=crypto.randomUUID();return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{pending.delete(Id);reject(new Error('LAICA did not finish this request. Your draft is still open.'));},Method==='refreshModels'?450000:65000);pending.set(Id,{resolve,reject,timer});window.chrome!.webview!.postMessage({Id,Method,Payload});});
}
/** Asks the desktop host for the real path of a file the user dropped (WebView2 hands the host the file itself). Empty when it has no path, e.g. a pasted image. */
export function resolveDroppedFile(file:File):Promise<string[]>{
  const wv=window.chrome?.webview;
  if(isRemote||!wv||!wv.postMessageWithAdditionalObjects)return Promise.resolve([]);
  const Id=crypto.randomUUID();
  return new Promise(resolve=>{
    const timer=setTimeout(()=>{pending.delete(Id);resolve([]);},8000);
    pending.set(Id,{resolve:(r:string[]|null)=>resolve(r??[]),reject:()=>resolve([]),timer});
    try{wv.postMessageWithAdditionalObjects!({Id,Method:'resolveFiles',Payload:{}},[file]);}catch{clearTimeout(timer);pending.delete(Id);resolve([]);}
  });
}
export const previewState:WorkspaceState={
  Version:'preview',Mode:'multi',SnapToGrid:false,Busy:false,Refreshing:false,Answer:'',Status:'Ready',WorkingDirectory:'',RunEvents:[],NodeStates:{},SelectedProfileId:null,Profiles:[],
  Services:[],Models:[{Id:'gpt-6.1-sol',Name:'GPT-6.1 Sol',ConnectionId:'codex',Efforts:['low','medium','high','xhigh']},{Id:'gpt-6-luna',Name:'GPT-6 Luna',ConnectionId:'codex',Efforts:['low','medium','high']}],
  Plan:{Version:1,Goal:'',Stages:[{Id:'$input',X:-200,Y:220},{Id:'$review',X:820,Y:220},{Id:'$output',X:1090,Y:220}],HiddenStageLinks:[],Nodes:[
    {Id:'root',ParentId:null,Name:'Sol',Role:'supervisor',Model:'gpt-6.1-sol',Effort:'high',ConnectionId:'codex',Job:'Plan the work, coordinate workers, and integrate their results.',X:70,Y:220},
    {Id:'investigator',ParentId:'root',Name:'Investigator',Role:'investigator',Model:'gpt-6-luna',Effort:'medium',ConnectionId:'codex',Job:'Investigate the problem and report evidence.',X:440,Y:40},
    {Id:'implementer',ParentId:'root',Name:'Implementer',Role:'implementer',Model:'gpt-6-luna',Effort:'medium',ConnectionId:'codex',Job:'Implement the agreed changes in the assigned files and run the required checks.',X:440,Y:220},
    {Id:'verifier',ParentId:'root',Name:'Verifier',Role:'verifier',Model:'gpt-6-luna',Effort:'medium',ConnectionId:'codex',Job:'Inspect the changes and report problems or gaps.',X:440,Y:400}
  ]}
};
