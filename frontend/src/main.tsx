import React from 'react';
import {createRoot} from 'react-dom/client';
import {GlassSystemProvider} from 'open-glass-ui';
import 'open-glass-ui/styles.css';
import './workspace.css';
import App from './App';
createRoot(document.getElementById('root')!).render(<React.StrictMode><GlassSystemProvider renderer="auto" motion="system" theme={{appearance:'dark',theme:{accent:'#b3a5f7',contrast:'high',glass:'smoked'}}} toasts={{maxVisible:2,defaultDuration:4500}}><App/></GlassSystemProvider></React.StrictMode>);
