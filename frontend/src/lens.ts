/* Liquid Glass lensing for the browser.
   Real glass bends what is behind it, most strongly at its rounded bezel. We build a displacement map for each floating glass
   element (a convex bezel profile around its rounded rectangle), publish it as an SVG filter, and point the element's
   backdrop-filter at it. Chromium (and so WebView2) supports SVG filters inside backdrop-filter; elsewhere the CSS falls back to blur.
   Only small floating surfaces get lensing: it is the edge that sells the glass, and big panels would be expensive. */
const NS='http://www.w3.org/2000/svg';
const SELECTOR='[data-lens],.harness-meta,.harness-compose,.composer,.palette,.usage-pop,.ogui-toast,.ogui-dialog,.menu-pop,.gs-pop';
const MAX_AREA=1000*760,STEP=16,KEEP=70;
let svg:SVGSVGElement|null=null,defs:SVGDefsElement|null=null;
const made=new Map<string,string>();           // key -> filter id (insertion ordered, used as a small LRU)
const watched=new WeakSet<Element>();
let ro:ResizeObserver|null=null;

const sd=(x:number,y:number,w:number,h:number,r:number)=>{const qx=Math.abs(x-w/2)-(w/2-r),qy=Math.abs(y-h/2)-(h/2-r);return Math.hypot(Math.max(qx,0),Math.max(qy,0))+Math.min(Math.max(qx,qy),0)-r;};

/** R and G encode the push direction (128 = none). The push points inward and is strongest at the very edge, easing to zero at `bezel`. */
function buildMap(w:number,h:number,r:number,bezel:number):string{
  const c=document.createElement('canvas');c.width=w;c.height=h;const g=c.getContext('2d');if(!g)return '';
  const img=g.createImageData(w,h);const d=img.data;
  for(let y=0;y<h;y++)for(let x=0;x<w;x++){
    const px=x+.5,py=y+.5,dist=-sd(px,py,w,h,r);let R=128,G=128;
    if(dist>=0&&dist<bezel){
      const t=1-dist/bezel,m=t*t*(3-2*t);                                    // smoothstep: soft falloff into the flat centre
      const nx=sd(px+1,py,w,h,r)-sd(px-1,py,w,h,r),ny=sd(px,py+1,w,h,r)-sd(px,py-1,w,h,r),len=Math.hypot(nx,ny)||1;
      R=Math.round(128-127*m*(nx/len));G=Math.round(128-127*m*(ny/len));
    }
    const i=(y*w+x)*4;d[i]=R;d[i+1]=G;d[i+2]=128;d[i+3]=255;
  }
  g.putImageData(img,0,0);return c.toDataURL('image/png');
}

function host(){
  if(svg)return defs!;
  svg=document.createElementNS(NS,'svg');svg.setAttribute('width','0');svg.setAttribute('height','0');svg.setAttribute('aria-hidden','true');
  svg.style.cssText='position:absolute;width:0;height:0;pointer-events:none';defs=document.createElementNS(NS,'defs');svg.appendChild(defs);document.body.appendChild(svg);return defs;
}

export function lensFor(w:number,h:number,r:number):string{
  const key=`${w}x${h}r${Math.round(r)}`;let id=made.get(key);
  if(id){made.delete(key);made.set(key,id);return `url(#${id})`;}
  const bezel=Math.max(8,Math.min(26,Math.floor(Math.min(w,h)/2)-2)),scale=Math.min(46,10+bezel*1.5);
  const url=buildMap(w,h,Math.min(r,Math.min(w,h)/2),bezel);if(!url)return 'blur(0px)';
  id='lens-'+key.replace(/[^a-z0-9]/gi,'_');
  const f=document.createElementNS(NS,'filter');f.setAttribute('id',id);f.setAttribute('x','0');f.setAttribute('y','0');f.setAttribute('width',String(w));f.setAttribute('height',String(h));
  f.setAttribute('filterUnits','userSpaceOnUse');f.setAttribute('primitiveUnits','userSpaceOnUse');f.setAttribute('color-interpolation-filters','sRGB');
  const img=document.createElementNS(NS,'feImage');img.setAttribute('href',url);img.setAttribute('x','0');img.setAttribute('y','0');img.setAttribute('width',String(w));img.setAttribute('height',String(h));img.setAttribute('preserveAspectRatio','none');img.setAttribute('result','map');
  const disp=document.createElementNS(NS,'feDisplacementMap');disp.setAttribute('in','SourceGraphic');disp.setAttribute('in2','map');disp.setAttribute('scale',String(scale));disp.setAttribute('xChannelSelector','R');disp.setAttribute('yChannelSelector','G');
  f.append(img,disp);host().appendChild(f);made.set(key,id);
  if(made.size>KEEP){const [oldKey,oldId]=made.entries().next().value as [string,string];made.delete(oldKey);defs?.querySelector('#'+oldId)?.remove();}
  return `url(#${id})`;
}

function apply(el:HTMLElement){
  if(window.matchMedia('(prefers-reduced-transparency: reduce)').matches||document.querySelector('.solid-material')){el.style.removeProperty('--lens');return;}
  const r=el.getBoundingClientRect(),w=Math.ceil(r.width/STEP)*STEP,h=Math.ceil(r.height/STEP)*STEP;
  if(r.width<48||r.height<32||r.width*r.height>MAX_AREA){el.style.removeProperty('--lens');return;}
  const radius=parseFloat(getComputedStyle(el).borderTopLeftRadius)||20;
  el.style.setProperty('--lens',lensFor(w,h,radius));
}

function watch(el:Element){
  if(watched.has(el)||!(el instanceof HTMLElement))return;watched.add(el);el.setAttribute('data-lens','');ro?.observe(el);apply(el);
}
function scan(root:ParentNode){
  if(root instanceof Element&&root.matches(SELECTOR))watch(root);
  root.querySelectorAll?.(SELECTOR).forEach(watch);
}

export function installLens(){
  if(typeof window==='undefined'||!('CSS' in window)||!CSS.supports('backdrop-filter','blur(1px)'))return;
  let pending=0;const queue=new Set<HTMLElement>();
  const flush=()=>{pending=0;queue.forEach(apply);queue.clear();};
  ro=new ResizeObserver(entries=>{entries.forEach(e=>queue.add(e.target as HTMLElement));if(!pending)pending=requestAnimationFrame(flush);});
  new MutationObserver(list=>{for(const m of list)m.addedNodes.forEach(n=>{if(n instanceof Element)scan(n);});}).observe(document.body,{childList:true,subtree:true});
  scan(document.body);
  // a light that follows the pointer: every glass surface uses --px/--py in its sheen
  let raf=0,x=0,y=0;const root=document.documentElement;
  document.addEventListener('pointermove',e=>{x=e.clientX;y=e.clientY;if(!raf)raf=requestAnimationFrame(()=>{raf=0;root.style.setProperty('--px',x+'px');root.style.setProperty('--py',y+'px');});},{passive:true});
}
