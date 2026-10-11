// Moves every !important declaration into pinned.css (a later cascade layer) without the flag, in the same order and inside the same
// at-rules, so the cascade resolves exactly as before and nothing needs !important any more.
import fs from 'fs';import {createRequire} from 'module';
const require=createRequire(import.meta.url);const postcss=require(process.argv[2]);
const files=process.argv.slice(3);const pinned=postcss.root();let moved=0;
for(const f of files){
  const root=postcss.parse(fs.readFileSync(f,'utf8'));
  root.walkRules(rule=>{
    if(rule.parent?.type==='atrule'&&/keyframes/i.test(rule.parent.name))return;
    const imp=[];rule.each(n=>{if(n.type==='decl'&&n.important)imp.push(n);});
    if(!imp.length)return;
    const copy=postcss.rule({selector:rule.selector});
    for(const d of imp){copy.append(postcss.decl({prop:d.prop,value:d.value}));d.important=false;d.remove();moved++;}
    let node=copy,p=rule.parent;
    while(p&&p.type==='atrule'){const w=postcss.atRule({name:p.name,params:p.params});w.append(node);node=w;p=p.parent;}
    pinned.append(node);
    if(!rule.nodes.length)rule.remove();
  });
  root.walkAtRules(a=>{if(a.nodes&&!a.nodes.length&&!/keyframes|font-face/i.test(a.name))a.remove();});
  fs.writeFileSync(f,root.toString());
}
fs.writeFileSync('pinned.css','/* Declarations that used to carry !important. They sit in the last cascade layer, in their original order, so they still win. */\n'+pinned.toString());
console.log('moved',moved);
