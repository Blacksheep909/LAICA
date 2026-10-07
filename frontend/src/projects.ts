import type {HSession,HistoryItem,CodexProject} from './harness-store';

export interface ProjectEntry { path:string; name:string; count:number; saved:boolean; recent:number; codex:boolean }
const folderName=(p:string)=>{const parts=p.split(/[\\/]/).filter(Boolean);return parts[parts.length-1]??p;};
const norm=(p:string)=>p.replace(/[\\/]+$/,'').toLowerCase();
const under=(path:string,root:string)=>{const a=norm(path),b=norm(root);return !!b&&(a===b||a.startsWith(b+'\\')||a.startsWith(b+'/'));};
const squash=(s:string)=>s.toLowerCase().replace(/[^a-z0-9]/g,'');
/** Codex keeps its own copies of projects in ~/.codex/worktrees/<id>/<name>. */
export const worktreeLeaf=(p:string)=>p.match(/[\\/]\.codex[\\/]worktrees[\\/][^\\/]+[\\/]([^\\/]+)/i)?.[1]??'';

/** Which of the user's projects a folder belongs to ('' when it belongs to none): Codex's own projects first, then saved ones, then the folder's own name. */
export function projectOf(path:string,codex:CodexProject[],saved:string[]):string{
  if(!path)return '';
  for(const p of codex)if(p.Roots.some(r=>under(path,r)))return p.Name;
  const leaf=worktreeLeaf(path);
  if(leaf)for(const p of codex)if(squash(p.Name)===squash(leaf)||p.Roots.some(r=>squash(folderName(r))===squash(leaf)))return p.Name;
  for(const s of saved)if(under(path,s))return folderName(s);
  return '';
}

/**
 * The project list: exactly the projects Codex shows (names and order), then projects you added, then folders used by LAICA chats
 * and Claude Code sessions. Codex conversations that Codex itself keeps outside any project are not turned into projects.
 */
export function buildProjects(saved:string[],sessions:HSession[],history:HistoryItem[],codex:CodexProject[]=[]):ProjectEntry[]{
  const map=new Map<string,ProjectEntry>();const order:string[]=[];
  const add=(name:string,path:string,isSaved:boolean,isCodex:boolean,when:number,n:number)=>{
    const key=name.toLowerCase();let e=map.get(key);
    if(!e){e={path,name,count:0,saved:isSaved,recent:when,codex:isCodex};map.set(key,e);order.push(key);}
    e.count+=n;e.saved=e.saved||isSaved;e.codex=e.codex||isCodex;if(when>e.recent&&!isCodex)e.recent=when;if(isSaved||(isCodex&&!e.codex))e.path=path;
  };
  codex.forEach((p,i)=>add(p.Name,p.Path,false,true,Number.MAX_SAFE_INTEGER-i,0));
  saved.forEach(p=>add(projectOf(p,codex,[])||folderName(p),p,true,false,Number.MAX_SAFE_INTEGER/2,0));
  sessions.forEach(s=>{const path=s.Project||s.Cwd;const name=projectOf(path,codex,saved)||folderName(path);if(!s.TeamId)add(name,path,false,false,Date.now(),1);});
  history.forEach(h=>{if(h.ProjectName)add(h.ProjectName,h.Project,false,h.Source==='codex',new Date(h.UpdatedUtc).getTime(),1);});
  return order.map(k=>map.get(k)!).sort((a,b)=>b.recent-a.recent);
}
