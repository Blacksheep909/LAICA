// One-off: removes every `!important` from the stylesheets (the cascade is now ordered by @layer instead).
import fs from 'fs';import {createRequire} from 'module';
const require=createRequire(import.meta.url);
const postcss=require(process.argv[2]);
let total=0;
for(const f of process.argv.slice(3)){
  const css=fs.readFileSync(f,'utf8');const root=postcss.parse(css);let n=0;
  root.walkDecls(d=>{if(d.important){d.important=false;n++;}});
  fs.writeFileSync(f,root.toString());console.log(f,n);total+=n;
}
console.log('total',total);
