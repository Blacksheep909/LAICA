import {CopyBtn} from './Composer';
import type {ReactNode} from 'react';

function inline(text:string,key:string):ReactNode[]{
  const out:ReactNode[]=[];const re=/(`[^`]+`)|(\*\*[^*]+\*\*)|(\*[^*\s][^*]*\*)|(\[[^\]]+\]\((https?:\/\/[^)\s]+)\))/g;let last=0,m:RegExpExecArray|null,i=0;
  while((m=re.exec(text))){if(m.index>last)out.push(text.slice(last,m.index));const k=`${key}-${i++}`;
    if(m[1])out.push(<code key={k}>{m[1].slice(1,-1)}</code>);
    else if(m[2])out.push(<strong key={k}>{m[2].slice(2,-2)}</strong>);
    else if(m[3])out.push(<em key={k}>{m[3].slice(1,-1)}</em>);
    else if(m[4]){const label=m[4].slice(1,m[4].indexOf(']'));out.push(<span key={k} className="md-link" title={m[5]}>{label}</span>);}
    last=m.index+m[0].length;}
  if(last<text.length)out.push(text.slice(last));return out;
}

export function Markdown({text}:{text:string}){
  const lines=text.replace(/\r/g,'').split('\n');const nodes:ReactNode[]=[];let i=0,n=0;
  while(i<lines.length){const line=lines[i];const key=`b${n++}`;
    const fence=line.match(/^```(\w*)/);
    if(fence){const body:string[]=[];i++;while(i<lines.length&&!lines[i].startsWith('```')){body.push(lines[i]);i++;}i++;nodes.push(<pre key={key} className="md-code" data-lang={fence[1]}><code>{body.join('\n')}</code><CopyBtn text={body.join('\n')}/></pre>);continue;}
    const h=line.match(/^(#{1,4})\s+(.*)/);
    if(h){const level=h[1].length;const Tag=(`h${Math.min(level+1,5)}`) as 'h2';nodes.push(<Tag key={key} className="md-h">{inline(h[2],key)}</Tag>);i++;continue;}
    if(/^\s*[-*+]\s+/.test(line)){const items:string[]=[];while(i<lines.length&&/^\s*[-*+]\s+/.test(lines[i])){items.push(lines[i].replace(/^\s*[-*+]\s+/,''));i++;}nodes.push(<ul key={key}>{items.map((t,j)=><li key={j}>{inline(t,`${key}-${j}`)}</li>)}</ul>);continue;}
    if(/^\s*\d+[.)]\s+/.test(line)){const items:string[]=[];while(i<lines.length&&/^\s*\d+[.)]\s+/.test(lines[i])){items.push(lines[i].replace(/^\s*\d+[.)]\s+/,''));i++;}nodes.push(<ol key={key}>{items.map((t,j)=><li key={j}>{inline(t,`${key}-${j}`)}</li>)}</ol>);continue;}
    if(/^>\s?/.test(line)){const q:string[]=[];while(i<lines.length&&/^>\s?/.test(lines[i])){q.push(lines[i].replace(/^>\s?/,''));i++;}nodes.push(<blockquote key={key}>{inline(q.join(' '),key)}</blockquote>);continue;}
    if(/^\s*---+\s*$/.test(line)){nodes.push(<hr key={key}/>);i++;continue;}
    if(!line.trim()){i++;continue;}
    const para:string[]=[];while(i<lines.length&&lines[i].trim()&&!/^(```|#{1,4}\s|\s*[-*+]\s|\s*\d+[.)]\s|>)/.test(lines[i])){para.push(lines[i]);i++;}
    nodes.push(<p key={key}>{inline(para.join(' '),key)}</p>);}
  return <div className="md">{nodes}</div>;
}
