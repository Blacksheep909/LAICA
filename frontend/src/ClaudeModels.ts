import {useEffect,useState} from 'react';
import {request} from './bridge';

export interface ClaudeChoice { value:string; label:string; description:string }
interface PriceState { Official?:{Model:string}[] }
let cache:Promise<ClaudeChoice[]>|null=null;
const fallback:ClaudeChoice[]=[{value:'default',label:'Claude Code default',description:'The model your Claude Code is set to use'},{value:'opus',label:'Claude Opus',description:'Latest Opus'},{value:'sonnet',label:'Claude Sonnet',description:'Latest Sonnet'},{value:'haiku',label:'Claude Haiku',description:'Latest Haiku'}];
const nice=(s:string)=>s.charAt(0).toUpperCase()+s.slice(1);

/** The Claude models by name and version. The versions come from the prices LAICA verified against Anthropic's own pricing page; until that has run the plain names are shown. */
function load():Promise<ClaudeChoice[]>{
  if(!cache)cache=request<PriceState>('pricesGet',{}).then(s=>{
    const best:Record<string,{v:number[];id:string}>={};
    for(const o of s.Official??[]){const m=/^claude-(opus|sonnet|haiku|fable|mythos)-(\d+)(?:-(\d+))?$/.exec(o.Model);if(!m)continue;const v=[Number(m[2]),Number(m[3]??0)];const cur=best[m[1]];if(!cur||v[0]>cur.v[0]||(v[0]===cur.v[0]&&v[1]>cur.v[1]))best[m[1]]={v,id:o.Model};}
    const ver=(f:string)=>best[f]?` ${best[f].v[0]}${best[f].v[1]?'.'+best[f].v[1]:''}`:'';
    if(!Object.keys(best).length){cache=null;return fallback;}
    const out:ClaudeChoice[]=[{value:'default',label:'Claude Code default',description:'The model your Claude Code is set to use'}];
    for(const f of ['fable','opus','sonnet','haiku'])if(best[f]||['opus','sonnet','haiku'].includes(f))out.push({value:f==='fable'?best[f].id:f,label:`Claude ${nice(f)}${ver(f)}`,description:best[f]?best[f].id:'Latest '+nice(f)});
    return out;
  }).catch(()=>fallback);
  return cache;
}
export function useClaudeModels():ClaudeChoice[]{
  const [list,setList]=useState<ClaudeChoice[]>(fallback);
  useEffect(()=>{let live=true;void load().then(l=>{if(live)setList(l);});return()=>{live=false;};},[]);
  return list;
}