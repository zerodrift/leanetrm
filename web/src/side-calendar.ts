import {cloneRead} from './read-references.js';
import type {ReferenceKey} from './swap-observations.js';

export const calendarKey=(reference:ReferenceKey)=>JSON.stringify([reference.revision.catalogId,reference.revision.revisionId,reference.code]);
export type CalendarPurpose='delivery'|'observation'|'payment';
export type CalendarPurposes=Record<CalendarPurpose,boolean>;
export type CalendarLocks=Partial<Record<CalendarPurpose,ReferenceKey>>;
export type SideCalendarPolicy={implementation:'side_calendar_policy_v1';delivery:CalendarBinding;observation:CalendarBinding;payment:CalendarBinding};
type CalendarBinding={applicable:boolean;calendar:ReferenceKey|null};
export type SideCalendarCatalogRow={reference:ReferenceKey;code:string;coverageFrom:string;coverageTo:string;weekendMask:number;exceptions:{date:string;open:boolean}[];definitionDigest:string;publication:{catalogId:string;eventOrder:number;knownFrom:string}};
export type SideCalendarChoice={value:string;label:string;reference:ReferenceKey;coverageFrom?:string;coverageTo?:string;weekendMask?:number;exceptions?:{date:string;open:boolean}[]};
export const referenceCalendarChoices=(rows:{reference:ReferenceKey;code:string;coverageFrom?:string;coverageTo?:string;weekendMask?:number;exceptions?:{date:string;open:boolean}[]}[]):SideCalendarChoice[]=>rows.map(row=>({...row,value:calendarKey(row.reference),label:row.code}));
const purposes:CalendarPurpose[]=['delivery','observation','payment'];
const same=(a:ReferenceKey,b:ReferenceKey)=>calendarKey(a)===calendarKey(b);

// Presentation state only. Exact calendar admission and every generated date
// remain the responsibility of the shared managed authoring operation.
export class SideCalendarPolicySelection{
 private selected:Partial<Record<CalendarPurpose,ReferenceKey>>={};
 restore(policy?:SideCalendarPolicy|null){
  this.selected={};
  if(policy)for(const purpose of purposes){const ref=policy[purpose].calendar;if(ref)this.selected[purpose]=cloneRead(ref);}
 }
 choose(purpose:CalendarPurpose,reference:ReferenceKey|null){if(reference)this.selected[purpose]=cloneRead(reference);else delete this.selected[purpose];}
 value(purpose:CalendarPurpose,locks:CalendarLocks={}){return this.selected[purpose]??locks[purpose];}
 useOne(reference:ReferenceKey,applicable:CalendarPurposes,locks:CalendarLocks){
  for(const purpose of purposes)if(applicable[purpose]&&locks[purpose]&&!same(reference,locks[purpose]!))throw Error(`The ${purpose} calendar is locked by the selected product.`);
  for(const purpose of purposes)if(applicable[purpose])this.choose(purpose,reference);
 }
 check(applicable:CalendarPurposes,choices:SideCalendarChoice[],locks:CalendarLocks={},required=false){
  for(const purpose of purposes){
   if(!applicable[purpose])continue;
   const ref=this.value(purpose,locks);
   if(!ref){if(required)throw Error(`Choose an exact ${purpose} calendar for this Side.`);continue;}
   if(locks[purpose]&&!same(ref,locks[purpose]!))throw Error(`The ${purpose} calendar is locked by the selected product.`);
   if(!choices.some(c=>same(c.reference,ref)))throw Error(`The selected ${purpose} calendar version is unavailable in this context.`);
  }
 }
 read(applicable:CalendarPurposes,choices:SideCalendarChoice[],locks:CalendarLocks={}):SideCalendarPolicy{
  this.check(applicable,choices,locks,true);
  const binding=(purpose:CalendarPurpose):CalendarBinding=>({applicable:applicable[purpose],calendar:applicable[purpose]?cloneRead(this.value(purpose,locks)!):null});
  return{implementation:'side_calendar_policy_v1',delivery:binding('delivery'),observation:binding('observation'),payment:binding('payment')};
 }
}

export function sideCalendar(options:{id:string;choices:()=>SideCalendarChoice[];purposes:()=>CalendarPurposes;locked?:()=>CalendarLocks;changed:()=>void}){
 const selection=new SideCalendarPolicySelection(),element=document.createElement('fieldset'),legend=document.createElement('legend'),note=document.createElement('p');
 element.dataset.ownControls='';element.className='side-calendar';legend.textContent='Schedule calendars';note.id=options.id+'-note';note.className='field-help';note.setAttribute('role','status');element.append(legend);
 const locks=()=>options.locked?.()??{},controls={} as Record<CalendarPurpose,HTMLSelectElement>,details={} as Record<CalendarPurpose,HTMLElement>;
 const labelFor=(ref:ReferenceKey)=>`${ref.code} · version ${ref.revision.revisionId}`;
 const populate=(control:HTMLSelectElement,selected?:ReferenceKey)=>{
  const rows=options.choices();control.replaceChildren(new Option('Choose…',''),...rows.map(c=>new Option(`${c.label} · ${c.reference.revision.revisionId}`,c.value)));
  if(selected&&!rows.some(c=>same(c.reference,selected))){const unavailable=new Option('Unavailable · '+labelFor(selected),calendarKey(selected));unavailable.disabled=true;control.append(unavailable);}
  control.value=selected?calendarKey(selected):'';
 };
 for(const purpose of purposes){
  const row=document.createElement('div'),label=document.createElement('label'),control=document.createElement('select'),detail=document.createElement('details'),summary=document.createElement('summary'),body=document.createElement('pre');
  row.dataset.calendarPurpose=purpose;control.id=options.id+'-'+purpose;control.dataset.sideCalendar=purpose;control.setAttribute('aria-describedby',note.id);label.htmlFor=control.id;label.textContent=purpose==='observation'?'Observation / reset':purpose[0].toUpperCase()+purpose.slice(1);summary.textContent='Inspect calendar';detail.append(summary,body);row.append(label,control,detail);element.append(row);controls[purpose]=control;details[purpose]=body;
  const changed=()=>{const ref=options.choices().find(c=>c.value===control.value)?.reference??null;if((ref?calendarKey(ref):'')===(selection.value(purpose,locks())?calendarKey(selection.value(purpose,locks())!):''))return;selection.choose(purpose,ref);sync();options.changed();};control.oninput=control.onchange=changed;
 }
 const shared=document.createElement('select'),apply=document.createElement('button'),sharedLabel=document.createElement('label');shared.id=options.id+'-shared';shared.setAttribute('aria-describedby',note.id);sharedLabel.htmlFor=shared.id;sharedLabel.textContent='Shared calendar';apply.type='button';apply.textContent='Use one calendar for all applicable schedules';element.append(sharedLabel,shared,apply,note);
 apply.onclick=()=>{try{const ref=options.choices().find(c=>c.value===shared.value)?.reference;if(!ref)throw Error('Choose the shared calendar explicitly.');selection.useOne(ref,options.purposes(),locks());sync();options.changed();}catch(error){note.textContent=(error as Error).message;}};
 function sync(disabled=false){
  const applicable=options.purposes(),locked=locks(),rows=options.choices();
  for(const purpose of purposes){
   const control=controls[purpose],ref=selection.value(purpose,locked),choice=rows.find(c=>ref&&same(c.reference,ref));populate(control,ref);control.disabled=disabled||!applicable[purpose]||!!locked[purpose];
   control.closest<HTMLElement>('[data-calendar-purpose]')!.hidden=!applicable[purpose];
   const weekends=choice?.weekendMask===undefined?'Unavailable': ['Monday','Tuesday','Wednesday','Thursday','Friday','Saturday','Sunday'].filter((_,i)=>choice.weekendMask!&(1<<i)).join(', ')||'None';
   details[purpose].textContent=ref?`${labelFor(ref)}\n${locked[purpose]?'Locked by product\n':''}Coverage: ${choice?.coverageFrom??'Unavailable'} to ${choice?.coverageTo??'Unavailable'} (exclusive)\nWeekends: ${weekends}\nExceptions: ${choice?.exceptions===undefined?'Unavailable':choice.exceptions.length?choice.exceptions.map(d=>`${d.date}: ${d.open?'open':'closed'}`).join('\n'):'None'}`:'No calendar selected';
  }
  const sharedValue=shared.value;populate(shared);shared.value=rows.some(c=>c.value===sharedValue)?sharedValue:'';shared.disabled=disabled;apply.disabled=disabled;
  try{selection.check(applicable,rows,locked);const selected=purposes.filter(p=>applicable[p]).map(p=>selection.value(p,locked)).filter((r):r is ReferenceKey=>!!r);note.textContent=new Set(selected.map(calendarKey)).size>1?'These schedules use different calendar versions.':'Select a calendar for every applicable purpose, including all-days and unadjusted rules.';}
  catch(error){note.textContent=(error as Error).message;}
 }
 return{element,sync,restore:(policy?:SideCalendarPolicy|null)=>{selection.restore(policy);sync();},reset:()=>{selection.restore();sync();},
  read:()=>selection.read(options.purposes(),options.choices(),locks()),check:()=>selection.check(options.purposes(),options.choices(),locks()),
  draft:():SideCalendarPolicy=>{const applicable=options.purposes();const binding=(purpose:CalendarPurpose)=>({applicable:applicable[purpose],calendar:applicable[purpose]?selection.value(purpose,locks())??null:null});return{implementation:'side_calendar_policy_v1',delivery:binding('delivery'),observation:binding('observation'),payment:binding('payment')};},
  reference:(purpose:CalendarPurpose)=>selection.value(purpose,locks())};
}
export type SideCalendar=ReturnType<typeof sideCalendar>;

// Definite managed authoring refusals. PendingCommand still preserves every
// previously uncertain outcome and HTTP/transport failures without such a code.
export const isCalendarAuthoringRefusal=(code:string|undefined):boolean=>!!code&&
 ['side_calendar_','calendar_','clock_','payment_','tou_'].some(prefix=>code.startsWith(prefix));
