// Curve kinds (D259). The backend names each curve's kind from its exact definition (curve_kinds_v1); the browser only draws it, as an
// icon on the kind's colour (app.css, --kind-<code>) named by its label. An unknown code means the contract is broken: it throws, never
// becomes a fallback badge.
export const kindsVersion='curve_kinds_v1';
export type CurveKind='market_settlement'|'trader_mark'|'interest_rate'|'basis_spread'|'scaled'|'weighted_blend'|'rolling_average'|'balance_of_month'|'prompt_contract'|'custom';

type Shape=readonly ['path'|'circle'|'rect',Readonly<Record<string,string>>];
const line=(d:string,extra:Record<string,string>={}):Shape=>['path',{d,...extra}];
const dot=(cx:number,cy:number,r:number):Shape=>['circle',{cx:String(cx),cy:String(cy),r:String(r)}];
// Each icon is drawn in a 20×20 box with round strokes and no fill.
const kinds:Readonly<Record<CurveKind,{label:string;icon:readonly Shape[]}>>={
 market_settlement:{label:'Market settlement',icon:[line('M3 16V4M3 16h14M6 12l3-3 3 2 4-5')]},
 trader_mark:{label:'Trader mark',icon:[line('M4 16l2-6 8-8 2 2-8 8-6 2zM12 4l2 2')]},
 interest_rate:{label:'Interest rate',icon:[dot(6,6,2),dot(14,14,2),line('M15 5L5 15')]},
 basis_spread:{label:'Basis / spread',icon:[dot(5,6,2.4),dot(15,14,2.4),line('M7 7.5l6 5M13 5h3v3')]},
 scaled:{label:'Scaled',icon:[line('M5 5l10 10M15 5L5 15')]},
 weighted_blend:{label:'Weighted blend',icon:[line('M15 4H5l5 6-5 6h10')]},
 rolling_average:{label:'Rolling history average',icon:[line('M3 13l3-4 3 3 3-5 2 2M16 3v4h-2'),line('M3 16h14',{'stroke-dasharray':'2 2'})]},
 balance_of_month:{label:'Balance-of-month average',icon:[['rect',{x:'3',y:'4',width:'14',height:'13',rx:'2'}],line('M3 8h14M10 11h5M10 14h5')]},
 prompt_contract:{label:'Prompt / rolling contract',icon:[line('M15.5 6.5A6.5 6.5 0 1 0 16.5 11M16 2.5V7h-4.5')]},
 custom:{label:'Custom graph',icon:[dot(5,5,2),dot(5,15,2),dot(15,10,2),line('M6.8 5.9l6.4 3.2M6.8 14.1l6.4-3.2')]},
};
/** The kinds as the pages group them: curves from the market, curves related to another by a rule, and custom graphs. */
export const kindGroups:readonly {label:string;kinds:readonly CurveKind[]}[]=[
 {label:'Source curves',kinds:['market_settlement','trader_mark','interest_rate']},
 {label:'Relationships',kinds:['basis_spread','scaled']},
 {label:'Synthetic',kinds:['weighted_blend','rolling_average','balance_of_month','prompt_contract','custom']},
];
export const isCurveKind=(value:unknown):value is CurveKind=>typeof value==='string'&&Object.hasOwn(kinds,value);
function known(kind:CurveKind){if(!isCurveKind(kind))throw Error('Unknown curve kind');return kinds[kind];}
export const kindLabel=(kind:CurveKind)=>known(kind).label;
/** The kind's badge: an image named by the kind's label (also its tooltip), whose drawing is decorative. Like the commodity dot it is an `i`, so a row's spans stay its text. */
export function kindBadge(kind:CurveKind,size:'sm'|'md'='sm'){
 const {label,icon}=known(kind),svgNs='http://www.w3.org/2000/svg',badge=document.createElement('i'),svg=document.createElementNS(svgNs,'svg');
 badge.className='curve-kind curve-kind-'+size;badge.dataset.kind=kind;badge.setAttribute('role','img');badge.setAttribute('aria-label',label);badge.setAttribute('title',label);
 svg.setAttribute('viewBox','0 0 20 20');svg.setAttribute('aria-hidden','true');svg.setAttribute('focusable','false');
 for(const [tag,attributes] of icon){const shape=document.createElementNS(svgNs,tag);for(const [name,value] of Object.entries(attributes))shape.setAttribute(name,value);svg.append(shape);}
 badge.append(svg);return badge;
}
