import type {TicketDescription,TicketDraft,TicketField} from './ticket-types.js';

// The state of one deal ticket: the user's values, drawn from a backend description. Every function
// returns a new state; the description alone says which fields exist, which are editable and what depends on what.
export type Source={kind:string;label:string};
export type FieldValue={value:string;looked?:Source;offered?:{value:string;kind:string;label:string}};
export type TicketSide={kind:string;code:string;values:Record<string,FieldValue>};
// removed holds the codes of the sides taken off the ticket, which no later side is given.
export type TicketState={description:TicketDescription;trade:Record<string,FieldValue>;sides:TicketSide[];removed:string[];notices:string[];previewSerial:number};

const sideCode=(kind:string,n:number)=>`${kind}-${n}`;

function lowestFreeCode(sides:TicketSide[],removed:string[],kind:string){
 let n=1;
 while(sides.some(s=>s.code===sideCode(kind,n))||removed.includes(sideCode(kind,n)))n++;
 return sideCode(kind,n);
}

export function newTicket(d:TicketDescription):TicketState{
 const sides:TicketSide[]=[];
 for(const k of d.sides)for(let i=0;i<k.initial;i++)sides.push({kind:k.kind,code:lowestFreeCode(sides,[],k.kind),values:{}});
 return {description:d,trade:{},sides,removed:[],notices:[],previewSerial:0};
}

function fieldsOf(s:TicketState,side:string|null):TicketField[]{
 if(side===null)return s.description.tradeFields;
 const found=s.sides.find(x=>x.code===side);
 if(!found)throw new Error(`The ticket has no side '${side}'.`);
 return s.description.sides.find(k=>k.kind===found.kind)!.fields;
}

function fieldOf(s:TicketState,side:string|null,id:string):TicketField{
 const field=fieldsOf(s,side).find(x=>x.id===id);
 if(!field)throw new Error(`The ticket has no field '${id}'${side===null?'':` on side '${side}'`}.`);
 return field;
}

function valuesOf(s:TicketState,side:string|null){return side===null?s.trade:s.sides.find(x=>x.code===side)!.values;}

function withValues(s:TicketState,side:string|null,values:Record<string,FieldValue>,notices:string[]=s.notices):TicketState{
 return side===null?{...s,trade:values,notices}:{...s,notices,sides:s.sides.map(x=>x.code===side?{...x,values}:x)};
}

// A dependent chosen under the old parent is cleared, never re-picked, and the user is told.
function clearDependents(fields:TicketField[],values:Record<string,FieldValue>,parent:TicketField,notices:string[]){
 for(const child of fields.filter(x=>x.dependsOn.includes(parent.id))){
  if(values[child.id]?.value){notices.push(`${child.label} was cleared because ${parent.label} changed.`);values[child.id]={value:''};}
  clearDependents(fields,values,child,notices);
 }
}

export function setValue(s:TicketState,side:string|null,field:string,value:string):TicketState{
 const changed=fieldOf(s,side,field);
 if(changed.state!=='editable')throw new Error(`Field '${field}' is not editable.`);
 const values={...valuesOf(s,side),[field]:{value}};
 const notices=[...s.notices];
 clearDependents(fieldsOf(s,side),values,changed,notices);
 return withValues(s,side,values,notices);
}

// A draft the backend returned, such as a booked trade's, as a ticket whose every value is the person's.
export function ticketOf(d:TicketDescription,draft:TicketDraft):TicketState{
 if(draft.product!==d.product||draft.version!==d.version)throw new Error(`The draft is for ${draft.product} version ${draft.version}, not ${d.product} version ${d.version}.`);
 const values=(given:Record<string,string>)=>Object.fromEntries(Object.entries(given).map(([id,value])=>[id,{value}]));
 for(const side of draft.sides)if(!d.sides.some(k=>k.kind===side.kind))throw new Error(`The ticket has no side kind '${side.kind}'.`);
 return {description:d,trade:values(draft.trade),sides:draft.sides.map(x=>({kind:x.kind,code:x.code,values:values(x.values)})),removed:[],notices:[],previewSerial:0};
}

export function applyLookup(s:TicketState,side:string|null,field:string,r:{value:string;source:Source}):TicketState{
 if(!fieldOf(s,side,field).lookup)throw new Error(`Field '${field}' has no lookup.`);
 const current=valuesOf(s,side)[field];
 // A proposal of the value the person already holds is nothing to offer, and withdraws an earlier offer.
 if(current&&!current.looked&&current.value===r.value)return current.offered?withValues(s,side,{...valuesOf(s,side),[field]:{value:current.value}}):s;
 const open=!current||current.value===''||current.looked!==undefined;
 const next:FieldValue=open?{value:r.value,looked:r.source}:{value:current.value,offered:{value:r.value,...r.source}};
 return withValues(s,side,{...valuesOf(s,side),[field]:next});
}

// A lookup that no longer resolves: the value it filled is emptied and its offer withdrawn; a value of the person's stays.
export function dropLookup(s:TicketState,side:string|null,field:string):TicketState{
 const current=valuesOf(s,side)[field];
 if(!current?.looked&&!current?.offered)return s;
 return withValues(s,side,{...valuesOf(s,side),[field]:{value:current.looked?'':current.value}});
}

export function acceptOffer(s:TicketState,side:string|null,field:string):TicketState{
 const offered=valuesOf(s,side)[field]?.offered;
 if(!offered)throw new Error(`Field '${field}' has no offered value.`);
 return withValues(s,side,{...valuesOf(s,side),[field]:{value:offered.value,looked:{kind:offered.kind,label:offered.label}}});
}

export function addSide(s:TicketState,kind:string):TicketState{
 const spec=s.description.sides.find(k=>k.kind===kind);
 if(!spec)throw new Error(`The ticket has no side kind '${kind}'.`);
 if(spec.soon)throw new Error(spec.soon);
 if(s.sides.filter(x=>x.kind===kind).length>=spec.max)throw new Error(`A ticket takes at most ${spec.max} ${kind} sides.`);
 for(const limit of s.description.sideLimits)
  if(limit.kinds.includes(kind)&&s.sides.filter(x=>limit.kinds.includes(x.kind)).length>=limit.max)
   throw new Error(`A ticket takes at most ${limit.max} ${limit.kinds.join(' and ')} sides together.`);
 return {...s,sides:[...s.sides,{kind,code:lowestFreeCode(s.sides,s.removed,kind),values:{}}]};
}

// A choice that named the removed side is cleared with its dependents, and the user is told; the side's code is not given again.
export function removeSide(s:TicketState,code:string):TicketState{
 const side=s.sides.find(x=>x.code===code);
 if(!side)throw new Error(`The ticket has no side '${code}'.`);
 const min=s.description.sides.find(k=>k.kind===side.kind)!.min;
 if(s.sides.filter(x=>x.kind===side.kind).length<=min)throw new Error(`A ticket needs at least ${min} ${side.kind} sides.`);
 const notices=[...s.notices];
 let next:TicketState={...s,sides:s.sides.filter(x=>x!==side),removed:[...s.removed,code]};
 for(const at of [null,...next.sides.map(x=>x.code)]){
  const fields=fieldsOf(next,at),values={...valuesOf(next,at)};
  for(const named of fields.filter(f=>f.choices!==null&&values[f.id]?.value===code)){
   notices.push(`${named.label} was cleared because side ${code} was removed.`);values[named.id]={value:''};
   clearDependents(fields,values,named,notices);
  }
  next=withValues(next,at,values,notices);
 }
 return next;
}

// Editable fields with a value only: soon, info and derived fields never reach the backend.
function editable(fields:TicketField[],values:Record<string,FieldValue>){
 return Object.fromEntries(fields.filter(f=>f.state==='editable'&&values[f.id]?.value).map(f=>[f.id,values[f.id].value]));
}

export function draftOf(s:TicketState):TicketDraft{
 const d=s.description;
 return {product:d.product,version:d.version,trade:editable(d.tradeFields,s.trade),
  sides:s.sides.map(x=>({kind:x.kind,code:x.code,values:editable(d.sides.find(k=>k.kind===x.kind)!.fields,x.values)}))};
}

export function nextPreview(s:TicketState):{state:TicketState;serial:number}{
 const serial=s.previewSerial+1;
 return {state:{...s,previewSerial:serial},serial};
}

export function acceptPreview(s:TicketState,serial:number):boolean{return serial===s.previewSerial;}
