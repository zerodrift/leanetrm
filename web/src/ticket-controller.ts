import {renderTicket,type TicketEntry,type TicketEvents,type TicketView} from './deal-ticket.js';
import {quantityRecord,quantityRecordChoices,quantityRecordFields,recordedBy,type GasQuantityReceipt,type GasQuantityView} from './gas-daily-quantities.js';
import {actorTransport,PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import {acceptOffer,acceptPreview,addSide,applyLookup,draftOf,dropLookup,newTicket,nextPreview,removeSide,resolveChoice,setValue,ticketOf,type FieldValue,type TicketState,type TicketSide} from './ticket-model.js';
import {previewed,reasonOf,type SchedulePanel} from './ticket-preview.js';
import type {FacilityAllocationTrack,ScheduleView,TicketChoice,TicketDescription,TicketDraft,TicketField,TicketLookup,TicketPreview,TicketReceipt,TicketTrade,TradeActions} from './ticket-types.js';

// Runs one deal ticket against the ticket routes (docs/deal-ticket.md). A new ticket reads the choices each field lists, previews the whole
// draft and reads its lookups once edits settle, and books through the retained request protocol; a booked one reads its trade and shows it.
// Every value, choice, lookup and schedule is the backend's; the controller only keeps what the person sees in step with what they did.
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
type Opened={tradeId:string;bindingId:string;revision:number};
type NewEntry={booked:(receipt:{tradeId:string})=>void;amending?:Opened};
// instant is the page's formatter for the instants the ticket shows.
export type TicketOptions={api:Api;context:()=>Record<string,unknown>;product:string;instant:(at:string)=>string;zone:()=>string;actor:()=>string;readOnly?:boolean;presentation?:'schedules';onAmend?:()=>void;sideSchedule?:(host:HTMLElement,side:TicketSide,slot:number,view:TicketView,context:Record<string,unknown>)=>()=>void}&({opened:Opened}|({opened?:undefined}&NewEntry));

export type TicketHandle={unsaved():boolean;pending():boolean;destroy():void};

/** Edits settle this long before the draft is previewed and its lookups read. */
const settleMs=300;
const keyOf=(side:string|null,field:string)=>side===null?field:`${side}.${field}`;
// A booking refused with one of these codes wrote nothing; any other failure leaves its outcome unknown.
const definite=(code:string|undefined)=>!!code&&(code.startsWith('gas_')||code.startsWith('ticket_')||['access_denied','historical_read_only','numeric_format','request_format','swap_context','trade_party'].includes(code));

// A booking request outlives the ticket that sent it, per product and person, until its outcome is known.
const transports=new Map<string,ReturnType<typeof actorTransport<RequestCommand>>>();
function requests(product:string,actor:()=>string){
 const owner=actor(),key=JSON.stringify([product,owner]);let transport=transports.get(key);
 if(!transport)transports.set(key,transport=actorTransport<RequestCommand>(()=>`deal-ticket-request:${product}:${owner}`,()=>sessionStorage));
 return transport;
}
// A booked side's day entry, recorded as a daily quantity revision; one awaiting its outcome outlives the ticket that sent it while the page
// is open, per product and trade, so a redrawn ticket offers to check or retry it.
type Entry={side:string;day:string;values:Record<string,string>;pending:PendingCommand;text:string};
const entries=new Map<string,Entry>();
const retainedBooking=(product:string,actor:()=>string)=>{const saved=restoreTradeRequest(requests(product,actor).read());return saved&&['tickets/book','tickets/amend'].includes(saved.path)?saved:undefined;};
/** Whether a booking of this product by this person awaits its outcome, whether or not its ticket is open. */
export const ticketBookingRetained=(product:string,actor:()=>string)=>retainedBooking(product,actor)!==undefined;

// The control the person is in, by its side and field, so a redraw puts them back where they were with what they had typed.
type Focus={side:string|null;field:string;value:string};
function focusIn(root:HTMLElement):Focus|undefined{
 const active=document.activeElement;
 if(!(active instanceof HTMLInputElement||active instanceof HTMLSelectElement)||!root.contains(active))return;
 const field=active.closest<HTMLElement>('[data-field]')?.dataset.field;
 return field?{side:active.closest<HTMLElement>('[data-side]')?.dataset.side??null,field,value:active.value}:undefined;
}
function refocus(root:HTMLElement,at:Focus|undefined){
 if(!at)return;
 const scope=at.side===null?root.querySelector('.ticket-strip'):root.querySelector(`.ticket-card[data-side="${CSS.escape(at.side)}"]`);
 const control=scope?.querySelector<HTMLInputElement|HTMLSelectElement>(`[data-field="${CSS.escape(at.field)}"] .ticket-control`);
 if(control&&!control.disabled){control.value=at.value;control.focus({preventScroll:true});}
}

export function setupTicket(host:HTMLElement,o:TicketOptions):TicketHandle{
 const owner=o.actor();
 const entry:NewEntry|undefined=o.opened?undefined:o,saved=entry?retainedBooking(o.product,()=>owner):undefined;
 const prior=saved?.path==='tickets/amend'?saved.body:undefined;
 const retained=prior&&typeof prior.tradeId==='string'&&typeof prior.bindingId==='string'&&typeof prior.expectedRevision==='number'
  ?{tradeId:prior.tradeId,bindingId:prior.bindingId,revision:prior.expectedRevision}:undefined;
 const amendment=retained??entry?.amending,mode=entry?amendment?'amend':'new':'booked';
 let reason=typeof prior?.reason==='string'?prior.reason:'';
 let definitionRevisionId=typeof prior?.definitionRevisionId==='string'?prior.definitionRevisionId:undefined;
 const view=document.createElement('div'),booking=document.createElement('section');
 booking.className='ticket-booking';booking.setAttribute('role','status');booking.hidden=true;
 const loading=document.createElement('p');loading.className='side-panel-status';loading.textContent='Loading the ticket…';view.append(loading);
 const reasonLabel=document.createElement('label'),reasonField=document.createElement('input');
 reasonLabel.className='trade-editor-actions';reasonLabel.append('Amendment reason',reasonField);reasonField.dataset.role='amendment-reason';reasonField.maxLength=500;reasonField.required=true;reasonField.value=reason;
 reasonField.oninput=()=>{reason=reasonField.value;change(s=>s,[]);};
 host.replaceChildren(...mode==='amend'?[reasonLabel]:[],view,booking);
 const transport=entry&&requests(o.product,()=>owner);
 const pending=new PendingCommand(definite);
 if(saved)pending.command={...saved,uncertain:true};
 reasonField.disabled=!!saved;
 let alive=true,d:TicketDescription|undefined,state:TicketState|undefined,actions:TradeActions|null=null,drawn:ReturnType<typeof renderTicket>|undefined;
 const current=()=>alive&&o.actor()===owner;
 let drawnKey='',drawnSchedules:[ScheduleView|null,ScheduleView|null]=[null,null];
 let panel:SchedulePanel={schedule:null,quantityUnit:null,previous:null,summaries:[],links:[],errors:{},status:{kind:'current',text:entry?'Not previewed yet. The ticket previews the trade after each change.':'Reading the booked trade…'}};
 const choices:Record<string,TicketChoice[]>={},choiceErrors:Record<string,string>={},lookupErrors:Record<string,string>={},asked=new Map<string,number>();
 const retainedChoices=new Map<string,FieldValue>(),choiceEdits=new Map<string,number>();
 const clearedChoices=new Set<string>();
 // Local date-times whose resolution is out or was refused, by field, with the latest entry's number: none may be booked as an earlier instant.
 const unresolved=new Set<string>(),entered=new Map<string,number>();
 const lifetime=new AbortController(),choiceReads=new Map<string,AbortController>();
 let fieldReads:Promise<void>=Promise.resolve(),lookupRead:AbortController|undefined,previewRead:AbortController|undefined;
 // Field reads share a server session lock. Dispatch one at a time so queued
 // fields do not spend their transport deadline waiting behind obsolete work.
 function readField<T>(path:string,body:unknown,signal:AbortSignal):Promise<T>{
  const result=fieldReads.then(()=>{signal.throwIfAborted();return o.api<T>(path,body,signal);});
  fieldReads=result.then(()=>{},()=>{});return result;
 }
 function cancelPreview(){previewRead?.abort();lookupRead?.abort();lookupRead=undefined;looked=undefined;}
 let sideSlots:Record<string,number>|undefined,scheduleContext:Record<string,unknown>|undefined;
 let selectedSide:string|null=null,tab=mode==='booked'&&o.sideSchedule?'':'instrument',level='',closed=new Set<string>();
 // The selection the booked schedule shows, as the trade read reports it, and the day entry open, if any.
 const entryKey=o.opened&&!o.readOnly?JSON.stringify([o.product,owner,o.opened.tradeId]):undefined;
 let facilityAllocations:FacilityAllocationTrack[]=[];
 let quantityPolicy:string|undefined,day=entryKey?entries.get(entryKey):undefined,recording=false;
 // The draft the ticket opened with, the last one previewed without refusal, and the one whose lookups were read last.
 let opened='',bookable:string|undefined,looked:string|undefined;
 let settling:ReturnType<typeof setTimeout>|undefined,queued=false,sending=false,booked:string|undefined,said='';
 const draft=()=>draftOf(state!),sent=()=>JSON.stringify(draft());
 const previewIdentity=(value:TicketDraft=draft())=>JSON.stringify(mode==='amend'?[value,reason]:value);
 const amendmentTerms=()=>{
  if(!amendment)return {};
  if(!definitionRevisionId)throw Error('The amendment requires its exact retained gas definition revision.');
  return {tradeId:amendment.tradeId,bindingId:amendment.bindingId,expectedRevision:amendment.revision,definitionRevisionId,reason,policy:'future_gas_composition_replace_v1'};
 };
 const blocked=()=>!current()||o.readOnly||!entry||!state||!!pending.command||sending||booked!==undefined;

 function draw(){
  queued=false;
  if(!current()||!d||!state)return;
  const levels=panel.schedule?.levels??d.levels;
  if(!levels.includes(level)){level=levels[0];closed=new Set();}
  const shown:TicketView={state,mode,presentation:o.presentation,actions,schedule:panel.schedule,quantityUnit:panel.quantityUnit,instant:o.instant,zone:o.zone(),previous:panel.previous,summaries:panel.summaries,links:panel.links,facilityAllocations,entry:entryView(),status:panel.status,

   fieldErrors:entry?{...choiceErrors,...lookupErrors,...panel.errors}:{},choices,selectedSide,tab,level,closed,on};
  if(mode==='booked'&&o.sideSchedule)shown.sideSchedule=(target,side)=>{
   const slot=sideSlots?.[side.code];if(slot===undefined)throw Error('The selected Side has no exact native membership.');
   if(!scheduleContext)throw Error('The Side schedule requires its certified ticket context.');
   return o.sideSchedule!(target,side,slot,shown,scheduleContext);
  };
  // An answer that changes nothing on screen draws nothing again, so the person keeps their place in the ticket and its schedule.
  // The schedules are compared by the preview they came from; everything else by its content.
  // Booked choices label retained fields; only the read-only links grid also consumes those labels.
  const labelsOnly=mode==='booked'&&tab!=='links';
  const key=JSON.stringify({...shown,choices:labelsOnly?null:shown.choices,schedule:null,previous:null,closed:[...closed]});
  if(!drawn||key!==drawnKey||shown.schedule!==drawnSchedules[0]||shown.previous!==drawnSchedules[1]){
   const at=focusIn(view);drawn?.destroy();drawn=undefined;
   // A view the ticket cannot draw says why in its place.
   try{drawn=renderTicket(view,shown);}
   catch(error){fail(error);return;}
   drawnKey=key;drawnSchedules=[shown.schedule,shown.previous];
   refocus(view,at);
  }
  if(labelsOnly)drawn?.updateBookedChoiceLabels(choices);
  view.inert=!!entry&&blocked();
  reasonField.disabled=blocked();
  drawBooking();
 }
 // Answers that arrive together are drawn once.
 function later(){if(!queued){queued=true;setTimeout(draw);}}
 function fail(error:unknown){
  if(!current())return;
  const note=document.createElement('p');note.className='side-panel-status side-panel-error';note.setAttribute('role','alert');note.textContent=reasonOf(error);view.replaceChildren(note);
 }
 function drawBooking(){
  const command=pending.command,noun=command?.path==='tickets/amend'?'amendment':'booking',text=command?`${said?said+' ':''}The ${noun} request is kept with its original terms until its outcome is known.`:said;
  booking.hidden=!text;booking.replaceChildren();if(!text)return;
  const line=document.createElement('p');line.textContent=text;booking.append(line);
  if(!command)return;
  for(const [label,run] of [[`Check ${noun} status`,check],[`Retry the original ${noun}`,send]] as const){
   const button=document.createElement('button');button.type='button';button.className='secondary';button.textContent=label;button.disabled=sending;button.onclick=()=>void run();booking.append(button);
  }
 }
 function say(text:string){said=text;draw();drawBooking();}

 // The fields that list choices: in a booked ticket only those holding a value, whose labels it shows.
 function fieldsOf(side:string|null){return side===null?d!.tradeFields:d!.sides.find(k=>k.kind===state!.sides.find(s=>s.code===side)!.kind)!.fields;}
 function valuesOf(side:string|null){return side===null?state!.trade:state!.sides.find(s=>s.code===side)!.values;}
 function everyField(pick:(f:TicketField,side:string|null)=>boolean):[string|null,TicketField][]{
  return [null,...state!.sides.map(s=>s.code)].flatMap(side=>fieldsOf(side).filter(f=>pick(f,side)).map(f=>[side,f] as [string|null,TicketField]));
 }
 const listed=()=>everyField((f,side)=>f.choices!==null&&(entry?f.state==='editable':!!valuesOf(side)[f.id]?.value));
 // The fields that depend on a field of the same side, however indirectly: their lists change with it.
 function dependents(side:string|null,field:string):[string|null,TicketField][]{
  const found=new Map<string,[string|null,TicketField]>(),all=everyField(()=>true);
  for(const parents:[string|null,string][]=[[side,field]];parents.length;){
   const [scope,parent]=parents.shift()!;
   for(const [at,child] of all.filter(([at,f])=>[...f.dependsOn,...f.refreshOn??[]].includes(parent)&&(!fieldsOf(at).some(f=>f.id===parent)||at===scope))){
    const key=keyOf(at,child.id);if(found.has(key))continue;found.set(key,[at,child]);parents.push([at,child.id]);
   }
  }
  return [...found.values()].filter(([,f])=>f.choices!==null);
 }
 function dependencyFilled(side:string|null,parent:string){
  if(fieldsOf(side).some(f=>f.id===parent))return !!valuesOf(side)[parent]?.value;
  return everyField(f=>f.id===parent).some(([at,f])=>!!valuesOf(at)[f.id]?.value);
 }
 async function readChoices(targets:[string|null,TicketField][]){
  await Promise.all(targets.map(async([side,f])=>{
   const key=keyOf(side,f.id),n=(asked.get(key)??0)+1;asked.set(key,n);
   choiceReads.get(key)?.abort();choiceReads.delete(key);
   // A list whose parent has no value yet lists nothing and is not asked for.
   if(f.dependsOn.some(parent=>!dependencyFilled(side,parent))){delete choices[key];delete choiceErrors[key];return;}
   const controller=new AbortController(),signal=AbortSignal.any([lifetime.signal,controller.signal]),edit=choiceEdits.get(key);choiceReads.set(key,controller);
   try{
    const r=await readField<{choices:TicketChoice[];startingValue?:TicketLookup|null}>('tickets/choices',{context:o.context(),draft:draft(),side,field:f.id},signal);
    if(signal.aborted||asked.get(key)!==n||!current())return;
    choices[key]=r.choices;delete choiceErrors[key];
    if(entry&&state&&choiceEdits.get(key)===edit&&!blocked()){
     const before=sent();state=resolveChoice(state,side,f.id,r.choices,retainedChoices.get(key),clearedChoices.has(key)?null:r.startingValue);retainedChoices.delete(key);
     if(sent()!==before){state=nextPreview(state).state;bookable=undefined;cancelPreview();panel={...panel,status:{kind:'updating',text:'Updating…'}};settle();void readChoices(dependents(side,f.id));}
    }
   }
   catch(error){if(signal.aborted||asked.get(key)!==n)return;choices[key]=[];choiceErrors[key]=reasonOf(error);}
   finally{if(choiceReads.get(key)===controller)choiceReads.delete(key);}
   later();
  }));
 }
 // Every lookup reads the whole draft, so all of them are read again once it changed.
 async function readLookups(){
  const asking=sent();if(asking===looked)return;looked=asking;
  lookupRead?.abort();const controller=lookupRead=new AbortController(),signal=AbortSignal.any([lifetime.signal,controller.signal]),context=o.context();
  const answers=await Promise.all(everyField(f=>f.lookup!==null).filter(([side,f])=>f.state!=='editable'||!clearedChoices.has(keyOf(side,f.id))).map(async([side,f])=>{
   try{return {side,f,found:await readField<TicketLookup>('tickets/lookup',{context,draft:JSON.parse(asking),side,field:f.id},signal)};}
   catch(error){return {side,f,found:undefined,refused:reasonOf(error)};}
  }));
  // Answers for a draft since edited are dropped, and that draft counts as unread, so returning to it reads its lookups again.
  if(signal.aborted||lookupRead!==controller)return;
  lookupRead=undefined;
  if(!alive||!state||sent()!==asking){if(looked===asking)looked=undefined;return;}
  const refresh=new Map<string,[string|null,TicketField]>();
  for(const a of answers){
   const key=keyOf(a.side,a.f.id);
   if(a.found){
    const before=valuesOf(a.side)[a.f.id]?.value,children=dependents(a.side,a.f.id).map(([at,f])=>({at,f,value:valuesOf(at)[f.id]}));
    state=applyLookup(state,a.side,a.f.id,a.found);delete lookupErrors[key];
    if(a.f.state==='editable'&&before!==valuesOf(a.side)[a.f.id]?.value)for(const child of children){const key=keyOf(child.at,child.f.id);if(child.value?.value)retainedChoices.set(key,child.value);refresh.set(key,[child.at,child.f]);}
   }
   else{state=dropLookup(state,a.side,a.f.id);lookupErrors[key]=a.refused!;}
  }
  // A lookup that filled a term changed the draft, which is previewed in turn.
  if(sent()!==asking){state=nextPreview(state).state;bookable=undefined;previewRead?.abort();panel={...panel,status:{kind:'updating',text:'Updating…'}};settle();}
  if(refresh.size)void readChoices([...refresh.values()]);
  draw();
 }
 function settle(){clearTimeout(settling);settling=setTimeout(()=>void preview(),settleMs);}
 async function preview(){
  clearTimeout(settling);settling=undefined;
  if(!state||blocked())return;
  previewRead?.abort();const controller=previewRead=new AbortController(),signal=AbortSignal.any([lifetime.signal,controller.signal]);
  const next=nextPreview(state),body={context:o.context(),draft:draftOf(next.state),...amendmentTerms()},asking=previewIdentity(body.draft);
  state=next.state;panel={...panel,status:{kind:'updating',text:'Updating…'}};draw();
  void readLookups();
  let outcome:{preview:TicketPreview}|{refused:unknown};
  try{outcome={preview:await o.api<TicketPreview>(amendment?'tickets/amend-preview':'tickets/preview',body,signal)};}catch(error){outcome={refused:error};}
  // Only the latest preview is drawn: an answer that arrives after a newer edit or preview is discarded.
  if(signal.aborted||!alive||!state||!acceptPreview(state,next.serial))return;
  try{panel=previewed(panel,outcome);bookable='preview' in outcome?asking:undefined;}
  catch(error){panel=previewed(panel,{refused:error});bookable=undefined;}
  draw();
 }
 // One change of the person's: it is drawn at once, any preview still out is outrun, and the draft previews once changes settle.
 function change(apply:(s:TicketState)=>TicketState,lists:'all'|[string|null,TicketField][]){
  if(!state||blocked())return;
  try{state=nextPreview(apply({...state,notices:[]})).state;}
  catch(error){state={...state,notices:[reasonOf(error)]};draw();return;}
  bookable=undefined;cancelPreview();
  said='';panel={...panel,status:{kind:'updating',text:'Updating…'}};draw();settle();
  void readChoices(lists==='all'?listed():lists);
 }
 const retry=()=>listed().filter(([side,f])=>keyOf(side,f.id) in choiceErrors);
 const on:TicketEvents={
  amend:o.onAmend,
  edit:(side,field,value)=>{
   const key=keyOf(side,field);choiceEdits.set(key,(choiceEdits.get(key)??0)+1);retainedChoices.delete(key);
   if(value)clearedChoices.delete(key);else clearedChoices.add(key);
   if(state)for(const [at,f] of dependents(side,field)){const prior=valuesOf(at)[f.id];if(prior?.value)retainedChoices.set(keyOf(at,f.id),prior);}
   change(s=>setValue(s,side,field,value),side===null?'all':[...dependents(side,field),...retry()]);
  },
  // A local date-time is resolved in the user's zone by the managed resolver; a time the clock repeats or skips is refused, not guessed.
  editLocal:(side,field,local)=>{
   const key=keyOf(side,field),zone=o.zone(),n=(entered.get(key)??0)+1;entered.set(key,n);delete lookupErrors[key];
   if(!local){unresolved.delete(key);on.edit(side,field,'');return;}
   unresolved.add(key);draw();
   const latest=()=>current()&&zone===o.zone()&&entered.get(key)===n;
   void o.api<{candidates:{instant:string}[]}>('time/inspect',{localTime:local,timeZone:zone}).then(result=>{
    if(!latest())return;
    if(result.candidates.length===1){unresolved.delete(key);on.edit(side,field,result.candidates[0].instant);return;}
    lookupErrors[key]=result.candidates.length===0?`${local} does not occur in ${zone}; the clock skips it.`:`${local} occurs twice in ${zone}; choose a time the clock does not repeat.`;draw();
   },error=>{if(latest()){lookupErrors[key]=reasonOf(error);draw();}});
  },
  accept:(side,field)=>change(s=>acceptOffer(s,side,field),[]),
  selectSide:code=>{selectedSide=tab=code;draw();},
  tab:id=>{tab=id;if(state!.sides.some(s=>s.code===id))selectedSide=id;draw();},
  level:next=>{level=next;closed=new Set();draw();},
  toggle:(row,open)=>{if(open)closed.delete(row);else closed.add(row);},
  addSide:kind=>change(s=>addSide(s,kind),'all'),
  removeSide:code=>{
   if(selectedSide===code)selectedSide=null;if(tab===code)tab='instrument';
   for(const read of [choices,choiceErrors,lookupErrors])for(const key of Object.keys(read))if(key.startsWith(`${code}.`))delete read[key];
   for(const key of [...unresolved])if(key.startsWith(`${code}.`))unresolved.delete(key);
   for(const [key,controller] of choiceReads)if(key.startsWith(`${code}.`)){controller.abort();choiceReads.delete(key);}
   change(s=>removeSide(s,code),'all');
  },
  preview:()=>void preview(),
  book:()=>void book(),
  enter:(side,at,value)=>{
   if(!current()||!entryKey||!actions?.record.allowed||day?.pending.command||recording)return;
   day={side,day:at,values:{quantity:value},pending:new PendingCommand(definite),text:''};entries.set(entryKey,day);draw();
  },
  entryValue:(field,value)=>{if(day&&!day.pending.command)day.values[field]=value;},
  cancelEntry:()=>{if(!day||day.pending.command||recording)return;entries.delete(entryKey!);day=undefined;draw();},
  record:()=>void record(),
  checkEntry:()=>void checkRecord(),
  retryEntry:()=>void sendRecord(),
 };

 function entryView():TicketEntry|null{
  return day?{side:day.side,day:day.day,fields:quantityRecordFields,values:day.values,choices:quantityRecordChoices,pending:!!day.pending.command,sending:recording,text:day.text}:null;
 }
 // A day's record is built against the trade revision and the day's previous record of its kind as the daily quantity read returns them now.
 async function record(){
  const at=day;if(!current()||o.readOnly||!at||at.pending.command||recording||!o.opened||!quantityPolicy)return;
  recording=true;at.text='';draw();
  try{
   const read=await o.api<GasQuantityView>('gas/quantities/read',{context:o.context(),tradeId:o.opened.tradeId,selectionPolicy:quantityPolicy,includeHistory:false});
   if(!current())return;
   const row=read.rows.find(r=>r.sideCode===at.side&&r.day===at.day);
   if(!row)throw Error(`The trade has no daily quantity for ${at.side} on ${at.day}.`);
   const v=at.values;at.pending.begin('gas/quantities/record',quantityRecord(o.context(),read,row,{kind:v.kind??'',status:v.status??'',quantity:v.quantity??'',evidence:v.evidence??''}));
  }catch(error){at.text=`Record refused: ${reasonOf(error)}`;}
  finally{recording=false;}
  if(at.pending.command)await sendRecord();else draw();
 }
 async function sendRecord(){
  const at=day,command=at?.pending.command;if(!current()||o.readOnly||!at||!command||recording)return;
  const uncertain=at.pending.sending();recording=true;at.text='';draw();
  try{const r=await o.api<GasQuantityReceipt>(command.path,{...command.body,operationId:command.operationId});recording=false;await recorded(at,command,r);}
  catch(error){
   recording=false;if(!current())return;at.pending.failed((error as {code?:string}).code,uncertain);
   at.text=at.pending.command?`The quantity record is not confirmed: ${reasonOf(error)}`:`Record refused: ${reasonOf(error)}`;draw();
  }
 }
 async function checkRecord(){
  const at=day,command=at?.pending.command;if(!current()||o.readOnly||!at||!command||recording)return;
  try{
   const r=await o.api<GasQuantityReceipt|{found:false}>('gas/quantities/status',{operationId:command.operationId});
   if(!current())return;
   if('revisionId' in r)await recorded(at,command,r);else{at.text='No accepted quantity record was found. Retry the original record.';draw();}
  }catch(error){if(!current())return;at.text=`The record status could not be read: ${reasonOf(error)}`;draw();}
 }
 // Once recorded, the ticket reads the trade again and draws the backend's schedule; the cells that moved are marked.
 async function recorded(at:Entry,command:RequestCommand,r:GasQuantityReceipt){
  // A replacement ticket holds the same retained Entry. Only its own status or
  // retry response may settle it; an abandoned view cannot strand its controls.
  if(!current())return;
  if(!recordedBy({...command.body,operationId:command.operationId},r)){at.text='The record response could not be reconciled with its command. Check its status.';draw();return;}
  at.pending.clear();entries.delete(entryKey!);day=undefined;
  said=`Quantity revision ${r.revision} recorded${r.unchanged?' · existing business fact retained':''}.`;
  try{await readTrade();}catch(error){fail(error);}
 }
 async function readTrade(){
  const r=await o.api<TicketTrade>('tickets/trade',{context:o.context(),...(o.opened??amendment)!},lifetime.signal);
  if(!current())return;
  if(amendment&&!r.actions.amend.allowed)throw Error(r.actions.amend.reason);
  if(amendment){if(typeof r.definitionRevisionId!=='string'||!r.definitionRevisionId)throw Error('The amendment requires its exact retained gas definition revision.');definitionRevisionId=r.definitionRevisionId;}
  // A trade that records day quantities names the selection its schedule shows, whether or not this reader may record one here.
  if(r.actions.record.allowed&&!(typeof r.quantityPolicy==='string'&&r.quantityPolicy))throw Error('A booked read that offers day entries must name the quantity selection its schedule shows.');
  state=ticketOf(d!,r.draft);actions=o.readOnly?{...r.actions,record:{allowed:false,reason:'This trade window is read only.'}}:r.actions;quantityPolicy=r.quantityPolicy??undefined;
  if(mode==='booked'&&o.sideSchedule){
   const requested=o.context();
   if(r.snapshotId!==requested.snapshotId||!r.context?.contextId||!r.context.effectiveOn||!r.context.knownAt||requested.knowledgeMode==='retained'&&r.context.contextId!==requested.contextId)
    throw Error('The booked schedule response does not match its certified snapshot and context.');
   scheduleContext={...requested,snapshotId:r.snapshotId,knowledgeMode:'retained',contextId:r.context.contextId};
   sideSlots=r.sideSlots;
   if(!sideSlots||Object.keys(sideSlots).length!==state.sides.length||state.sides.some(s=>!Number.isInteger(sideSlots![s.code])||sideSlots![s.code]<0)||new Set(Object.values(sideSlots)).size!==state.sides.length)
    throw Error('The booked Side schedules require their exact native membership slots.');
   if(tab===''){if(!state.sides.length)throw Error('This trade has no Side schedules.');selectedSide=tab=state.sides[0].code;}
  }
  facilityAllocations=o.presentation?[]:r.facilityAllocations;panel=previewed(panel,{preview:r});
  if(panel.status.kind==='current')panel={...panel,status:{kind:'current',text:amendment?'Edit the retained terms, enter a reason and preview before saving a new revision.':'Booked terms and their retained schedule.'}};

  draw();
 }

 function retain(){transport!.write(pending.command);}
 async function book(){
  if(!state||blocked())return;
  if(unresolved.size){say('Book once every local time you entered resolves to one instant.');return;}
  if(settling!==undefined||bookable!==previewIdentity()){say('Save once the preview of your latest change is up to date.');return;}
  pending.begin(amendment?'tickets/amend':'tickets/book',{context:o.context(),draft:draft(),...amendmentTerms()});retain();
  await send();
 }
 async function send(){
  const command=pending.command;if(!current()||!command||sending)return;
  const uncertain=pending.sending();retain();sending=true;said='';draw();
  try{const r=await o.api<TicketReceipt>(command.path,{...command.body,operationId:command.operationId});sending=false;receipt(command,r);}
  catch(error){
   sending=false;if(!current())return;pending.failed((error as {code?:string}).code,uncertain);retain();
   say(pending.command?`The booking is not confirmed: ${reasonOf(error)}`:`Booking refused: ${reasonOf(error)}`);
  }
 }
 async function check(){
  const command=pending.command;if(!current()||!command||sending)return;
  try{
   const r=await o.api<TicketReceipt|{found:false}>('tickets/status',{operationId:command.operationId});
   if('tradeId' in r)receipt(command,r);else say('No completed booking was found. Retry the original booking.');
  }catch(error){say(`The booking status could not be read: ${reasonOf(error)}`);}
 }
 function receipt(command:RequestCommand,r:TicketReceipt){
  // A remounted ticket reconciles the retained request itself. An old reply
  // cannot clear or overwrite a newer request owned by that same actor.
  if(!current())return;
  const expected=command.path==='tickets/amend'?Number(command.body.expectedRevision)+(r.unchanged?0:1):1;
  if(r.operationId!==command.operationId||r.revision!==expected||typeof r.tradeId!=='string'||command.path==='tickets/amend'&&r.tradeId!==command.body.tradeId){say('The trade response could not be reconciled with its request. Check its status.');return;}
  pending.clear();retain();booked=r.tradeId;say(command.path==='tickets/amend'?r.unchanged?`No contract changes · revision ${r.revision} retained.`:`Amendment saved · revision ${r.revision}.`:'Trade booked.');
  entry!.booked({tradeId:r.tradeId});
 }

 async function load(){
  try{
   d=await o.api<TicketDescription>('tickets/description',{context:o.context(),product:o.product},lifetime.signal);
   if(!current())return;
   level=d.levels[0];
   if(o.opened)await readTrade();
   else{
    // A booking whose outcome is unknown opens with the exact terms it was sent with, to be checked or retried, never edited.
    if(saved){
     // Terms the ticket cannot show still leave the request to be checked or retried.
     try{state=ticketOf(d,saved.body.draft as TicketDraft);}catch(error){fail(error);drawBooking();return;}
    }else if(amendment){if(!d.modes.includes('amend'))throw Error('This ticket does not support amendments.');await readTrade();}
    else state=newTicket(d);
    opened=sent();
   }
   draw();void readChoices(listed());
  }catch(error){fail(error);drawBooking();}
 }
 view.inert=!!entry&&blocked();
 drawBooking();
 void load();
 return {
  unsaved:()=>!!day||!!entry&&!!state&&booked===undefined&&(!!pending.command||sent()!==opened||!!amendment&&!!reason),
  pending:()=>recording||!!pending.command||!!day?.pending.command,
  destroy(){alive=false;lifetime.abort();clearTimeout(settling);drawn?.destroy();host.replaceChildren();},
 };
}
