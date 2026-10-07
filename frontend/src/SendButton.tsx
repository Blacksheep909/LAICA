import {ArrowUp,Square} from 'lucide-react';

/** The round lavender send button. While the agent works it becomes a rounded square with a stop glyph. */
export default function SendButton({busy,disabled,onSend,onStop,label='Send'}:{busy?:boolean;disabled?:boolean;onSend:()=>void;onStop?:()=>void;label?:string}){
  const stop=!!busy&&!!onStop;
  return <button type="button" className={`send-btn ${stop?'is-stop':''}`} aria-label={stop?'Stop':label} title={stop?'Stop':label} disabled={!stop&&disabled} onClick={stop?onStop:onSend}>
    {stop?<Square size={13} fill="currentColor" strokeWidth={0}/>:<ArrowUp size={18} strokeWidth={2.4}/>}
  </button>;
}