import type { WorkspaceState } from './types';
type HostMessage = { Type:string; State?:WorkspaceState; Page?:string; Id?:string; Result?:unknown; Error?:string };
declare global { interface Window { chrome?:{webview?:{postMessage:(value:unknown)=>void;addEventListener:(name:string,fn:(event:{data:HostMessage})=>void)=>void}} } }
export const isDesktop = !!window.chrome?.webview;
const listeners = new Set<(message:HostMessage)=>void>();
const pending = new Map<string,{resolve:(result:any)=>void;reject:(error:Error)=>void;timer:ReturnType<typeof setTimeout>}>();
window.chrome?.webview?.addEventListener('message',event=>{
  const message=event.data;
  if(message.Type==='response' && message.Id){const request=pending.get(message.Id);if(request){clearTimeout(request.timer);pending.delete(message.Id);message.Error?request.reject(new Error(message.Error)):request.resolve(message.Result);}}
  else listeners.forEach(listener=>listener(message));
});
export function subscribe(listener:(message:HostMessage)=>void){listeners.add(listener);return ()=>{listeners.delete(listener);};}
export function request<T=any>(Method:string,Payload:unknown={}):Promise<T>{
  if(!isDesktop)return Promise.reject(new Error('This is a design preview. Open the desktop app to use your services and saved profiles.'));
  const Id=crypto.randomUUID();return new Promise((resolve,reject)=>{const timer=setTimeout(()=>{pending.delete(Id);reject(new Error('LAICA did not finish this request. Your draft is still open.'));},Method==='refreshModels'?450000:65000);pending.set(Id,{resolve,reject,timer});window.chrome!.webview!.postMessage({Id,Method,Payload});});
}
export const previewState:WorkspaceState={
  Version:'0.7.0',Mode:'multi',SnapToGrid:false,Busy:false,Refreshing:false,Answer:'',Status:'Ready',WorkingDirectory:'',RunEvents:[],NodeStates:{},SelectedProfileId:null,Profiles:[],
  Services:[],Models:[{Id:'gpt-6.1-sol',Name:'GPT-6.1 Sol',ConnectionId:'codex',Efforts:['low','medium','high','xhigh']},{Id:'gpt-6-luna',Name:'GPT-6 Luna',ConnectionId:'codex',Efforts:['low','medium','high']}],
  Plan:{Version:1,Goal:'',Stages:[{Id:'$input',X:-200,Y:220},{Id:'$review',X:820,Y:220},{Id:'$output',X:1090,Y:220}],HiddenStageLinks:[],Nodes:[
    {Id:'root',ParentId:null,Name:'Sol',Role:'supervisor',Model:'gpt-6.1-sol',Effort:'high',ConnectionId:'codex',Job:'Plan the work, coordinate workers, and integrate their results.',X:70,Y:220},
    {Id:'investigator',ParentId:'root',Name:'Investigator',Role:'investigator',Model:'gpt-6-luna',Effort:'medium',ConnectionId:'codex',Job:'Investigate the problem and report evidence.',X:440,Y:40},
    {Id:'implementer',ParentId:'root',Name:'Implementer',Role:'implementer',Model:'gpt-6-luna',Effort:'medium',ConnectionId:'codex',Job:'Implement the agreed changes in the assigned files and run the required checks.',X:440,Y:220},
    {Id:'verifier',ParentId:'root',Name:'Verifier',Role:'verifier',Model:'gpt-6-luna',Effort:'medium',ConnectionId:'codex',Job:'Inspect the changes and report problems or gaps.',X:440,Y:400}
  ]}
};
