import type {ReactNode} from 'react';

/** Anything in the app that names a file can call this: the chat opens its side panel on that file. */
export const openFile=(path:string)=>window.dispatchEvent(new CustomEvent('laica-open-file',{detail:{path}}));
const EXT=/\.(ts|tsx|js|jsx|mjs|cjs|cs|py|md|json|css|scss|html|htm|txt|yml|yaml|toml|cpp|c|h|hpp|rs|go|java|kt|swift|sh|ps1|bat|csproj|sln|xml|svg|sql|ini|cfg|lock|gradle|rb|php|vue|svelte|csv|log|png|jpg|jpeg|gif|webp|pdf)$/i;
/** True for text that reads as a file path or name ("src/App.tsx", "HANDOFF.md"), so it can be offered as a link. */
export const looksLikeFile=(s:string)=>{const t=s.trim();return t.length>2&&t.length<260&&!/\s{2,}|[<>|*?"`]/.test(t)&&!/^https?:/i.test(t)&&EXT.test(t);};

export function FileLink({path,children,className}:{path:string;children?:ReactNode;className?:string}){
  return <button type="button" className={'file-link '+(className??'')} title={'Open '+path+' in the side panel'} onClick={e=>{e.preventDefault();e.stopPropagation();openFile(path);}}>{children??path}</button>;
}