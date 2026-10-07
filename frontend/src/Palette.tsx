import {useEffect,useMemo,useState} from 'react';
import {MessageSquare,Users,SquarePen,Settings as SettingsIcon,Plug,Workflow,Activity as ActivityIcon,CalendarClock,Sparkles,PanelLeft,Maximize2,BookOpen,Pause,Play,Store,Undo2,Pin} from 'lucide-react';
import type {ReactNode} from 'react';
import {request} from './bridge';
import {useHarness} from './harness-store';
import {runUndo,undoDepth,lastUndoLabel} from './undo';
import {Dialog,DialogContent,DialogTitle,Command,CommandInput,CommandList,CommandEmpty,CommandGroup,CommandItem,Kbd} from './ui/kit';

interface Cmd { id:string; label:string; hint?:string; group:string; icon:ReactNode; keys?:string; run:()=>void }
interface Props { go:(page:string)=>void; openChat:(id:string)=>void; openTeam:(id:string)=>void; newChat:()=>void; toggleSidebar:()=>void; fullscreen:()=>void; guide:()=>void }

/** Ctrl+K: jump to any chat, team or page, and run common actions, from the keyboard (shadcn's Command, in glass). */
export default function PaletteHost({go,openChat,openTeam,newChat,toggleSidebar,fullscreen,guide}:Props){
  const {sessions,active,teams,pinned}=useHarness();
  const [open,setOpen]=useState(false);
  useEffect(()=>{const key=(e:KeyboardEvent)=>{if((e.ctrlKey||e.metaKey)&&e.key.toLowerCase()==='k'){e.preventDefault();setOpen(o=>!o);}};document.addEventListener('keydown',key);return()=>document.removeEventListener('keydown',key);},[]);
  const current=sessions.find(s=>s.Id===active);
  const groups=useMemo(()=>{
    const actions:Cmd[]=[
      {id:'new',label:'New chat',group:'Actions',icon:<SquarePen size={16}/>,keys:'Ctrl N',run:newChat},
      ...(undoDepth()?[{id:'undo',label:'Undo: '+lastUndoLabel(),group:'Actions',icon:<Undo2 size={16}/>,keys:'Ctrl Z',run:()=>{void runUndo();}}]:[]),
      ...(current&&current.Busy&&current.Harness!=='workflow'?[{id:'pause',label:current.Paused?'Resume the work in this chat':'Pause the work in this chat',group:'Actions',icon:current.Paused?<Play size={16}/>:<Pause size={16}/>,run:()=>{void request(current.Paused?'harnessResume':'harnessPause',{Id:current.Id}).catch(()=>undefined);}}]:[])
    ];
    const pages:Cmd[]=[
      {id:'p-settings',label:'Settings',group:'Go to',icon:<SettingsIcon size={16}/>,keys:'Ctrl ,',run:()=>go('settings')},
      {id:'p-plugins',label:'Plugin library',hint:'MCP servers, skills and agents',group:'Go to',icon:<Store size={16}/>,run:()=>go('plugins')},
      {id:'p-services',label:'Services',group:'Go to',icon:<Plug size={16}/>,run:()=>go('services')},
      {id:'p-workflow',label:'Workflow designer',group:'Go to',icon:<Workflow size={16}/>,run:()=>go('teams')},
      {id:'p-activity',label:'Activity',group:'Go to',icon:<ActivityIcon size={16}/>,run:()=>go('activity')},
      {id:'p-tasks',label:'Scheduled tasks',group:'Go to',icon:<CalendarClock size={16}/>,run:()=>go('tasks')},
    ];
    const view:Cmd[]=[
      {id:'v-side',label:'Toggle sidebar',group:'View',icon:<PanelLeft size={16}/>,keys:'Ctrl B',run:toggleSidebar},
      {id:'v-full',label:'Full screen',group:'View',icon:<Maximize2 size={16}/>,keys:'F11',run:fullscreen},
      {id:'v-guide',label:'Guide and shortcuts',group:'View',icon:<BookOpen size={16}/>,run:guide}
    ];
    const t:Cmd[]=teams.map(x=>({id:'t-'+x.Id,label:x.Title,hint:'Team',group:'Teams',icon:<Users size={16}/>,run:()=>openTeam(x.Id)}));
    const c:Cmd[]=[...sessions].sort((a,b)=>Number(pinned.includes('s'+b.Id))-Number(pinned.includes('s'+a.Id))).map(s=>({id:'c-'+s.Id,label:s.Title,hint:s.Project.split(/[\\/]/).filter(Boolean).pop(),group:'Chats',icon:pinned.includes('s'+s.Id)?<Pin size={16}/>:<MessageSquare size={16}/>,run:()=>openChat(s.Id)}));
    return [['Actions',actions],['Chats',c],['Teams',t],['Go to',pages],['View',view]] as [string,Cmd[]][];
  },[sessions,teams,current,pinned,go,openChat,openTeam,newChat,toggleSidebar,fullscreen,guide,open]); // eslint-disable-line react-hooks/exhaustive-deps
  const run=(c:Cmd)=>{setOpen(false);setTimeout(c.run,40);};
  return <Dialog open={open} onOpenChange={setOpen}>
    <DialogContent bare showClose={false} className="palette-dialog" aria-describedby={undefined}>
      <DialogTitle className="sr-only">Command palette</DialogTitle>
      <Command loop>
        <CommandInput placeholder="Search chats, teams and actions" autoFocus/>
        <CommandList>
          <CommandEmpty>Nothing matches that.</CommandEmpty>
          {groups.filter(([,items])=>items.length>0).map(([name,items])=><CommandGroup key={name} heading={name}>
            {items.map(c=><CommandItem key={c.id} value={c.group+' '+c.label+' '+(c.hint??'')} onSelect={()=>run(c)}>{c.icon}<span className="ui-grow"><span>{c.label}</span>{c.hint&&<small>{c.hint}</small>}</span>{c.keys&&<Kbd>{c.keys}</Kbd>}</CommandItem>)}
          </CommandGroup>)}
        </CommandList>
      </Command>
    </DialogContent>
  </Dialog>;
}
