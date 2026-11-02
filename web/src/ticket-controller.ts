import {renderTicket,type TicketEntry,type TicketEvents,type TicketView} from './deal-ticket.js';
import {quantityRecord,quantityRecordChoices,quantityRecordFields,recordedBy,type GasQuantityReceipt,type GasQuantityView} from './gas-daily-quantities.js';
import {actorTransport,PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import {acceptOffer,acceptPreview,addSide,applyLookup,draftOf,dropLookup,newTicket,nextPreview,removeSide,setValue,ticketOf,type TicketState} from './ticket-model.js';
import {previewed,reasonOf,type SchedulePanel} from './ticket-preview.js';
import type {ScheduleView,TicketChoice,TicketDescription,TicketDraft,TicketField,TicketLookup,TicketPreview,TicketReceipt,TicketTrade,TradeActions} from './ticket-types.js';

// Runs one deal ticket against the ticket routes (docs/deal-ticket.md). A new ticket reads the choices each field lists, previews the whole
// draft and reads its lookups once edits settle, and books through the retained request protocol; a booked one reads its trade and shows it.
// Every value, choice, lookup and schedule is the backend's; the controller only keeps what the person sees in step with what they did.
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
type Opened={tradeId:string;bindingId:string;revision:number};
type NewEntry={booked:(receipt:{tradeId:string})=>void};
// instant is the page's formatter for the instants the ticket shows.
export type TicketOptions={api:Api;context:()=>Record<string,unknown>;product:string;instant:(at:string)=>string;actor:()=>string;readOnly?:boolean}&({opened:Opened}|({opened?:undefined}&NewEntry));
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
const retainedBooking=(product:string,actor:()=>string)=>{const saved=restoreTradeRequest(requests(product,actor).read());return saved?.path==='tickets/book'?saved:undefined;};
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
 const entry:NewEntry|undefined=o.opened?undefined:o,mode=entry?'new':'booked';
 const owner=o.actor();
 const view=document.createElement('div'),booking=document.createElement('section');
 booking.className='ticket-booking';booking.setAttribute('role','status');booking.hidden=true;
 const loading=document.createElement('p');loading.className='side-panel-status';loading.textContent='Loading the ticket…';view.append(loading);
 host.replaceChildren(view,booking);
 const transport=entry&&requests(o.product,()=>owner);
 const pending=new PendingCommand(definite);
 let alive=true,d:TicketDescription|undefined,state:TicketState|undefined,actions:TradeActions|null=null,drawn:ReturnType<typeof renderTicket>|undefined;
 const current=()=>alive&&o.actor()===owner;
 let drawnKey='',drawnSchedules:[ScheduleView|null,ScheduleView|null]=[null,null];
 let panel:SchedulePanel={schedule:null,quantityUnit:null,previous:null,summaries:[],links:[],errors:{},status:{kind:'current',text:entry?'Not previewed yet. The ticket previews the trade after each change.':'Reading the booked trade…'}};
 const choices:Record<string,TicketChoice[]>={},choiceErrors:Record<string,string>={},lookupErrors:Record<string,string>={},asked=new Map<string,number>();
 const lifetime=new AbortController(),choiceReads=new Map<string,AbortController>();
 let fieldReads:Promise<void>=Promise.resolve(),lookupRead:AbortController|undefined,previewRead:AbortController|undefined;
 // Field reads share a server session lock. Dispatch one at a time so queued
 // fields do not spend their transport deadline waiting behind obsolete work.
 function readField<T>(path:string,body:unknown,signal:AbortSignal):Promise<T>{
  const result=fieldReads.then(()=>{signal.throwIfAborted();return o.api<T>(path,body,signal);});
  fieldReads=result.then(()=>{},()=>{});return result;
 }
 function cancelPreview(){previewRead?.abort();lookupRead?.abort();lookupRead=undefined;looked=undefined;}
 let selectedSide:string|null=null,tab='instrument',level='',closed=new Set<string>();
 // The selection the booked schedule shows, as the trade read reports it, and the day entry open, if any.
 const entryKey=o.opened&&!o.readOnly?JSON.stringify([o.product,owner,o.opened.tradeId]):undefined;
 let quantityPolicy:string|undefined,day=entryKey?entries.get(entryKey):undefined,recording=false;
 // The draft the ticket opened with, the last one previewed without refusal, and the one whose lookups were read last.
 let opened='',bookable:string|undefined,looked:string|undefined;
 let settling:ReturnType<typeof setTimeout>|undefined,queued=false,sending=false,booked:string|undefined,said='';
 const draft=()=>draftOf(state!),sent=()=>JSON.stringify(draft());
 const blocked=()=>!current()||o.readOnly||!entry||!state||!!pending.command||sending||booked!==undefined;

 function draw(){
  queued=false;
  if(!current()||!d||!state)return;
  const shown:TicketView={state,mode,actions,schedule:panel.schedule,quantityUnit:panel.quantityUnit,instant:o.instant,previous:panel.previous,summaries:panel.summaries,links:panel.links,entry:entryView(),status:panel.status,
   fieldErrors:entry?{...choiceErrors,...lookupErrors,...panel.errors}:{},choices,selectedSide,tab,level,closed,on};
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
  drawBooking();
 }
 // Answers that arrive together are drawn once.
 function later(){if(!queued){queued=true;setTimeout(draw);}}
 function fail(error:unknown){
  if(!current())return;
  const note=document.createElement('p');note.className='side-panel-status side-panel-error';note.setAttribute('role','alert');note.textContent=reasonOf(error);view.replaceChildren(note);
 }
 function drawBooking(){
  const command=pending.command,text=command?`${said?said+' ':''}The booking request is kept with its original terms until its outcome is known.`:said;
  booking.hidden=!text;booking.replaceChildren();if(!text)return;
  const line=document.createElement('p');line.textContent=text;booking.append(line);
  if(!command)return;
  for(const [label,run] of [['Check booking status',check],['Retry the original booking',send]] as const){
   const button=document.createElement('button');button.type='button';button.className='secondary';button.textContent=label;button.disabled=sending;button.onclick=()=>void run();booking.append(button);
  }
 }
 function say(text:string){said=text;draw();}

 // The fields that list choices: in a booked ticket only those holding a value, whose labels it shows.
 function fieldsOf(side:string|null){return side===null?d!.tradeFields:d!.sides.find(k=>k.kind===state!.sides.find(s=>s.code===side)!.kind)!.fields;}
 function valuesOf(side:string|null){return side===null?state!.trade:state!.sides.find(s=>s.code===side)!.values;}
 function everyField(pick:(f:TicketField,side:string|null)=>boolean):[string|null,TicketField][]{
  return [null,...state!.sides.map(s=>s.code)].flatMap(side=>fieldsOf(side).filter(f=>pick(f,side)).map(f=>[side,f] as [string|null,TicketField]));
 }
 const listed=()=>everyField((f,side)=>f.choices!==null&&(entry?f.state==='editable':!!valuesOf(side)[f.id]?.value));
 // The fields that depend on a field of the same side, however indirectly: their lists change with it.
 function dependents(side:string|null,field:string):[string|null,TicketField][]{
  const fields=fieldsOf(side),found:TicketField[]=[];
  for(const parents=[field];parents.length;){
   const parent=parents.shift()!;
   for(const child of fields.filter(f=>f.dependsOn.includes(parent)&&!found.includes(f))){found.push(child);parents.push(child.id);}
  }
  return found.filter(f=>f.choices!==null).map(f=>[side,f]);
 }
 async function readChoices(targets:[string|null,TicketField][]){
  await Promise.all(targets.map(async([side,f])=>{
   const key=keyOf(side,f.id),values=valuesOf(side),n=(asked.get(key)??0)+1;asked.set(key,n);
   choiceReads.get(key)?.abort();choiceReads.delete(key);
   // A list whose parent has no value yet lists nothing and is not asked for.
   if(f.dependsOn.some(parent=>!values[parent]?.value)){delete choices[key];delete choiceErrors[key];return;}
   const controller=new AbortController(),signal=AbortSignal.any([lifetime.signal,controller.signal]);choiceReads.set(key,controller);
   try{const r=await readField<{choices:TicketChoice[]}>('tickets/choices',{context:o.context(),draft:draft(),side,field:f.id},signal);if(signal.aborted||asked.get(key)!==n)return;choices[key]=r.choices;delete choiceErrors[key];}
   catch(error){if(signal.aborted||asked.get(key)!==n)return;choices[key]=[];choiceErrors[key]=reasonOf(error);}
   finally{if(choiceReads.get(key)===controller)choiceReads.delete(key);}
   later();
  }));
 }
 // Every lookup reads the whole draft, so all of them are read again once it changed.
 async function readLookups(){
  const asking=sent();if(asking===looked)return;looked=asking;
  lookupRead?.abort();const controller=lookupRead=new AbortController(),signal=AbortSignal.any([lifetime.signal,controller.signal]),context=o.context();
  const answers=await Promise.all(everyField(f=>f.lookup!==null).map(async([side,f])=>{
   try{return {side,f,found:await readField<TicketLookup>('tickets/lookup',{context,draft:JSON.parse(asking),side,field:f.id},signal)};}
   catch(error){return {side,f,found:undefined,refused:reasonOf(error)};}
  }));
  // Answers for a draft since edited are dropped, and that draft counts as unread, so returning to it reads its lookups again.
  if(signal.aborted||lookupRead!==controller)return;
  lookupRead=undefined;
  if(!alive||!state||sent()!==asking){if(looked===asking)looked=undefined;return;}
  for(const a of answers){
   const key=keyOf(a.side,a.f.id);
   if(a.found){state=applyLookup(state,a.side,a.f.id,a.found);delete lookupErrors[key];}
   else{state=dropLookup(state,a.side,a.f.id);lookupErrors[key]=a.refused!;}
  }
  // A lookup that filled a term changed the draft, which is previewed in turn.
  if(sent()!==asking)settle();
  draw();
 }
 function settle(){clearTimeout(settling);settling=setTimeout(()=>void preview(),settleMs);}
 async function preview(){
  clearTimeout(settling);settling=undefined;
  if(!state||blocked())return;
  previewRead?.abort();const controller=previewRead=new AbortController(),signal=AbortSignal.any([lifetime.signal,controller.signal]);
  const next=nextPreview(state),body={context:o.context(),draft:draftOf(next.state)},asking=JSON.stringify(body.draft);
  state=next.state;panel={...panel,status:{kind:'updating',text:'Updating…'}};draw();
  void readLookups();
  let outcome:{preview:TicketPreview}|{refused:unknown};
  try{outcome={preview:await o.api<TicketPreview>('tickets/preview',body,signal)};}catch(error){outcome={refused:error};}
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
  cancelPreview();
  said='';panel={...panel,status:{kind:'updating',text:'Updating…'}};draw();settle();
  void readChoices(lists==='all'?listed():lists);
 }
 const retry=()=>listed().filter(([side,f])=>keyOf(side,f.id) in choiceErrors);
 const on:TicketEvents={
  edit:(side,field,value)=>change(s=>setValue(s,side,field,value),side===null?'all':[...dependents(side,field),...retry()]),
  accept:(side,field)=>change(s=>acceptOffer(s,side,field),[]),
  selectSide:code=>{selectedSide=tab=code;draw();},
  tab:id=>{tab=id;if(state!.sides.some(s=>s.code===id))selectedSide=id;draw();},
  level:next=>{level=next;closed=new Set();draw();},
  toggle:(row,open)=>{if(open)closed.delete(row);else closed.add(row);},
  addSide:kind=>change(s=>addSide(s,kind),'all'),
  removeSide:code=>{
   if(selectedSide===code)selectedSide=null;if(tab===code)tab='instrument';
   for(const read of [choices,choiceErrors,lookupErrors])for(const key of Object.keys(read))if(key.startsWith(`${code}.`))delete read[key];
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
  const r=await o.api<TicketTrade>('tickets/trade',{context:o.context(),...o.opened!},lifetime.signal);
  if(!current())return;
  // A trade that records day quantities names the selection its schedule shows, whether or not this reader may record one here.
  if(r.actions.record.allowed&&!(typeof r.quantityPolicy==='string'&&r.quantityPolicy))throw Error('A booked read that offers day entries must name the quantity selection its schedule shows.');
  state=ticketOf(d!,r.draft);actions=o.readOnly?{...r.actions,record:{allowed:false,reason:'This trade window is read only.'}}:r.actions;quantityPolicy=r.quantityPolicy??undefined;
  panel=previewed(panel,{preview:r});
  if(panel.status.kind==='current')panel={...panel,status:{kind:'current',text:'Booked terms and their retained schedule.'}};
  draw();
 }

 function retain(){transport!.write(pending.command);}
 async function book(){
  if(!state||blocked())return;
  if(settling!==undefined||bookable!==sent()){say('Book once the preview of your latest change is up to date.');return;}
  pending.begin('tickets/book',{context:o.context(),draft:draft()});retain();
  await send();
 }
 async function send(){
  const command=pending.command;if(!current()||!command||sending)return;
  const uncertain=pending.sending();retain();sending=true;said='';draw();
  try{const r=await o.api<TicketReceipt>('tickets/book',{...command.body,operationId:command.operationId});sending=false;receipt(command,r);}
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
  if(r.operationId!==command.operationId||r.revision!==1||typeof r.tradeId!=='string'){say('The booking response could not be reconciled with its request. Check its status.');return;}
  pending.clear();retain();booked=r.tradeId;say(`Booked trade ${r.tradeId}.`);
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
    const saved=retainedBooking(o.product,()=>owner);
    if(saved){
     pending.command={...saved,uncertain:true};
     // Terms the ticket cannot show still leave the request to be checked or retried.
     try{state=ticketOf(d,saved.body.draft as TicketDraft);}catch(error){fail(error);drawBooking();return;}
    }else state=newTicket(d);
    opened=sent();
   }
   draw();void readChoices(listed());
  }catch(error){fail(error);}
 }
 void load();
 return {
  unsaved:()=>!!entry&&!!state&&booked===undefined&&(!!pending.command||sent()!==opened),
  pending:()=>!!pending.command||!!day?.pending.command,
  destroy(){alive=false;lifetime.abort();clearTimeout(settling);drawn?.destroy();host.replaceChildren();},
 };
}
