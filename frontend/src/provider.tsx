import type {HSession,HistoryItem} from './harness-store';

export type ProviderKind='gpt'|'claude'|'gemini'|'team'|'other';
export interface Provider { kind:ProviderKind; label:string; detail:string; duo?:{first:Provider;second:Provider;active:0|1} }

const fromModel=(model:string|undefined,fallback:string):Provider=>{
  const m=(model??'').toLowerCase();
  if(/claude|opus|sonnet|haiku/.test(m))return {kind:'claude',label:'Claude',detail:model||'Claude'};
  if(/gpt|^o\d|codex|chatgpt/.test(m))return {kind:'gpt',label:'GPT',detail:model||'GPT'};
  if(/gemini|gemma/.test(m))return {kind:'gemini',label:'Gemini',detail:model||'Gemini'};
  return {kind:'other',label:fallback,detail:model?`${fallback} · ${model}`:fallback};
};

/** Which provider is behind a LAICA chat: GPT, Claude, Gemini, a team, or another agent / API. */
export function providerOfSession(s:Pick<HSession,'Harness'|'Model'|'TeamId'>&{PartnerHarness?:string;TagTeam?:string},harnessName:(id:string)=>string):Provider{
  const one=providerOfOne(s,harnessName);
  if(s.PartnerHarness&&!s.TeamId){const two=providerOfOne({Harness:s.PartnerHarness,Model:undefined,TeamId:undefined},harnessName);return {kind:'other',label:one.label+' + '+two.label,detail:one.detail+' + '+two.detail+' (tag-team)',duo:{first:one,second:two,active:s.TagTeam==='partner'?1:0}};}
  return one;
}
function providerOfOne(s:Pick<HSession,'Harness'|'Model'|'TeamId'>,harnessName:(id:string)=>string):Provider{
  if(s.TeamId)return {kind:'team',label:'Team',detail:'Agent team'};
  if(s.Harness==='workflow')return {kind:'team',label:'Team',detail:`Workflow team${s.Model?' · '+s.Model:''}`};
  if(s.Harness==='codex')return {kind:'gpt',label:'GPT',detail:s.Model?`Codex · ${s.Model}`:'Codex'};
  if(s.Harness==='claude')return {kind:'claude',label:'Claude',detail:s.Model&&s.Model!=='default'?`Claude Code · ${s.Model}`:'Claude Code'};
  if(s.Harness==='laica')return fromModel(s.Model,'API');
  if(/gemini/i.test(s.Harness))return {kind:'gemini',label:'Gemini',detail:harnessName(s.Harness)};
  return {kind:'other',label:harnessName(s.Harness).replace(/ CLI$| Code$/,'').slice(0,12),detail:harnessName(s.Harness)};
}
export const providerOfHistory=(h:Pick<HistoryItem,'Source'>):Provider=>h.Source==='claude'?{kind:'claude',label:'Claude',detail:'Claude Code'}:{kind:'gpt',label:'GPT',detail:'Codex'};

export function ProviderChip({p,className=''}:{p:Provider;className?:string}){
  if(p.duo)return <span className={`prov prov-duo ${className}`} title={p.detail}><b className={`prov-${p.duo.first.kind}${p.duo.active===0?' now':''}`}>{p.duo.first.label}</b><i>+</i><b className={`prov-${p.duo.second.kind}${p.duo.active===1?' now':''}`}>{p.duo.second.label}</b></span>;
  return <span className={`prov prov-${p.kind} ${className}`} title={p.detail}>{p.label}</span>;
}
