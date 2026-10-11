'use strict';
// Runs before the page. It hands the page the engine's session token and the few native things a browser cannot do.
const { contextBridge, ipcRenderer, webUtils } = require('electron');

const handles = ['windowControl', 'pickFolder', 'pickFiles', 'revealPath', 'saveText', 'openLink'];

try {
  sessionStorage.setItem('laica-remote-token', ipcRenderer.sendSync('laica:token'));
} catch (e) {
  /* the page shows its sign-in screen instead */
}

contextBridge.exposeInMainWorld('laicaShell', {
  platform: process.platform,
  handles,
  invoke: (method, payload) => ipcRenderer.invoke('laica:rpc', method, payload),
  onWindow: (cb) => {
    const fn = (_e, state) => cb(state);
    ipcRenderer.on('laica:window', fn);
    return () => ipcRenderer.removeListener('laica:window', fn);
  },
  pathsFor: (files) => files.map((f) => webUtils.getPathForFile(f)).filter(Boolean),
});
