// Removes declarations that can never apply: the same selector, in the same at-rule, sets the same property again later in the cascade.
import fs from 'fs';import {createRequire} from 'module';
const require=createRequire(import.meta.url);const postcss=require(process.argv[2]);
const files=process.argv.slice(3);
const roots=files.map(f=>({f,root:postcss.parse(fs.readFileSync(f,'utf8'))}));
const ctx=r=>{const a=[];let p=r.parent;while(p&&p.type==='atrule'){a.unshift('@'+p.name+' '+p.params);p=p.parent;}return a.join(' > ');};
const norm=s=>s.replace(/\s+/g,' ').replace(/\s*([,>+~])\s*/g,'$1').trim();
const later=new Map();let removed=0,bytes=0;
for(let i=roots.length-1;i>=0;i--){
  const rules=[];roots[i].root.walkRules(r=>{if(r.parent?.type==='atrule'&&/keyframes|font-face/i.test(r.parent.name))return;rules.push(r);});
  for(let j=rules.length-1;j>=0;j--){
    const r=rules[j];const key=ctx(r)+'||'+norm(r.selector);let set=later.get(key);if(!set){set=new Set();later.set(key,set);}
    const mine=[];
    r.each(n=>{if(n.type!=='decl')return;if(set.has(n.prop)){bytes+=n.toString().length;removed++;n.remove();}else mine.push(n.prop);});
    for(const p of mine)set.add(p);
    if(!r.nodes.length)r.remove();
  }
  roots[i].root.walkAtRules(a=>{if(a.nodes&&!a.nodes.length&&!/keyframes|font-face/i.test(a.name))a.remove();});
}
for(const {f,root} of roots)fs.writeFileSync(f,root.toString());
console.log('removed',removed,'declarations,',bytes,'bytes');
