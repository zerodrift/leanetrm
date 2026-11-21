import {facilityCurveChoice,structuredFields,type EntryGroup,type FacilityActivity} from './structured-entry.js';
import {trackEdits} from './edit-tracking.js';
import {keepOfferedEntry,tollChoices,tollCommand,tollFields,type Choice,type TollCatalog,type TollEntry,type TollField} from './heat-rate-call-ticket-model.js';
import {publicText} from './market-view.js';
import {bookingAllowed} from './new-trade.js';
import {structuredTicketBooking} from './structured-ticket-booking.js';
import type {PartyCatalog,Trader} from './trade-annotations.js';

// New trade's heat-rate call option ticket (D431): power and fuel references frame one cash-settled option. Every choice, unit and refusal is the backend's;
// the page lays them out and sends what the person entered to the authorized booking.
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
export const tollConvention='heat_rate_call_trade_v1';
const bookPath='heat-rate-calls/book';
type Catalog=TollCatalog&PartyCatalog&{books:{id:string;name:string;portfolio:string}[];bookingActor:Trader;canBook:boolean;bookingUnavailableReason?:string|null};
// A request refused with one of these codes wrote nothing; any other failure leaves its outcome unknown until it is checked or retried.
const definite=(code:string|undefined)=>!!code&&(code.startsWith('heat_rate_call_')||['access_denied','historical_read_only','numeric_format','request_format','trade_party','payment_rule','missing_curve','missing_curve_view'].includes(code));
// A version this page has no words for is listed by its identifier, never hidden.
const names:Readonly<Record<string,string>>={daily_automatic_cash_exercise_v1:'Daily, automatic, cash settled',spread_call_kirk_lognormal_v1:'Two-price spread call (Kirk)',
 act_365_fixed_instant_v1:'Actual / 365 fixed',business_day_lead_local_cutoff_v1:'Business days ahead, at a local cutoff',all:'All hours',peak:'Peak',off:'Off-peak'};
const named=(codes:readonly (string|number)[]):Choice[]=>codes.map(code=>({value:String(code),label:names[String(code)]??String(code)}));

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
 const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=publicText(text);return node;
}
/** A value drawn but not yet hooked up: disabled, marked Coming soon, holding and sending nothing. */
function soon(label:string,later:string){
 const line=el('div','option-row ticket-soon'),caption=el('span','option-fact-label',label),control=el('input','ticket-control');
 control.disabled=true;control.setAttribute('aria-label',label);control.title=later;line.append(caption,control,el('span','ticket-soon-tag','Coming soon'));return line;
}

const layout:readonly EntryGroup<TollField>[]=[
 {title:'Facility',area:'profile',fields:[['facilityProfileId','Thermal facility (optional)','select']]},
 {title:'Power reference',area:'input',fields:[['powerCurve','Power curve','select'],['strike','Strike','decimal']]},
 {title:'Cash-settled option',area:'contract',fields:[['direction','Buy / sell','select'],['capacityQuantity','Capacity unit','select'],['capacity','Capacity','decimal']]},
 {title:'Fuel reference',area:'output',fields:[['fuelCurve','Fuel curve','select'],['heatRate','Heat rate','decimal']]},
 {title:'Delivery',fields:[['deliveryFrom','First delivery day','date'],['deliveryTo','Delivery ends (day after the last)','date'],['clock','Delivery clock','select'],
  ['intervalMinutes','Delivery interval (minutes)','select'],['membership','Delivery hours','select'],['block','Block product','select'],['holidayCalendar','Delivery holiday calendar','select']]},
 {title:'Exercise',fields:[['exerciseStyle','Exercise style','select'],['exerciseRule','Exercise rule','select'],['leadBusinessDays','Business days ahead','whole'],
  ['localCutoff','Cutoff (delivery clock)','time'],['exerciseCalendar','Exercise calendar','select']]},
 {title:'Valuation',fields:[['model','Model','select'],['dayCount','Day count','select']]},
 {title:'Payment',fields:[['paymentFrequency','Frequency','select'],['paymentRule','Payment rule','select'],['paymentNumber','Days','whole'],['paymentDayBasis','Day basis','select'],
  ['paymentShortMonth','Short month','select'],['paymentAdjustment','Date adjustment','select'],['paymentCalendar','Payment calendar','select']]},
 {title:'Trade',area:'wide',fields:[['book','Book','select'],['legalEntity','Legal entity','select'],['counterparty','Counterparty','select'],['reason','Reason','text']]},
];

/** New trade's heat-rate call option form: terms chosen from the backend's catalog and booked as one daily strip. */
export function setupHeatRateCallTicket(o:{api:Api;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;facilityActivity?:FacilityActivity;onBooked:(tradeId:string)=>Promise<void>}){
 // A booking request outlives the form that sent it, per person, until its outcome is known.
 // The form keeps its controls' states itself: the page's busy state leaves its coming-soon items disabled and its dependent lists as drawn.
 const root=document.getElementById('heat-rate-call-page')!;root.classList.add('option-ticket','structured-entry');root.dataset.ownControls='';
 const status=el('p','side-panel-status');status.setAttribute('role','status');
 const form=el('form'),fields=el('fieldset','option-fields'),edits=trackEdits(form);
 const {controls,captions,lines,sections,setProfile,syncActivity}=structuredFields(fields,'toll',layout,o.facilityActivity);
 const upcoming=el('details','entry-upcoming');
 upcoming.append(el('summary',undefined,'Additional valuation terms'),soon('Premium','Premium cash at booking is coming soon.'),soon('Capacity payment','Capacity payments are coming soon.'),
  soon('Discounting','Payments are valued undiscounted until a discount curve can be chosen here.'));
 sections.get('Valuation')!.append(upcoming);
 const trader=el('div','option-row option-fact');trader.append(el('span','option-fact-label','Trader'),el('span','option-fact-value','—'));trader.id='toll-trader';sections.get('Trade')!.append(trader);
 const submit=el('button',undefined,'Book option');submit.type='submit';submit.id='toll-book-submit';
 const booking=el('div','option-booking'),said=el('p','field-help');said.id='toll-booking-status';said.setAttribute('role','status');
 const check=el('button','secondary','Check booking status'),retry=el('button','secondary','Retry the original booking');check.type=retry.type='button';check.id='toll-check';retry.id='toll-retry';
 booking.append(submit,check,retry,said);form.append(fields,booking);root.replaceChildren(status,form);
 const bookingRequest=structuredTicketBooking(o,{
  storageKey:'heat-rate-call-ticket-request:',bookPath,statusPath:'heat-rate-calls/status',knownRejection:definite,
  workingMessage:'Return to working data to book a heat-rate call option.',successMessage:'Trade booked.',changed:sync,clearEdits:edits.clear,status:said,
 });
 let catalog:Catalog|undefined;

 const entry=():TollEntry=>Object.fromEntries(tollFields.map(f=>[f,controls[f].value]));
 function fill(field:TollField,choices:readonly Choice[],none='Choose…'){
  const select=controls[field] as HTMLSelectElement,value=select.value;
  select.replaceChildren(new Option(none,''),...choices.map(c=>new Option(publicText(c.label),c.value)));
  select.value=choices.some(c=>c.value===value)?value:'';
 }
 const withUnit=(field:TollField,label:string,unit:string|null)=>{captions[field].node.textContent=unit?publicText(`${label} · ${unit}`):label;};
 // The lists that depend on what is entered, the units the entry now has, and the payment fields its rule uses.
 function draw(){
  if(!catalog)return;
  const entered=entry(),p=catalog.facilityProfiles?.find(x=>x.id===entered.facilityProfileId);
  const kept=keepOfferedEntry(catalog,p?{...entered,powerCurve:p.outputCurve,fuelCurve:p.inputCurve}:entered);
  const shown=tollChoices(catalog,kept);
  fill('powerCurve',p?[facilityCurveChoice(p.outputCurve,catalog.curves)]:shown.powerCurves);
  fill('fuelCurve',p?[facilityCurveChoice(p.inputCurve,catalog.curves)]:shown.fuelCurves);
  controls.powerCurve.value=kept.powerCurve??'';controls.fuelCurve.value=p?.inputCurve??kept.fuelCurve??'';
  fill('capacityQuantity',shown.capacityQuantities);fill('block',shown.blocks);
  controls.powerCurve.disabled=controls.fuelCurve.disabled=!!p;
  withUnit('heatRate','Heat rate',shown.heatRateUnit);withUnit('strike','Strike',shown.strikeUnit);withUnit('capacity','Capacity',shown.capacityUnit);
  const rule=controls.paymentRule.value,lag=rule==='days_after_period_end';
  lines.block.hidden=kept.membership===''||kept.membership==='all'||kept.membership===undefined;
  lines.paymentDayBasis.hidden=!rule||!lag;lines.paymentShortMonth.hidden=!rule||lag;
  captions.paymentNumber.node.textContent=!rule?'Days':lag?'Days after the last delivery day':'Day of the following month';
  sync();
 }
 function sync(){
  const held=bookingRequest.pending(),sending=bookingRequest.busy();
  fields.disabled=!catalog||held||sending;
  submit.disabled=!catalog||!bookingAllowed(catalog).enabled||held||sending;
  check.hidden=retry.hidden=!held;check.disabled=retry.disabled=sending;
 }
 controls.facilityProfileId.onchange=()=>{
  const p=catalog?.facilityProfiles?.find(x=>x.id===controls.facilityProfileId.value);
  setProfile(p,p&&{input:p.outputLocation,output:p.inputLocation});
 };
 form.addEventListener('change',draw);
 form.onsubmit=event=>{
  event.preventDefault();
  if(!catalog||bookingRequest.pending())return;
  const allowed=bookingAllowed(catalog);if(!allowed.enabled){said.textContent=allowed.reason;return;}
  const command=tollCommand(catalog,entry());if(!command.ok){said.textContent=command.reason;return;}
  bookingRequest.book({context:{snapshotId:o.source().snapshotId,convention:tollConvention,knowledgeMode:'current'},...command.body},'Heat-rate call option booking');
 };
 retry.onclick=()=>void bookingRequest.retry();
 check.onclick=()=>void bookingRequest.check();
 function reset(){
  bookingRequest.reset();catalog=undefined;form.reset();setProfile(undefined);
  for(const field of tollFields){
   const control=controls[field];if(control instanceof HTMLSelectElement)control.replaceChildren();
   captions[field].node.textContent=captions[field].text;
  }
  controls.powerCurve.disabled=controls.fuelCurve.disabled=false;
  trader.lastElementChild!.textContent='—';edits.clear();said.textContent='';status.textContent='';sync();
 }
 return {root,reset,
  async begin(){
   reset();
   if(o.historical())throw Error('Return to working data to enter a heat-rate call option.');
   status.textContent='Loading the option choices…';
   const view=bookingRequest.open();
   let read:Catalog;
   try{read=await o.api<Catalog>('heat-rate-calls/catalog',{snapshotId:view.snapshotId,convention:tollConvention,knowledgeMode:'current'});}catch(error){if(bookingRequest.current(view))throw error;return;}
   if(!bookingRequest.current(view))return;
   catalog=read;
   const pay=read.paymentOptions,partyChoices=(rows:Trader[])=>rows.map(p=>({value:JSON.stringify([p.id,p.revision]),label:`${p.name} · ${p.code}`}));
   fill('facilityProfileId',(read.facilityProfiles??[]).filter(p=>p.kind==='thermal').map(p=>({value:p.id,label:p.name+' · revision '+p.revision})),'Unlinked financial call');
   fill('direction',[{value:'buy',label:'Buy'},{value:'sell',label:'Sell'}]);
   fill('clock',named(read.clocks));fill('intervalMinutes',named(read.intervalMinutes));fill('membership',named(read.memberships));
   fill('holidayCalendar',named(read.calendars));fill('exerciseCalendar',named(read.calendars));fill('paymentCalendar',named(read.calendars),'None');
   fill('exerciseStyle',named(read.policies.exerciseStyles));fill('exerciseRule',named(read.policies.exerciseRules));fill('model',named(read.policies.models));fill('dayCount',named(read.policies.dayCounts));
   fill('paymentFrequency',pay.frequencies);fill('paymentRule',pay.rules);fill('paymentDayBasis',pay.dayBases);fill('paymentShortMonth',pay.shortMonths);fill('paymentAdjustment',pay.adjustments);
   fill('book',read.books.map(b=>({value:b.id,label:`${b.portfolio} · ${b.name}`})));fill('legalEntity',partyChoices(read.legalEntities));fill('counterparty',partyChoices(read.counterparties));
   root.querySelector('#toll-trader .option-fact-value')!.textContent=publicText(read.bookingActor.name);
   status.textContent=read.curves.length?'':'This close lists no outright curves a heat-rate call option can be valued from.';
   // A booking whose outcome is unknown is shown as it was sent, to be checked or retried, never edited.
   if(bookingRequest.pending()){said.textContent='A heat-rate call option booking awaits its outcome. Check its status or retry it as it was sent.';}
   draw();
  },
  inspect:()=>Promise.reject(Error('A booked heat-rate call option opens in the Details pane.')),
  unsaved:()=>!!catalog&&(edits.edited()||bookingRequest.pending()),
  pending:()=>bookingRequest.pending(),
  sync:syncActivity};
}
