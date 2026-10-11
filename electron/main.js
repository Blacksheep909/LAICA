'use strict';
// LAICA for macOS and Linux. The engine (laica-server, .NET) runs next to this window and serves the same interface
// and commands the Windows app uses; this file starts it, signs the window in, and supplies the native dialogs.
const { app, BrowserWindow, Menu, dialog, ipcMain, shell, screen } = require('electron');
const { spawn } = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const net = require('net');
const path = require('path');

const EXTERNAL_HOSTS = ['github.com', 'learn.microsoft.com', 'ollama.com', 'lmstudio.ai'];
let engine = null;
let origin = '';
let token = '';
let win = null;
let quitting = false;

if (!app.requestSingleInstanceLock()) {
  app.quit();
}
app.on('second-instance', () => {
  if (win) {
    if (win.isMinimized()) win.restore();
    win.focus();
  }
});

function enginePath() {
  if (process.env.LAICA_SERVER) return process.env.LAICA_SERVER; // development: a laica-server.dll or executable
  const exe = process.platform === 'win32' ? 'laica-server.exe' : 'laica-server';
  return path.join(process.resourcesPath, 'server', exe);
}

function uiPath() {
  if (process.env.LAICA_UI) return process.env.LAICA_UI;
  return app.isPackaged ? path.join(process.resourcesPath, 'ui') : path.join(__dirname, '..', 'frontend', 'dist');
}

function freePort() {
  return new Promise((resolve, reject) => {
    const s = net.createServer();
    s.once('error', reject);
    s.listen(0, '127.0.0.1', () => {
      const { port } = s.address();
      s.close(() => resolve(port));
    });
  });
}

async function startEngine() {
  const port = await freePort();
  const password = crypto.randomBytes(24).toString('base64url');
  const bin = enginePath();
  const args = ['--ui', uiPath(), '--data', app.getPath('userData'), '--port', String(port), '--work', app.getPath('home')];
  const isDll = bin.endsWith('.dll');
  engine = spawn(isDll ? 'dotnet' : bin, isDll ? [bin, ...args] : args, {
    env: { ...process.env, LAICA_APP_PASSWORD: password },
    stdio: ['pipe', 'pipe', 'pipe'], // the engine stops when this end of its input closes
  });
  await new Promise((resolve, reject) => {
    let out = '';
    let err = '';
    const timer = setTimeout(() => reject(new Error('The engine did not start in time.\n' + err)), 45000);
    engine.stdout.on('data', (d) => {
      out += d;
      if (out.includes('READY')) {
        clearTimeout(timer);
        resolve();
      }
    });
    engine.stderr.on('data', (d) => (err += d));
    engine.once('error', (e) => {
      clearTimeout(timer);
      reject(new Error('Could not run the engine (' + bin + '): ' + e.message));
    });
    engine.once('exit', (code) => {
      clearTimeout(timer);
      reject(new Error('The engine stopped (' + code + ').\n' + err));
    });
  });
  engine.removeAllListeners('exit');
  engine.once('exit', onEngineExit);
  origin = 'http://127.0.0.1:' + port;
  const res = await fetch(origin + '/api/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ password }),
  });
  const body = await res.json();
  if (!res.ok || !body.token) throw new Error('The engine would not sign this window in.');
  token = body.token;
}

function onEngineExit(code) {
  engine = null;
  if (quitting) return;
  const pick = dialog.showMessageBoxSync({
    type: 'error',
    message: 'LAICA\'s engine stopped (' + code + ').',
    detail: 'Your chats are saved. Restart to continue.',
    buttons: ['Restart', 'Quit'],
    defaultId: 0,
  });
  if (pick === 0) {
    app.relaunch();
  }
  app.quit();
}

function externalUrl(raw) {
  try {
    const u = new URL(raw);
    return u.protocol === 'https:' || u.protocol === 'http:' ? u.toString() : null;
  } catch (e) {
    return null;
  }
}

function boundsFile() {
  return path.join(app.getPath('userData'), 'window.json');
}

function loadBounds() {
  try {
    const b = JSON.parse(fs.readFileSync(boundsFile(), 'utf8'));
    const ok = screen.getAllDisplays().some((d) => {
      const a = d.workArea;
      return b.x >= a.x - 40 && b.y >= a.y - 40 && b.x < a.x + a.width - 100 && b.y < a.y + a.height - 100;
    });
    if (ok && b.width >= 900 && b.height >= 600) return b;
  } catch (e) {
    /* first run */
  }
  return { width: 1360, height: 860 };
}

function windowState() {
  return { Maximized: win.isMaximized(), Fullscreen: win.isFullScreen() };
}

function createWindow() {
  const mac = process.platform === 'darwin';
  win = new BrowserWindow({
    ...loadBounds(),
    minWidth: 900,
    minHeight: 600,
    show: false,
    backgroundColor: '#0b0d12',
    title: 'LAICA',
    frame: mac,
    titleBarStyle: mac ? 'hiddenInset' : undefined,
    trafficLightPosition: mac ? { x: 16, y: 13 } : undefined,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false, // the preload needs webUtils; it never loads remote code
      spellcheck: true,
    },
  });
  win.once('ready-to-show', () => win.show());
  const push = () => win && !win.isDestroyed() && win.webContents.send('laica:window', windowState());
  for (const e of ['maximize', 'unmaximize', 'enter-full-screen', 'leave-full-screen']) win.on(e, push);
  win.on('close', () => {
    try {
      if (!win.isMaximized() && !win.isFullScreen()) fs.writeFileSync(boundsFile(), JSON.stringify(win.getBounds()));
    } catch (e) {
      /* not worth stopping a close for */
    }
  });
  win.on('closed', () => (win = null));
  win.webContents.setWindowOpenHandler(({ url }) => {
    const u = externalUrl(url);
    if (u) shell.openExternal(u);
    return { action: 'deny' };
  });
  win.webContents.on('will-navigate', (e, url) => {
    if (new URL(url).origin === origin) return;
    e.preventDefault();
    const u = externalUrl(url);
    if (u) shell.openExternal(u);
  });
  win.loadURL(origin + '/');
}

const rpc = {
  async windowControl({ Action }) {
    const wc = win.webContents;
    switch (Action) {
      case 'minimize':
        win.minimize();
        break;
      case 'maximize':
        if (win.isFullScreen()) win.setFullScreen(false);
        else if (win.isMaximized()) win.unmaximize();
        else win.maximize();
        break;
      case 'fullscreen':
        win.setFullScreen(!win.isFullScreen());
        break;
      case 'close':
        win.close();
        break;
      case 'exit':
        app.quit();
        break;
      case 'zoomIn':
        wc.setZoomFactor(Math.min(3, wc.getZoomFactor() + 0.1));
        break;
      case 'zoomOut':
        wc.setZoomFactor(Math.max(0.5, wc.getZoomFactor() - 0.1));
        break;
      case 'zoomReset':
        wc.setZoomFactor(1);
        break;
      default: // 'drag' is handled by the window itself
        break;
    }
    return windowState();
  },
  async pickFolder() {
    const r = await dialog.showOpenDialog(win, {
      title: 'Choose a project folder',
      properties: ['openDirectory', 'createDirectory'],
    });
    return r.canceled ? null : r.filePaths[0];
  },
  async pickFiles() {
    const r = await dialog.showOpenDialog(win, { title: 'Attach files', properties: ['openFile', 'multiSelections'] });
    return r.canceled ? null : r.filePaths;
  },
  async revealPath({ Path }) {
    if (!fs.existsSync(Path)) return true;
    if (fs.statSync(Path).isDirectory()) await shell.openPath(Path);
    else shell.showItemInFolder(Path);
    return true;
  },
  async saveText({ Name, Text }) {
    const r = await dialog.showSaveDialog(win, {
      defaultPath: Name,
      filters: [
        { name: 'Markdown', extensions: ['md'] },
        { name: 'Text file', extensions: ['txt'] },
      ],
    });
    if (r.canceled || !r.filePath) return null;
    fs.writeFileSync(r.filePath, String(Text), 'utf8');
    return { Message: 'Saved.' };
  },
  async openLink({ Url }) {
    const u = externalUrl(Url);
    if (u && new URL(u).protocol === 'https:' && EXTERNAL_HOSTS.some((h) => new URL(u).hostname === h)) {
      await shell.openExternal(u);
    }
    return null;
  },
};

ipcMain.on('laica:token', (e) => {
  e.returnValue = token;
});
ipcMain.handle('laica:rpc', (e, method, payload) => {
  if (!win || e.sender !== win.webContents || !rpc[method]) throw new Error('Unknown command ' + method);
  return rpc[method](payload || {});
});

function buildMenu() {
  const mac = process.platform === 'darwin';
  const template = [
    ...(mac ? [{ role: 'appMenu' }] : []),
    { role: 'fileMenu' },
    { role: 'editMenu' },
    {
      label: 'View',
      submenu: [
        { role: 'reload' },
        { role: 'toggleDevTools' },
        { type: 'separator' },
        { role: 'resetZoom' },
        { role: 'zoomIn', accelerator: 'CommandOrControl+=' },
        { role: 'zoomOut' },
        { type: 'separator' },
        { role: 'togglefullscreen' },
      ],
    },
    { role: 'windowMenu' },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

app.on('before-quit', () => {
  quitting = true;
  if (engine) {
    try {
      engine.stdin.end(); // asking the engine to finish; it stops when its input closes
    } catch (e) {
      /* already gone */
    }
    const e = engine;
    setTimeout(() => e.kill(), 4000).unref();
  }
});
app.on('window-all-closed', () => {
  app.quit();
});
app.on('activate', () => {
  if (!win && origin) createWindow();
});

app.whenReady().then(async () => {
  buildMenu();
  try {
    await startEngine();
  } catch (e) {
    dialog.showErrorBox('LAICA could not start', String(e.message || e));
    quitting = true;
    app.quit();
    return;
  }
  createWindow();
});
