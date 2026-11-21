import {entryFacts,facilityCurveChoice,structuredFields,type EntryGroup,type FacilityActivity} from './structured-entry.js';
import {trackEdits} from './edit-tracking.js';
import {drawRatchets} from './gas-storage-charts.js';
import {ratchetBands,storageChoices,storageCommand,storageFields,type Choice,type RatchetRow,type StorageCatalog,type StorageEntry,type StorageField} from './gas-storage-ticket-model.js';
import {publicText} from './market-view.js';
import {bookingAllowed} from './new-trade.js';
import {structuredTicketBooking} from './structured-ticket-booking.js';
import type {PartyCatalog,Trader} from './trade-annotations.js';

// New trade's gas storage contract ticket (D432): facility pricing, negotiated entitlements and commercial ratchets, with
// the limits they describe drawn as they are typed. Every choice, unit and refusal is the backend's; the drawing decides nothing.
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
export const storageConvention='gas_storage_trade_v1';
const bookPath='gas-storage/book';
type Catalog=StorageCatalog&PartyCatalog&{books:{id:string;name:string;portfolio:string}[];bookingActor:Trader;canBook:boolean;bookingUnavailableReason?:string|null};
// A request refused with one of these codes wrote nothing; any other failure leaves its outcome unknown until it is checked or retried.
const definite=(code:string|undefined)=>!!code&&(code.startsWith('gas_storage_')||['access_denied','historical_read_only','numeric_format','request_format','trade_party','structured_power_payment','gas_day_clock','gas_day_boundary','missing_curve','missing_curve_view'].includes(code));
// A version this page has no words for is listed by its identifier, never hidden.
const names:Readonly<Record<string,string>>={gas_day_opening_inventory_band_v1:'By the band a gas day opens in',single_direction_per_gas_day_v1:'One direction per gas day',in_kind_fuel_at_storage_point_v1:'In kind, at the storage point'};
const named=(codes:readonly string[]):Choice[]=>codes.map(code=>({value:code,label:names[code]??code}));

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
 const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=publicText(text);return node;
}
function soon(label:string,later:string){
 const line=el('div','option-row ticket-soon'),caption=el('span','option-fact-label',label),control=el('input','ticket-control');
 control.disabled=true;control.setAttribute('aria-label',label);control.title=later;line.append(caption,control,el('span','ticket-soon-tag','Coming soon'));return line;
}
function fact(label:string,value:string){const line=el('div','option-row option-fact');line.append(el('span','option-fact-label',label),el('span','option-fact-value',value));return line;}

const layout:readonly EntryGroup<StorageField>[]=[
 {title:'Facility',area:'profile',fields:[['facilityProfileId','Storage facility','select']]},
 {title:'Injection',area:'input',fields:[['injectionCurve','Injection priced at','select'],['injectionCharge','Injection charge','decimal','price'],['injectionFuel','Injection fuel (fraction delivered in)','decimal']]},
 {title:'Storage entitlement',area:'contract',fields:[['maximumInventory','Contracted capacity','decimal','quantity'],['minimumInventory','Minimum inventory','decimal','quantity'],['openingInventory','Opening inventory','decimal','quantity']]},
 {title:'Withdrawal',area:'output',fields:[['withdrawalCurve','Withdrawal priced at','select'],['withdrawalCharge','Withdrawal charge','decimal','price'],['withdrawalFuel','Withdrawal fuel (fraction leaving inventory)','decimal']]},
 {title:'Daily ratchets',area:'wide',fields:[['ratchetBasis','Ratchets apply','select'],['flowPolicy','Daily flow','select'],['fuelBasis','Fuel retained','select']]},
 {title:'Term and inventory',fields:[['termFrom','First gas day','date'],['termTo','Term ends (day after the last gas day)','date'],['clock','Gas-day clock','select'],['gasDayStart','Gas day starts (clock time)','time'],['terminalMinimum','Terminal inventory, lowest','decimal','quantity'],['terminalMaximum','Terminal inventory, highest','decimal','quantity']]},
 {title:'Capacity fee and payment',fields:[['monthlyCapacityCharge','Capacity charge per unit of capacity, each month','decimal','price'],['paymentDays','Paid, calendar days after each month','whole']]},
 {title:'Trade',area:'wide',fields:[['book','Book','select'],['legalEntity','Legal entity','select'],['counterparty','Counterparty','select'],['reason','Reason','text']]},
];

/** New trade's gas storage form: the contract's capacity, ratchets, fuel, term and charges, booked as one held contract. */
export function setupGasStorageTicket(o:{api:Api;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;facilityActivity?:FacilityActivity;onBooked:(tradeId:string)=>Promise<void>}){
 const root=document.getElementById('gas-storage-page')!;root.classList.add('option-ticket','structured-entry');root.dataset.ownControls='';
 const status=el('p','side-panel-status');status.setAttribute('role','status');
 const form=el('form'),fields=el('fieldset','option-fields'),edits=trackEdits(form);
 const {controls,captions,sections,setProfile,syncActivity}=structuredFields(fields,'storage',layout,o.facilityActivity);
 const table=el('div','storage-ratchets'),add=el('button','secondary','Add ratchet band'),chart=el('div'),trader=fact('Trader','—');
 const limits=el('div','storage-limits'),editor=el('div','storage-ratchet-editor'),range=el('div','entry-point');
 range.hidden=true;sections.get('Storage entitlement')!.querySelector('h3')!.after(range);
 add.type='button';add.id='storage-add-band';table.id='storage-ratchet-rows';
 const ratchetHelp=el('p','field-help');
 const ratchetSection=sections.get('Daily ratchets')!;ratchetSection.classList.add('storage-rules');
 editor.append(table,add);limits.append(editor,chart);ratchetSection.append(ratchetHelp,limits);
 sections.get('Capacity fee and payment')!.append(fact('Payment rule','Monthly, unadjusted'),soon('Discounting','Payments are valued undiscounted until a discount curve can be chosen here.'));
 sections.get('Trade')!.append(trader);
 const submit=el('button',undefined,'Book storage contract');submit.type='submit';submit.id='storage-book-submit';
 const booking=el('div','option-booking'),said=el('p','field-help');said.id='storage-booking-status';said.setAttribute('role','status');
 const check=el('button','secondary','Check booking status'),retry=el('button','secondary','Retry the original booking');check.type=retry.type='button';check.id='storage-check';retry.id='storage-retry';
 booking.append(el('p','field-help','The plan of injections and withdrawals is proposed and recorded on the booked contract, in its Details.'),submit,check,retry,said);
 form.append(fields,booking);root.replaceChildren(status,form);
 const bookingRequest=structuredTicketBooking(o,{
  storageKey:'gas-storage-ticket-request:',bookPath,statusPath:'gas-storage/status',knownRejection:definite,
  workingMessage:'Return to working data to book a storage contract.',successMessage:'Contract booked.',changed:sync,clearEdits:edits.clear,status:said,
 });
 let catalog:Catalog|undefined,rows:{line:HTMLElement;upTo:HTMLInputElement;injection:HTMLInputElement;withdrawal:HTMLInputElement}[]=[];

 const ratchets=():RatchetRow[]=>rows.map(r=>({upTo:r.upTo.value,injection:r.injection.value,withdrawal:r.withdrawal.value}));
 const entry=():StorageEntry=>({fields:Object.fromEntries(storageFields.map(f=>[f,controls[f].value])),ratchets:ratchets()});
 function addRow(){
  const line=el('div','storage-ratchet-row');
  line.setAttribute('role','group');line.append(el('span','storage-ratchet-name'));
  function cell(name:string,caption:string){
   const label=el('label'),input=el('input');input.inputMode='decimal';input.dataset.cell=name;
   label.append(el('span',undefined,caption),input);line.append(label);return input;
  }
  const upTo=cell('upTo','Upper inventory'),injection=cell('injection','Daily injection limit'),withdrawal=cell('withdrawal','Daily withdrawal limit');
  const remove=el('button','secondary','Remove');remove.type='button';
  remove.onclick=()=>{const i=rows.findIndex(r=>r.line===line);rows.splice(i,1);line.remove();edits.mark();draw();(rows[i]?.injection??add).focus();};
  line.append(remove);table.append(line);rows.push({line,upTo,injection,withdrawal});
 }
 add.onclick=()=>{addRow();edits.mark();draw();};
 function fill(field:StorageField,choices:readonly Choice[]){
  const select=controls[field] as HTMLSelectElement,value=select.value;
  select.replaceChildren(new Option('Choose…',''),...choices.map(c=>new Option(publicText(c.label),c.value)));
  select.value=choices.some(c=>c.value===value)?value:'';
 }
 // The lists that depend on what is entered, the units the entry now has, the band labels and the limits the rows now describe.
 function draw(){
  if(!catalog)return;
  const current=entry(),shown=storageChoices(catalog,current);
  for(const field of storageFields){const c=captions[field],unit=c.unit==='quantity'?shown.unit:c.unit==='price'?shown.priceUnit:null;c.node.textContent=unit?publicText(`${c.text} · ${unit}`):c.text;}
  rows.forEach((r,i)=>{
   const last=i===rows.length-1,name=`Band ${i+1}`;
   r.line.firstElementChild!.textContent=name;r.line.setAttribute('aria-label',name);
   r.line.querySelector('button')!.setAttribute('aria-label','Remove '+name.toLowerCase());
   if(last)r.upTo.value=controls.maximumInventory.value;
   else if(r.upTo.disabled)r.upTo.value='';
   r.upTo.disabled=last;r.upTo.placeholder=last?'Contracted capacity':'';
  });
  ratchetHelp.textContent=(shown.unit?`Inventory in ${shown.unit} and flow in ${shown.unit} per gas day. `:'')+'The last band ends at contracted capacity.';
  add.disabled=rows.length>=catalog.maximumRatchets;
  try{drawRatchets(chart,{bands:ratchetBands(current),unit:shown.unit??'',opening:current.fields.openingInventory?.trim()||null,
   terminal:current.fields.terminalMinimum?.trim()&&current.fields.terminalMaximum?.trim()?[current.fields.terminalMinimum,current.fields.terminalMaximum]:null});}
  catch{chart.replaceChildren();}
  sync();
 }
 function sync(){
  const held=bookingRequest.pending(),sending=bookingRequest.busy();
  fields.disabled=!catalog||held||sending;
  submit.disabled=!catalog?.facilityProfiles?.some(p=>p.kind==='storage')||!bookingAllowed(catalog).enabled||held||sending;
  check.hidden=retry.hidden=!held;check.disabled=retry.disabled=sending;
 }
 controls.injectionCurve.disabled=controls.withdrawalCurve.disabled=true;
 controls.facilityProfileId.onchange=()=>{
  const p=catalog?.facilityProfiles?.find(x=>x.id===controls.facilityProfileId.value);
  const curves=catalog?.curves??[];
  fill('injectionCurve',p?[facilityCurveChoice(p.inputCurve,curves)]:[]);
  fill('withdrawalCurve',p?[facilityCurveChoice(p.outputCurve,curves)]:[]);
  controls.injectionCurve.value=p?.inputCurve??'';controls.withdrawalCurve.value=p?.outputCurve??'';
  setProfile(p,p&&{input:p.inputLocation,output:p.outputLocation},{
   input:catalog?.curves.find(c=>c.code===p?.inputCurve)?.priceUnit,
   output:catalog?.curves.find(c=>c.code===p?.outputCurve)?.priceUnit});
  range.hidden=!p;range.replaceChildren();
  if(p){
   const unit=curves.find(c=>c.code===p.inputCurve&&c.quantity===p.quantity)?.quantityUnit??p.quantity;
   range.append(entryFacts([['Facility inventory range',p.minimum+' – '+p.maximum+' '+unit]]));
  }
  rows=[];table.replaceChildren();
  controls.minimumInventory.value=p?.minimum??'';controls.maximumInventory.value=p?.maximum??'';controls.clock.value=p?.clock??'';
  controls.gasDayStart.value=p?String(Math.floor(p.dayStartMinute/60)).padStart(2,'0')+':'+String(p.dayStartMinute%60).padStart(2,'0'):'';
  for(const band of p?.ratchets??[]){addRow();const r=rows[rows.length-1];r.upTo.value=band.upper;r.injection.value=band.maximumDailyInjection;r.withdrawal.value=band.maximumDailyWithdrawal;}
  if(!rows.length)addRow();draw();
 };
 form.addEventListener('change',draw);form.addEventListener('input',event=>{if((event.target as HTMLElement).tagName==='INPUT')draw();});
 form.onsubmit=event=>{
  event.preventDefault();
  if(!catalog||bookingRequest.pending())return;
  const allowed=bookingAllowed(catalog);if(!allowed.enabled){said.textContent=allowed.reason;return;}
  const command=storageCommand(catalog,entry());if(!command.ok){said.textContent=command.reason;return;}
  bookingRequest.book({context:{snapshotId:o.source().snapshotId,convention:storageConvention,knowledgeMode:'current'},...command.body},'Gas storage contract booking');
 };
 retry.onclick=()=>void bookingRequest.retry();
 check.onclick=()=>void bookingRequest.check();
 function reset(){
  bookingRequest.reset();catalog=undefined;form.reset();setProfile(undefined);range.replaceChildren();range.hidden=true;
  for(const field of storageFields){
   const control=controls[field];if(control instanceof HTMLSelectElement)control.replaceChildren();
   captions[field].node.textContent=captions[field].text;
  }
  trader.lastElementChild!.textContent='—';rows=[];table.replaceChildren();chart.replaceChildren();ratchetHelp.textContent='';
  edits.clear();said.textContent='';status.textContent='';sync();
 }
 return {root,reset,
  async begin(){
   reset();
   if(o.historical())throw Error('Return to working data to enter a storage contract.');
   status.textContent='Loading the storage choices…';
   const view=bookingRequest.open();
   let read:Catalog;
   try{read=await o.api<Catalog>('gas-storage/catalog',{snapshotId:view.snapshotId,convention:storageConvention,knowledgeMode:'current'});}catch(error){if(bookingRequest.current(view))throw error;return;}
   if(!bookingRequest.current(view))return;
   catalog=read;
   const partyChoices=(list:Trader[])=>list.map(p=>({value:JSON.stringify([p.id,p.revision]),label:`${p.name} · ${p.code}`}));
   fill('clock',read.clocks.map(code=>({value:code,label:code})));
   fill('ratchetBasis',named(read.policies.ratchetBases));fill('flowPolicy',named(read.policies.flowPolicies));fill('fuelBasis',named(read.policies.fuelBases));
   fill('book',read.books.map(b=>({value:b.id,label:`${b.portfolio} · ${b.name}`})));fill('legalEntity',partyChoices(read.legalEntities));fill('counterparty',partyChoices(read.counterparties));
   trader.lastElementChild!.textContent=publicText(read.bookingActor.name);
   status.textContent=read.curves.length?'':'This close lists no outright curves a storage contract can be priced at.';
   // A ticket opens with one empty band, which reaches capacity: more are added as the contract states them.
   addRow();
   fill('facilityProfileId',(read.facilityProfiles??[]).filter(p=>p.kind==='storage').map(p=>({value:p.id,label:p.name+' · revision '+p.revision})));
   if(!(read.facilityProfiles??[]).some(p=>p.kind==='storage'))status.textContent='Configure a storage facility and commercial profile in Facilities before booking.';
   if(bookingRequest.pending()){said.textContent='A storage contract booking awaits its outcome. Check its status or retry it as it was sent.';}
   draw();
  },
  inspect:()=>Promise.reject(Error('A booked storage contract opens in the Details pane.')),
  unsaved:()=>!!catalog&&(edits.edited()||bookingRequest.pending()),
  pending:()=>bookingRequest.pending(),
  sync:syncActivity};
}
