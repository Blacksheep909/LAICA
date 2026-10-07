import {HandoffCard} from './Handoff';
import {CoAuthorCard} from './CoAuthor';
import {UpdatesCard} from './Updates';
import {ComputerCard} from './ComputerCard';
import {loadLook,saveLook,DEFAULT_LOOK,ACCENTS,type Look,type GlassKind} from './look';
import {getDictationMode,setDictationMode} from './Composer';
import type {DictationMode} from './Composer';
import {useCallback,useEffect,useState} from 'react';
import {Button,Glass,Dialog,Switch,useToast} from 'open-glass-ui';
import {Select} from './GlassSelect';
import {Plus,Trash2,Pencil,Send,Globe,ShieldAlert,Copy} from 'lucide-react';
import {request,subscribe,isDesktop,isRemote} from './bridge';
import {useHarness,modeLabel} from './harness-store';
import type {Service,Model} from './types';
import './Harness.css';

export const CSS_KEY='laica-custom-css';
interface Item { Name:string; Source:string; Target:string; Detail:string }
interface Agent { Id:string; Name:string; Command:string; Args:string; Parser:string }
interface RemoteStatus { Enabled:boolean; Running:boolean; Port:number; Lan:boolean; HasPassword:boolean; Urls:string[]; Error:string|null; Sessions:number }
interface Channels { Telegram:{Enabled:boolean;HasToken:boolean;Connected:boolean;Error:string|null;Bot:string|null;Chats:string[];Harness:string;Cwd:string;Mode:string;ServiceId:string;Model:string}; Webhooks:{Id:string;Name:string;Kind:string}[] }
const blankAgent:Agent={Id:'',Name:'',Command:'',Args:'',Parser:'text'};
const tabs=[['agents','Agents'],['mcp','MCP & skills'],['remote','Remote access'],['channels','Channels'],['appearance','Appearance']] as const;
const platforms:[string,string][]=[['slack','Slack'],['discord','Discord'],['lark','Lark / Feishu'],['dingtalk','DingTalk'],['wecom','WeCom'],['generic','Other (JSON {"text":…})']];

export default function SettingsPage({onCss,workingDirectory,services,models,animate,onAnimate,reduce,onReduce,snap,snapDisabled,onSnap}:{animate:boolean;onAnimate:(v:boolean)=>void;onCss:(css:string)=>void;workingDirectory:string;services:Service[];models:Model[];reduce:boolean;onReduce:(v:boolean)=>void;snap:boolean;snapDisabled:boolean;onSnap:(v:boolean)=>void}){
  const {toast}=useToast();
  const {harnesses}=useHarness();
  const [tab,setTab]=useState<typeof tabs[number][0]>('agents');
  const fail=useCallback((title:string)=>(e:Error)=>toast({title,description:e.message,duration:7000}),[toast]);
  if(!isDesktop)return <div className="empty-state compact"><h2>Settings need the desktop app</h2></div>;
  return <div className="settings-content page-pad">
    <div className="pv-switch settings-tabs" role="tablist">{tabs.filter(([id])=>!(isRemote&&(id==='remote'))).map(([id,label])=><button key={id} role="tab" aria-selected={tab===id} className={tab===id?'on':''} onClick={()=>setTab(id)}>{label}</button>)}</div>
    {tab==='agents'&&<><Glass material="regular" className="setting-card"><HandoffCard/></Glass><Glass material="regular" className="setting-card"><CoAuthorCard/></Glass><Glass material="regular" className="setting-card"><UpdatesCard/></Glass><Glass material="regular" className="setting-card"><ComputerCard/></Glass><AgentsTab fail={fail}/></>}
    {tab==='mcp'&&<McpTab fail={fail}/>}
    {tab==='remote'&&<RemoteTab fail={fail}/>}
    {tab==='channels'&&<ChannelsTab fail={fail} harnesses={harnesses} workingDirectory={workingDirectory} services={services} models={models}/>}
    {tab==='appearance'&&<AppearanceTab onCss={onCss} animate={animate} onAnimate={onAnimate} reduce={reduce} onReduce={onReduce} snap={snap} snapDisabled={snapDisabled} onSnap={onSnap}/>}
  </div>;
}
type Fail=(title:string)=>(e:Error)=>void;

function AgentsTab({fail}:{fail:Fail}){
  const {toast}=useToast();const {harnesses}=useHarness();const [agent,setAgent]=useState<Agent>(blankAgent);
  const add=()=>request('agentSave',agent).then(()=>{setAgent(blankAgent);toast({title:'Agent added'});}).catch(fail('Could not add agent'));
  return <>
    <Glass material="regular" className="setting-card"><span className="eyebrow">AGENTS</span><h2>Detected on this computer</h2><p>LAICA finds installed agent command-line tools automatically. The LAICA Agent uses any service you add under Services.</p>
      <div className="page-pad" style={{marginTop:16}}>{harnesses.map(h=><div key={h.Id} className="list-row"><div className="grow"><strong>{h.Name}</strong><small>{h.Path??'Not installed'}{h.Modes.length?` · ${h.Modes.map(m=>modeLabel[m]??m).join(', ')}`:''}</small></div><span className={`pill ${h.Available?'ok':'no'}`}>{h.Available?'Ready':'Not found'}</span>{h.Custom&&<Button size="small" variant="quiet" aria-label="Remove agent" onClick={()=>request('agentDelete',{Id:h.Id}).catch(fail('Could not remove'))}><Trash2 size={14}/></Button>}</div>)}</div></Glass>
    <Glass material="regular" className="setting-card"><span className="eyebrow">CUSTOM AGENT</span><h2>Add a command-line agent</h2><p>The prompt is sent on standard input, or put {'{prompt}'} in the arguments to pass it as an argument. {'{cwd}'} is the chat's folder.</p>
      <div className="form-grid" style={{marginTop:16}}><label>Name<input className="hinput" value={agent.Name} onChange={e=>setAgent({...agent,Name:e.target.value})}/></label><label>Command or full path<input className="hinput" value={agent.Command} onChange={e=>setAgent({...agent,Command:e.target.value})} placeholder="my-agent.exe"/></label>
      <label>Arguments<input className="hinput" value={agent.Args} onChange={e=>setAgent({...agent,Args:e.target.value})} placeholder="run --prompt {prompt}"/></label><Select aria-label="Output format" label="Output format" value={agent.Parser} options={[{value:'text',label:'Plain text'},{value:'claude',label:'Claude stream-json'},{value:'codex',label:'Codex --json'}]} onChange={e=>setAgent({...agent,Parser:e.target.value})}/>
      <div className="full dialog-actions"><Button variant="primary" leadingIcon={<Plus size={15}/>} disabled={!agent.Name.trim()||!agent.Command.trim()} onClick={add}>Add agent</Button></div></div></Glass>
  </>;
}

function McpTab({fail}:{fail:Fail}){
  const {toast}=useToast();
  const [tools,setTools]=useState<{Mcp:Item[];Skills:Item[]}>({Mcp:[],Skills:[]});
  const [mcp,setMcp]=useState({Target:'claude',Name:'',Command:'',Url:'',Env:''}),[skill,setSkill]=useState<null|{Target:string;Name:string;Description:string;Body:string;New:boolean}>(null);
  const load=useCallback(()=>request<{Mcp:Item[];Skills:Item[]}>('harnessTools').then(setTools).catch(fail('Could not read tools')),[fail]);
  useEffect(()=>{load();return subscribe(m=>{if(m.Type==='harnessSessions')load();});},[load]);
  const addMcp=()=>request('mcpAdd',mcp).then(()=>{setMcp({...mcp,Name:'',Command:'',Url:'',Env:''});toast({title:'MCP server added'});load();}).catch(fail('Could not add server'));
  const editSkill=async(s:Item)=>{try{const text=await request<string>('skillRead',{Target:s.Target,Name:s.Name});const body=text.replace(/^---[\s\S]*?---\s*/,'');setSkill({Target:s.Target,Name:s.Name,Description:s.Detail,Body:body,New:false});}catch(e){fail('Could not open skill')(e as Error);}};
  const saveSkill=()=>skill&&request('skillSave',skill).then(()=>{setSkill(null);toast({title:'Skill saved'});load();}).catch(fail('Could not save skill'));
  return <>
    <Glass material="regular" className="setting-card"><span className="eyebrow">MCP SERVERS</span><h2>Tools your agents can use</h2><p>Added through each agent's own command line, so Claude Code and Codex see them exactly as if you had run their <code>mcp add</code> command.</p>
      <div className="page-pad" style={{marginTop:16}}>{!tools.Mcp.length&&<small>No MCP servers configured.</small>}{tools.Mcp.map((m,i)=><div key={i} className="list-row"><div className="grow"><strong>{m.Name}</strong><small>{m.Detail||'—'}</small></div><span className="pill">{m.Source}</span><Button size="small" variant="quiet" aria-label={`Remove ${m.Name}`} onClick={()=>{if(window.confirm(`Remove ${m.Name} from ${m.Source}?`))request('mcpRemove',{Target:m.Target,Name:m.Name}).then(()=>{toast({title:'Removed'});load();}).catch(fail('Could not remove'));}}><Trash2 size={14}/></Button></div>)}</div>
      <div className="form-grid" style={{marginTop:18}}><Select aria-label="Agent" label="Agent" value={mcp.Target} options={[{value:'claude',label:'Claude Code'},{value:'codex',label:'Codex'}]} onChange={e=>setMcp({...mcp,Target:e.target.value})}/><label>Name<input className="hinput" value={mcp.Name} onChange={e=>setMcp({...mcp,Name:e.target.value})} placeholder="my-server"/></label>
        <label>Command (local server)<input className="hinput" value={mcp.Command} onChange={e=>setMcp({...mcp,Command:e.target.value})} placeholder="npx -y @scope/server"/></label><label>…or server URL<input className="hinput" value={mcp.Url} onChange={e=>setMcp({...mcp,Url:e.target.value})} placeholder="https://example.com/mcp"/></label>
        <label className="full">Environment (KEY=value, space separated)<input className="hinput" value={mcp.Env} onChange={e=>setMcp({...mcp,Env:e.target.value})}/></label>
        <div className="full dialog-actions"><Button variant="primary" leadingIcon={<Plus size={15}/>} disabled={!mcp.Name.trim()||(!mcp.Command.trim()&&!mcp.Url.trim())} onClick={addMcp}>Add server</Button></div></div></Glass>
    <Glass material="regular" className="setting-card"><span className="eyebrow">SKILLS</span><h2>Reusable instructions</h2><p>Skills are folders with a SKILL.md file. Deleting one moves it to an archive instead of erasing it.</p>
      <div className="page-pad" style={{marginTop:16}}>{!tools.Skills.length&&<small>No skills found.</small>}{tools.Skills.map((m,i)=><div key={i} className="list-row"><div className="grow"><strong>{m.Name}</strong><small>{m.Detail}</small></div><span className="pill">{m.Source}</span><Button size="small" variant="quiet" aria-label={`Edit ${m.Name}`} onClick={()=>editSkill(m)}><Pencil size={14}/></Button><Button size="small" variant="quiet" aria-label={`Delete ${m.Name}`} onClick={()=>{if(window.confirm(`Archive the skill ${m.Name}?`))request('skillDelete',{Target:m.Target,Name:m.Name}).then(()=>{toast({title:'Skill archived'});load();}).catch(fail('Could not delete'));}}><Trash2 size={14}/></Button></div>)}</div>
      <div className="dialog-actions"><Button leadingIcon={<Plus size={15}/>} onClick={()=>setSkill({Target:'claude',Name:'',Description:'',Body:'',New:true})}>New skill</Button></div></Glass>
    <Dialog title={skill?.New?'New skill':`Edit ${skill?.Name}`} open={skill!==null} onOpenChange={o=>{if(!o)setSkill(null);}}>{skill&&<div className="form-grid">
      {skill.New&&<><Select aria-label="For" label="For" value={skill.Target} options={[{value:'claude',label:'Claude Code'},{value:'codex',label:'Codex'},{value:'shared',label:'Shared (.agents)'}]} onChange={e=>setSkill({...skill,Target:e.target.value})}/><label>Name<input className="hinput" value={skill.Name} onChange={e=>setSkill({...skill,Name:e.target.value})} placeholder="my-skill"/></label></>}
      <label className="full">When should it be used?<input className="hinput" value={skill.Description} onChange={e=>setSkill({...skill,Description:e.target.value})}/></label>
      <label className="full">Instructions<textarea style={{minHeight:180}} value={skill.Body} onChange={e=>setSkill({...skill,Body:e.target.value})}/></label>
      <div className="full dialog-actions"><Button variant="quiet" onClick={()=>setSkill(null)}>Cancel</Button><Button variant="primary" disabled={!skill.Name.trim()||!skill.Description.trim()||!skill.Body.trim()} onClick={saveSkill}>Save skill</Button></div></div>}</Dialog>
  </>;
}

function RemoteTab({fail}:{fail:Fail}){
  const {toast}=useToast();
  const [st,setSt]=useState<RemoteStatus|null>(null),[enabled,setEnabled]=useState(false),[port,setPort]=useState(8977),[lan,setLan]=useState(false),[password,setPassword]=useState('');
  const load=useCallback(()=>request<RemoteStatus>('remoteStatus').then(s=>{setSt(s);setEnabled(s.Enabled);setPort(s.Port);setLan(s.Lan);}).catch(fail('Could not read remote settings')),[fail]);
  useEffect(()=>{load();},[load]);
  const apply=()=>request<RemoteStatus>('remoteConfigure',{Enabled:enabled,Port:port,Lan:lan,Password:password||undefined}).then(s=>{setSt(s);setPassword('');toast({title:s.Running?'Remote access is on':'Remote access is off'});}).catch(fail('Could not apply'));
  return <Glass material="regular" className="setting-card"><span className="eyebrow">REMOTE ACCESS</span><h2>Use LAICA from another browser</h2><p>Opens LAICA's interface on a web address so you can reach your chats and agents from a phone, tablet or another computer. It is off until you set a password.</p>
    <div className="remote-warn"><ShieldAlert size={16}/><span>Anyone with the password can run agents, read files and change files on this computer. Use a long, unique password. The connection is not encrypted, so only allow your home or office network, or put LAICA behind a VPN or tunnel you trust.</span></div>
    <div className="form-grid" style={{marginTop:16}}>
      <label className="chk"><input type="checkbox" checked={enabled} onChange={e=>setEnabled(e.target.checked)}/> Turn on remote access</label><label>Port<input className="hinput" type="number" min={1024} max={65535} value={port} onChange={e=>setPort(Number(e.target.value))}/></label>
      <label className="full chk"><input type="checkbox" checked={lan} onChange={e=>setLan(e.target.checked)}/><Globe size={13}/> Allow other devices on my network (otherwise only this computer can connect)</label>
      <label className="full">{st?.HasPassword?'New password (leave blank to keep the current one)':'Password (at least 8 characters)'}<input className="hinput" type="password" autoComplete="new-password" value={password} onChange={e=>setPassword(e.target.value)}/></label>
      <div className="full dialog-actions"><Button variant="primary" disabled={enabled&&!st?.HasPassword&&password.length<8} onClick={apply}>Apply</Button></div></div>
    {st?.Error&&<p className="error-text">{st.Error}</p>}
    {st?.Running&&<div className="page-pad" style={{marginTop:12}}><strong style={{fontSize:12}}>Open one of these addresses ({st.Sessions} signed in)</strong>{st.Urls.map(u=><div key={u} className="list-row"><div className="grow"><strong>{u}</strong></div><Button size="small" variant="quiet" aria-label="Copy address" onClick={()=>navigator.clipboard?.writeText(u).then(()=>toast({title:'Copied'}))}><Copy size={14}/></Button></div>)}</div>}
  </Glass>;
}

function ChannelsTab({fail,harnesses,workingDirectory,services,models}:{fail:Fail;harnesses:ReturnType<typeof useHarness>['harnesses'];workingDirectory:string;services:Service[];models:Model[]}){
  const {toast}=useToast();
  const [ch,setCh]=useState<Channels|null>(null),[token,setToken]=useState(''),[tg,setTg]=useState({Enabled:false,Harness:'',Cwd:workingDirectory,Mode:'',ServiceId:'',Model:''}),[code,setCode]=useState(''),[hook,setHook]=useState({Name:'',Kind:'slack',Url:''});
  const load=useCallback(()=>request<Channels>('channelStatus').then(s=>{setCh(s);setTg(t=>({Enabled:s.Telegram.Enabled,Harness:s.Telegram.Harness||t.Harness||harnesses.find(h=>h.Available)?.Id||'',Cwd:s.Telegram.Cwd||t.Cwd,Mode:s.Telegram.Mode,ServiceId:s.Telegram.ServiceId,Model:s.Telegram.Model}));}).catch(fail('Could not read channels')),[fail,harnesses]);
  useEffect(()=>{load();return subscribe(m=>{if(m.Type==='channels')load();});},[load]);
  const info=harnesses.find(h=>h.Id===tg.Harness);
  useEffect(()=>{if(!tg.Harness){const f=harnesses.find(h=>h.Available);if(f)setTg(x=>x.Harness?x:{...x,Harness:f.Id});}},[harnesses,tg.Harness]);
  const saveTg=()=>request('channelSaveTelegram',{...tg,Token:token||undefined}).then(()=>{setToken('');toast({title:'Telegram saved'});load();}).catch(fail('Could not save Telegram'));
  return <>
    <Glass material="regular" className="setting-card"><span className="eyebrow">TELEGRAM</span><h2>Talk to an agent from your phone</h2><p>Create a bot with @BotFather, paste its token, then pair your chat with a one-time code. Messages from chats that are not paired are ignored.</p>
      <div className="form-grid" style={{marginTop:16}}>
        <label className="chk"><input type="checkbox" checked={tg.Enabled} onChange={e=>setTg({...tg,Enabled:e.target.checked})}/> Turn on the Telegram bot</label><label>{ch?.Telegram.HasToken?'Bot token (saved — paste to replace)':'Bot token'}<input className="hinput" type="password" autoComplete="off" value={token} onChange={e=>setToken(e.target.value)} placeholder="123456:ABC…"/></label>
        <Select aria-label="Agent" label="Agent" value={tg.Harness} options={harnesses.filter(h=>h.Available).map(h=>({value:h.Id,label:h.Name}))} onChange={e=>setTg({...tg,Harness:e.target.value,Mode:''})}/><Select aria-label="Permissions" label="Permissions" value={tg.Mode||info?.DefaultMode||''} options={(info?.Modes??[]).map(m=>({value:m,label:modeLabel[m]??m}))} onChange={e=>setTg({...tg,Mode:e.target.value})}/>
        {tg.Harness==='laica'&&<><Select aria-label="Service" label="Service" value={tg.ServiceId} options={services.map(s=>({value:s.Id,label:s.Name}))} onChange={e=>setTg({...tg,ServiceId:e.target.value,Model:models.find(m=>m.ConnectionId===e.target.value)?.Id??''})}/><label>Model<input className="hinput" value={tg.Model} onChange={e=>setTg({...tg,Model:e.target.value})}/></label></>}
        <label className="full">Working folder<input className="hinput" value={tg.Cwd} onChange={e=>setTg({...tg,Cwd:e.target.value})}/></label>
        <div className="full dialog-actions"><Button variant="primary" onClick={saveTg}>Save</Button></div></div>
      {ch?.Telegram.Error&&<p className="error-text">{ch.Telegram.Error}</p>}{ch?.Telegram.Connected&&<p className="field-note">Connected as @{ch.Telegram.Bot}.</p>}
      <div className="page-pad" style={{marginTop:12}}><div className="dialog-actions" style={{marginTop:0,justifyContent:'flex-start'}}><Button leadingIcon={<Send size={14}/>} disabled={!ch?.Telegram.Connected} onClick={()=>request<string>('channelPairCode').then(setCode).catch(fail('Could not create code'))}>Create pairing code</Button></div>
        {code&&<p className="field-note">Send <b>/pair {code}</b> to your bot within 10 minutes.</p>}
        {ch?.Telegram.Chats.map(c=><div key={c} className="list-row"><div className="grow"><strong>Chat {c}</strong></div><Button size="small" variant="quiet" onClick={()=>request('channelUnpair',{ChatId:c}).catch(fail('Could not unpair'))}>Unpair</Button></div>)}</div></Glass>
    <Glass material="regular" className="setting-card"><span className="eyebrow">NOTIFICATIONS</span><h2>Slack, Discord, Lark, DingTalk and WeCom</h2><p>LAICA posts a message to these incoming webhooks when a scheduled task or a team finishes. Webhook addresses are encrypted and never shown again.</p>
      <div className="page-pad" style={{marginTop:16}}>{!ch?.Webhooks.length&&<small>No webhooks yet.</small>}{ch?.Webhooks.map(h=><div key={h.Id} className="list-row"><div className="grow"><strong>{h.Name}</strong><small>{platforms.find(p=>p[0]===h.Kind)?.[1]??h.Kind}</small></div><Button size="small" variant="quiet" onClick={()=>request('webhookTest',{Id:h.Id}).then(()=>toast({title:'Test message sent'})).catch(fail('Test failed'))}>Send test</Button><Button size="small" variant="quiet" aria-label="Delete" onClick={()=>request('webhookDelete',{Id:h.Id}).catch(fail('Could not delete'))}><Trash2 size={14}/></Button></div>)}</div>
      <div className="form-grid" style={{marginTop:16}}><label>Name<input className="hinput" value={hook.Name} onChange={e=>setHook({...hook,Name:e.target.value})}/></label><Select aria-label="Platform" label="Platform" value={hook.Kind} options={platforms.map(([v,l])=>({value:v,label:l}))} onChange={e=>setHook({...hook,Kind:e.target.value})}/>
        <label className="full">Webhook address<input className="hinput" type="password" autoComplete="off" value={hook.Url} onChange={e=>setHook({...hook,Url:e.target.value})} placeholder="https://…"/></label>
        <div className="full dialog-actions"><Button variant="primary" leadingIcon={<Plus size={15}/>} disabled={!hook.Name.trim()||!hook.Url.trim()} onClick={()=>request('webhookSave',hook).then(()=>{setHook({Name:'',Kind:hook.Kind,Url:''});toast({title:'Webhook saved'});}).catch(fail('Could not save webhook'))}>Add webhook</Button></div></div></Glass>
  </>;
}

function AppearanceTab({onCss,animate,onAnimate,reduce,onReduce,snap,snapDisabled,onSnap}:{animate:boolean;onAnimate:(v:boolean)=>void;onCss:(css:string)=>void;reduce:boolean;onReduce:(v:boolean)=>void;snap:boolean;snapDisabled:boolean;onSnap:(v:boolean)=>void}){
  const {toast}=useToast();
  const [css,setCss]=useState(()=>{try{return localStorage.getItem(CSS_KEY)??'';}catch{return '';}});
  const apply=()=>{try{localStorage.setItem(CSS_KEY,css);}catch{/* storage unavailable */}onCss(css);toast({title:'Custom CSS applied'});};
  const [look,setLookState]=useState(loadLook);
  const setLook=(p:Partial<Look>)=>{const n={...look,...p};setLookState(n);saveLook(n);};
  const [dict,setDict]=useState<DictationMode>(getDictationMode);
  return <><Glass material="regular" className="setting-card"><span className="eyebrow">LOOK</span><h2>Glass style</h2><p>Pick how panels and menus are rendered. Applies instantly.</p><div className="look-grid">{([['smoked','Smoked','Dark, deep tint'],['clear','Clear','Almost see-through'],['frosted','Frosted','Soft milky blur'],['liquid','Liquid','Bright, refractive'],['lensed','Lensed','Strong lens edge']] as [GlassKind,string,string][]).map(([k,n,d])=><button key={k} type="button" className="look-tile" aria-pressed={look.glass===k} onClick={()=>setLook({glass:k})}><b>{n}</b><span>{d}</span></button>)}</div>
<div className="look-swatches" role="group" aria-label="Accent colour">{ACCENTS.map(([n,c])=><button key={c} type="button" title={n} aria-label={n} className="look-swatch" style={{background:c}} aria-pressed={look.accent===c} onClick={()=>setLook({accent:c})}/>)}</div>
<label className="look-range">Panel opacity<input type="range" min="10" max="90" value={Math.round(look.panel*100)} onChange={e=>setLook({panel:Number(e.target.value)/100})}/><span>{Math.round(look.panel*100)}%</span></label>
<label className="look-range">Blur<input type="range" min="0" max="24" value={look.blur} onChange={e=>setLook({blur:Number(e.target.value)})}/><span>{look.blur}px</span></label>
<Switch label="High contrast text" description="Brighter text on glass." checked={look.contrast==='high'} onCheckedChange={v=>setLook({contrast:v?'high':'standard'})}/>
<Switch label="Starfield backdrop" description="Off gives a plain dark backdrop." checked={look.backdrop==='space'} onCheckedChange={v=>setLook({backdrop:v?'space':'plain'})}/>
<div className="dialog-actions"><Button variant="quiet" onClick={()=>setLook(DEFAULT_LOOK)}>Reset look</Button></div></Glass><Glass material="regular" className="setting-card"><span className="eyebrow">DICTATION</span><h2>Talk instead of typing</h2><p>The microphone in the message box turns speech into text. Windows listening works offline with no account; cloud transcription uses an OpenAI or Groq service from Services and is more accurate.</p><div className="look-grid">{([['auto','Automatic','Cloud if you have a service, else Windows'],['windows','Windows','Offline, built in'],['cloud','Cloud','OpenAI or Groq service']] as [DictationMode,string,string][]).map(([k,n,d])=><button key={k} type="button" className="look-tile" aria-pressed={dict===k} onClick={()=>{setDict(k);setDictationMode(k);}}><b>{n}</b><span>{d}</span></button>)}</div></Glass><Glass material="regular" className="setting-card"><span className="eyebrow">APPEARANCE</span><h2>A quieter workspace</h2><Switch label="Animate the background" description="Stars slowly fade in and out. Turn off for a perfectly still sky." checked={animate} onCheckedChange={onAnimate}/><Switch label="Reduce transparency" description="Use solid surfaces for clearer text and fewer effects. Also pauses the starfield." checked={reduce} onCheckedChange={onReduce}/><Switch label="Snap workflow nodes to grid" description="Keep agents aligned as you move them in the Workflow designer." checked={snap} disabled={snapDisabled} onCheckedChange={onSnap}/></Glass><Glass material="regular" className="setting-card"><span className="eyebrow">APPEARANCE</span><h2>Custom CSS</h2><p>Style LAICA yourself. Applied instantly and remembered on this computer.</p>
    <div className="form-grid" style={{marginTop:16}}><label className="full"><textarea style={{minHeight:160,fontFamily:'Consolas,monospace'}} spellCheck={false} value={css} onChange={e=>setCss(e.target.value)} placeholder={'.sidebar { border-radius: 12px !important; }'}/></label>
    <div className="full dialog-actions"><Button variant="quiet" onClick={()=>{setCss('');try{localStorage.removeItem(CSS_KEY);}catch{/* ignore */}onCss('');}}>Reset</Button><Button variant="primary" onClick={apply}>Apply</Button></div></div></Glass></>;
}
