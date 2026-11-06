import {displayExactInstant,friendly} from './market-view.js';
type Reference={revision:{catalogId:string;revisionId:string};code:string};
type Column={code:string;label:string};
export type PhysicalDeliveryRow={key:string;sideCode:string;period:string;start:string;end:string;direction:string;location:Reference;locationLabel:string;quantitySpecification:Reference;unit:string;contractual:string;selected:string;selectedTrack:string;sourceStatus:string;
 heads:{id:string;kind:string;status:string;quantity:string|null;evidence:string}[];attributes:Record<string,string>;
 origin:{slot:number;sideId:string;scheduleId:string;programDigest:string;root:string;instruction:number;grid:number;cell:number;obligationId:string|null}};
export type PhysicalDeliveryView={version:string;tradeId:string;bindingId:string;revision:number;periodLabel:string;selectionPolicy:string;tracks:Column[];attributes:Column[];capabilities:{code:string;label:string;implemented:boolean}[];rows:PhysicalDeliveryRow[]};
/** Validate transport shape and selected identity. Native adapters own quantity,
 * calendar, reference and capability semantics; presentation adds none. */
export function readPhysicalDelivery(value:unknown,expected:{tradeId:string;bindingId?:string;revision:number}):PhysicalDeliveryView{
 const v=value as PhysicalDeliveryView;const bad=()=>{throw Error('The delivery view does not match its complete booked contract. Refresh the instrument.');};
 if(!v||v.version!=='physical_delivery_view_v1'||v.tradeId!==expected.tradeId||expected.bindingId!==undefined&&v.bindingId!==expected.bindingId||v.revision!==expected.revision||
  !v.bindingId||!v.periodLabel||!v.selectionPolicy||![v.tracks,v.attributes,v.capabilities,v.rows].every(Array.isArray))bad();
 if([...v.tracks,...v.attributes].some(c=>!c||!c.code||!c.label)||v.capabilities.some(c=>!c?.code||!c.label||typeof c.implemented!=='boolean'))bad();
 const keys=['period','side','point','contract',...v.tracks.map(t=>t.code),'selected','track','status','specification',...v.attributes.map(a=>'attribute:'+a.code),'start','end','direction','unit'];
 if(new Set(keys).size!==keys.length||v.rows.some(r=>!r)||new Set(v.rows.map(r=>r.key)).size!==v.rows.length)bad();
 const reference=(r:Reference)=>!!r?.code&&!!r.revision?.catalogId&&!!r.revision.revisionId;
 for(const r of v.rows)if(['key','sideCode','period','start','end','direction','locationLabel','unit','contractual','selected','selectedTrack','sourceStatus'].some(k=>typeof r[k as keyof PhysicalDeliveryRow]!=='string'||r[k as keyof PhysicalDeliveryRow]==='')||
  !reference(r.quantitySpecification)||!reference(r.location)||!Array.isArray(r.heads)||!r.attributes||!r.origin?.scheduleId||!r.origin.programDigest)bad();
 return v;
}
export function physicalDeliveryGrid(view:PhysicalDeliveryView,zone:string){
 const fixed=['period','side','point','contract'];
 const keys=[...fixed,...view.tracks.map(t=>t.code),'selected','track','status','specification',...view.attributes.map(a=>'attribute:'+a.code),'start','end','direction','unit'];
 const headers=[view.periodLabel,'Side','Delivery point','Contract',...view.tracks.map(t=>t.label),'Selected input','Selected track','Source status','Quantity spec',...view.attributes.map(a=>a.label),'Start','End · exclusive','Direction','Unit'];
 const rows=view.rows.map(r=>[r.period,r.sideCode,r.locationLabel,r.contractual,...view.tracks.map(t=>r.heads.find(h=>h.kind===t.code)?.quantity??''),r.selected,friendly(r.selectedTrack),friendly(r.sourceStatus),r.quantitySpecification.code,...view.attributes.map(a=>r.attributes[a.code]??''),r.start,r.end,r.direction,r.unit]);
 return{keys,headers,rows,rowKeys:view.rows.map(r=>r.key),numeric:[3,...view.tracks.map((_,i)=>i+4),4+view.tracks.length],
  displayRows:rows.map((row,i)=>row.map((value,c)=>keys[c]==='start'||keys[c]==='end'?displayExactInstant(value,zone):c>=4&&c<4+view.tracks.length&&view.rows[i].heads.find(h=>h.kind===view.tracks[c-4].code)?.status==='withdrawn'?'Withdrawn':value||'—')),
  searchText:view.rows.map(r=>r.heads.map(h=>`${h.kind} ${h.status} ${h.evidence}`).join(' '))};
}
