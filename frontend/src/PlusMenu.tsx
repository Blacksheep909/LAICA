import {useEffect,useRef,useState} from 'react';
import {useToast} from 'open-glass-ui';
import {Paperclip,PenLine,Lightbulb,Target,Plug,Sparkles,Store,Plus,Check,Eraser,Undo2,Image as ImageIcon} from 'lucide-react';
import {Popover,PopoverTrigger,PopoverContent,Command,CommandInput,CommandList,CommandEmpty,CommandGroup,CommandItem,Dialog,DialogContent,DialogTitle,DialogDescription,Slider,Tip,Kbd} from './ui/kit';
import {request,isDesktop} from './bridge';
import type {Flags} from './Composer';

interface Tools { Mcp:{Name:string;Target:string;Source:string}[]; Skills:{Name:string;Target:string;Source:string;Detail:string}[] }
interface Suggest { Id:string; Name:string; Kind:string; Because:string; Summary:string; Suggested:boolean; Category:string }

/** The + menu: add files, sketch, switch on Plan mode or a Goal, drop in an installed tool or skill, or find something new. */
export default function PlusMenu({onPick,onFiles,flags,setFlags,insert,disabled}:{onPick:()=>void;onFiles:(f:File[])=>void;flags:Flags;setFlags:(f:Flags)=>void;insert:(text:string)=>void;disabled?:boolean}){
  const openPlugins=()=>window.dispatchEvent(new CustomEvent('laica-go',{detail:'plugins'}));
  const [open,setOpen]=useState(false),[tools,setTools]=useState<Tools|null>(null),[suggest,setSuggest]=useState<Suggest[]>([]),[sketch,setSketch]=useState(false);
  useEffect(()=>{
    if(!open||!isDesktop)return;
    request<Tools>('harnessTools').then(setTools).catch(()=>setTools({Mcp:[],Skills:[]}));
    request<{Items:Suggest[]}>('pluginLibrary').then(l=>setSuggest(l.Items.filter(i=>i.Suggested&&i.Kind!=='runtime').slice(0,3))).catch(()=>undefined);
  },[open]);
  const {toast}=useToast();
  const pasteImage=async()=>{
    try{
      const items=await navigator.clipboard.read();
      for(const it of items){const type=it.types.find(t=>t.startsWith('image/'));if(type){const blob=await it.getType(type);onFiles([new File([blob],'clipboard.'+(type.split('/')[1]||'png'),{type})]);return;}}
      toast({title:'No picture on the clipboard',description:'Copy an image or take a screenshot first, then try again.',duration:5000});
    }catch{toast({title:'Could not read the clipboard',description:'Press Ctrl+V in the message box instead.',duration:5000});}
  };
  const pick=(fn:()=>void)=>()=>{setOpen(false);setTimeout(fn,60);};
  const mcp=(tools?.Mcp??[]).filter((m,i,a)=>a.findIndex(x=>x.Name===m.Name)===i);
  const skills=(tools?.Skills??[]).filter((m,i,a)=>!m.Name.startsWith('.')&&a.findIndex(x=>x.Name===m.Name)===i);
  return <>
    <Popover open={open} onOpenChange={setOpen}>
      <Tip label="Add files, tools and more" side="top"><PopoverTrigger asChild><button type="button" className="plus-btn" aria-label="Add" disabled={disabled}><Plus size={18}/></button></PopoverTrigger></Tip>
      <PopoverContent className="plus-pop" side="top" align="start" onCloseAutoFocus={e=>e.preventDefault()}>
        <Command loop>
          <CommandInput placeholder="Search actions, tools and skills" autoFocus/>
          <CommandList>
            <CommandEmpty>Nothing matches. Try the plugin library.</CommandEmpty>
            <CommandGroup heading="Add">
              <CommandItem value="files and folders attach upload" onSelect={pick(onPick)}><Paperclip size={16}/><span className="ui-grow"><span>Files and folders</span></span></CommandItem>
              <CommandItem value="photo screenshot image clipboard paste" onSelect={pick(()=>{void pasteImage();})}><ImageIcon size={16}/><span className="ui-grow"><span>Image from clipboard</span><small>Attach the picture you copied (or press Ctrl+V)</small></span><Kbd>Ctrl V</Kbd></CommandItem>
              <CommandItem value="sketch draw" onSelect={pick(()=>setSketch(true))}><PenLine size={16}/><span className="ui-grow"><span>Sketch</span><small>Draw an idea to attach</small></span></CommandItem>
              <CommandItem value="plan mode" onSelect={()=>{setFlags({...flags,plan:!flags.plan});setOpen(false);}}><Lightbulb size={16}/><span className="ui-grow"><span>Plan mode</span><small>Plan first, change nothing</small></span>{flags.plan&&<Check size={15}/>}</CommandItem>
              <CommandItem value="goal keep going autonomously" onSelect={()=>{setFlags({...flags,goal:!flags.goal});setOpen(false);}}><Target size={16}/><span className="ui-grow"><span>Goal</span><small>Keep pursuing it until it is done</small></span>{flags.goal&&<Check size={15}/>}</CommandItem>
            </CommandGroup>
            {mcp.length>0&&<CommandGroup heading="MCP servers">{mcp.map(m=><CommandItem key={m.Name} value={'mcp '+m.Name} onSelect={pick(()=>insert(`Use the ${m.Name} MCP server to `))}><Plug size={16}/><span className="ui-grow"><span>{m.Name}</span><small>{m.Source}</small></span></CommandItem>)}</CommandGroup>}
            {skills.length>0&&<CommandGroup heading="Skills">{skills.slice(0,12).map(s=><CommandItem key={s.Target+s.Name} value={'skill '+s.Name+' '+s.Detail} onSelect={pick(()=>insert(`Use the ${s.Name} skill. `))}><Sparkles size={16}/><span className="ui-grow"><span>{s.Name}</span><small>{s.Detail||s.Source}</small></span></CommandItem>)}</CommandGroup>}
            <CommandGroup heading="Plugins">
              {suggest.map(s=><CommandItem key={s.Id} value={'plugin suggested '+s.Name+' '+s.Because} onSelect={pick(openPlugins)}><Store size={16}/><span className="ui-grow"><span>{s.Name}</span><small>{s.Because||s.Category}</small></span></CommandItem>)}
              <CommandItem value="plugin library browse install more" onSelect={pick(openPlugins)}><Store size={16}/><span className="ui-grow"><span>Browse the plugin library</span><small>MCP servers, skills and agents</small></span></CommandItem>
            </CommandGroup>
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
    <SketchDialog open={sketch} onOpenChange={setSketch} onDone={f=>onFiles([f])}/>
  </>;
}

const inks=['#f5f3fb','#b5a6ff','#6cb6ff','#7be0b4','#ffc27a','#ff8fb0'];
/** A small drawing pad. The picture is attached to your message like any other image. */
function SketchDialog({open,onOpenChange,onDone}:{open:boolean;onOpenChange:(o:boolean)=>void;onDone:(f:File)=>void}){
  const cv=useRef<HTMLCanvasElement>(null);const [ink,setInk]=useState(inks[0]),[size,setSize]=useState([4]),[erase,setErase]=useState(false);
  const strokes=useRef<ImageData[]>([]);const last=useRef<{x:number;y:number}|null>(null);
  useEffect(()=>{if(!open)return;const t=setTimeout(()=>{const c=cv.current;if(!c)return;const g=c.getContext('2d')!;g.fillStyle='#14112a';g.fillRect(0,0,c.width,c.height);strokes.current=[];},40);return()=>clearTimeout(t);},[open]);
  const at=(e:React.PointerEvent)=>{const c=cv.current!;const r=c.getBoundingClientRect();return {x:(e.clientX-r.left)*c.width/r.width,y:(e.clientY-r.top)*c.height/r.height};};
  const down=(e:React.PointerEvent<HTMLCanvasElement>)=>{const c=cv.current!;c.setPointerCapture(e.pointerId);const g=c.getContext('2d')!;strokes.current.push(g.getImageData(0,0,c.width,c.height));if(strokes.current.length>30)strokes.current.shift();last.current=at(e);draw(e);};
  const draw=(e:React.PointerEvent<HTMLCanvasElement>)=>{if(!last.current)return;const c=cv.current!;const g=c.getContext('2d')!;const p=at(e);g.lineCap='round';g.lineJoin='round';g.lineWidth=erase?size[0]*4:size[0];g.strokeStyle=erase?'#14112a':ink;g.beginPath();g.moveTo(last.current.x,last.current.y);g.lineTo(p.x+.01,p.y+.01);g.stroke();last.current=p;};
  const clear=()=>{const c=cv.current!;const g=c.getContext('2d')!;strokes.current.push(g.getImageData(0,0,c.width,c.height));g.fillStyle='#14112a';g.fillRect(0,0,c.width,c.height);};
  const undo=()=>{const c=cv.current!;const s=strokes.current.pop();if(s)c.getContext('2d')!.putImageData(s,0,0);};
  const attach=()=>{cv.current!.toBlob(b=>{if(b){onDone(new File([b],'sketch.png',{type:'image/png'}));onOpenChange(false);}},'image/png');};
  return <Dialog open={open} onOpenChange={onOpenChange}><DialogContent className="sketch-dialog" aria-describedby={undefined}>
    <DialogTitle>Sketch</DialogTitle><DialogDescription>Draw a layout, a diagram or an idea. It is attached as an image.</DialogDescription>
    <canvas ref={cv} width={1000} height={560} className="sketch-canvas" onPointerDown={down} onPointerMove={draw} onPointerUp={()=>{last.current=null;}} onPointerCancel={()=>{last.current=null;}}/>
    <div className="sketch-tools">
      <div className="sketch-inks">{inks.map(c=><button key={c} type="button" className={`sketch-ink ${ink===c&&!erase?'on':''}`} style={{background:c}} aria-label={`Ink ${c}`} onClick={()=>{setInk(c);setErase(false);}}/>)}
        <Tip label="Eraser"><button type="button" className={`sketch-tool ${erase?'on':''}`} aria-label="Eraser" onClick={()=>setErase(e=>!e)}><Eraser size={15}/></button></Tip></div>
      <Slider value={size} min={1} max={18} step={1} onValueChange={setSize} aria-label="Brush size" className="sketch-size"/>
      <Tip label="Undo"><button type="button" className="sketch-tool" aria-label="Undo" onClick={undo}><Undo2 size={15}/></button></Tip>
      <button type="button" className="sketch-text" onClick={clear}>Clear</button>
      <button type="button" className="sketch-go" onClick={attach}>Attach sketch</button>
    </div>
  </DialogContent></Dialog>;
}
