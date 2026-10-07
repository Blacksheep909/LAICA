export type GlassKind='liquid'|'frosted'|'clear'|'smoked'|'lensed';
export interface Look{glass:GlassKind;accent:string;contrast:'standard'|'high';panel:number;blur:number;backdrop:'space'|'plain'}
export const LOOK_EVENT='laica-look';
const KEY='laica-look';
export const DEFAULT_LOOK:Look={glass:'smoked',accent:'#b3a5f7',contrast:'high',panel:.42,blur:7,backdrop:'space'};
export const ACCENTS:[string,string][]=[['Lavender','#b3a5f7'],['Sky','#7cc4ff'],['Mint','#72e0c0'],['Rose','#f58db8'],['Amber','#ffc27a'],['Silver','#d0d4de']];
export function loadLook():Look{try{const raw=localStorage.getItem(KEY);if(raw)return {...DEFAULT_LOOK,...JSON.parse(raw)};}catch{/* storage unavailable */}return DEFAULT_LOOK;}
export function saveLook(l:Look){try{localStorage.setItem(KEY,JSON.stringify(l));}catch{/* storage unavailable */}window.dispatchEvent(new Event(LOOK_EVENT));}