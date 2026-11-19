import {trackEdits} from './edit-tracking.js';
import {drawRatchets} from './gas-storage-charts.js';
import {keepOfferedStorage,ratchetBands,storageChoices,storageCommand,storageFields,type Choice,type RatchetRow,type StorageCatalog,type StorageEntry,type StorageField} from './gas-storage-ticket-model.js';
import {publicText} from './market-view.js';
import {bookingAllowed} from './new-trade.js';
import {actorTransport,PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import type {PartyCatalog,Trader} from './trade-annotations.js';

// New trade's gas storage contract ticket (D431): one field per row in stacked sections, the ratchet bands as a small table, and
// the limits they describe drawn as they are typed. Every choice, unit and refusal is the backend's; the drawing decides nothing.
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
export const storageConvention='gas_storage_trade_v1';
const bookPath='gas-storage/book';
type Catalog=StorageCatalog&PartyCatalog&{books:{id:string;name:string;portfolio:string}[];bookingActor:Trader;canBook:boolean;bookingUnavailableReason?:string|null};
type Receipt={operationId:string;tradeId:string;revision:number};
// A request refused with one of these codes wrote nothing; any other failure leaves its outcome unknown until it is checked or retried.
const definite=(code:string|undefined)=>!!code&&(code.startsWith('gas_storage_')||['access_denied','historical_read_only','numeric_format','request_format','trade_party','structured_power_payment','gas_day_clock','gas_day_boundary','missing_curve','missing_curve_view'].includes(code));
const reason=(error:unknown)=>error instanceof Error?error.message:String(error);
const codeOf=(error:unknown)=>(error as {code?:string}).code;
// A version this page has no words for is listed by its identifier, never hidden.
const names:Readonly<Record<string,string>>={gas_day_opening_inventory_band_v1:'By the band a gas day opens in',single_direction_per_gas_day_v1:'One direction per gas day',in_kind_fuel_at_storage_point_v1:'In kind, at the storage point'};
const named=(codes:readonly string[]):Choice[]=>codes.map(code=>({value:code,label:names[code]??code}));

function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
 const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=publicText(text);return node;
}
function section(title:string,...rows:HTMLElement[]){const box=el('section','option-section');box.append(el('h3','option-section-title',title),...rows);return box;}
function soon(label:string,later:string){
 const line=el('div','option-row ticket-soon'),caption=el('span','option-fact-label',label),control=el('input','ticket-control');
 control.disabled=true;control.setAttribute('aria-label',label);control.title=later;line.append(caption,control,el('span','ticket-soon-tag','Coming soon'));return line;
}
function fact(label:string,value:string){const line=el('div','option-row option-fact');line.append(el('span','option-fact-label',label),el('span','option-fact-value',value));return line;}

type Kind='select'|'text'|'decimal'|'whole'|'date'|'time';
type Unit='quantity'|'price'|null;
const layout:readonly {title:string;fields:readonly [StorageField,string,Kind,Unit][]}[]=[
 {title:'Contract',fields:[['injectionCurve','Injection priced at','select',null],['withdrawalCurve','Withdrawal priced at','select',null],
  ['maximumInventory','Contracted capacity','decimal','quantity'],['minimumInventory','Minimum inventory','decimal','quantity']]},
 {title:'Ratchets',fields:[['ratchetBasis','Ratchets apply','select',null],['flowPolicy','Daily flow','select',null]]},
 {title:'Fuel',fields:[['injectionFuel','Injection fuel (fraction of what is delivered in)','decimal',null],['withdrawalFuel','Withdrawal fuel (fraction of what leaves inventory)','decimal',null],['fuelBasis','Fuel retained','select',null]]},
 {title:'Inventory',fields:[['openingInventory','Opening inventory','decimal','quantity'],['terminalMinimum','Terminal inventory, lowest','decimal','quantity'],['terminalMaximum','Terminal inventory, highest','decimal','quantity']]},
 {title:'Term',fields:[['termFrom','First gas day','date',null],['termTo','Term ends (day after the last gas day)','date',null],['clock','Gas-day clock','select',null],['gasDayStart','Gas day starts (clock time)','time',null]]},
 {title:'Charges and payment',fields:[['injectionCharge','Injection charge','decimal','price'],['withdrawalCharge','Withdrawal charge','decimal','price'],
  ['monthlyCapacityCharge','Capacity charge per unit of capacity, each month','decimal','price'],['paymentDays','Paid, calendar days after each month','whole',null]]},
 {title:'Trade',fields:[['book','Book','select',null],['legalEntity','Legal entity','select',null],['counterparty','Counterparty','select',null],['reason','Reason','text',null]]},
];

/** New trade's gas storage form: the contract's capacity, ratchets, fuel, term and charges, booked as one held contract. */
export function setupGasStorageTicket(o:{api:Api;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;onBooked:(tradeId:string)=>Promise<void>}){
 const requests=actorTransport<RequestCommand>(()=>`gas-storage-ticket-request:${o.actor()}`,()=>sessionStorage);
 const root=document.getElementById('gas-storage-page')!;root.classList.add('option-ticket');root.dataset.ownControls='';
 const status=el('p','side-panel-status');status.setAttribute('role','status');
 const form=el('form'),fields=el('fieldset','option-fields'),edits=trackEdits(form);
 const controls={} as Record<StorageField,HTMLInputElement|HTMLSelectElement>,captions={} as Record<StorageField,{node:HTMLLabelElement;text:string;unit:Unit}>;
 const table=el('div','storage-ratchets'),add=el('button','secondary','Add ratchet band'),chart=el('div'),trader=fact('Trader','—');
 add.type='button';add.id='storage-add-band';table.id='storage-ratchet-rows';
 for(const group of layout){
  const box=section(group.title);
  for(const [field,label,kind,unit] of group.fields){
   const control=kind==='select'?el('select'):el('input'),line=el('div','option-row'),caption=el('label',undefined,label);
   control.id=`storage-${field}`;caption.htmlFor=control.id;
   if(control instanceof HTMLInputElement){if(kind==='date'||kind==='time')control.type=kind;else if(kind!=='text')control.inputMode=kind==='whole'?'numeric':'decimal';}
   line.append(caption,control);box.append(line);controls[field]=control;captions[field]={node:caption,text:label,unit};
  }
  if(group.title==='Ratchets')box.append(el('p','field-help','Each band states the most that may be injected and withdrawn on a gas day that opens inside it. Bands run from the minimum inventory to capacity with no gaps: enter where each one ends.'),table,add,chart);
  if(group.title==='Charges and payment')box.append(fact('Payment rule','Monthly, unadjusted'),soon('Discounting','Payments are valued undiscounted until a discount curve can be chosen here.'));
  if(group.title==='Contract')box.append(soon('Storage facility','Linking the contract to a physical facility is coming soon.'));
  if(group.title==='Trade')box.append(trader);
  fields.append(box);
 }
 const submit=el('button',undefined,'Book storage contract');submit.type='submit';submit.id='storage-book-submit';
 const booking=el('div','option-booking'),said=el('p','field-help');said.id='storage-booking-status';said.setAttribute('role','status');
 const check=el('button','secondary','Check booking status'),retry=el('button','secondary','Retry the original booking');check.type=retry.type='button';check.id='storage-check';retry.id='storage-retry';
 booking.append(el('p','field-help','The plan of injections and withdrawals is proposed and recorded on the booked contract, in its Details.'),submit,check,retry,said);
 form.append(fields,booking);root.replaceChildren(status,form);
 const pending=new PendingCommand(definite);
 let catalog:Catalog|undefined,sending=false,rows:{line:HTMLElement;upTo:HTMLInputElement;injection:HTMLInputElement;withdrawal:HTMLInputElement}[]=[];

 const ratchets=():RatchetRow[]=>rows.map(r=>({upTo:r.upTo.value,injection:r.injection.value,withdrawal:r.withdrawal.value}));
 const entry=():StorageEntry=>({fields:Object.fromEntries(storageFields.map(f=>[f,controls[f].value])),ratchets:ratchets()});
 function addRow(){
  const line=el('div','storage-ratchet-row'),n=rows.length+1;
  const cell=(name:string,label:string)=>{const input=el('input');input.inputMode='decimal';input.setAttribute('aria-label',`Band ${n} ${label}`);input.placeholder=label;input.dataset.cell=name;return input;};
  const upTo=cell('upTo','ends at inventory'),injection=cell('injection','max injection a day'),withdrawal=cell('withdrawal','max withdrawal a day'),remove=el('button','secondary','Remove');
  remove.type='button';remove.setAttribute('aria-label',`Remove band ${n}`);
  remove.onclick=()=>{rows=rows.filter(r=>r.line!==line);line.remove();edits.mark();draw();};
  line.append(el('span','storage-ratchet-name',`Band ${n}`),upTo,injection,withdrawal,remove);table.append(line);rows.push({line,upTo,injection,withdrawal});
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
  if(keepOfferedStorage(catalog,entry()).fields.withdrawalCurve==='')controls.withdrawalCurve.value='';
  const current=entry(),shown=storageChoices(catalog,current);
  fill('injectionCurve',shown.injectionCurves);fill('withdrawalCurve',shown.withdrawalCurves);
  for(const field of storageFields){const c=captions[field],unit=c.unit==='quantity'?shown.unit:c.unit==='price'?shown.priceUnit:null;c.node.textContent=unit?publicText(`${c.text} · ${unit}`):c.text;}
  rows.forEach((r,i)=>{
   const last=i===rows.length-1;r.line.firstElementChild!.textContent=`Band ${i+1}`;
   // The last band ends at capacity, which is entered once above.
   r.upTo.disabled=last;r.upTo.placeholder=last?'ends at capacity':'ends at inventory';if(last)r.upTo.value='';
  });
  add.disabled=rows.length>=catalog.maximumRatchets;
  try{drawRatchets(chart,{bands:ratchetBands(current),unit:shown.unit??'',opening:current.fields.openingInventory?.trim()||null,
   terminal:current.fields.terminalMinimum?.trim()&&current.fields.terminalMaximum?.trim()?[current.fields.terminalMinimum,current.fields.terminalMaximum]:null});}
  catch{chart.replaceChildren();}
  sync();
 }
 function sync(){
  const held=!!pending.command;
  fields.disabled=!catalog||held||sending;
  submit.disabled=!catalog||!bookingAllowed(catalog).enabled||held||sending;
  check.hidden=retry.hidden=!held;check.disabled=retry.disabled=sending;
 }
 form.addEventListener('change',draw);form.addEventListener('input',event=>{if((event.target as HTMLElement).tagName==='INPUT')draw();});
 function retain(){requests.write(pending.command);}
 async function send(){
  const command=pending.command;if(!command||sending)return;
  if(o.historical()){said.textContent='Return to working data to book a storage contract.';return;}
  const uncertain=pending.sending();retain();sending=true;said.textContent='Booking…';sync();
  try{const r=await o.api<Receipt>(bookPath,{...command.body,operationId:command.operationId});sending=false;await receipt(command,r);}
  catch(error){
   sending=false;pending.failed(codeOf(error),uncertain);retain();
   said.textContent=pending.command?`The booking is not confirmed: ${reason(error)}`:`Booking refused: ${reason(error)}`;
  }finally{sync();}
 }
 async function receipt(command:RequestCommand,r:Receipt){
  if(r.operationId!==command.operationId||typeof r.tradeId!=='string'){said.textContent='The booking response could not be reconciled with its request. Check its status.';return;}
  pending.clear();retain();edits.clear();said.textContent='Contract booked.';
  await o.onBooked(r.tradeId);
 }
 form.onsubmit=event=>{
  event.preventDefault();
  if(!catalog||pending.command)return;
  const allowed=bookingAllowed(catalog);if(!allowed.enabled){said.textContent=allowed.reason;return;}
  const command=storageCommand(catalog,entry());if(!command.ok){said.textContent=command.reason;return;}
  pending.begin(bookPath,{context:{snapshotId:o.source().snapshotId,convention:storageConvention,knowledgeMode:'current'},...command.body},undefined,'Gas storage contract booking');
  retain();void send();
 };
 retry.onclick=()=>void send();
 check.onclick=()=>void (async()=>{
  const command=pending.command;if(!command||sending)return;
  try{const r=await o.api<Receipt|null>('gas-storage/status',{operationId:command.operationId});
   if(r)await receipt(command,r);else said.textContent='No completed booking was found. Retry the original booking.';}
  catch(error){said.textContent=`The booking status could not be read: ${reason(error)}`;}
  finally{sync();}
 })();
 function reset(){catalog=undefined;sending=false;pending.clear();form.reset();rows=[];table.replaceChildren();chart.replaceChildren();edits.clear();said.textContent='';status.textContent='';sync();}
 return {root,reset,
  async begin(){
   reset();
   if(o.historical())throw Error('Return to working data to enter a storage contract.');
   status.textContent='Loading the storage choices…';
   const read=await o.api<Catalog>('gas-storage/catalog',{snapshotId:o.source().snapshotId,convention:storageConvention,knowledgeMode:'current'});
   catalog=read;
   const partyChoices=(list:Trader[])=>list.map(p=>({value:JSON.stringify([p.id,p.revision]),label:`${p.name} · ${p.code}`}));
   fill('clock',read.clocks.map(code=>({value:code,label:code})));
   fill('ratchetBasis',named(read.policies.ratchetBases));fill('flowPolicy',named(read.policies.flowPolicies));fill('fuelBasis',named(read.policies.fuelBases));
   fill('book',read.books.map(b=>({value:b.id,label:`${b.portfolio} · ${b.name}`})));fill('legalEntity',partyChoices(read.legalEntities));fill('counterparty',partyChoices(read.counterparties));
   trader.lastElementChild!.textContent=publicText(read.bookingActor.name);
   status.textContent=read.curves.length?'':'This close lists no outright curves a storage contract can be priced at.';
   // A ticket opens with one empty band, which reaches capacity: more are added as the contract states them.
   addRow();
   const saved=restoreTradeRequest(requests.read());
   if(saved?.path===bookPath){pending.command={...saved,uncertain:true};said.textContent='A storage contract booking awaits its outcome. Check its status or retry it as it was sent.';}
   draw();
  },
  inspect:()=>Promise.reject(Error('A booked storage contract opens in the Details pane.')),
  unsaved:()=>!!catalog&&(edits.edited()||!!pending.command),
  pending:()=>!!pending.command||restoreTradeRequest(requests.read())?.path===bookPath,
  sync:()=>undefined};
}
