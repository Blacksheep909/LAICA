import {useEffect,useState} from 'react';
import type {ReactNode} from 'react';
import {Button,Glass} from 'open-glass-ui';
import {Lock} from 'lucide-react';
import {isRemote,hasToken,remoteLogin} from './bridge';

/** In the desktop window this renders its children directly. In a remote browser it asks for the password first. */
export default function RemoteGate({children}:{children:ReactNode}){
  const [authed,setAuthed]=useState(!isRemote||hasToken()),[password,setPassword]=useState(''),[error,setError]=useState(''),[busy,setBusy]=useState(false);
  useEffect(()=>{const lost=()=>setAuthed(false);window.addEventListener('laica-auth',lost);return()=>window.removeEventListener('laica-auth',lost);},[]);
  if(authed)return <>{children}</>;
  const submit=async()=>{setBusy(true);setError('');try{await remoteLogin(password);setPassword('');setAuthed(true);}catch(e){setError((e as Error).message);}finally{setBusy(false);}};
  return <div className="remote-login"><Glass material="regular" className="remote-card"><Lock size={22}/><h2>LAICA</h2><p>Enter the remote access password you set on the computer running LAICA.</p>
    <input className="hinput" type="password" autoFocus autoComplete="current-password" aria-label="Password" value={password} onChange={e=>setPassword(e.target.value)} onKeyDown={e=>{if(e.key==='Enter'&&password)submit();}} placeholder="Password"/>
    {error&&<p className="error-text">{error}</p>}<Button variant="primary" disabled={!password||busy} onClick={submit}>{busy?'Signing in…':'Sign in'}</Button></Glass></div>;
}
