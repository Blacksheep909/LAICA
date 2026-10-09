import {useMemo} from 'react';
import {useToast} from 'open-glass-ui';
import {request} from './bridge';
import {Select} from './GlassSelect';
import type {GlassOption} from './GlassSelect';
import {useHarness,modeLabel,effortLabel} from './harness-store';
import type {HSession} from './harness-store';
import type {Model} from './types';

import {useClaudeModels} from './ClaudeModels';
import {TagTeamPills} from './TagTeam';

/** Model, reasoning and permission pills for an open chat, the same choices the new-chat screen offers. Changes apply from the next message. */
export default function ChatPills({session,models}:{session:HSession;models:Model[]}){
  const {toast}=useToast();const {harnesses,reload}=useHarness();const claudeList=useClaudeModels();
  const info=harnesses.find(h=>h.Id===session.Harness);
  const modelOptions=useMemo<GlassOption[]>(()=>{
    if(session.Harness==='codex'){const list=models.filter(m=>m.ConnectionId==='codex');return [{value:'default',label:'Codex default',description:'Uses your Codex setting'},...list.map(m=>({value:m.Id,label:m.Name}))];}
    if(session.Harness==='claude')return claudeList.map(c=>({value:c.value,label:c.label,description:c.description}));
    if(session.Harness==='laica'&&session.ServiceId)return models.filter(m=>m.ConnectionId===session.ServiceId).map(m=>({value:m.Id,label:m.Name||m.Id}));
    return [];
  },[session.Harness,session.ServiceId,models,claudeList]);
  const levels=useMemo<string[]>(()=>{
    if(session.Harness==='claude')return ['low','medium','high','xhigh','max'];
    if(session.Harness==='codex')return (models.find(m=>m.ConnectionId==='codex'&&m.Id===(session.Model||''))?.Efforts??models.find(m=>m.ConnectionId==='codex')?.Efforts??[]).filter(l=>l!=='default'&&effortLabel[l]);
    return [];
  },[session.Harness,session.Model,models]);
  const apply=(patch:{Mode?:string;Model?:string;Effort?:string})=>request('harnessConfigure',{Id:session.Id,...patch}).then(()=>reload()).catch(e=>toast({title:'Could not change that',description:(e as Error).message,duration:6000}));
  const modelValue=session.Model&&modelOptions.some(o=>o.value===session.Model)?session.Model:modelOptions[0]?.value??'';
  const modes=info?.Modes??[];
  return <>
    {modelOptions.length>1&&<Select variant="pill" aria-label="Model" value={modelValue} options={modelOptions} onChange={e=>void apply({Model:e.target.value})} searchable={modelOptions.length>9} menuWidth={300} triggerLabel={modelOptions.find(o=>o.value===modelValue)?.label}/>}
    {levels.length>0&&<Select variant="pill" aria-label="Reasoning effort" value={session.Effort??''} options={[{value:'',label:'Default',description:'Let the model decide'},...levels.map(l=>({value:l,label:effortLabel[l]}))]} onChange={e=>void apply({Effort:e.target.value})} triggerLabel={'Effort · '+(session.Effort?effortLabel[session.Effort]??session.Effort:'Default')} menuWidth={220}/>}
    {modes.length>1&&<Select variant="pill" aria-label="Permissions" value={session.Mode||info?.DefaultMode||''} options={modes.map(m=>({value:m,label:modeLabel[m]??m}))} onChange={e=>void apply({Mode:e.target.value})}/>}
    <TagTeamPills session={session}/>
  </>;
}