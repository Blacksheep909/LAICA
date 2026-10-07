import {createContext,useCallback,useContext,useEffect,useMemo,useRef,useState} from 'react';
import type {ReactNode} from 'react';
import {useToast} from 'open-glass-ui';
import {request,subscribe,isDesktop} from './bridge';
import {pushUndo,runUndo} from './undo';
import {builtinAssistants} from './assistants-data';
import type {Assistant} from './assistants-data';

export interface HarnessInfo { Id:string; Name:string; Path:string|null; Available:boolean; Custom:boolean; Modes:string[]; DefaultMode:string }
export interface HSession { Id:string; Harness:string; Title:string; Cwd:string; Project:string; Mode:string; Busy:boolean; BusySince?:string; UpdatedUtc?:string; Paused?:boolean; AssistantId?:string; Isolated?:boolean; Branch?:string; Effort?:string; ServiceId?:string; Model?:string; TeamId?:string }
export interface HEvent { SessionId:string; Kind:string; Text:string; Detail?:string|null; TimeUtc:string }
export interface TeamRun { Id:string; Title:string; Running:boolean; Paused?:boolean; Cwd:string; Leader:string; Members:{Name:string;Harness:string;Role:string}[]; Goal:string; Phase:string }
export interface HistoryItem { Source:'codex'|'claude'; ExternalId:string; Title:string; Project:string; ProjectName:string; ProjectId:string; UpdatedUtc:string; SessionId:string }
export interface PlanWindow { Label:string; Percent:number; ResetsUtc:string; SeenUtc:string; Source:string; Stale:boolean }
export interface VendorUsage { Replies?:number; Windows:PlanWindow[]; TokensWeek:number; WeekBudget:number; Source:string; TopPercent:number; Key:string; Name:string; Available:boolean; Turns:number; TurnsToday:number; TokensIn:number; TokensOut:number; TokensToday:number; Budget:number; Limited:boolean; LimitedUntilUtc:string; LimitText:string; Warn:''|'near'|'over'|'limited'; LastUtc:string }
export interface CodexProject { Id:string; Name:string; Path:string; Roots:string[] }
export interface CreateOptions { Harness:string; Cwd:string; Mode?:string; Assistant?:Assistant; Isolate?:boolean; Title?:string; ServiceId?:string; Model?:string; Effort?:string }

export const effortLabel:Record<string,string>={minimal:'Minimal',low:'Low',medium:'Medium',high:'High',xhigh:'Extra high',max:'Max'};
export const modeLabel:Record<string,string>={'read-only':'Read only','workspace-write':'Workspace write','danger-full-access':'Full access',plan:'Plan only',default:'Default',acceptEdits:'Accept edits',bypassPermissions:'YOLO',yolo:'YOLO','ask-first':'Ask first','accept-edits':'Accept edits'};

interface Store {
  pinned:string[]; togglePin:(key:string,title?:string)=>void;
  usage:VendorUsage[]; reloadUsage:()=>void; setBudget:(key:string,tokens?:number,weekly?:number)=>void; clearLimit:(key:string)=>void; continueElsewhere:(id:string,harness:string,serviceId?:string,model?:string)=>Promise<void>;
  project:string|null; setProject:(p:string|null)=>void; savedProjects:string[]; addProject:(p:string)=>void; removeProject:(p:string)=>void;
  codexProjects:CodexProject[]; history:HistoryItem[]; historyLoading:boolean; reloadHistory:(refresh?:boolean)=>void; openHistory:(h:HistoryItem,cwd:string)=>Promise<void>;
  teams:TeamRun[]; reloadTeams:()=>void;
  harnesses:HarnessInfo[]; assistants:Assistant[]; sessions:HSession[]; active:string|null; events:Record<string,HEvent[]>; refreshKey:number;
  setActive:(id:string|null)=>void; reload:(prefer?:string|null)=>Promise<void>; reloadAssistants:()=>void;
  create:(o:CreateOptions)=>Promise<HSession>; send:(id:string,text:string)=>Promise<void>; stop:(id:string)=>void; close:(id:string)=>void; rename:(id:string,title:string)=>void;
  nameOf:(id:string)=>string;
}
const Ctx=createContext<Store|null>(null);
export const useHarness=()=>{const s=useContext(Ctx);if(!s)throw new Error('HarnessProvider missing');return s;};

export function HarnessProvider({children}:{children:ReactNode}){
  const {toast}=useToast();
  const [harnesses,setHarnesses]=useState<HarnessInfo[]>([]),[assistants,setAssistants]=useState<Assistant[]>(builtinAssistants),[sessions,setSessions]=useState<HSession[]>([]),[active,setActive]=useState<string|null>(null);
  const [events,setEvents]=useState<Record<string,HEvent[]>>({}),[refreshKey,setRefreshKey]=useState(0),[teams,setTeams]=useState<TeamRun[]>([]),[project,setProject]=useState<string|null>(null),[savedProjects,setSavedProjects]=useState<string[]>([]),[history,setHistory]=useState<HistoryItem[]>([]),[codexProjects,setCodexProjects]=useState<CodexProject[]>([]),[historyLoading,setHistoryLoading]=useState(false);
  const [usage,setUsage]=useState<VendorUsage[]>([]);const warned=useRef<Record<string,string>>({});
  const reloadUsage=useCallback(()=>{if(!isDesktop)return;request<VendorUsage[]>('usageGet').then(list=>{setUsage(list);for(const u of list){
        for(const w of u.Windows??[]){const lvl=w.Percent>=95?'95':w.Percent>=80?'80':'';const k=u.Key+'|'+w.Label;const seen=warned.current[k]??'';
          if(lvl&&lvl!==seen&&!(seen==='95'&&lvl==='80')){const what=w.Label==='Weekly'?'weekly limit':w.Label==='5-hour'?'5-hour limit':w.Label+' limit';toast({title:u.Name+': '+Math.round(w.Percent)+'% of the '+what+' used',description:lvl==='95'?'Almost out. Switch to another agent, or pause the work until it resets.':'Getting close.'+(w.ResetsUtc?' Resets '+new Date(w.ResetsUtc).toLocaleString([],{weekday:'short',hour:'numeric',minute:'2-digit'})+'.':''),duration:10000});}
          warned.current[k]=lvl;}
        const prev=warned.current[u.Key]??'';if(u.Warn&&u.Warn!==prev&&!(u.Windows??[]).some(w=>w.Percent>=80)){const t=u.Warn==='limited'?{title:u.Name+' is out of usage',description:'Chats on it can carry on with another agent or a team: use the banner in the chat, or pick another model.'}:u.Warn==='near'?{title:u.Name+' is close to its daily budget',description:'Over 80% of the token budget you set for today.'}:{title:u.Name+' passed its daily budget',description:'You can raise the budget in the usage meter, bottom right.'};toast({...t,duration:9000});}warned.current[u.Key]=u.Warn;}}).catch(()=>{});},[toast]);
  const setBudget=useCallback((key:string,tokens?:number,weekly?:number)=>{request<VendorUsage[]>('usageBudget',{Key:key,Tokens:tokens,Weekly:weekly}).then(setUsage).catch(()=>{});},[]);
  const clearLimit=useCallback((key:string)=>{request<VendorUsage[]>('usageClear',{Key:key}).then(setUsage).catch(()=>{});},[]);
  useEffect(()=>{reloadUsage();const t=setInterval(reloadUsage,60000);return()=>clearInterval(t);},[reloadUsage]);
  const reloadHistory=useCallback((refresh=false)=>{if(!isDesktop)return;setHistoryLoading(true);request<CodexProject[]>('codexProjects').then(setCodexProjects).catch(()=>{});request<HistoryItem[]>('historyList',{Refresh:refresh}).then(setHistory).catch(()=>{}).finally(()=>setHistoryLoading(false));},[]);
  useEffect(()=>{let last=Date.now();const onFocus=()=>{if(Date.now()-last>15000){last=Date.now();reloadHistory(true);request<HarnessInfo[]>('harnesses').then(setHarnesses).catch(()=>{});}};window.addEventListener('focus',onFocus);return()=>window.removeEventListener('focus',onFocus);},[reloadHistory]);
  const persistProjects=useCallback((next:string[])=>{setSavedProjects(next);request('storeSet',{Name:'projects',Value:next}).catch(()=>{});},[]);
  const addProject=useCallback((p:string)=>{const path=p.trim().replace(/[\\\\/]+$/,'');if(!path)return;setSavedProjects(cur=>{if(cur.some(x=>x.toLowerCase()===path.toLowerCase()))return cur;const next=[path,...cur];request('storeSet',{Name:'projects',Value:next}).catch(()=>{});return next;});setProject(path);},[]);
  const removeProject=useCallback((p:string)=>{pushUndo('Removed project '+(p.split(/[\\\\/]/).filter(Boolean).pop()??p),()=>addProject(p));setSavedProjects(cur=>{const next=cur.filter(x=>x!==p);request('storeSet',{Name:'projects',Value:next}).catch(()=>{});return next;});setProject(cur=>cur===p?null:cur);},[]);
  void persistProjects;
  const reloadTeams=useCallback(()=>{request<TeamRun[]>('teamList').then(setTeams).catch(()=>{});},[]);
  const fail=useCallback((title:string)=>(e:Error)=>toast({title,description:e.message,duration:7000}),[toast]);
  const reload=useCallback(async(prefer?:string|null)=>{try{const list=await request<HSession[]>('harnessList');setSessions(list);if(prefer!==undefined)setActive(prefer);else setActive(a=>a&&list.some(s=>s.Id===a)?a:null);}catch(e){fail('Could not list chats')(e as Error);}},[fail]);
  const reloadAssistants=useCallback(()=>{request<Assistant[]|null>('storeGet',{Name:'assistants'}).then(a=>setAssistants([...builtinAssistants,...(a??[])])).catch(()=>{});},[]);
  useEffect(()=>{if(!isDesktop)return;
    request<HarnessInfo[]>('harnesses').then(setHarnesses).catch(fail('Could not detect agents'));reloadAssistants();reload();reloadTeams();reloadHistory(true);request<string[]|null>('storeGet',{Name:'projects'}).then(p=>p&&setSavedProjects(p)).catch(()=>{});
    return subscribe(m=>{const msg=m as unknown as {Type:string;Event?:HEvent};
      if(msg.Type==='harness'&&msg.Event){const e=msg.Event;setEvents(cur=>({...cur,[e.SessionId]:[...(cur[e.SessionId]??[]),e]}));if(e.Kind==='done'||e.Kind==='limit')reloadUsage();if(e.Kind==='done')setRefreshKey(k=>k+1);}
      else if(msg.Type==='harnessSessions'){reload();reloadTeams();reloadUsage();request<HarnessInfo[]>('harnesses').then(setHarnesses).catch(()=>{});}});},[reload,reloadAssistants,reloadTeams,reloadHistory,reloadUsage,fail]);
  useEffect(()=>{if(!active||!isDesktop)return;request<HEvent[]>('harnessHistory',{Id:active}).then(h=>setEvents(cur=>({...cur,[active]:h}))).catch(()=>{});},[active]);
  const create=useCallback(async(o:CreateOptions)=>{const s=await request<HSession>('harnessCreate',{Harness:o.Harness,Cwd:o.Cwd,Mode:o.Mode,Rules:o.Assistant?.Rules,AssistantId:o.Assistant?.Id,Title:o.Title??o.Assistant?.Name,Isolate:!!o.Isolate,ServiceId:o.ServiceId,Model:o.Model,Effort:o.Effort});await reload(s.Id);return s;},[reload]);
  const send=useCallback((id:string,text:string)=>request('harnessSend',{Id:id,Prompt:text}).then(()=>undefined),[]);
  const stop=useCallback((id:string)=>{request('harnessStop',{Id:id}).catch(fail('Could not stop'));},[fail]);
  const sessionsRef=useRef<HSession[]>([]);sessionsRef.current=sessions;
  const close=useCallback((id:string)=>{
    const title=sessionsRef.current.find(s=>s.Id===id)?.Title??'Chat';
    request('harnessClose',{Id:id}).then(()=>{const uid=pushUndo('Closed '+title,async()=>{await request('harnessRestore',{Id:id});await reload(id);});toast({title:'Closed '+title,description:'You can bring it back for two weeks.',actionLabel:'Undo',onAction:()=>{void runUndo(uid);},duration:9000});}).catch(fail('Could not close chat'));
  },[fail,reload,toast]);
  const rename=useCallback((id:string,title:string)=>{
    if(!title.trim())return;const old=sessionsRef.current.find(s=>s.Id===id)?.Title;
    request('harnessRename',{Id:id,Title:title}).then(()=>{if(old&&old!==title)pushUndo('Renamed '+old,()=>request('harnessRename',{Id:id,Title:old}).then(()=>undefined));}).catch(fail('Could not rename'));
  },[fail]);
  const [pinned,setPinned]=useState<string[]>(()=>{try{return JSON.parse(localStorage.getItem('laica-pins')??'[]') as string[];}catch{return [];}});
  const togglePin=useCallback((key:string,title?:string)=>{setPinned(cur=>{const had=cur.includes(key);const next=had?cur.filter(k=>k!==key):[key,...cur];try{localStorage.setItem('laica-pins',JSON.stringify(next));}catch{/* storage unavailable */}pushUndo((had?'Unpinned ':'Pinned ')+(title??'chat'),()=>setPinned(c2=>{const n2=had?[key,...c2.filter(k=>k!==key)]:c2.filter(k=>k!==key);try{localStorage.setItem('laica-pins',JSON.stringify(n2));}catch{/* ignore */}return n2;}));return next;});},[]);
  const openHistory=useCallback(async(h:HistoryItem,cwd:string)=>{try{const s=await request<HSession>('historyOpen',{Source:h.Source,ExternalId:h.ExternalId,Cwd:cwd});await reload(s.Id);setHistory(cur=>cur.map(x=>x.ExternalId===h.ExternalId&&x.Source===h.Source?{...x,SessionId:s.Id}:x));}catch(e){fail('Could not open that conversation')(e as Error);}},[reload,fail]);
  const continueElsewhere=useCallback(async(id:string,harness:string,serviceId?:string,model?:string)=>{const s=await request<HSession>('harnessContinue',{Id:id,Harness:harness,ServiceId:serviceId??'',Model:model??''});await reload(s.Id);},[reload]);
  const nameOf=useCallback((id:string)=>harnesses.find(h=>h.Id===id)?.Name??id,[harnesses]);
  const value=useMemo(()=>({pinned,togglePin,usage,reloadUsage,setBudget,clearLimit,continueElsewhere,project,setProject,savedProjects,addProject,removeProject,codexProjects,history,historyLoading,reloadHistory,openHistory,teams,reloadTeams,harnesses,assistants,sessions,active,events,refreshKey,setActive,reload,reloadAssistants,create,send,stop,close,rename,nameOf}),[pinned,togglePin,usage,reloadUsage,setBudget,clearLimit,continueElsewhere,project,savedProjects,addProject,removeProject,codexProjects,history,historyLoading,reloadHistory,openHistory,teams,reloadTeams,harnesses,assistants,sessions,active,events,refreshKey,reload,reloadAssistants,create,send,stop,close,rename,nameOf]);
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}
