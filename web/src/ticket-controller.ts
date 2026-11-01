import {renderTicket,type TicketEvents} from './deal-ticket.js';
import {actorTransport,PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import {acceptOffer,acceptPreview,addSide,applyLookup,draftOf,dropLookup,newTicket,nextPreview,removeSide,setValue,ticketOf,type TicketState} from './ticket-model.js';
import {previewed,type SchedulePanel} from './ticket-preview.js';
import type {TicketChoice,TicketDescription,TicketDraft,TicketField,TicketLookup,TicketPreview,TicketReceipt,TicketTrade,TradeActions} from './ticket-types.js';

// Runs one deal ticket against the ticket routes (docs/deal-ticket.md). A new ticket reads the choices each field lists, previews the whole
// draft and reads its lookups once edits settle, and books through the retained request protocol; a booked one reads its trade and shows it.
// Every value, choice, lookup and schedule is the backend's; the controller only keeps what the person sees in step with what they did.
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Opened={tradeId:string;bindingId:string;revision:number};
type Entry={actor:()=>string;booked:(receipt:{tradeId:string})=>void};
export type TicketOptions={api:Api;context:()=>Record<string,unknown>;product:string}&({opened:Opened}|({opened?:undefined}&Entry));
export type TicketHandle={unsaved():boolean;pending():boolean;destroy():void};

/** Edits settle this long before the draft is previewed and its lookups read. */
const settleMs=300;
const keyOf=(side:string|null,field:string)=>side===null?field:`${side}.${field}`;
const reason=(error:unknown)=>error instanceof Error?error.message:String(error);
// A booking refused with one of these codes wrote nothing; any other failure leaves its outcome unknown.
const definite=(code:string|undefined)=>!!code&&(code.startsWith('gas_')||code.startsWith('ticket_')||['access_denied','historical_read_only','numeric_format','request_format','swap_context','trade_party'].includes(code));

// A booking request outlives the ticket that sent it, per product and person, until its outcome is known.
const transports=new Map<string,ReturnType<typeof actorTransport<RequestCommand>>>();
function requests(product:string,actor:()=>string){
 let transport=transports.get(product);
 if(!transport)transports.set(product,transport=actorTransport<RequestCommand>(()=>`deal-ticket-request:${product}:${actor()}`,()=>sessionStorage));
 return transport;
}
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
 const entry:Entry|undefined=o.opened?undefined:o,mode=entry?'new':'booked';
 const view=document.createElement('div'),booking=document.createElement('section');
 booking.className='ticket-booking';booking.setAttribute('role','status');booking.hidden=true;
 const loading=document.createElement('p');loading.className='side-panel-status';loading.textContent='Loading the ticket…';view.append(loading);
 host.replaceChildren(view,booking);
 const transport=entry&&requests(o.product,entry.actor);
 const pending=new PendingCommand(definite);
 let alive=true,d:TicketDescription|undefined,state:TicketState|undefined,actions:TradeActions|null=null,drawn:{destroy():void}|undefined;
 let panel:SchedulePanel={schedule:null,previous:null,summaries:[],errors:{},status:{kind:'current',text:entry?'Not previewed yet. The ticket previews the trade after each change.':'Reading the booked trade…'}};
 const choices:Record<string,TicketChoice[]>={},choiceErrors:Record<string,string>={},lookupErrors:Record<string,string>={},asked=new Map<string,number>();
 let selectedSide:string|null=null,tab='instrument',level='',closed=new Set<string>();
 // The draft the ticket opened with, the last one previewed without refusal, and the one whose lookups were read last.
 let opened='',bookable:string|undefined,looked:string|undefined;
 let settling:ReturnType<typeof setTimeout>|undefined,queued=false,sending=false,booked:string|undefined,said='';
 const draft=()=>draftOf(state!),sent=()=>JSON.stringify(draft());
 const blocked=()=>!entry||!state||!!pending.command||sending||booked!==undefined;

 function draw(){
  queued=false;
  if(!alive||!d||!state)return;
  const at=focusIn(view);drawn?.destroy();drawn=undefined;
  // A view the ticket cannot draw says why in its place.
  try{drawn=renderTicket(view,{state,mode,actions,schedule:panel.schedule,previous:panel.previous,summaries:panel.summaries,status:panel.status,
   fieldErrors:entry?{...choiceErrors,...lookupErrors,...panel.errors}:{},choices,selectedSide,tab,level,closed,on});}
  catch(error){fail(error);return;}
  view.inert=!!entry&&blocked();
  refocus(view,at);drawBooking();
 }
 // Answers that arrive together are drawn once.
 function later(){if(!queued){queued=true;setTimeout(draw);}}
 function fail(error:unknown){
  if(!alive)return;
  const note=document.createElement('p');note.className='side-panel-status side-panel-error';note.setAttribute('role','alert');note.textContent=reason(error);view.replaceChildren(note);
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
   // A list whose parent has no value yet lists nothing and is not asked for.
   if(f.dependsOn.some(parent=>!values[parent]?.value)){delete choices[key];delete choiceErrors[key];return;}
   try{const r=await o.api<{choices:TicketChoice[]}>('tickets/choices',{context:o.context(),draft:draft(),side,field:f.id});if(asked.get(key)!==n)return;choices[key]=r.choices;delete choiceErrors[key];}
   catch(error){if(asked.get(key)!==n)return;choices[key]=[];choiceErrors[key]=reason(error);}
   later();
  }));
 }
 // Every lookup reads the whole draft, so all of them are read again once it changed; an answer for a draft since edited is dropped.
 async function readLookups(){
  const asking=sent();if(asking===looked)return;looked=asking;
  const answers=await Promise.all(everyField(f=>f.lookup!==null).map(async([side,f])=>{
   try{return {side,f,found:await o.api<TicketLookup>('tickets/lookup',{context:o.context(),draft:JSON.parse(asking),side,field:f.id})};}
   catch(error){return {side,f,found:undefined,refused:reason(error)};}
  }));
  if(!alive||!state||sent()!==asking)return;
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
  const next=nextPreview(state),body={context:o.context(),draft:draftOf(next.state)},asking=JSON.stringify(body.draft);
  state=next.state;panel={...panel,status:{kind:'updating',text:'Updating…'}};draw();
  void readLookups();
  let outcome:{preview:TicketPreview}|{refused:unknown};
  try{outcome={preview:await o.api<TicketPreview>('tickets/preview',body)};}catch(error){outcome={refused:error};}
  // Only the latest preview is drawn: an answer that arrives after a newer edit or preview is discarded.
  if(!alive||!state||!acceptPreview(state,next.serial))return;
  try{panel=previewed(panel,outcome);bookable='preview' in outcome?asking:undefined;}
  catch(error){panel=previewed(panel,{refused:error});bookable=undefined;}
  draw();
 }
 // One change of the person's: it is drawn at once, any preview still out is outrun, and the draft previews once changes settle.
 function change(apply:(s:TicketState)=>TicketState,lists:'all'|[string|null,TicketField][]){
  if(!state||blocked())return;
  try{state=nextPreview(apply({...state,notices:[]})).state;}
  catch(error){state={...state,notices:[reason(error)]};draw();return;}
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
   change(s=>removeSide(s,code),'all');
  },
  preview:()=>void preview(),
  book:()=>void book(),
 };

 function retain(){transport!.write(pending.command);}
 async function book(){
  if(!state||blocked())return;
  if(settling!==undefined||bookable!==sent()){say('Book once the preview of your latest change is up to date.');return;}
  pending.begin('tickets/book',{context:o.context(),draft:draft()});retain();
  await send();
 }
 async function send(){
  const command=pending.command;if(!command||sending)return;
  const uncertain=pending.sending();retain();sending=true;said='';draw();
  try{const r=await o.api<TicketReceipt>('tickets/book',{...command.body,operationId:command.operationId});sending=false;receipt(command,r);}
  catch(error){
   sending=false;pending.failed((error as {code?:string}).code,uncertain);retain();
   say(pending.command?`The booking is not confirmed: ${reason(error)}`:`Booking refused: ${reason(error)}`);
  }
 }
 async function check(){
  const command=pending.command;if(!command||sending)return;
  try{
   const r=await o.api<TicketReceipt|{found:false}>('tickets/status',{operationId:command.operationId});
   if('tradeId' in r)receipt(command,r);else say('No completed booking was found. Retry the original booking.');
  }catch(error){say(`The booking status could not be read: ${reason(error)}`);}
 }
 function receipt(command:RequestCommand,r:TicketReceipt){
  if(r.operationId!==command.operationId||r.revision!==1||typeof r.tradeId!=='string'){say('The booking response could not be reconciled with its request. Check its status.');return;}
  pending.clear();retain();booked=r.tradeId;say(`Booked trade ${r.tradeId}.`);
  entry!.booked({tradeId:r.tradeId});
 }

 async function load(){
  try{
   d=await o.api<TicketDescription>('tickets/description',{context:o.context(),product:o.product});
   if(!alive)return;
   level=d.levels[0];
   if(o.opened){
    const r=await o.api<TicketTrade>('tickets/trade',{context:o.context(),...o.opened});
    if(!alive)return;
    state=ticketOf(d,r.draft);actions=r.actions;
    panel=previewed(panel,{preview:r});
    if(panel.status.kind==='current')panel={...panel,status:{kind:'current',text:'Booked terms and their retained schedule.'}};
   }else{
    // A booking whose outcome is unknown opens with the exact terms it was sent with, to be checked or retried, never edited.
    const saved=retainedBooking(o.product,entry!.actor);
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
  pending:()=>!!pending.command,
  destroy(){alive=false;clearTimeout(settling);drawn?.destroy();host.replaceChildren();},
 };
}
