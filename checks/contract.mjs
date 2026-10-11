// The interface and the engine talk through named commands. This checks both sides agree: every command the interface sends is one the
// engine (or the Windows window) handles, and no command is handled that nothing sends.
import fs from 'fs';
const read=f=>fs.readFileSync(f,'utf8');
const files=fs.readdirSync('frontend/src').filter(f=>/\.(ts|tsx)$/.test(f)).map(f=>'frontend/src/'+f);
const sent=new Set();
for(const f of files){for(const m of read(f).matchAll(/\brequest(?:<[^>(]*(?:<[^>]*>)?[^>(]*>)?\(\s*['"`]([A-Za-z0-9_]+)['"`]/g))sent.add(m[1]);}
const handled=new Set();
for(const m of read('host/WorkspaceBackend.cs').matchAll(/case "([A-Za-z0-9_]+)":/g))handled.add(m[1]);
const windowOnly=new Set();
for(const m of read('host/GlassHost.cs').matchAll(/method\s*==\s*"([A-Za-z0-9_]+)"/g)){handled.add(m[1]);windowOnly.add(m[1]);}
for(const m of read('host/GlassHost.cs').matchAll(/case "([A-Za-z0-9_]+)":/g)){handled.add(m[1]);windowOnly.add(m[1]);}
const missing=[...sent].filter(x=>!handled.has(x)).sort();
const unused=[...handled].filter(x=>!sent.has(x)).sort();
console.log(`${sent.size} commands sent, ${handled.size} handled`);
if(missing.length)console.log('SENT BUT NOT HANDLED:',missing.join(', '));
if(unused.length)console.log('HANDLED BUT NEVER SENT:',unused.join(', '));
process.exit(missing.length?1:0);
