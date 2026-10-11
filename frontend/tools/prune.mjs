import fs from 'fs';import {createRequire} from 'module';
const require=createRequire(import.meta.url);const postcss=require(process.argv[2]);let n=0;
for(const f of process.argv.slice(3)){const root=postcss.parse(fs.readFileSync(f,'utf8'));
 root.walkRules(r=>{if(!r.nodes.length){r.remove();n++;}});root.walkAtRules(a=>{if(a.nodes&&!a.nodes.length&&!/keyframes|font-face/i.test(a.name)){a.remove();n++;}});
 fs.writeFileSync(f,root.toString());}
console.log('removed',n,'empty rules');
