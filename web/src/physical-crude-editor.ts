import {crudeChoices,crudeSelection,crudeReferenceFields,type CrudeReference} from './crude-selection.js';
import {zonedInput} from './user-time.js';
import {displayExactInstant} from './market-view.js';
import {decorateSideCard,sideAddButton} from './side-card.js';
import {PendingCommand,actorTransport,type RequestCommand} from './pending-command.js';
import {crudeConvention,crudeRequest,restoreCrudeCommand,type CrudeProfile,type CrudeFields} from './physical-crude-model.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Party={id:string;revision:number;name:string};
type Assignment=CrudeReference&{code:string;definitionRevisionId:string;commodity:string;commodityName:string;grade:string;gradeName:string;facility:string;facilityName:string;custodyPoint:string;custodyPointName:string;standardTemperature:string;temperatureScale:string;quantityCode:string;quantityUnit:string;priceUnit:string;curve:string};
type Catalog={assignments:Assignment[];profiles:CrudeProfile[];books:{id:string;name:string}[];legalEntities:Party[];counterparties:Party[];canBook:boolean;scope:string};
type Value={presentValue:string;currency:string;sides:{role:string;direction:string;presentValue:string;quantity:unknown}[];deliveryFrom:string;deliveryTo:string;quantity:string;quantityUnit:string};
type Receipt={tradeId:string;instrumentId:string;operationId:string;revision:number};
type DirectoryRow={tradeId:string;revision:number;instrumentId:string;bookId:string;bookName:string;direction:string;quantity:string;quantityUnit:string;deliveryFrom:string;deliveryTo:string;gradeName:string;facilityName:string;custodyPointName:string};
// An accepted receipt survives context changes, but its follow-up views belong only
// to the identity and generation that accepted it.
export async function showAcceptedCrudeTrade(o:{current:()=>boolean;open:()=>Promise<void>;directory:()=>Promise<void>;booked:()=>Promise<void>}){
 if(!o.current())return;
 await o.open();if(!o.current())return;
 await o.directory();if(!o.current())return;
 await o.booked();
}
export function setupPhysicalCrude(o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;zone:()=>string;busy:()=>boolean;onBooked?:(tradeId:string)=>Promise<void>}){
 const root=document.getElementById('crude-page')!;
 root.innerHTML=`<section class="card"><h2>Physical crude trade</h2><p id="crude-scope" class="field-help"></p>
 <form id="crude-form"><fieldset><legend>Trade details</legend><div class="schedule-fields"><label>Book<select id="crude-book" required></select></label><label>Legal entity<select id="crude-entity" required></select></label><label>Counterparty<select id="crude-party" required></select></label></div></fieldset>
 <fieldset><legend>Crude</legend><div class="schedule-fields">
 <label>Commodity<select id="crude-commodity" required></select></label><label>Grade<select id="crude-grade" required></select></label><label>Facility<select id="crude-facility" required></select></label><label>Custody point<select id="crude-custodyPoint" required></select></label><label>Volume specification<select id="crude-quantityCode" required></select></label><label>Valuation curve<select id="crude-curve" required></select></label><label>Delivery clock<select id="crude-clock" required></select></label><input id="crude-assignment" type="hidden"><button id="crude-clear-references" type="button" class="text-button">Clear reference selections</button><p id="crude-selection-note" class="field-help" role="status"></p><label>Contract profile<select id="crude-profile" required></select></label>
 <label>Physical direction<select id="crude-direction" required><option value="">Choose direction</option><option value="receive">Receive crude</option><option value="deliver">Deliver crude</option></select></label>
 <label>Delivery from · <span class="crude-zone"></span><input id="crude-from" type="datetime-local" step="1" required></label><label>Delivery to · <span class="crude-zone"></span>, exclusive<input id="crude-to" type="datetime-local" step="1" required></label>
 <label>Contract volume · <span id="crude-quantity-unit"></span><input id="crude-quantity" inputmode="decimal" required></label></div><dl id="crude-reference"></dl></fieldset>
 <fieldset><legend>Fixed price</legend><div class="schedule-fields"><label>Cash direction<select id="crude-cash-direction" required><option value="">Choose direction</option><option value="pay">Pay consideration</option><option value="receive">Receive consideration</option></select></label><label>Fixed price · <span id="crude-price-unit"></span><input id="crude-price" inputmode="decimal" required></label><label>Payment instant · <span class="crude-zone"></span><input id="crude-payment" type="datetime-local" step="1" required></label></div></fieldset>
 <fieldset><legend>Handling fee</legend><div class="schedule-fields"><label>Fee terms<select id="crude-fee-mode" required><option value="">Choose fee terms</option><option value="none">No handling fee</option><option value="per_unit">Per-unit handling fee</option></select></label><label>Fee · <span id="crude-fee-unit"></span><input id="crude-fee" inputmode="decimal"></label><label>Fee direction<select id="crude-fee-direction"><option value="">Choose direction</option><option value="pay">Pay fee</option><option value="receive">Receive fee</option></select></label></div></fieldset>
 <div class="schedule-actions"><button id="crude-preview" type="submit">Preview trade</button><button id="crude-book-submit" type="button" disabled>Book reviewed trade</button></div></form>
 <section id="crude-pending" hidden><h3>Submission awaiting confirmation</h3><p>The original request is retained. Check its status or retry the same request.</p><button id="crude-check" type="button">Check original status</button><button id="crude-retry" type="button">Retry original request</button></section><p id="crude-receipt" role="status"></p></section>
 <section id="crude-result" class="card" hidden><h3 id="crude-result-title"></h3><dl id="crude-terms"></dl><p id="crude-total"></p><div id="crude-sides" class="table-wrap"></div></section>
 <section id="crude-book-directory" class="card"><h3>Physical crude bookings</h3><button id="crude-refresh" type="button">Refresh bookings</button><div id="crude-directory" class="table-wrap"></div></section>
 <dialog id="crude-leave"><p>Discard unsaved crude trade changes?</p><button id="crude-keep" type="button">Keep editing</button><button id="crude-discard" type="button">Discard changes</button></dialog>`;
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>root.querySelector<T>('#'+id)!;
 const input=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id),val=(id:string)=>input(id).value;
 const times={from:zonedInput(get<HTMLInputElement>('crude-from'),o.zone,o.api),to:zonedInput(get<HTMLInputElement>('crude-to'),o.zone,o.api),payment:zonedInput(get<HTMLInputElement>('crude-payment'),o.zone,o.api)};let displayZone=o.zone();
 const sideDeck=document.createElement('div');sideDeck.className='entry-side-deck';sideDeck.id='crude-side-cards';
 const sideFields=[...get('crude-form').querySelectorAll<HTMLFieldSetElement>('fieldset')].slice(1);sideFields[0].before(sideDeck);
 const sideCards=sideFields.map((fields,index)=>{const card=document.createElement('article'),title=document.createElement('h3');title.textContent=fields.querySelector('legend')!.textContent;fields.querySelector('legend')!.remove();card.id=['crude-delivery-side','crude-consideration-side','crude-handling-side'][index];card.append(title,fields);decorateSideCard(card,title,['Crude','Price','Fee'][index],['gas','cash','fee'][index]);sideDeck.append(card);return card;});
 const sideAdder=document.createElement('div');sideAdder.className='ticket-add';sideAdder.setAttribute('aria-label','Add crude side');
 const addTitle=document.createElement('span');addTitle.className='ticket-add-title';addTitle.textContent='Add side';
 const feeTerms=input('crude-fee-mode').closest('label')!;
 const addFee=sideAddButton('Handling fee',()=>{if(o.busy()||pending.command||o.historical()||!catalog)return;input('crude-fee-mode').value='per_unit';invalidate();});addFee.id='crude-add-fee';
 const removeFee=document.createElement('button');removeFee.type='button';removeFee.id='crude-remove-fee';removeFee.className='text-button';removeFee.textContent='Remove fee side';removeFee.onclick=()=>{if(o.busy()||pending.command||o.historical()||!catalog)return;input('crude-fee-mode').value='none';input('crude-fee').value='';input('crude-fee-direction').value='';invalidate();sync();};sideCards[2].append(removeFee);
 sideAdder.append(addTitle,feeTerms,addFee);sideDeck.append(sideAdder);
 let catalog:Catalog|undefined,reviewed:Record<string,unknown>|undefined,epoch=0,dirty=false,owner=o.actor(),shownValue:{value:Value;title:string;terms?:Record<string,unknown>}|undefined,directoryRows:DirectoryRow[]|undefined;
 const pending=new PendingCommand(code=>!!code&&['crude_input','crude_definition','crude_profile','trade_party','access_denied','request_format'].includes(code));
 const transport=actorTransport<RequestCommand>(()=>'physical-crude-request:'+o.actor(),()=>sessionStorage);
 const context=()=>({...o.source(),convention:crudeConvention,knowledgeMode:'current'});
 function options(id:string,rows:{value:string;label:string}[],selected=''){const n=get<HTMLSelectElement>(id);n.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(r.label,r.value)));n.value=rows.some(r=>r.value===selected)?selected:'';}
 function details(id:string,rows:[string,string][]){get(id).replaceChildren(...rows.flatMap(([label,value])=>{const dt=document.createElement('dt'),dd=document.createElement('dd');dt.textContent=label;dd.textContent=value;return[dt,dd];}));}
 function persist(){transport.write(pending.command);}
 function invalidate(){reviewed=undefined;dirty=true;epoch++;get('crude-result').hidden=true;sync();}
 function selectedReferences(){return Object.fromEntries(crudeReferenceFields.map(k=>[k,val('crude-'+k)]));}
 function selection(){return crudeSelection(catalog?.assignments??[],selectedReferences());}
 function referenceChoices(){
  for(let pass=0;pass<2;pass++){const choices=crudeChoices(catalog?.assignments??[],selectedReferences());for(const key of crudeReferenceFields)options('crude-'+key,choices[key],val('crude-'+key));}
  let chosen:Assignment|undefined;try{chosen=selection();get('crude-selection-note').textContent=chosen?'Each selected reference is bound to the exact authorized valuation setup.':'Choose each reference. Only configured compatible combinations are offered.';}catch(error){get('crude-selection-note').textContent=(error as Error).message;}
  input('crude-assignment').value=chosen?.code??'';reference(chosen);
 }
 function reference(a:Assignment|undefined){get('crude-quantity-unit').textContent=a?.quantityUnit??'';for(const id of ['crude-price-unit','crude-fee-unit'])get(id).textContent=a?.priceUnit??'';details('crude-reference',a?[['Commodity',a.commodityName],['Grade',a.gradeName],['Facility',a.facilityName],['Custody point',a.custodyPointName],['Volume specification',a.quantityCode],['Standard temperature',`${a.standardTemperature} ${a.temperatureScale}`],['Valuation curve',a.curve]]:[]);}
 function sync(){
  if(displayZone!==o.zone()){displayZone=o.zone();Object.values(times).forEach(t=>t.refresh());epoch++;reviewed=undefined;if(shownValue?.title.startsWith('Booked trade'))render(shownValue.value,shownValue.title,shownValue.terms);else get('crude-result').hidden=true;if(directoryRows)renderDirectory(directoryRows);}
  root.querySelectorAll('.crude-zone').forEach(n=>n.textContent=o.zone());
  const locked=o.busy()||!!pending.command||o.historical()||!catalog;
  for(const node of get<HTMLFormElement>('crude-form').querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))node.disabled=locked;
  for(const id of ['crude-fee','crude-fee-direction']){input(id).disabled=locked||val('crude-fee-mode')!=='per_unit';input(id).required=val('crude-fee-mode')==='per_unit';}
  for(const key of crudeReferenceFields)get<HTMLSelectElement>('crude-'+key).disabled=locked||get<HTMLSelectElement>('crude-'+key).options.length<=1;
  get<HTMLButtonElement>('crude-clear-references').disabled=locked;sideCards[2].hidden=val('crude-fee-mode')!=='per_unit';addFee.disabled=locked||val('crude-fee-mode')==='per_unit';removeFee.disabled=locked;
  get<HTMLButtonElement>('crude-preview').disabled=locked||!val('crude-assignment');get<HTMLButtonElement>('crude-book-submit').disabled=locked||!reviewed||!catalog?.canBook;
  get('crude-pending').hidden=!pending.command;get<HTMLButtonElement>('crude-check').disabled=o.busy()||!pending.command;
  get<HTMLButtonElement>('crude-retry').disabled=o.busy()||!pending.command||o.historical()||!catalog?.canBook;
  get<HTMLButtonElement>('crude-refresh').disabled=o.busy()||!catalog;
 }
 function reset(){epoch++;owner=o.actor();catalog=undefined;reviewed=undefined;shownValue=undefined;directoryRows=undefined;dirty=false;pending.command=restoreCrudeCommand(transport.read());get<HTMLFormElement>('crude-form').reset();Object.values(times).forEach(t=>t.clear());displayZone=o.zone();for(const id of ['crude-book','crude-entity','crude-party',...crudeReferenceFields.map(k=>'crude-'+k),'crude-profile'])options(id,[]);for(const id of ['crude-reference','crude-terms','crude-sides','crude-directory'])get(id).replaceChildren();for(const id of ['crude-receipt','crude-total','crude-quantity-unit','crude-price-unit','crude-fee-unit'])get(id).textContent='';get('crude-result').hidden=true;sync();}
 async function request(){const entities=catalog?.legalEntities.find(p=>JSON.stringify([p.id,p.revision])===val('crude-entity')),party=catalog?.counterparties.find(p=>JSON.stringify([p.id,p.revision])===val('crude-party'));
  if(!entities||!party||!catalog?.books.some(b=>b.id===val('crude-book')))throw Error('Choose the book, legal entity and counterparty.');
  const fields:CrudeFields={deliveryFrom:val('crude-from'),deliveryTo:val('crude-to'),quantity:val('crude-quantity'),direction:val('crude-direction'),fixedPrice:val('crude-price'),considerationDirection:val('crude-cash-direction'),paymentAt:val('crude-payment'),feeMode:val('crude-fee-mode'),handlingFee:val('crude-fee'),handlingFeeDirection:val('crude-fee-direction')};
  const started=epoch,actor=owner,zone=o.zone(),selectedContext=context(),header={bookId:val('crude-book'),legalEntityId:entities.id,legalEntityRevision:entities.revision,counterpartyId:party.id,counterpartyRevision:party.revision},assignment=selection(),profile=catalog?.profiles.find(p=>p.code===val('crude-profile'));
  const [deliveryFrom,deliveryTo,paymentAt]=await Promise.all([times.from.instant(),times.to.instant(),times.payment.instant()]);
  if(started!==epoch||actor!==o.actor()||zone!==o.zone()||JSON.stringify(selectedContext)!==JSON.stringify(context()))throw Error('Trade context or display timezone changed. Preview the current terms again.');
  return crudeRequest(selectedContext,header,assignment,profile,{...fields,deliveryFrom,deliveryTo,paymentAt});
 }
 function render(value:Value,title:string,terms?:Record<string,unknown>){
  shownValue={value,title,terms};get('crude-result-title').textContent=title;get('crude-total').textContent=`Contractual present value: ${value.presentValue} ${value.currency}`;
  details('crude-terms',[['Delivery window',`${displayExactInstant(value.deliveryFrom,o.zone())} to ${displayExactInstant(value.deliveryTo,o.zone())} · exclusive`],['Contract volume',`${value.quantity} ${value.quantityUnit}`],...(terms?[['Assignment',String(terms.assignmentCode)],['Physical direction',String(terms.direction)],['Consideration direction',String(terms.considerationDirection)],['Fixed price',String(terms.fixedPrice)],['Payment · '+o.zone(),displayExactInstant(String(terms.paymentAt),o.zone())],['Handling fee',terms.handlingFee===null?'None':`${terms.handlingFee} · ${terms.handlingFeeDirection}`],['Profile',String(terms.profile)],['Tolerance',String(terms.tolerance)]] as [string,string][]:[])]);
  const table=document.createElement('table'),head=table.createTHead().insertRow();for(const name of ['Side','Direction','Signed PV']){const th=document.createElement('th');th.textContent=name;head.append(th);}const body=table.createTBody();for(const side of value.sides){const row=body.insertRow();for(const text of [side.role,String(side.direction),side.presentValue])row.insertCell().textContent=text;}get('crude-sides').replaceChildren(table);get('crude-result').hidden=false;
 }
 async function open(tradeId:string){const g=epoch,a=owner;const r=await o.api<{terms:Record<string,unknown>;valuation:Value;tradeId:string;revision:number}>('crude/editor',{context:context(),tradeId});if(g!==epoch||a!==o.actor())return;reviewed=undefined;render(r.valuation,`Booked trade · revision ${r.revision} · read only`,r.terms);sync();}
 async function directory(){const g=epoch,a=owner;const result=await o.api<{trades:DirectoryRow[]}>('crude/directory',context());if(g!==epoch||a!==o.actor())return;
  directoryRows=result.trades;renderDirectory(result.trades);
 }
 function renderDirectory(trades:DirectoryRow[]){
  const table=document.createElement('table'),head=table.createTHead().insertRow();for(const label of ['Trade','Book','Grade','Facility / custody point','Direction','Contract volume','Delivery · '+o.zone()]){const th=document.createElement('th');th.textContent=label;head.append(th);}const body=table.createTBody();for(const trade of trades){const row=body.insertRow(),button=document.createElement('button');button.type='button';button.className='text-button';button.textContent=trade.tradeId;button.onclick=()=>void o.act(()=>open(trade.tradeId));row.insertCell().append(button);for(const text of [trade.bookName,trade.gradeName,`${trade.facilityName} / ${trade.custodyPointName}`,trade.direction,`${trade.quantity} ${trade.quantityUnit}`,`${displayExactInstant(trade.deliveryFrom,o.zone())} to ${displayExactInstant(trade.deliveryTo,o.zone())}`])row.insertCell().textContent=text;}get('crude-directory').replaceChildren(table);
 }
 async function accept(receipt:Receipt,command:RequestCommand,actor:string){if(actor!==o.actor()||pending.command?.operationId!==command.operationId)return;
  if(receipt.operationId!==command.operationId||typeof receipt.tradeId!=='string'||typeof receipt.instrumentId!=='string'||receipt.revision!==1)throw Error('The booking response could not be reconciled. Check the original status.');
  pending.clear();persist();reviewed=undefined;dirty=false;get('crude-receipt').textContent=`Booked trade ${receipt.tradeId} · revision ${receipt.revision}`;sync();const acceptedEpoch=epoch;await showAcceptedCrudeTrade({current:()=>actor===o.actor()&&acceptedEpoch===epoch,open:()=>open(receipt.tradeId),directory,booked:async()=>{await o.onBooked?.(receipt.tradeId);}});
 }
 async function submit(){const command=pending.command,a=owner;if(!command||a!==o.actor())throw Error('The original request belongs to a different workspace identity.');const previouslyUncertain=pending.sending();persist();sync();try{const receipt=await o.api<Receipt>('crude/book',{...command.body,operationId:command.operationId});await accept(receipt,command,a);}catch(error){if(a===o.actor()&&pending.command?.operationId===command.operationId){pending.failed((error as {code?:string}).code,previouslyUncertain);persist();}throw error;}finally{sync();}}
 async function load(){reset();if(o.historical())throw Error('Return to working data to open physical crude bookings.');const g=epoch,a=owner,c=await o.api<Catalog>('crude/catalog',context());if(g!==epoch||a!==o.actor())return;catalog=c;get('crude-scope').textContent=c.scope;
  options('crude-book',c.books.map(b=>({value:b.id,label:b.name})));for(const [id,parties] of [['crude-entity',c.legalEntities],['crude-party',c.counterparties]] as const)options(id,parties.map(p=>({value:JSON.stringify([p.id,p.revision]),label:p.name})));
  referenceChoices();options('crude-profile',c.profiles.map(p=>({value:p.code,label:p.label??p.code})));sync();await directory();
 }
 get('crude-clear-references').onclick=()=>{if(o.busy()||pending.command||o.historical()||!catalog)return;for(const key of crudeReferenceFields)input('crude-'+key).value='';for(const id of ['crude-quantity','crude-price','crude-fee'])input(id).value='';invalidate();referenceChoices();sync();};
 get('crude-form').oninput=invalidate;get('crude-form').addEventListener('change',invalidate);for(const key of crudeReferenceFields)input('crude-'+key).onchange=()=>{for(const id of ['crude-quantity','crude-price','crude-fee'])input(id).value='';invalidate();referenceChoices();sync();};input('crude-fee-mode').onchange=()=>{invalidate();sync();};
 get<HTMLFormElement>('crude-form').onsubmit=e=>{e.preventDefault();void o.act(async()=>{invalidate();const g=epoch,a=owner,body=await request();if(g!==epoch||a!==o.actor())return;const result=await o.api<Value>('crude/preview',{...body,operationId:crypto.randomUUID()});if(g!==epoch||a!==o.actor())return;reviewed=body;render(result,'Unbooked preview',body);sync();});};
 get('crude-book-submit').onclick=()=>void o.act(async()=>{if(!reviewed)throw Error('Preview the exact current terms before booking.');pending.begin('crude/book',reviewed);persist();reviewed=undefined;await submit();});
 get('crude-retry').onclick=()=>void o.act(submit);
 get('crude-check').onclick=()=>void o.act(async()=>{const command=pending.command,a=owner;if(!command)return;const r=await o.api<Receipt|{found:false}>('crude/status',{operationId:command.operationId});if(a!==o.actor()||pending.command?.operationId!==command.operationId)return;if('tradeId'in r)await accept(r,command,a);else get('crude-receipt').textContent='No completed booking found. Retry the original request.';});
 get('crude-refresh').onclick=()=>void o.act(directory);
 async function leave(){if(pending.command||!dirty&&!reviewed)return true;const dialog=get<HTMLDialogElement>('crude-leave');return new Promise<boolean>(resolve=>{get('crude-keep').onclick=()=>{dialog.close();resolve(false);};get('crude-discard').onclick=()=>{dialog.close();reset();resolve(true);};dialog.oncancel=e=>{e.preventDefault();dialog.close();resolve(false);};dialog.showModal();});}
 return{load,inspect:async(trade:string)=>{await load();await open(trade);},reset,sync,leave,pending:()=>!!pending.command,unsaved:()=>dirty||!!reviewed||!!pending.command};
}
