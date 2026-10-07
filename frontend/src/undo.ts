/* One undo stack for the whole app. Anything destructive (closing a chat, removing a project, deleting a team, dropping an attachment,
   renaming) pushes an entry; Ctrl+Z, or the Undo button on the toast, takes the newest one back. */
export interface UndoEntry { id:number; label:string; undo:()=>void|Promise<void> }
const stack:UndoEntry[]=[];let next=1;
const listeners=new Set<()=>void>();
const changed=()=>listeners.forEach(l=>l());

export function pushUndo(label:string,undo:()=>void|Promise<void>):number{
  const id=next++;stack.push({id,label,undo});if(stack.length>40)stack.shift();changed();return id;
}
/** Runs one entry (the newest by default). Returns its label, or null when there was nothing to undo. */
export async function runUndo(id?:number):Promise<string|null>{
  const i=id===undefined?stack.length-1:stack.findIndex(e=>e.id===id);
  if(i<0)return null;const [e]=stack.splice(i,1);changed();await e.undo();return e.label;
}
export const undoDepth=()=>stack.length;
export const lastUndoLabel=()=>stack[stack.length-1]?.label??'';
export function onUndoChange(fn:()=>void){listeners.add(fn);return()=>{listeners.delete(fn);};}
