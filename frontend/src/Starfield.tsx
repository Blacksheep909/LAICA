import {useEffect,useRef} from 'react';

interface Speck { x:number; y:number; base:number; size:number; phase:number; speed:number; tint:number }

// Modelled on the Firefox "Dark space full transparent" theme: pure black, a dense field of 1px specks that never move.
// Nearly every speck slowly fades out and back in on its own cycle (a few seconds to ~12 s), so the sky shimmers in place.
// Most specks are very faint; many are tinted warm orange or cool blue instead of white.
const TINTS=[[255,255,255],[255,255,255],[255,255,255],[255,255,255],[255,192,132],[255,192,132],[255,210,160],[150,200,255],[170,210,255],[200,172,255],[130,232,220]];
const LEVELS=24;

export default function Starfield({paused}:{paused:boolean}){
  const ref=useRef<HTMLCanvasElement>(null);
  useEffect(()=>{
    const canvas=ref.current;if(!canvas)return;const ctx=canvas.getContext('2d');if(!ctx)return;
    // the in-app 'Animate the background' switch decides; the OS flag is ignored because Windows with 'Show animations' off reports reduce for every app
    const still=paused;
    // one cached colour string per (tint, brightness level): no string building inside the draw loop
    const styles:string[][]=TINTS.map(t=>Array.from({length:LEVELS+1},(_,i)=>`rgba(${t[0]},${t[1]},${t[2]},${(i/LEVELS).toFixed(3)})`));
    let w=0,h=0,dpr=1,raf=0,last=0,visible=!document.hidden;const specks:Speck[]=[];
    const build=()=>{
      const r=canvas.getBoundingClientRect();dpr=Math.min(window.devicePixelRatio||1,2);w=Math.max(1,r.width);h=Math.max(1,r.height);
      canvas.width=Math.floor(w*dpr);canvas.height=Math.floor(h*dpr);
      const count=Math.round(Math.min(5200,Math.max(1400,(w*h)/300)));specks.length=0;
      for(let i=0;i<count;i++){
        const roll=Math.random(),b=Math.pow(Math.random(),2.1);
        specks.push({x:Math.floor(Math.random()*w),y:Math.floor(Math.random()*h),base:.07+.78*b+(roll>.992?.15:0),size:roll>.992?2:1,phase:Math.random()*6.2832,speed:.45+Math.random()*1.5,tint:Math.floor(Math.random()*TINTS.length)});
      }
    };
    const draw=(now:number)=>{
      if(!still)raf=requestAnimationFrame(draw);
      if(!visible)return;if(!still&&now-last<50)return;last=now;
      ctx.setTransform(dpr,0,0,dpr,0,0);ctx.clearRect(0,0,w,h);
      const t=now/1000;
      for(const s of specks){
        const wave=still?1:.5+.5*Math.sin(t*s.speed+s.phase),a=Math.min(1,s.base*(.06+.94*wave*wave));
        const level=Math.round(a*LEVELS);if(level<=0)continue;
        ctx.fillStyle=styles[s.tint][level];ctx.fillRect(s.x,s.y,s.size,s.size);
      }
    };
    const onVis=()=>{visible=!document.hidden;};
    build();const ro=new ResizeObserver(()=>{build();if(still)draw(performance.now());});ro.observe(canvas);
    document.addEventListener('visibilitychange',onVis);raf=requestAnimationFrame(draw);
    return()=>{cancelAnimationFrame(raf);ro.disconnect();document.removeEventListener('visibilitychange',onVis);};
  },[paused]);
  return <canvas ref={ref} className="starfield" aria-hidden="true"/>;
}
