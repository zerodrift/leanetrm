import {isCashStatus,cashStatusLabels} from './cash-status.js';
import type {Holding} from './desk-model.js';
import {trackEdits} from './edit-tracking.js';
import {keepOffered,noOptionPick,optionChoices,optionLevels,optionTicketStatus,pickOption,underlyingLabel,type OptionCatalog,type OptionLevel,type OptionPick} from './futures-option-model.js';
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

/** New trade's futures option form: the listed series picked from the backend's catalog, booked with premium paid up front. Once a
 *  series is chosen, `surface` opens its product's volatility surface. */
export function setupOptionTicket(o:{api:Api;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;instant:(at:string)=>string;
 zone:()=>string;onBooked:(tradeId:string)=>Promise<void>;surface:(product:string,definitionRevisionId:string)=>void}){
 // A booking request outlives the form that sent it, per person, until its outcome is known.
 const requests=actorTransport<RequestCommand>(()=>`option-ticket-request:${o.actor()}`,()=>sessionStorage);
 // The form keeps its controls' states itself: the page's busy state leaves its coming-soon items disabled and its cascade as drawn.
 const root=document.getElementById('option-page')!;root.classList.add('option-ticket');root.dataset.ownControls='';
 const status=el('p','side-panel-status');status.setAttribute('role','status');
 const form=el('form'),fields=el('fieldset','option-fields'),edits=trackEdits(form);
 const trade=section('Trade'),terms=el('div');terms.id='option-trade-terms';trade.append(terms,soon('Custom trade fields','Custom trade fields are coming soon.'));
 const contract=section('Contract'),selects=Object.fromEntries(optionLevels.map(level=>[level,el('select')])) as Record<OptionLevel,HTMLSelectElement>;
 for(const level of optionLevels)contract.append(row(`option-${level}`,levelLabels[level],selects[level]));
 const expiry=fact('Expiry'),code=fact('Exchange contract'),series=fact('Series'),surface=el('button','secondary','Volatility surface');
 surface.type='button';surface.id='option-ticket-vol-surface';surface.hidden=true;contract.append(expiry.line,code.line,series.line,surface);
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
  surface.hidden=!s;surface.onclick=s?()=>o.surface(s.product,s.definitionRevisionId):null;
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
  pending.clear();retain();edits.clear();said.textContent='Trade booked.';
  await o.onBooked(r.tradeId);
 }
 form.onsubmit=event=>{
  event.preventDefault();
  if(!catalog||pending.command)return;
  const allowed=bookingAllowed(catalog);if(!allowed.enabled){said.textContent=allowed.reason;return;}
  const chosen=optionChoices(catalog,pick).series;if(!chosen){said.textContent='Choose the exchange, commodity, underlying future, call or put and strike.';return;}
  if(!side.value){said.textContent='Choose buy or sell.';return;}
  pending.begin('options/book',{...annotations.read(),snapshotId:o.source().snapshotId,convention:optionConvention,knowledgeMode:'current',bookId:(document.getElementById('option-book') as HTMLSelectElement).value,
   definitionRevisionId:chosen.definitionRevisionId,series:chosen.series,side:side.value,lots:lots.value,premium:premium.value,premiumPaymentDate:paid.value||null,premiumStyle:style.value||null},undefined,'Futures option booking');
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
 function reset(){catalog=undefined;pick={...noOptionPick};surface.hidden=true;surface.onclick=null;sending=false;pending.clear();form.reset();annotations.reset();edits.clear();said.textContent='';status.textContent='';sync();}
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
   status.textContent=optionTicketStatus(read);
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

type Outcome={eventId:string;kind:string;lots:string|null;remainingLots:string;futuresTradeId:string|null;futuresTradeReference?:string|null;businessAt:string;outcome?:string|null};
type HoldingRead={tradeId:string;lots:string;openLots:string;events:Outcome[]};
const eventNames:Record<string,string>={exercise:'Exercise',assignment:'Assignment',expiry:'Expiry',exercise_instruction:'Exercise at expiry instruction',abandon_instruction:'Abandon instruction'};
/** What an option view needs of its host: whether lifecycle events may be recorded now, asked again when one is sent; who is recording,
 *  whose unanswered requests it keeps; what hears a recorded event; and what opens a product's volatility surface, null where the
 *  host has no Vol surfaces page. */
export type OptionHost={actor:()=>string;recorded:(note:string)=>void;surface:((product:string,definitionRevisionId:string)=>void)|null;lifecycle?:()=>void};

/**
 * An option trade's Details: its series, lots, settlement and value per contract from the Blotter row, its recorded events from
 * options/holding, and its lifecycle actions, each offered as the row's `actions` state, with the backend's reason when it is not.
 */
export async function readOptionView(api:Api,context:Record<string,unknown>&{snapshotId:string},h:Holding,host:OptionHost):Promise<DetailRead>{
 const held=await api<HoldingRead>('options/holding',{context:{...context,convention:optionConvention},tradeId:h.tradeId});
 if(held.tradeId!==h.tradeId)throw Error('The option holding read is for another trade. Refresh the Blotter.');
 const option=h.component==='option';
 let widget:ReturnType<typeof lifecycle>|undefined;
 return {amend:{allowed:false,reason:'Option trades are not amended; exercise, assignment and expiry are recorded as their own events.'},updatesBlocked:()=>!!widget?.blocked(),view:zone=>{
  const terms:TermSection[]=[{title:'Futures option trade',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Exchange',h.exchange?.name??''],
    ['Product',h.productName??''],['Call / put',h.right??''],['Strike',h.strike??''],['Expiry',h.expiryAt?displayInstant(h.expiryAt,zone):''],
    ['Underlying future',h.underlyingName&&h.deliveryFrom&&h.deliveryTo?underlyingLabel(h.deliveryFrom,h.deliveryTo,h.underlyingName):'']]},
   {title:'Lots and lifecycle',rows:[['Booked lots',held.lots],['Open lots',held.openLots],['Lifecycle',publicText(h.lifecycleState??'')],
    ['Linked futures trades',h.linkedFuturesTrades?.length?h.linkedFuturesTrades.join(', '):'None']]},
   option?{title:'From the backend',rows:[['Settlement price',h.settlement??'None'],['Value per contract',h.unitPresentValue?`${h.unitPresentValue} ${h.currency}`:'None'],
    ['Implied volatility',h.impliedVolatility??'None'],['Calibration mode',h.calibrationMode??'None'],['Numerical policy',h.numericalPolicy??'None'],['Value measure','Option value excluding settled premium cash']]}
    :{title:'Premium paid up front',rows:[['Premium',h.premium??''],['Premium cash',`${h.premiumAmount} ${h.currency}`],['Payment date',h.premiumPaymentDate??''],['Cash state',isCashStatus(h.cashState)?cashStatusLabels[h.cashState]:h.cashState??'']]}];
  const history:TermSection[]=[{title:`Revision ${h.revision}`,rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]},
   ...held.events.map(e=>({title:eventNames[e.kind]??publicText(e.kind),rows:[['Recorded for',displayInstant(e.businessAt,zone)],['Lots',e.lots??'Open lots at expiry'],['Open lots after',e.remainingLots],
    ['Linked future',e.futuresTradeReference??'None'],...(e.outcome?[['Outcome',publicText(e.outcome)] as const]:[])] as const}))];
  const surface=host.surface,product=h.product;
  return {terms,history,...(option?{termsContent:(box:HTMLElement)=>{widget=lifecycle(box,api,h,host);return()=>{widget?.dispose();widget=undefined;};}}:{}),
   ...(surface&&product&&h.definitionRevisionId?{actions:[{id:'option-trade-vol-surface',label:'Volatility surface',run:()=>surface(product,h.definitionRevisionId)}]}:{})};
 }};
}

// Each lifecycle request outlives the view that sent it, per person and trade, until its outcome is known: a redraw offers only its retry.
const lifecycleRequests=new Map<string,ReturnType<typeof actorTransport<RequestCommand>>>();
function lifecycleTransport(actor:string,trade:string){
 const key=JSON.stringify([actor,trade]);let transport=lifecycleRequests.get(key);
 if(!transport)lifecycleRequests.set(key,transport=actorTransport<RequestCommand>(()=>`option-lifecycle-request:${actor}:${trade}`,()=>sessionStorage));
 return transport;
}

// Older releases retained event requests in the Details pane. Their status is
// recoverable here; new actions use the policy-bound workspace.
function lifecycle(host:HTMLElement,api:Api,h:Holding,o:OptionHost){
 const box=section('Lifecycle');box.classList.add('option-lifecycle');box.dataset.ownControls='';host.append(box);
 box.append(el('p','field-help','Review exercise, assignment and expiry with the saved action policies in Scheduling → Lifecycle.'));
 const owner=o.actor(),transport=lifecycleTransport(owner,h.tradeId);
 let command=restoreTradeRequest(transport.read()),checking=false,alive=true,notFound=false;
 const current=()=>alive&&o.actor()===owner;
 const said=el('p','field-help');said.setAttribute('role','status');
 const open=el('button','secondary','Open Lifecycle');open.id='option-open-lifecycle';open.type='button';open.onclick=()=>{if(current()&&!command)o.lifecycle?.();};
 if(o.lifecycle)box.append(open);else box.append(el('p','field-help','Open Lifecycle under Scheduling in the main workspace.'));
 const check=el('button','secondary','Check earlier request');check.id='option-lifecycle-status';check.type='button';
 const dismiss=el('button','secondary','Dismiss unrecorded request');dismiss.id='option-lifecycle-dismiss';dismiss.type='button';
 function sync(){
  open.disabled=checking||!!command;check.hidden=!command;check.disabled=checking;
  dismiss.hidden=!command||!notFound;dismiss.disabled=checking;
 }
 if(command)said.textContent='An earlier event request has an unconfirmed outcome. Check its status before recording another action.';
 check.onclick=()=>{if(!current()||!command||checking)return;const sent=command;checking=true;notFound=false;sync();
  void(async()=>{try{
   const status=await api<{found?:boolean;operationId?:string;tradeId?:string}>('options/status',{operationId:sent.operationId});
   if(status.found===false){if(current()){notFound=true;said.textContent='No accepted event was found for this earlier request. Dismiss it before reviewing an action in Lifecycle.';}return;}
   if(status.operationId!==sent.operationId||status.tradeId!==h.tradeId)throw Error('The earlier request status did not identify this exact operation and trade.');
   if(restoreTradeRequest(transport.read())?.operationId===sent.operationId)transport.write(undefined);
   if(!current())return;command=undefined;said.textContent='The earlier request was recorded. Refresh the trade to read its accepted events.';o.recorded(said.textContent);
  }catch(error){if(current())said.textContent=`Status not confirmed: ${reason(error)} The original request remains retained.`;}
  finally{checking=false;if(current())sync();}})();
 };
 dismiss.onclick=()=>{if(!current()||!notFound||checking||!command)return;
  if(restoreTradeRequest(transport.read())?.operationId!==command.operationId)return;
  transport.write(undefined);command=undefined;notFound=false;said.textContent='The unrecorded request was dismissed. Review the action in Lifecycle.';sync();
 };
 box.append(check,dismiss,said);sync();
 return {dispose:()=>{alive=false;box.remove();},blocked:()=>checking||!!command};
}
