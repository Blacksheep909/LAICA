// Snaps the app's near-duplicate values (28 font sizes, 22 radii, 39 white tints, 10 durations) onto small scales and writes them as custom properties.
import fs from 'fs';import {createRequire} from 'module';
const require=createRequire(import.meta.url);const postcss=require(process.argv[2]);
const FONT=[[10,'2xs'],[11,'xs'],[12,'sm'],[13,'base'],[14,'md'],[16,'lg'],[18,'xl'],[20,'2xl'],[24,'3xl'],[32,'4xl']];
const RADIUS=[[4,'xs'],[6,'sm'],[8,'md'],[10,'lg'],[12,'xl'],[14,'2xl'],[16,'3xl'],[20,'4xl'],[24,'5xl']];
const WHITE=[[.04,'04'],[.06,'06'],[.08,'08'],[.12,'12'],[.16,'16'],[.24,'24']];
const TIME=[[.15,'fast'],[.25,'base'],[.4,'slow']];
const near=(v,scale,roundDown)=>{let best=scale[0],d=1e9;for(const s of scale){const e=Math.abs(s[0]-v);if(e<d-1e-9||(Math.abs(e-d)<1e-9&&!roundDown)){best=s;d=e;}}return best;};
const used={font:new Set(),radius:new Set(),white:new Set(),time:new Set()};
const files=process.argv.slice(3);let n=0;
for(const f of files){
  const root=postcss.parse(fs.readFileSync(f,'utf8'));
  root.walkDecls(d=>{
    if(d.parent?.parent?.type==='atrule'&&/keyframes/i.test(d.parent.parent.name))return;
    const before=d.value;let v=d.value;
    if(d.prop==='font-size'&&/^[\d.]+px$/.test(v)){const px=parseFloat(v);if(px<=32){const s=near(px,FONT,true);used.font.add(s[1]);v=`var(--text-${s[1]})`;}}
    if(/^border(-[a-z]+)*-radius$/.test(d.prop)&&!v.includes('var(')){v=v.replace(/(^|\s)([\d.]+)px(?=\s|$)/g,(m,pre,num)=>{const px=parseFloat(num);if(px>=90||px<=0)return m;const s=near(px,RADIUS,false);used.radius.add(s[1]);return `${pre}var(--radius-${s[1]})`;});}
    if(!/^--/.test(d.prop)||true){v=v.replace(/rgba\(\s*255\s*,\s*255\s*,\s*255\s*,\s*(0?\.\d+)\s*\)/g,(m,a)=>{const al=parseFloat(a);if(al>0.25)return m;const s=near(al,WHITE,false);used.white.add(s[1]);return `var(--white-${s[1]})`;});}
    if(/^transition(-duration)?$/.test(d.prop)){v=v.replace(/(^|[\s,])(0?\.\d+)s(?=[\s,]|$)/g,(m,pre,num)=>{const t=parseFloat(num);if(t>0.5)return m;const s=near(t,TIME,false);used.time.add(s[1]);return `${pre}var(--time-${s[1]})`;});}
    if(v!==before){d.value=v;n++;}
  });
  fs.writeFileSync(f,root.toString());
}
const lines=['/* Design tokens: the only place a size, radius, tint or duration is defined. */',':root{'];
lines.push('  /* type scale */');for(const [px,k] of FONT)if(used.font.has(k))lines.push(`  --text-${k}: ${px}px;`);
lines.push('  /* corner radii; pills use 999px */');for(const [px,k] of RADIUS)if(used.radius.has(k))lines.push(`  --radius-${k}: ${px}px;`);
lines.push('  /* white tints on dark glass, from faint fill to bright edge */');for(const [a,k] of WHITE)if(used.white.has(k))lines.push(`  --white-${k}: rgba(255, 255, 255, ${a});`);
lines.push('  /* motion */');for(const [t,k] of TIME)if(used.time.has(k))lines.push(`  --time-${k}: ${t}s;`);
lines.push('}','');fs.writeFileSync('src/tokens.css',lines.join('\n'));
console.log('rewrote',n,'declarations');
