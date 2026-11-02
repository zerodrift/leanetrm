import type {Holding} from './desk-model.js';
import {trackEdits} from './edit-tracking.js';
import {keepOffered,noOptionPick,optionChoices,optionLevels,pickOption,type LifecycleWrites,type OptionCatalog,type OptionLevel,type OptionPick} from './futures-option-model.js';
import {displayInstant,publicText} from './market-view.js';
import {bookingAllowed} from './new-trade.js';
import {actorTransport,PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import type {DetailRead,TermSection} from './position-panel.js';
import {tradeAnnotations,type PartyCatalog,type Trader} from './trade-annotations.js';

// Listed futures options (D288): New trade's option ticket and the option position's lifecycle actions. Every series, value, rule and
// refusal is the backend's; the page lays them out, one field per row, and sends what the person entered to the authorized operations.
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
/** The booking and lifecycle convention option operations state. */
export const optionConvention='futures_option_premium_v1';
type Catalog=OptionCatalog&PartyCatalog&{books:{id:string;name:string;portfolio:string}[];bookingActor:Trader;canBook:boolean;bookingUnavailableReason?:string|null};
type Receipt={operationId:string;tradeId:string;revision:number};
// A request refused with one of these codes wrote nothing; any other failure leaves its outcome unknown until it is checked or retried.
const definite=(code:string|undefined)=>!!code&&(code.startsWith('option_')||['access_denied','historical_read_only','numeric_format','request_format','trade_party','trade_annotation','trade_booking_read_only'].includes(code));
const reason=(error:unknown)=>error instanceof Error?error.message:String(error);
const codeOf=(error:unknown)=>(error as {code?:string}).code;

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
 const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=publicText(text);return node;
}
function section(title:string){const box=el('section','option-section'),head=el('h3','option-section-title',title);box.append(head);return box;}
/** One labelled control in its own row. */
function row(id:string,label:string,control:HTMLElement){
 const line=el('div','option-row'),caption=el('label',undefined,label);caption.htmlFor=id;control.id=id;line.append(caption,control);return line;
}
/** A read-only value from the backend, as a label and value in the same column. */
function fact(label:string){const line=el('div','option-row option-fact'),term=el('span','option-fact-label',label),value=el('span','option-fact-value','—');line.append(term,value);return {line,value};}
/** A control drawn but not yet hooked up: disabled, marked Coming soon, holding and sending nothing. */
function soon(label:string,later:string){
 const line=el('div','option-row ticket-soon'),caption=el('span','option-fact-label',label),control=el('input','ticket-control');
 control.disabled=true;control.setAttribute('aria-label',label);control.title=later;line.append(caption,control,el('span','ticket-soon-tag','Coming soon'));return line;
}
function soonButton(label:string,later:string){const button=el('button','ticket-soon',label);button.type='button';button.disabled=true;button.title=later;button.append(el('span','ticket-soon-tag','Coming soon'));return button;}
function options(select:HTMLSelectElement,choices:readonly {value:string;label:string}[],value:string){
 select.replaceChildren(new Option('Choose…',''),...choices.map(c=>new Option(publicText(c.label),c.value)));select.value=value;select.disabled=!choices.length;
}

const levelLabels:Record<OptionLevel,string>={exchange:'Exchange',commodity:'Commodity',underlying:'Underlying future',right:'Call / put',strike:'Strike'};

/** New trade's futures option form: the listed series picked from the backend's catalog, booked with premium paid up front. */
export function setupOptionTicket(o:{api:Api;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;instant:(at:string)=>string;
 zone:()=>string;onBooked:(tradeId:string)=>Promise<void>}){
 // A booking request outlives the form that sent it, per person, until its outcome is known.
 const requests=actorTransport<RequestCommand>(()=>`option-ticket-request:${o.actor()}`,()=>sessionStorage);
 // The form keeps its controls' states itself: the page's busy state leaves its coming-soon items disabled and its cascade as drawn.
 const root=document.getElementById('option-page')!;root.classList.add('option-ticket');root.dataset.ownControls='';
 const status=el('p','side-panel-status');status.setAttribute('role','status');
 const form=el('form'),fields=el('fieldset','option-fields'),edits=trackEdits(form);
 const trade=section('Trade'),terms=el('div');terms.id='option-trade-terms';trade.append(terms,soon('Custom trade fields','Custom trade fields are coming soon.'));
 const contract=section('Contract'),selects=Object.fromEntries(optionLevels.map(level=>[level,el('select')])) as Record<OptionLevel,HTMLSelectElement>;
 for(const level of optionLevels)contract.append(row(`option-${level}`,levelLabels[level],selects[level]));
 const expiry=fact('Expiry'),code=fact('Exchange contract'),series=fact('Series');contract.append(expiry.line,code.line,series.line);
 const execution=section('Execution'),side=el('select'),lots=el('input'),premium=el('input'),paid=el('input'),style=el('select');
 side.append(new Option('Choose…',''),new Option('Buy','buy'),new Option('Sell','sell'));lots.inputMode='numeric';premium.inputMode='decimal';paid.type='date';
 style.append(new Option('Choose…',''),new Option('Paid up front','premium_paid_up_front'));
 const margined=new Option('Margined daily — Coming soon','futures_style_margin');margined.disabled=true;style.append(margined);
 execution.append(row('option-side','Buy / sell',side),row('option-lots','Lots',lots),row('option-premium','Premium',premium),row('option-premium-date','Premium payment date',paid),
  row('option-premium-style','Premium style',style));
 const executed=el('div');execution.append(executed,soon('Executing broker','Executing brokers are coming soon.'));
 const backend=section('From the backend'),settlement=fact('Settlement at this close'),multiplier=fact('Multiplier'),tick=fact('Premium tick'),range=fact('Premium range'),
  exercise=fact('Exercise style'),instructions=fact('Contrary instructions');
 backend.append(settlement.line,multiplier.line,tick.line,range.line,exercise.line,instructions.line);
 const more=section('More'),later=el('div','option-soon-actions');
 for(const label of ['Comments','Broker fees','Settlement instructions','Credit check','Risk-limit check','Compare versions','Import / export','Print'])later.append(soonButton(label,`${label} is coming soon.`));
 more.append(later);
 const submit=el('button',undefined,'Book option');submit.type='submit';submit.id='option-book-submit';
 const booking=el('div','option-booking'),said=el('p','field-help');said.id='option-booking-status';said.setAttribute('role','status');
 const check=el('button','secondary','Check booking status'),retry=el('button','secondary','Retry the original booking');check.type=retry.type='button';check.id='option-check';retry.id='option-retry';
 booking.append(submit,check,retry,said);
 fields.append(trade,contract,execution,backend,more);form.append(fields,booking);root.replaceChildren(status,form);
 const annotations=tradeAnnotations('option',o.zone,executed);
 const pending=new PendingCommand(definite);
 let catalog:Catalog|undefined,pick:OptionPick={...noOptionPick},sending=false;

 function refreshDisplay(){
  annotations.refresh();
  const s=catalog?optionChoices(catalog,pick).series:null;
  expiry.value.textContent=s?o.instant(s.expiryAt):'—';
 }
 function draw(){
  if(!catalog)return;
  const shown=optionChoices(catalog,pick),s=shown.series;
  for(const level of optionLevels)options(selects[level],shown[level],pick[level]);
  refreshDisplay();code.value.textContent=s?publicText(`${s.name} · ${s.product}`):'—';series.value.textContent=s?publicText(s.series):'—';
  settlement.value.textContent=s?`${s.settlementPrice} ${s.currency}`:'—';multiplier.value.textContent=s?s.multiplier:'—';tick.value.textContent=s?s.premiumTick:'—';
  range.value.textContent=s?`${s.minimumPremium} to ${s.maximumPremium} ${s.currency}`:'—';exercise.value.textContent=s?publicText(s.exerciseStyle):'—';
  instructions.value.textContent=s?(s.contraryInstructionsAllowed?'Accepted':'Not accepted'):'—';
  sync();
 }
 function sync(){
  const held=!!pending.command;
  fields.disabled=!catalog||held||sending;
  submit.disabled=!catalog||!bookingAllowed(catalog).enabled||held||sending;
  check.hidden=retry.hidden=!held;check.disabled=retry.disabled=sending;
 }
 // A catalog that cannot answer a selection, such as one listing two series for it, is said so, and nothing is picked.
 for(const level of optionLevels)selects[level].onchange=()=>{
  if(!catalog)return;
  try{pick=pickOption(catalog,pick,level,selects[level].value);draw();}
  catch(error){pick=pickOption(catalog,pick,level,'');said.textContent=reason(error);draw();}
 };
 function retain(){requests.write(pending.command);}
 async function send(){
  const command=pending.command;if(!command||sending)return;
  // The view is checked when the request is sent, not only when the form opened: a historical context books nothing.
  if(o.historical()){said.textContent='Return to working data to book a futures option.';return;}
  const uncertain=pending.sending();retain();sending=true;said.textContent='Booking…';sync();
  try{const r=await o.api<Receipt>('options/book',{...command.body,operationId:command.operationId});sending=false;await receipt(command,r);}
  catch(error){
   sending=false;pending.failed(codeOf(error),uncertain);retain();
   said.textContent=pending.command?`The booking is not confirmed: ${reason(error)}`:`Booking refused: ${reason(error)}`;
  }finally{sync();}
 }
 async function receipt(command:RequestCommand,r:Receipt){
  if(r.operationId!==command.operationId||typeof r.tradeId!=='string'){said.textContent='The booking response could not be reconciled with its request. Check its status.';return;}
  pending.clear();retain();edits.clear();said.textContent=`Booked trade ${r.tradeId}.`;
  await o.onBooked(r.tradeId);
 }
 form.onsubmit=event=>{
  event.preventDefault();
  if(!catalog||pending.command)return;
  const allowed=bookingAllowed(catalog);if(!allowed.enabled){said.textContent=allowed.reason;return;}
  const chosen=optionChoices(catalog,pick).series;if(!chosen){said.textContent='Choose the exchange, commodity, underlying future, call or put and strike.';return;}
  if(!side.value){said.textContent='Choose buy or sell.';return;}
  pending.begin('options/book',{...annotations.read(),snapshotId:o.source().snapshotId,convention:optionConvention,knowledgeMode:'current',bookId:(document.getElementById('option-book') as HTMLSelectElement).value,
   series:chosen.series,side:side.value,lots:lots.value,premium:premium.value,premiumPaymentDate:paid.value||null,premiumStyle:style.value||null},undefined,'Futures option booking');
  retain();void send();
 };
 retry.onclick=()=>void send();
 check.onclick=()=>void (async()=>{
  const command=pending.command;if(!command||sending)return;
  try{const r=await o.api<Receipt|{found:false}>('options/status',{operationId:command.operationId});
   if('tradeId' in r)await receipt(command,r);else said.textContent='No completed booking was found. Retry the original booking.';}
  catch(error){said.textContent=`The booking status could not be read: ${reason(error)}`;}
  finally{sync();}
 })();
 function reset(){catalog=undefined;pick={...noOptionPick};sending=false;pending.clear();form.reset();annotations.reset();edits.clear();said.textContent='';status.textContent='';sync();}
 return {root,reset,
  async begin(){
   reset();
   if(o.historical())throw Error('Return to working data to enter a futures option trade.');
   status.textContent='Loading the listed options…';
   const read=await o.api<Catalog>('options/catalog',{snapshotId:o.source().snapshotId,convention:optionConvention,knowledgeMode:'current'});
   catalog=read;pick=keepOffered(read,pick);
   const books=document.getElementById('option-book') as HTMLSelectElement;
   books.replaceChildren(new Option('Choose…',''),...read.books.map(b=>new Option(publicText(`${b.portfolio} · ${b.name}`),b.id)));
   annotations.populate(read.bookingActor,read);
   const allowed=bookingAllowed(read);status.textContent=allowed.enabled?'Pick the listed series, then enter the execution.':allowed.reason;
   // A booking whose outcome is unknown is shown as it was sent, to be checked or retried, never edited.
   const saved=restoreTradeRequest(requests.read());
   if(saved?.path==='options/book'){
    pending.command={...saved,uncertain:true};
    said.textContent=publicText(`A booking of ${saved.body.lots} lots of ${saved.body.series} awaits its outcome. Check its status or retry it as it was sent.`);
   }
   draw();
  },
  inspect:()=>Promise.reject(Error('A booked futures option opens in the Details pane.')),
  unsaved:()=>!!catalog&&(edits.edited()||!!pending.command),
  pending:()=>!!pending.command||restoreTradeRequest(requests.read())?.path==='options/book',
  // Display changes preserve the selection, edits and the exact pending request.
  sync:refreshDisplay};
}

type Outcome={eventId:string;kind:string;lots:string|null;remainingLots:string;futuresTradeId:string|null;businessAt:string;outcome?:string|null};
type HoldingRead={tradeId:string;lots:string;openLots:string;events:Outcome[]};
const eventNames:Record<string,string>={exercise:'Exercise',assignment:'Assignment',expiry:'Expiry',exercise_instruction:'Exercise at expiry instruction',abandon_instruction:'Abandon instruction'};
const lotsWord=(lots:string)=>`${lots} ${lots.replace(/^-/,'')==='1'?'lot':'lots'}`;
/** What an option view needs of its host: whether lifecycle events may be recorded now, asked again when one is sent; who is recording,
 *  whose unanswered requests it keeps; and what hears a recorded event. */
export type OptionHost={writes:()=>LifecycleWrites|Promise<LifecycleWrites>;actor:()=>string;recorded:(note:string)=>void};

/**
 * An option trade's Details: its series, lots, settlement and value per contract from the Blotter row, its recorded events from
 * options/holding, and its lifecycle actions, each offered as the row's `actions` state, with the backend's reason when it is not.
 */
export async function readOptionView(api:Api,context:Record<string,unknown>&{snapshotId:string},h:Holding,host:OptionHost):Promise<DetailRead>{
 const held=await api<HoldingRead>('options/holding',{context:{...context,convention:optionConvention},tradeId:h.tradeId});
 if(held.tradeId!==h.tradeId)throw Error('The option holding read is for another trade. Refresh the Blotter.');
 const option=h.component==='option',writes=await host.writes();
 return {amend:{allowed:false,reason:'Option trades are not amended; exercise, assignment and expiry are recorded as their own events.'},view:zone=>{
  const terms:TermSection[]=[{title:'Futures option trade',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Exchange',h.exchange?.name??''],
    ['Product',h.product??''],['Series',h.series??''],['Call / put',h.right??''],['Strike',h.strike??''],['Expiry',h.expiryAt?displayInstant(h.expiryAt,zone):''],['Underlying future',h.underlyingSeries??'']]},
   {title:'Lots and lifecycle',rows:[['Booked lots',held.lots],['Open lots',held.openLots],['Lifecycle',publicText(h.lifecycleState??'')],
    ['Linked futures trades',h.linkedFuturesTradeIds?.length?h.linkedFuturesTradeIds.join(', '):'None']]},
   option?{title:'From the backend',rows:[['Settlement price',h.settlement??'None'],['Value per contract',h.unitPresentValue?`${h.unitPresentValue} ${h.currency}`:'None'],
    ['Implied volatility',h.impliedVolatility??'None'],['Calibration mode',h.calibrationMode??'None'],['Numerical policy',h.numericalPolicy??'None'],['Value measure','Option value excluding settled premium cash']]}
    :{title:'Premium paid up front',rows:[['Premium',h.premium??''],['Premium cash',`${h.premiumAmount} ${h.currency}`],['Payment date',h.premiumPaymentDate??''],['Cash state',h.cashState??'']]}];
  const history:TermSection[]=[{title:`Revision ${h.revision}`,rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]},
   ...held.events.map(e=>({title:eventNames[e.kind]??publicText(e.kind),rows:[['Recorded for',displayInstant(e.businessAt,zone)],['Lots',e.lots??'Open lots at expiry'],['Open lots after',e.remainingLots],
    ['Linked future',e.futuresTradeId??'None'],...(e.outcome?[['Outcome',publicText(e.outcome)] as const]:[])] as const}))];
  return {terms,history,...(option&&h.actions?{schedules:(box:HTMLElement)=>lifecycle(box,api,context.snapshotId,h,writes,host)}:{})};
 }};
}

// Each lifecycle request outlives the view that sent it, per person and trade, until its outcome is known: a redraw offers only its retry.
const lifecycleRequests=new Map<string,ReturnType<typeof actorTransport<RequestCommand>>>();
function lifecycleTransport(actor:()=>string,trade:string){
 let transport=lifecycleRequests.get(trade);
 if(!transport)lifecycleRequests.set(trade,transport=actorTransport<RequestCommand>(()=>`option-lifecycle-request:${actor()}:${trade}`,()=>sessionStorage));
 return transport;
}

// The holding's lifecycle actions, drawn from the row's `actions` and the host's `writes`; each request is sent once with its own identity
// and retried with it until its outcome is known. Once an event is recorded, the view offers no other.
function lifecycle(host:HTMLElement,api:Api,snapshotId:string,h:Holding,writes:LifecycleWrites,o:OptionHost){
 const actions=h.actions!,box=section('Lifecycle');box.classList.add('option-lifecycle');box.dataset.ownControls='';host.append(box);
 const blocked=el('p','field-help option-lifecycle-reason'),said=el('p','field-help');said.setAttribute('role','status');
 const transport=lifecycleTransport(o.actor,h.tradeId),pending=new PendingCommand(definite);let sending=false,recorded=false;
 const saved=restoreTradeRequest(transport.read());if(saved)pending.command={...saved,uncertain:true};
 const lots=el('input'),all=el('button','secondary','All open lots'),retry=el('button',undefined,'Retry the original request');
 lots.inputMode='numeric';all.type=retry.type='button';all.id='option-exercise-all';retry.id='option-lifecycle-retry';
 const button=(id:string,label:string,action:keyof NonNullable<Holding['actions']>)=>{const b=el('button','secondary',label);b.type='button';b.id=id;b.dataset.for=action;return b;};
 const exercise=button('option-exercise','Exercise','exercise'),assign=button('option-assign','Record assignment','assignment'),
  abandon=button('option-abandon','Abandon at expiry','instruction'),atExpiry=button('option-instruct-exercise','Exercise at expiry','instruction');
 const why=(action:keyof NonNullable<Holding['actions']>)=>{const line=el('p','field-help option-action-reason',actions[action].reason??'');line.dataset.action=action;line.hidden=actions[action].allowed;return line;};
 box.append(blocked,row('option-exercise-lots','Lots',lots),all,exercise,assign,why('assignment'),why('exercise'),abandon,atExpiry,why('instruction'),retry,said);
 if(saved)said.textContent=publicText(`The ${saved.label??'lifecycle'} request for this holding awaits its outcome. Retry it as it was sent.`);
 function sync(){
  const held=!!pending.command,open=writes.enabled&&!recorded&&!held&&!sending;
  blocked.textContent=recorded?'An event was recorded here. Open the trade again from the Blotter to read it at current knowledge.':writes.enabled?'':writes.reason;blocked.hidden=!blocked.textContent;
  for(const b of [exercise,assign,abandon,atExpiry])b.disabled=!open||!actions[b.dataset.for as keyof typeof actions].allowed;
  lots.disabled=all.disabled=!open||!(actions.exercise.allowed||actions.assignment.allowed);
  retry.hidden=!held;retry.disabled=sending||!writes.enabled;
 }
 async function send(){
  const command=pending.command;if(!command||sending)return;
  sending=true;said.textContent='Checking that events may be recorded…';sync();
  // Null until the request is sent: a failure before then sent nothing, and the request is kept as it was entered.
  let uncertain:boolean|null=null;
  try{
   // The view is asked again when the request is sent: its context or the person's authority may have changed since it was drawn.
   const now=await o.writes();if(!now.enabled){writes=now;said.textContent=now.reason;return;}
   uncertain=pending.sending();transport.write(pending.command);said.textContent='Recording…';
   const r=await api<Outcome>(command.path,{...command.body,operationId:command.operationId});pending.clear();transport.write(undefined);recorded=true;
   const note=`${eventNames[r.kind]??publicText(r.kind)} recorded${r.lots?` for ${lotsWord(r.lots)}`:''} · ${lotsWord(r.remainingLots)} open${r.futuresTradeId?` · linked future ${r.futuresTradeId}`:''}.`;
   said.textContent=note;o.recorded(note);
  }catch(error){
   if(uncertain===null){said.textContent=publicText(`Not sent: ${reason(error)} Retry the request once events may be recorded.`);return;}
   pending.failed(codeOf(error),uncertain);transport.write(pending.command);
   said.textContent=pending.command?`Not confirmed: ${reason(error)} The request awaits its outcome; retry it as it was sent.`:`Refused: ${reason(error)}`;
  }finally{sending=false;sync();}
 }
 const begin=(path:string,label:string,body:Record<string,unknown>)=>{
  if(pending.command)return;
  pending.begin(path,{snapshotId,convention:optionConvention,knowledgeMode:'current',tradeId:h.tradeId,...body},undefined,label);void send();
 };
 all.onclick=()=>{lots.value=h.closableLots??'';};
 exercise.onclick=()=>begin('options/exercise','exercise',{lots:lots.value});
 assign.onclick=()=>begin('options/assignment','assignment',{lots:lots.value});
 abandon.onclick=()=>begin('options/instruction','abandon instruction',{instruction:'abandon'});
 atExpiry.onclick=()=>begin('options/instruction','exercise at expiry instruction',{instruction:'exercise'});
 retry.onclick=()=>void send();
 sync();
 return ()=>box.remove();
}
