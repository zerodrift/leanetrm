import {trackEdits} from './edit-tracking.js';
import {keepOfferedEntry,tollChoices,tollCommand,tollFields,type Choice,type TollCatalog,type TollEntry,type TollField} from './heat-rate-call-ticket-model.js';
import {publicText} from './market-view.js';
import {bookingAllowed} from './new-trade.js';
import {actorTransport,PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import type {PartyCatalog,Trader} from './trade-annotations.js';

// New trade's heat-rate call option ticket (D431): one field per row, sections stacked. Every choice, unit and refusal is the backend's;
// the page lays them out and sends what the person entered to the authorized booking.
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
export const tollConvention='heat_rate_call_trade_v1';
const bookPath='heat-rate-calls/book';
type Catalog=TollCatalog&PartyCatalog&{books:{id:string;name:string;portfolio:string}[];bookingActor:Trader;canBook:boolean;bookingUnavailableReason?:string|null};
type Receipt={operationId:string;tradeId:string;revision:number};
// A request refused with one of these codes wrote nothing; any other failure leaves its outcome unknown until it is checked or retried.
const definite=(code:string|undefined)=>!!code&&(code.startsWith('heat_rate_call_')||['access_denied','historical_read_only','numeric_format','request_format','trade_party','payment_rule','missing_curve','missing_curve_view'].includes(code));
const reason=(error:unknown)=>error instanceof Error?error.message:String(error);
const codeOf=(error:unknown)=>(error as {code?:string}).code;
// A version this page has no words for is listed by its identifier, never hidden.
const names:Readonly<Record<string,string>>={daily_automatic_cash_exercise_v1:'Daily, automatic, cash settled',spread_call_kirk_lognormal_v1:'Two-price spread call (Kirk)',
 act_365_fixed_instant_v1:'Actual / 365 fixed',business_day_lead_local_cutoff_v1:'Business days ahead, at a local cutoff',all:'All hours',peak:'Peak',off:'Off-peak'};
const named=(codes:readonly (string|number)[]):Choice[]=>codes.map(code=>({value:String(code),label:names[String(code)]??String(code)}));

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
 const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=publicText(text);return node;
}
function section(title:string,...rows:HTMLElement[]){const box=el('section','option-section');box.append(el('h3','option-section-title',title),...rows);return box;}
/** A value drawn but not yet hooked up: disabled, marked Coming soon, holding and sending nothing. */
function soon(label:string,later:string){
 const line=el('div','option-row ticket-soon'),caption=el('span','option-fact-label',label),control=el('input','ticket-control');
 control.disabled=true;control.setAttribute('aria-label',label);control.title=later;line.append(caption,control,el('span','ticket-soon-tag','Coming soon'));return line;
}

type Kind='select'|'text'|'decimal'|'whole'|'date'|'time';
const layout:readonly {title:string;fields:readonly [TollField,string,Kind][]}[]=[
 {title:'Contract',fields:[['direction','Buy / sell','select'],['capacity','Capacity','decimal'],['powerCurve','Power curve','select'],['capacityQuantity','Capacity unit','select'],
  ['fuelCurve','Fuel curve','select'],['heatRate','Heat rate','decimal'],['strike','Strike','decimal']]},
 {title:'Delivery',fields:[['deliveryFrom','First delivery day','date'],['deliveryTo','Delivery ends (day after the last)','date'],['clock','Delivery clock','select'],
  ['intervalMinutes','Delivery interval (minutes)','select'],['membership','Delivery hours','select'],['block','Block product','select'],['holidayCalendar','Delivery holiday calendar','select']]},
 {title:'Exercise',fields:[['exerciseStyle','Exercise style','select'],['exerciseRule','Exercise rule','select'],['leadBusinessDays','Business days ahead','whole'],
  ['localCutoff','Cutoff (delivery clock)','time'],['exerciseCalendar','Exercise calendar','select']]},
 {title:'Valuation',fields:[['model','Model','select'],['dayCount','Day count','select']]},
 {title:'Payment',fields:[['paymentFrequency','Frequency','select'],['paymentRule','Payment rule','select'],['paymentNumber','Days','whole'],['paymentDayBasis','Day basis','select'],
  ['paymentShortMonth','Short month','select'],['paymentAdjustment','Date adjustment','select'],['paymentCalendar','Payment calendar','select']]},
 {title:'Trade',fields:[['book','Book','select'],['legalEntity','Legal entity','select'],['counterparty','Counterparty','select'],['reason','Reason','text']]},
];

/** New trade's heat-rate call option form: terms chosen from the backend's catalog and booked as one daily strip. */
export function setupHeatRateCallTicket(o:{api:Api;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;onBooked:(tradeId:string)=>Promise<void>}){
 // A booking request outlives the form that sent it, per person, until its outcome is known.
 const requests=actorTransport<RequestCommand>(()=>`heat-rate-call-ticket-request:${o.actor()}`,()=>sessionStorage);
 // The form keeps its controls' states itself: the page's busy state leaves its coming-soon items disabled and its dependent lists as drawn.
 const root=document.getElementById('heat-rate-call-page')!;root.classList.add('option-ticket');root.dataset.ownControls='';
 const status=el('p','side-panel-status');status.setAttribute('role','status');
 const form=el('form'),fields=el('fieldset','option-fields'),edits=trackEdits(form);
 const controls={} as Record<TollField,HTMLInputElement|HTMLSelectElement>,captions={} as Record<TollField,HTMLLabelElement>,lines={} as Record<TollField,HTMLElement>;
 for(const group of layout){
  const box=section(group.title);
  for(const [field,label,kind] of group.fields){
   const control=kind==='select'?el('select'):el('input'),line=el('div','option-row'),caption=el('label',undefined,label);
   control.id=`toll-${field}`;caption.htmlFor=control.id;
   if(control instanceof HTMLInputElement){if(kind==='date'||kind==='time')control.type=kind;else if(kind!=='text')control.inputMode=kind==='whole'?'numeric':'decimal';}
   line.append(caption,control);box.append(line);controls[field]=control;captions[field]=caption;lines[field]=line;
  }
  if(group.title==='Valuation')box.append(soon('Premium','Premium cash at booking is coming soon.'),soon('Capacity payment','Capacity payments are coming soon.'),
   soon('Discounting','Payments are valued undiscounted until a discount curve can be chosen here.'));
  if(group.title==='Trade'){const trader=el('div','option-row option-fact');trader.append(el('span','option-fact-label','Trader'),el('span','option-fact-value','—'));trader.id='toll-trader';box.append(trader);}
  fields.append(box);
 }
 const submit=el('button',undefined,'Book option');submit.type='submit';submit.id='toll-book-submit';
 const booking=el('div','option-booking'),said=el('p','field-help');said.id='toll-booking-status';said.setAttribute('role','status');
 const check=el('button','secondary','Check booking status'),retry=el('button','secondary','Retry the original booking');check.type=retry.type='button';check.id='toll-check';retry.id='toll-retry';
 booking.append(submit,check,retry,said);form.append(fields,booking);root.replaceChildren(status,form);
 const pending=new PendingCommand(definite);
 let catalog:Catalog|undefined,sending=false;

 const entry=():TollEntry=>Object.fromEntries(tollFields.map(f=>[f,controls[f].value]));
 function fill(field:TollField,choices:readonly Choice[],none='Choose…'){
  const select=controls[field] as HTMLSelectElement,value=select.value;
  select.replaceChildren(new Option(none,''),...choices.map(c=>new Option(publicText(c.label),c.value)));
  select.value=choices.some(c=>c.value===value)?value:'';
 }
 const withUnit=(field:TollField,label:string,unit:string|null)=>{captions[field].textContent=unit?publicText(`${label} · ${unit}`):label;};
 // The lists that depend on what is entered, the units the entry now has, and the payment fields its rule uses.
 function draw(){
  if(!catalog)return;
  const kept=keepOfferedEntry(catalog,entry());for(const field of ['fuelCurve','capacityQuantity','block'] as const)if(kept[field]==='')controls[field].value='';
  const shown=tollChoices(catalog,kept);
  fill('powerCurve',shown.powerCurves);fill('fuelCurve',shown.fuelCurves);fill('capacityQuantity',shown.capacityQuantities);fill('block',shown.blocks);
  withUnit('heatRate','Heat rate',shown.heatRateUnit);withUnit('strike','Strike',shown.strikeUnit);withUnit('capacity','Capacity',shown.capacityUnit);
  const rule=controls.paymentRule.value,lag=rule==='days_after_period_end';
  lines.block.hidden=kept.membership===''||kept.membership==='all'||kept.membership===undefined;
  lines.paymentDayBasis.hidden=!rule||!lag;lines.paymentShortMonth.hidden=!rule||lag;
  captions.paymentNumber.textContent=!rule?'Days':lag?'Days after the last delivery day':'Day of the following month';
  sync();
 }
 function sync(){
  const held=!!pending.command;
  fields.disabled=!catalog||held||sending;
  submit.disabled=!catalog||!bookingAllowed(catalog).enabled||held||sending;
  check.hidden=retry.hidden=!held;check.disabled=retry.disabled=sending;
 }
 form.addEventListener('change',draw);
 function retain(){requests.write(pending.command);}
 async function send(){
  const command=pending.command;if(!command||sending)return;
  // The view is checked when the request is sent, not only when the form opened: a historical context books nothing.
  if(o.historical()){said.textContent='Return to working data to book a heat-rate call option.';return;}
  const uncertain=pending.sending();retain();sending=true;said.textContent='Booking…';sync();
  try{const r=await o.api<Receipt>(bookPath,{...command.body,operationId:command.operationId});sending=false;await receipt(command,r);}
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
  const command=tollCommand(catalog,entry());if(!command.ok){said.textContent=command.reason;return;}
  pending.begin(bookPath,{context:{snapshotId:o.source().snapshotId,convention:tollConvention,knowledgeMode:'current'},...command.body},undefined,'Heat-rate call option booking');
  retain();void send();
 };
 retry.onclick=()=>void send();
 check.onclick=()=>void (async()=>{
  const command=pending.command;if(!command||sending)return;
  try{const r=await o.api<Receipt|null>('heat-rate-calls/status',{operationId:command.operationId});
   if(r)await receipt(command,r);else said.textContent='No completed booking was found. Retry the original booking.';}
  catch(error){said.textContent=`The booking status could not be read: ${reason(error)}`;}
  finally{sync();}
 })();
 function reset(){catalog=undefined;sending=false;pending.clear();form.reset();edits.clear();said.textContent='';status.textContent='';sync();}
 return {root,reset,
  async begin(){
   reset();
   if(o.historical())throw Error('Return to working data to enter a heat-rate call option.');
   status.textContent='Loading the option choices…';
   const read=await o.api<Catalog>('heat-rate-calls/catalog',{snapshotId:o.source().snapshotId,convention:tollConvention,knowledgeMode:'current'});
   catalog=read;
   const pay=read.paymentOptions,partyChoices=(rows:Trader[])=>rows.map(p=>({value:JSON.stringify([p.id,p.revision]),label:`${p.name} · ${p.code}`}));
   fill('direction',[{value:'buy',label:'Buy'},{value:'sell',label:'Sell'}]);
   fill('clock',named(read.clocks));fill('intervalMinutes',named(read.intervalMinutes));fill('membership',named(read.memberships));
   fill('holidayCalendar',named(read.calendars));fill('exerciseCalendar',named(read.calendars));fill('paymentCalendar',named(read.calendars),'None');
   fill('exerciseStyle',named(read.policies.exerciseStyles));fill('exerciseRule',named(read.policies.exerciseRules));fill('model',named(read.policies.models));fill('dayCount',named(read.policies.dayCounts));
   fill('paymentFrequency',pay.frequencies);fill('paymentRule',pay.rules);fill('paymentDayBasis',pay.dayBases);fill('paymentShortMonth',pay.shortMonths);fill('paymentAdjustment',pay.adjustments);
   fill('book',read.books.map(b=>({value:b.id,label:`${b.portfolio} · ${b.name}`})));fill('legalEntity',partyChoices(read.legalEntities));fill('counterparty',partyChoices(read.counterparties));
   root.querySelector('#toll-trader .option-fact-value')!.textContent=publicText(read.bookingActor.name);
   status.textContent=read.curves.length?'':'This close lists no outright curves a heat-rate call option can be valued from.';
   // A booking whose outcome is unknown is shown as it was sent, to be checked or retried, never edited.
   const saved=restoreTradeRequest(requests.read());
   if(saved?.path===bookPath){pending.command={...saved,uncertain:true};said.textContent='A heat-rate call option booking awaits its outcome. Check its status or retry it as it was sent.';}
   draw();
  },
  inspect:()=>Promise.reject(Error('A booked heat-rate call option opens in the Details pane.')),
  unsaved:()=>!!catalog&&(edits.edited()||!!pending.command),
  pending:()=>!!pending.command||restoreTradeRequest(requests.read())?.path===bookPath,
  sync:()=>undefined};
}
