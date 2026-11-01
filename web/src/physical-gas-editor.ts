import {actorTransport} from './pending-command.js';
import {setupGasComposer} from './gas-composer.js';
import {setupTicket,ticketBookingRetained,type TicketHandle} from './ticket-controller.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type ValuationMapping={quote:string;group:string;groupAdjustment:string;locationAdjustment:string;index:string;curve:string;marketLocation:string;marketLocationName:string;quantityUnit:string;priceUnit:string;adjustment:string};
type PhysicalLocation={clock:string;dayStartMinute:number;definitionRevisionId:string;pipeline:string;pipelineName:string;zone:string;zoneName:string;deliveryPoint:string;deliveryPointName:string;product:string;productName:string;valuation:ValuationMapping|null};
type PhysicalTerms=PhysicalLocation&{deliveryFrom:string;deliveryTo:string;quantity:string;quantityBasis:string;direction:string};
type Party={id:string;revision:number;name:string};
type Tariff={code:string;name:string;pipeline:string;product:string;quote:string;rate:string;validFrom:string;validTo:string;basis:string};
type Meter={identifier:string;pipeline:string;quantitySpecification:string};
export type GasCatalog={gasSetupId:string|null;locations:PhysicalLocation[];tariffs:Tariff[];books:{id:string;name:string}[];legalEntities:Party[];counterparties:Party[];canBook:boolean;scope:string};
type Quantity={numerator:string;denominator:string};
type Value={presentValue:string;currency:string;sides:{role:string;economicClass:string;presentValue:string}[];deliveryRows:{day:string;start:string;end:string;quantity:Quantity;meter:Meter|null}[]};
type Result={physical:PhysicalTerms;valuation:Value};
type Receipt={tradeId:string;instrumentId:string;operationId:string;revision:number};
const convention='physical_gas_fixed_quantity_v4';

export function setupPhysicalGas(o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;busy:()=>boolean;onBooked?:(tradeId:string)=>Promise<void>}){
 const root=document.getElementById('gas-page')!;
 root.innerHTML=`<section class="card"><h2>Physical gas trade</h2><p id="gas-scope" class="field-help"></p>
 <form id="gas-form">
 <fieldset><legend>Trade details</legend><div class="schedule-fields">
 <label>Book<select id="gas-book" required></select></label>
 <label>Legal entity<select id="gas-entity" required></select></label><label>Counterparty<select id="gas-party" required></select></label>
 </div></fieldset>
 <fieldset><legend>Physical delivery side</legend><div class="schedule-fields">
 <label>Buy / sell<select id="gas-direction" required><option value="">Choose direction</option><option value="receive">Buy · receive gas</option><option value="deliver">Sell · deliver gas</option></select></label>
 <label>Product<select id="gas-product" required></select></label><label>Pipeline<select id="gas-pipeline" required></select></label>
 <label>Zone<select id="gas-zone" required></select></label><label>Delivery location<select id="gas-location" required></select></label>
 <label>Delivery from<input id="gas-from" type="date" required></label><label>Delivery to · exclusive<input id="gas-to" type="date" required></label>
 <label>Contract quantity · <span id="gas-quantity-unit"></span><input id="gas-quantity" inputmode="decimal" required></label>
 <label>Quantity basis<select id="gas-basis" required><option value="">Choose basis</option><option value="period_total_equal_days">Period total · equal days</option><option value="per_operating_day">Quantity per operating day</option></select></label>
 </div><p id="gas-day" class="field-help"></p>
 <div class="gas-valuation"><h3>Physical valuation</h3><p id="gas-reference">Select the product, pipeline, zone and delivery location to resolve its valuation index.</p><dl id="gas-valuation-details"></dl></div>
 </fieldset>
 <fieldset><legend>Cash consideration side</legend><div class="schedule-fields">
 <label>Fixed price · <span id="gas-price-unit"></span><input id="gas-price" inputmode="decimal" required></label>
 <label>Payment instant · UTC<input id="gas-payment" type="datetime-local" step="1" required></label>
 <label>Valuation policy<select id="gas-discount" required><option value="">Choose policy</option><option value="false">No discounting</option></select></label>
 </div></fieldset>
 <fieldset><legend>Usage fee side · optional</legend><p class="field-help">A per-unit charge on the same contractual delivery quantity.</p>
 <label>Fee source<select id="gas-tariff"><option value="">No usage fee</option><option value="manual">Manual per-unit fee</option></select></label><p id="gas-tariff-details" class="field-help"></p>
 <label>Fee direction<select id="gas-fee-direction"><option value="">Choose direction</option><option value="pay">Pay fee</option><option value="receive">Receive fee</option></select></label>
 <label id="gas-fee-label">Fee · <span id="gas-fee-unit"></span> · blank for no fee<input id="gas-fee" inputmode="decimal"></label></fieldset>
 <div class="schedule-actions"><button id="gas-instructions" type="button">Prepare meter schedule</button><button id="gas-preview" type="submit">Preview trade</button><button id="gas-book-submit" type="button" disabled>Book reviewed trade</button></div>
 </form></section>
 <section class="card" id="gas-pending" hidden><h3>Submission awaiting confirmation</h3><p>The original request is retained. Check status or retry the same request.</p><button id="gas-check" type="button">Check original status</button><button id="gas-retry" type="button">Retry original request</button></section>
 <section class="card" id="gas-result" hidden><h3 id="gas-result-title">Unbooked preview</h3><dl id="gas-physical-details"></dl><p id="gas-total"></p><div id="gas-sides" class="table-wrap"></div>
 <details><summary>Daily contractual delivery schedule</summary><div id="gas-delivery-rows" class="table-wrap"></div></details></section>
 <section class="card" id="gas-instruction-result" hidden><h3>Contractual meter schedule</h3><p id="gas-instruction-status">Prepared from contractual quantities. Not submitted, confirmed or measured.</p><div id="gas-instruction-rows" class="table-wrap"></div></section>
 <section class="card"><h3>Single-point gas bookings</h3><button id="gas-list" type="button">Refresh gas trades</button><div id="gas-directory" class="table-wrap"></div><h3>Reopen a gas trade</h3><p id="gas-receipt" role="status"></p><label>Trade ID<input id="gas-trade-id"></label><button id="gas-open" type="button">Open retained trade</button></section>`;
 // A new gas trade is entered in the deal ticket; the composition and single-point editors below it stay until the ticket replaces them.
 root.innerHTML='<div id="gas-ticket"></div><div id="gas-composer"></div><details id="gas-template"><summary>Single-point purchase / sale template</summary>'+root.innerHTML+'</details>';
 const composer=setupGasComposer(o);
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const input=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 const val=(id:string)=>input(id).value;
 let ticket:TicketHandle|undefined,catalog:GasCatalog|undefined,reviewed:Record<string,unknown>|undefined,pending:Record<string,unknown>|undefined,generation=0,owner='',uncertain=false;
 const storageKey=()=>`physical-gas-request:${o.actor()}`;
 const context=()=>({...o.source(),convention,knowledgeMode:'current'});
 const exact=(q:Quantity)=>q.denominator==='1'?q.numerator:`${q.numerator}/${q.denominator}`;
 const gasDay=(v:PhysicalLocation)=>`${v.clock} · starts ${String(Math.floor(v.dayStartMinute/60)).padStart(2,'0')}:${String(v.dayStartMinute%60).padStart(2,'0')} local time`;
 const transport=actorTransport<{body:Record<string,unknown>;uncertain:boolean}>(storageKey,()=>sessionStorage);
 function retain(){transport.write(pending?{body:pending,uncertain}:undefined);}
 function restore(){const saved=transport.read();if(saved?.body?.convention===convention&&typeof saved.body.operationId==='string'){pending=saved.body;uncertain=true;}}
 function selections(id:string,rows:{value:string;label:string}[],selected=''){
  get<HTMLSelectElement>(id).replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(r.label,r.value)));
  input(id).value=rows.some(r=>r.value===selected)?selected:'';
 }
 function definitions(id:string,rows:[string,string][]){const nodes=rows.flatMap(([key,value])=>{const dt=document.createElement('dt'),dd=document.createElement('dd');dt.textContent=key;dd.textContent=value;return[dt,dd];});get(id).replaceChildren(...nodes);}
 function table(id:string,headers:string[],rows:string[][]){const table=document.createElement('table'),head=table.createTHead().insertRow();for(const label of headers){const th=document.createElement('th');th.textContent=label;head.append(th);}const body=table.createTBody();for(const values of rows){const row=body.insertRow();for(const value of values)row.insertCell().textContent=value;}get(id).replaceChildren(table);}
 function invalidate(){reviewed=undefined;get<HTMLButtonElement>('gas-book-submit').disabled=true;get('gas-result').hidden=true;get('gas-instruction-result').hidden=true;generation++;}
 function reset(){ticket?.destroy();ticket=undefined;composer.reset();catalog=undefined;reviewed=undefined;pending=undefined;generation++;owner=o.actor();uncertain=false;restore();if(pending)get<HTMLDetailsElement>('gas-template').open=true;get<HTMLFormElement>('gas-form').reset();get('gas-result').hidden=true;get('gas-instruction-result').hidden=true;get('gas-pending').hidden=!pending;get('gas-receipt').textContent='';get('gas-total').textContent='';input('gas-trade-id').value='';get('gas-reference').textContent='Select the product, pipeline, zone and delivery location to resolve its valuation index.';for(const id of ['gas-product','gas-pipeline','gas-zone','gas-location','gas-book','gas-entity','gas-party'])selections(id,[]);for(const id of ['gas-sides','gas-delivery-rows','gas-physical-details','gas-valuation-details','gas-directory'])get(id).replaceChildren();for(const id of ['gas-quantity-unit','gas-price-unit','gas-fee-unit','gas-day','gas-tariff-details'])get(id).textContent='';sync();}
 function selected(){return catalog?.locations.find(p=>p.product===val('gas-product')&&p.pipeline===val('gas-pipeline')&&p.zone===val('gas-zone')&&p.deliveryPoint===val('gas-location'));}
 function sync(){
  composer.sync();
  get('gas-pending').hidden=!pending;
  for(const field of get<HTMLFormElement>('gas-form').elements)if(field instanceof HTMLInputElement||field instanceof HTMLSelectElement)field.disabled=!!pending||o.busy();
  input('gas-pipeline').disabled=!!pending||o.busy()||!val('gas-product');input('gas-zone').disabled=!!pending||o.busy()||!val('gas-pipeline');input('gas-location').disabled=!!pending||o.busy()||!val('gas-zone');
  input('gas-fee-direction').disabled=!!pending||o.busy()||!val('gas-tariff');input('gas-fee-direction').required=!!val('gas-tariff');
  input('gas-fee').disabled=!!pending||o.busy()||val('gas-tariff')!=='manual';get<HTMLButtonElement>('gas-instructions').disabled=!!pending||o.busy();
  get<HTMLButtonElement>('gas-preview').disabled=!!pending||o.busy();get<HTMLButtonElement>('gas-book-submit').disabled=!reviewed||!!pending||!catalog?.canBook||o.busy();for(const id of ['gas-check','gas-retry'])get<HTMLButtonElement>(id).disabled=!pending||o.busy();
 }
 function physicalChoices(){
  const paths=catalog?.locations??[],product=val('gas-product'),pipeline=val('gas-pipeline'),zone=val('gas-zone'),point=val('gas-location');
  const choices=(rows:PhysicalLocation[],key:'pipeline'|'zone'|'deliveryPoint',label:'pipelineName'|'zoneName'|'deliveryPointName')=>[...new Map(rows.map(p=>[p[key],{value:p[key],label:p[label]}])).values()];
  selections('gas-pipeline',choices(paths.filter(p=>p.product===product),'pipeline','pipelineName'),pipeline);
  selections('gas-zone',choices(paths.filter(p=>p.product===product&&p.pipeline===val('gas-pipeline')),'zone','zoneName'),zone);
  selections('gas-location',choices(paths.filter(p=>p.product===product&&p.pipeline===val('gas-pipeline')&&p.zone===val('gas-zone')),'deliveryPoint','deliveryPointName'),point);
  const location=selected(),v=location?.valuation;
  get('gas-reference').textContent=!location?'Select the product, pipeline, zone and delivery location to resolve its valuation index.':v?'Resolved from this physical location and product.':'No valuation mapping is configured for this physical location and product. A preview cannot be priced until one is configured.';
  get('gas-day').textContent=location?`Gas day: ${gasDay(location)}`:'';get('gas-quantity-unit').textContent=v?.quantityUnit??'';get('gas-price-unit').textContent=v?.priceUnit??'';get('gas-fee-unit').textContent=v?.priceUnit??'';
  const previous=val('gas-tariff');
  const tariffs=(catalog?.tariffs??[]).filter(t=>t.pipeline===location?.pipeline&&t.product===location?.product&&t.quote===v?.quote);
  get<HTMLSelectElement>('gas-tariff').replaceChildren(new Option('No usage fee',''),new Option('Manual per-unit fee','manual'),...tariffs.map(t=>new Option(`${t.name} · ${t.rate} ${v?.priceUnit??''}`,t.code)));
  input('gas-tariff').value=previous==='manual'||tariffs.some(t=>t.code===previous)?previous:'';
  tariffDetails();
  definitions('gas-valuation-details',v?[['Valuation group',v.group],['Valuation index',v.index],['Market location',v.marketLocationName],['Forward curve',v.curve],['Group price adjustment',`${v.groupAdjustment} ${v.priceUnit}`],['Point price adjustment',`${v.locationAdjustment} ${v.priceUnit}`],['Combined price adjustment',`${v.adjustment} ${v.priceUnit}`]]:[]);sync();
 }
 function tariffDetails(){const tariff=catalog?.tariffs.find(t=>t.code===val('gas-tariff'));get('gas-tariff-details').textContent=tariff?`Per contractual delivery quantity · valid ${tariff.validFrom} to ${tariff.validTo} exclusive. This payable charge is separate from physical valuation adjustments.`:'';sync();}
 input('gas-tariff').onchange=()=>{invalidate();tariffDetails();};
 for(const [id,children] of [['gas-product',['gas-pipeline','gas-zone','gas-location']],['gas-pipeline',['gas-zone','gas-location']],['gas-zone',['gas-location']],['gas-location',[]]] as const)
  input(id).onchange=()=>{invalidate();for(const child of children)input(child).value='';physicalChoices();};
 get('gas-form').oninput=()=>invalidate();
 async function load(){reset();if(o.historical())throw Error('Return to working data to enter a gas trade.');const g=generation,c=await o.api<GasCatalog>('gas/catalog',context());if(g!==generation)return false;catalog=c;await composer.setCatalog(c);if(g!==generation)return false;get('gas-scope').textContent=c.scope;
  selections('gas-product',[...new Map(c.locations.map(p=>[p.product,{value:p.product,label:p.productName}])).values()]);physicalChoices();selections('gas-book',c.books.map(b=>({value:b.id,label:b.name})));
  for(const [id,parties] of [['gas-entity',c.legalEntities],['gas-party',c.counterparties]] as const)selections(id,parties.map(p=>({value:JSON.stringify([p.id,p.revision]),label:p.name})));sync();await directory();return g===generation;
 }
 function terms(){const location=selected();if(!location)throw Error('Select product, pipeline, zone and delivery location.');const [legalEntityId,legalEntityRevision]=JSON.parse(val('gas-entity')),[counterpartyId,counterpartyRevision]=JSON.parse(val('gas-party'));if(val('gas-discount')!=='false')throw Error('Select the valuation policy.');
  return {...context(),gasSetupId:catalog!.gasSetupId,operationId:crypto.randomUUID(),bookId:val('gas-book'),definitionRevisionId:location.definitionRevisionId,pipeline:location.pipeline,zone:location.zone,deliveryPoint:location.deliveryPoint,product:location.product,
   deliveryFrom:val('gas-from'),deliveryTo:val('gas-to'),quantity:val('gas-quantity'),quantityBasis:val('gas-basis'),direction:val('gas-direction'),fixedPrice:val('gas-price'),usageFee:val('gas-tariff')==='manual'?(val('gas-fee')||null):null,usageTariffCode:!['','manual'].includes(val('gas-tariff'))?val('gas-tariff'):null,usageFeeDirection:val('gas-tariff')?val('gas-fee-direction'):null,
   paymentAt:val('gas-payment')+(val('gas-payment').length===16?':00Z':'Z'),discounting:false,legalEntityId,legalEntityRevision,counterpartyId,counterpartyRevision};
 }
 function render(result:Result,title:string){const p=result.physical,v=p.valuation;get('gas-result-title').textContent=title;definitions('gas-physical-details',[
  ['Product',p.productName],['Pipeline',p.pipelineName],['Zone',p.zoneName],['Delivery location',p.deliveryPointName],['Delivery period',`${p.deliveryFrom} to ${p.deliveryTo} · exclusive`],
  ['Physical side direction',p.direction==='receive'?'Buy · receive gas':'Sell · deliver gas'],['Contract quantity',`${p.quantity} ${v?.quantityUnit??''}`],['Quantity basis',p.quantityBasis==='per_operating_day'?'Per operating day':'Period total, equal operating days'],
  ['Gas day',gasDay(p)],['Valuation index',v?.index??''],['Market location',v?.marketLocationName??''],['Valuation group',v?.group??'']]);
  get('gas-total').textContent=`Present value: ${result.valuation.presentValue} ${result.valuation.currency.toUpperCase()}`;
  table('gas-sides',['Side','Economic class','Signed PV'],result.valuation.sides.map(s=>[s.role==='delivery'?'Physical delivery':s.role==='consideration'?'Cash consideration':'Usage fee',s.economicClass==='physical_value'?'Physical value':'Cash',s.presentValue]));
  table('gas-delivery-rows',['Gas day','Start · UTC','End · UTC',`Contractual quantity · ${v?.quantityUnit??''}`,'Meter identifier'],result.valuation.deliveryRows.map(r=>[r.day,r.start,r.end,exact(r.quantity),r.meter?.identifier??'Unassigned']));get('gas-result').hidden=false;
 }
 get('gas-instructions').onclick=()=>{if(!get<HTMLFormElement>('gas-form').reportValidity())return;void o.act(async()=>{const g=generation,result=await o.api<{status:string;rows:Value['deliveryRows']}>('gas/instructions',terms());if(g!==generation)return;table('gas-instruction-rows',['Gas day','Pipeline','Meter identifier','Native quantity specification','Contractual quantity'],result.rows.map(r=>[r.day,r.meter!.pipeline,r.meter!.identifier,r.meter!.quantitySpecification,exact(r.quantity)]));get('gas-instruction-result').hidden=false;});};
 get<HTMLFormElement>('gas-form').onsubmit=e=>{e.preventDefault();void o.act(async()=>{invalidate();const body=terms(),g=generation,result=await o.api<Result>('gas/preview',body);if(g!==generation)return;reviewed=body;render(result,'Unbooked preview');sync();});};
 async function open(id:string){const g=generation,result=await o.api<Result>('gas/editor',{context:context(),tradeId:id});if(g!==generation)return;render(result,'Booked trade · retained physical and cash schedules');}
 function receipt(r:Receipt){if(!pending||r.operationId!==pending.operationId||typeof r.tradeId!=='string'||!/^[0-9a-f-]{36}$/i.test(r.tradeId)||typeof r.instrumentId!=='string'||r.revision!==1)throw Error('The booking response could not be reconciled. Check the original request status.');pending=undefined;uncertain=false;reviewed=undefined;retain();get('gas-receipt').textContent=`Booked trade ${r.tradeId} · revision ${r.revision}`;input('gas-trade-id').value=r.tradeId;sync();}
 async function submit(){if(!pending||owner!==o.actor())throw Error('The original submission belongs to a different workspace identity.');const body=pending,previouslyUncertain=uncertain;uncertain=true;retain();try{const r=await o.api<Receipt>('gas/book',body);receipt(r);await open(r.tradeId);await directory();await o.onBooked?.(r.tradeId);}catch(error){const code=(error as {code?:string}).code;if(!previouslyUncertain&&code&&(code.startsWith('gas_')||['access_denied','trade_party','request_format','side_schedule_request_conflict'].includes(code))){pending=undefined;uncertain=false;retain();}throw error;}finally{sync();}}
 get('gas-book-submit').onclick=()=>void o.act(async()=>{if(!reviewed)throw Error('Preview the exact current terms before booking.');pending=structuredClone(reviewed);uncertain=false;reviewed=undefined;retain();sync();await submit();});get('gas-retry').onclick=()=>void o.act(submit);
 get('gas-check').onclick=()=>void o.act(async()=>{if(!pending)return;const r=await o.api<Receipt|{found:false}>('gas/status',{operationId:pending.operationId});if('tradeId'in r){receipt(r);await open(r.tradeId);await o.onBooked?.(r.tradeId);}else get('gas-receipt').textContent='No completed booking found. Retry the original request.';});
 async function directory(){const g=generation,result=await o.api<{trades:{trade_id:string;book:string;quantity:string;direction:string;delivery_from:string;delivery_to:string}[]}>('gas/directory',context());if(g!==generation)return;const table=document.createElement('table'),head=table.createTHead().insertRow();for(const name of ['Trade','Book','Direction','Contract quantity','Delivery']){const th=document.createElement('th');th.textContent=name;head.append(th);}const body=table.createTBody();for(const trade of result.trades){const row=body.insertRow(),button=document.createElement('button');button.type='button';button.className='text-button';button.textContent=trade.trade_id;button.onclick=()=>void o.act(async()=>{input('gas-trade-id').value=trade.trade_id;await open(trade.trade_id);});row.insertCell().append(button);for(const value of [trade.book,trade.direction==='1'?'Receive':'Deliver',trade.quantity,`${trade.delivery_from} to ${trade.delivery_to}`])row.insertCell().textContent=value;}get('gas-directory').replaceChildren(table);}
 get('gas-list').onclick=()=>void o.act(directory);get('gas-open').onclick=()=>void o.act(()=>open(val('gas-trade-id')));
 async function begin(){
  if(!await load())return;
  ticket=setupTicket(get('gas-ticket'),{api:o.api,context,product:'physical_gas',actor:o.actor,booked:r=>void o.onBooked?.(r.tradeId)});
 }
 return {begin,load,reset,sync,inspect:async(tradeId:string)=>{if(await load())await composer.quantities(tradeId);},pending:()=>!!pending||composer.pending()||ticketBookingRetained('physical_gas',o.actor),leave:composer.leave,unsaved:()=>!!ticket?.unsaved()||!!pending||!!reviewed||composer.unsaved()};
}
