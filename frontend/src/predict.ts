import type {HEvent} from './harness-store';

/** What the user will most likely type next, guessed from the agent's last message. It only speaks when the agent has just asked something
    it can answer with a plain yes ("Want me to add a high-contrast option too?"), so a suggestion is never noise. Works for every agent. */
const strip=(t:string)=>t.replace(/```[\s\S]*?```/g,' ').replace(/`/g,'').replace(/\*\*|__/g,'').replace(/\s+/g,' ').trim();
const lastSentences=(t:string)=>{const s=strip(t);const parts=s.split(/(?<=[.!?])\s+/);return parts.slice(-2).join(' ');};
const clause=(s:string)=>{const c=s.replace(/\s*\?+$/,'').replace(/\s+(too|as well|also)$/i,' $1').trim();return c.charAt(0).toLowerCase()+c.slice(1);};
const tidy=(c:string)=>'Yes, '+c.replace(/\bmy\b/gi,'the').replace(/\bI['’]ll\b/g,'').trim().replace(/\s+/g,' ')+'.';

export function predictNext(text:string):string{
  const tail=lastSentences(text);if(!/\?\s*$/.test(tail))return /(let me know when|say the word|ready to (?:proceed|continue|go))[^?]*[.!]?\s*$/i.test(strip(text).slice(-160))?'Go ahead.':'';
  const q=tail.slice(tail.search(/[A-Z][^.!?]*\?\s*$/));
  let m=/(?:do you )?(?:want|like) me to (.+?)\?/i.exec(q)||/would you like me to (.+?)\?/i.exec(q)||/(?:should|shall|can|could|may) I (.+?)\?/i.exec(q)||/(?:do you want|would you like) (?:to )?(.+?)\?/i.exec(q);
  if(m&&m[1].length>3&&m[1].length<140)return tidy(clause(m[1]));
  if(/^(ready|ok|okay|sound good|does that (work|look)|is that (ok|okay|right|correct))/i.test(q)||/\b(sound good|look good|work for you|okay with that|ok with that)\?\s*$/i.test(q))return 'Yes, go ahead.';
  return '';
}

/** The suggestion for the end of a finished turn, or '' while the agent is working or the user has already typed. */
export function predictionFor(events:HEvent[],busy:boolean):{text:string;key:string}{
  if(busy)return {text:'',key:''};
  for(let i=events.length-1;i>=0;i--){
    const e=events[i];
    if(e.Kind==='user')return {text:'',key:''};
    if(e.Kind==='assistant'&&e.Text){const t=predictNext(e.Text);return {text:t,key:e.TimeUtc+t};}
  }
  return {text:'',key:''};
}
