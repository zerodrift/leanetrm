import {lngChoices,lngSelection,lngPhysicalFields,type LngReferences} from './lng-selection.js';
import {lngConvention,lngRequest,restoreLngCommand,type LngProfile,type LngFields} from './physical-lng-model.js';
import {zonedInput} from './user-time.js';
import {displayExactInstant} from './market-view.js';
import {decorateSideCard} from './side-card.js';
import {PendingCommand,actorTransport,type RequestCommand} from './pending-command.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Party={id:string;revision:number;name:string};
type Catalog=LngReferences&{profiles:LngProfile[];books:{id:string;name:string}[];legalEntities:Party[];counterparties:Party[];canBook:boolean};
type Source={curveName:string;curve:string;currency:string;energyUnit:string;quoteMultiplier:string};
type Value={presentValue:string;currency:string;energyQuantity:string;energyUnit:string;sides:{role:string;presentValue:string}[]};
type Receipt={tradeId:string;instrumentId:string;operationId:string;revision:number};
type DirectoryRow={tradeId:string;revision:string;bookName:string;productName:string;loadFacilityName:string;dischargeFacilityName:string;quantity:string;quantityUnit:string;loadFrom:string;dischargeTo:string};
const dates=['loadFrom','loadTo','dischargeFrom','dischargeTo','paymentAt'] as const;
const referenceFields=[...lngPhysicalFields,'quantityCode','quantityUnit','conversionCode','edition','rule','namedPlace'] as const;
export function setupPhysicalLng(o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;zone:()=>string;busy:()=>boolean;canBook:()=>boolean;onBooked?:(trade:string)=>Promise<void>}){
 const root=document.getElementById('lng-page')!;
 const select=(key:string,label:string)=>`<label>${label}<select id="lng-${key}" required></select></label>`;
 const date=(key:string,label:string)=>`<label>${label} · <span class="lng-zone"></span><input id="lng-${key}" type="datetime-local" step="1" required></label>`;
 root.innerHTML=`<section class="card"><h2>Physical LNG cargo</h2><p>Future cargo valuation before loading starts. Operational actuals, freight and other charges require separate contracts.</p>
 <form id="lng-form"><fieldset><legend>Trade details</legend><div class="schedule-fields">${select('book','Book')}${select('entity','Legal entity')}${select('party','Counterparty')}</div></fieldset>
 <article id="lng-cargo-side"><h3>Cargo</h3><fieldset><legend>Physical parameters</legend><div class="schedule-fields">${select('commodity','Commodity')}${select('product','Product')}${select('loadFacility','Loading terminal')}${select('loadCustody','Loading custody point')}${select('dischargeFacility','Discharge terminal')}${select('dischargeCustody','Discharge custody point')}${select('energyQuantityCode','Pricing energy specification')}${select('clock','Delivery clock')}</div><button id="lng-clear-references" type="button" class="text-button">Clear reference selections</button><p id="lng-selection-note" class="field-help" role="status"></p><dl id="lng-source"></dl></fieldset>
 <fieldset><legend>Cargo terms</legend><div class="schedule-fields">${select('profile','Contract profile')}<label>Physical direction<select id="lng-direction" required><option value="">Choose…</option><option value="receive">Receive cargo</option><option value="deliver">Deliver cargo</option></select></label>${date('loadFrom','Loading from')}${date('loadTo','Loading to, exclusive')}${date('dischargeFrom','Discharge from')}${date('dischargeTo','Discharge to, exclusive')}<label>Forecast discharge quantity<input id="lng-quantity" inputmode="decimal" required></label>${select('quantityCode','Input quantity specification')}${select('quantityUnit','Input unit')}</div><div role="group" aria-label="Quantity conversion"><label><input type="radio" name="lng-conversion" value="direct" required>Direct gross discharge energy</label><label><input type="radio" name="lng-conversion" value="factor" required>Discharge mass with selected energy factor</label></div>${select('conversionCode','Mass to gross discharge energy factor')}</fieldset>
 <fieldset><legend>Incoterms</legend><div class="schedule-fields">${select('edition','Edition')}${select('rule','Rule')}${select('namedPlace','Named contractual place')}</div><p class="field-help">Saved contractual terms do not create title events or charges.</p></fieldset></article>
 <article id="lng-cash-side"><h3>Fixed consideration</h3><fieldset><legend>Cash terms</legend><div class="schedule-fields"><label>Cash direction<select id="lng-considerationDirection" required><option value="">Choose…</option><option value="pay">Pay consideration</option><option value="receive">Receive consideration</option></select></label><label>Fixed price · <span id="lng-price-unit"></span><input id="lng-fixedPrice" inputmode="decimal" required></label>${date('paymentAt','Payment instant')}</div></fieldset></article>
 <div class="schedule-actions"><button id="lng-preview" type="submit">Preview cargo</button><button id="lng-book-submit" type="button" disabled>Book reviewed cargo</button></div></form>
 <section id="lng-pending" hidden><h3>Submission awaiting confirmation</h3><p>The original request is retained. Check its status or retry it.</p><button id="lng-check" type="button">Check original status</button><button id="lng-retry" type="button">Retry original request</button></section><p id="lng-receipt" role="status"></p></section>
 <section id="lng-result" class="card" hidden><h3 id="lng-result-title"></h3><dl id="lng-terms"></dl><p id="lng-total"></p><div id="lng-sides" class="table-wrap"></div></section>
 <section id="lng-book-directory" class="card"><h3>Physical LNG bookings</h3><button id="lng-refresh" type="button">Refresh bookings</button><div id="lng-directory" class="table-wrap"></div></section>`;
 const get=<T extends HTMLElement=HTMLElement>(key:string)=>root.querySelector<T>('#lng-'+key)!;
 const input=(key:string)=>get<HTMLInputElement|HTMLSelectElement>(key),val=(key:string)=>input(key).value;
 const times=Object.fromEntries(dates.map(k=>[k,zonedInput(get<HTMLInputElement>(k),o.zone,o.api)])) as Record<typeof dates[number],ReturnType<typeof zonedInput>>;
 for(const [key,tag,kind] of [['cargo-side','LNG','gas'],['cash-side','Price','cash']] as const){const card=get(key);decorateSideCard(card,card.querySelector('h3')!,tag,kind);}
 let catalog:Catalog|undefined,reviewed:Record<string,unknown>|undefined,epoch=0,referenceEpoch=0,dirty=false,owner=o.actor(),zone=o.zone(),shown:{value:Value;title:string;terms:Record<string,unknown>}|undefined,rows:DirectoryRow[]|undefined;
 const pending=new PendingCommand(code=>!!code&&(code.startsWith('lng_')||['access_denied','request_format','trade_party'].includes(code)));
 const transport=actorTransport<RequestCommand>(()=>'physical-lng-request:'+o.actor(),()=>sessionStorage);
 const context=()=>({...o.source(),convention:lngConvention,knowledgeMode:'current'});
 const conversionMode=()=>root.querySelector<HTMLInputElement>('input[name="lng-conversion"]:checked')?.value??'';
 const selectedPhysical=()=>lngSelection(Object.fromEntries(lngPhysicalFields.map(k=>[k,val(k)])));
 const physical=()=>{const selected=selectedPhysical();if(!catalog||!selected)throw Error('Choose every physical parameter.');return{referenceCatalogId:catalog.referenceCatalogId,referenceRevisionId:catalog.referenceRevisionId,...selected};};
 function options(key:string,values:{value:string;label:string}[],selected=''){const n=get<HTMLSelectElement>(key);n.replaceChildren(new Option('Choose…',''),...values.map(v=>new Option(v.label,v.value)));n.value=values.some(v=>v.value===selected)?selected:'';}
 function details(key:string,values:[string,string][]){get(key).replaceChildren(...values.flatMap(([label,value])=>{const dt=document.createElement('dt'),dd=document.createElement('dd');dt.textContent=label;dd.textContent=value;return[dt,dd];}));}
 function persist(){transport.write(pending.command);}
 function invalidate(){epoch++;dirty=true;reviewed=undefined;get('result').hidden=true;sync();}
 function clearSource(){referenceEpoch++;get('source').replaceChildren();get('price-unit').textContent='';get('selection-note').textContent='Choose each physical parameter and the discharge dates to derive the pricing source.';}
 function showSource(value:Source){details('source',[['Pricing source · derived',`${value.curveName} · ${value.curve}`],['Quote multiplier',value.quoteMultiplier]]);get('price-unit').textContent=`${value.currency} / ${value.energyUnit} · multiplier ${value.quoteMultiplier}`;get('selection-note').textContent='Pricing is derived from the maintained physical mapping.';}
 const current=(g:number,a:string,c:ReturnType<typeof context>,z:string)=>g===epoch&&a===o.actor()&&z===o.zone()&&JSON.stringify(c)===JSON.stringify(context());
 async function resolveReferences(){
  if(!catalog||!selectedPhysical()||!val('dischargeFrom')||!val('dischargeTo'))return;
  const g=epoch,a=owner,c=context(),z=o.zone(),r=referenceEpoch,p=physical();
  try{const [dischargeFrom,dischargeTo]=await Promise.all([times.dischargeFrom.instant(),times.dischargeTo.instant()]);if(!current(g,a,c,z)||r!==referenceEpoch)return;
   const result=await o.api<Source>('lng/resolve',{...c,physical:p,dischargeFrom,dischargeTo});if(current(g,a,c,z)&&r===referenceEpoch)showSource(result);
  }catch(error){if(current(g,a,c,z)&&r===referenceEpoch){clearSource();get('selection-note').textContent=(error as Error).message;}}
 }
 function sync(){
  if(zone!==o.zone()){zone=o.zone();clearSource();Object.values(times).forEach(t=>t.refresh());epoch++;reviewed=undefined;if(shown?.title.startsWith('Booked'))render(shown.value,shown.title,shown.terms);else get('result').hidden=true;if(rows)renderDirectory(rows);}
  root.querySelectorAll('.lng-zone').forEach(n=>n.textContent=o.zone());
  const locked=o.busy()||!!pending.command||o.historical()||!catalog;
  for(const n of get('form').querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))n.disabled=locked;
  for(const key of referenceFields)get<HTMLSelectElement>(key).disabled=locked||get<HTMLSelectElement>(key).options.length<=1;
  input('conversionCode').disabled=locked||conversionMode()!=='factor'||get<HTMLSelectElement>('conversionCode').options.length<=1;input('conversionCode').required=conversionMode()==='factor';
  get<HTMLButtonElement>('clear-references').disabled=locked;get<HTMLButtonElement>('preview').disabled=locked||!selectedPhysical();get<HTMLButtonElement>('book-submit').disabled=locked||!reviewed||!catalog?.canBook;
  get('pending').hidden=!pending.command;get<HTMLButtonElement>('check').disabled=o.busy()||!pending.command;get<HTMLButtonElement>('retry').disabled=o.busy()||!pending.command||o.historical()||!o.canBook();get<HTMLButtonElement>('refresh').disabled=o.busy()||!catalog;
 }
 function reset(){epoch++;referenceEpoch++;owner=o.actor();catalog=undefined;reviewed=undefined;shown=undefined;rows=undefined;dirty=false;pending.command=restoreLngCommand(transport.read());get<HTMLFormElement>('form').reset();Object.values(times).forEach(t=>t.clear());zone=o.zone();for(const key of ['book','entity','party','profile',...referenceFields])options(key,[]);for(const key of ['source','terms','sides','directory'])get(key).replaceChildren();for(const key of ['receipt','selection-note','price-unit','total'])get(key).textContent='';get('result').hidden=true;sync();}
 async function request(){
  const entity=catalog?.legalEntities.find(p=>JSON.stringify([p.id,p.revision])===val('entity')),party=catalog?.counterparties.find(p=>JSON.stringify([p.id,p.revision])===val('party'));
  if(!entity||!party||!catalog?.books.some(b=>b.id===val('book')))throw Error('Choose the book, legal entity and counterparty.');
  const g=epoch,a=owner,c=context(),z=o.zone(),p=physical(),profile=catalog.profiles.find(p=>p.code===val('profile'));
  const fields:LngFields={loadFrom:val('loadFrom'),loadTo:val('loadTo'),dischargeFrom:val('dischargeFrom'),dischargeTo:val('dischargeTo'),paymentAt:val('paymentAt'),quantity:val('quantity'),quantityCode:val('quantityCode'),quantityUnit:val('quantityUnit'),conversionMode:conversionMode(),conversionCode:val('conversionCode'),direction:val('direction'),considerationDirection:val('considerationDirection'),fixedPrice:val('fixedPrice')};
  const header={bookId:val('book'),legalEntityId:entity.id,legalEntityRevision:entity.revision,counterpartyId:party.id,counterpartyRevision:party.revision},incoterms={edition:val('edition'),rule:val('rule'),namedPlace:val('namedPlace')};
  const resolved=Object.fromEntries(await Promise.all(dates.map(async k=>[k,await times[k].instant()]))) as Pick<LngFields,typeof dates[number]>;
  if(!current(g,a,c,z))throw Error('The trade context changed. Preview the current terms again.');
  referenceEpoch++;const result=await o.api<Source>('lng/resolve',{...c,physical:p,dischargeFrom:resolved.dischargeFrom,dischargeTo:resolved.dischargeTo});
  if(!current(g,a,c,z))throw Error('The physical selections changed. Preview again.');showSource(result);
  return lngRequest(c,header,p,incoterms,profile,{...fields,...resolved});
 }
 function render(value:Value,title:string,terms:Record<string,unknown>){
  shown={value,title,terms};get('result-title').textContent=title;get('total').textContent=`Contractual present value: ${value.presentValue} ${value.currency}`;
  const selected=terms.incoterms as {edition:string;rule:string;namedPlace:string},p=terms.physical as Record<string,string>;
  details('terms',[["Loading window",`${displayExactInstant(String(terms.loadFrom),o.zone())} to ${displayExactInstant(String(terms.loadTo),o.zone())} · exclusive`],["Discharge window",`${displayExactInstant(String(terms.dischargeFrom),o.zone())} to ${displayExactInstant(String(terms.dischargeTo),o.zone())} · exclusive`],['Loading terminal / custody',`${p.loadFacility} / ${p.loadCustody}`],['Discharge terminal / custody',`${p.dischargeFacility} / ${p.dischargeCustody}`],['Original quantity',`${terms.quantity} · ${terms.quantityUnit}`],['Gross discharge energy',`${value.energyQuantity} ${value.energyUnit}`],['Mass-energy conversion',terms.conversionCode===null?'Direct energy':String(terms.conversionCode)],['Incoterms',`${selected.edition} · ${selected.rule} · ${selected.namedPlace}`],['Physical / cash direction',`${terms.direction} / ${terms.considerationDirection}`],['Fixed price',String(terms.fixedPrice)],['Payment · '+o.zone(),displayExactInstant(String(terms.paymentAt),o.zone())],['Profile',String(terms.profile)]]);
  const table=document.createElement('table'),head=table.createTHead().insertRow();for(const label of ['Side','Signed PV']){const th=document.createElement('th');th.textContent=label;head.append(th);}for(const side of value.sides){const row=table.createTBody().insertRow();row.insertCell().textContent=side.role;row.insertCell().textContent=side.presentValue;}get('sides').replaceChildren(table);get('result').hidden=false;
 }
 async function open(tradeId:string){const g=epoch,a=owner;const result=await o.api<{terms:Record<string,unknown>;valuation:Value;revision:number}>('lng/editor',{context:context(),tradeId});if(g!==epoch||a!==o.actor())return;reviewed=undefined;render(result.valuation,`Booked cargo · revision ${result.revision} · read only`,result.terms);sync();}
 function renderDirectory(trades:DirectoryRow[]){const table=document.createElement('table'),head=table.createTHead().insertRow();for(const label of ['Trade','Book','Product','Loading / discharge','Original quantity','Voyage · '+o.zone()]){const th=document.createElement('th');th.textContent=label;head.append(th);}const body=table.createTBody();for(const trade of trades){const row=body.insertRow(),button=document.createElement('button');button.type='button';button.className='text-button';button.textContent=trade.tradeId;button.onclick=()=>void o.act(()=>open(trade.tradeId));row.insertCell().append(button);for(const text of [trade.bookName,trade.productName,`${trade.loadFacilityName} / ${trade.dischargeFacilityName}`,`${trade.quantity} ${trade.quantityUnit}`,`${displayExactInstant(trade.loadFrom,o.zone())} to ${displayExactInstant(trade.dischargeTo,o.zone())}`])row.insertCell().textContent=text;}get('directory').replaceChildren(table);}
 async function directory(){const g=epoch,a=owner,result=await o.api<{trades:DirectoryRow[]}>('lng/directory',context());if(g!==epoch||a!==o.actor())return;rows=result.trades;renderDirectory(rows);}
 async function accept(receipt:Receipt,command:RequestCommand,actor:string){
  if(actor!==o.actor()||pending.command?.operationId!==command.operationId)return;
  if(receipt.operationId!==command.operationId||typeof receipt.tradeId!=='string'||typeof receipt.instrumentId!=='string'||receipt.revision!==1)throw Error('The booking response could not be reconciled. Check the original status.');
  pending.clear();persist();reviewed=undefined;dirty=false;get('receipt').textContent=`Booked cargo ${receipt.tradeId} · revision ${receipt.revision}`;sync();const g=epoch;await open(receipt.tradeId);if(g!==epoch||actor!==o.actor())return;await directory();if(g===epoch&&actor===o.actor())await o.onBooked?.(receipt.tradeId);
 }
 async function submit(){const command=pending.command,a=owner;if(!command||a!==o.actor())throw Error('The original request belongs to another identity.');const old=pending.sending();persist();sync();try{await accept(await o.api<Receipt>('lng/book',{...command.body,operationId:command.operationId}),command,a);}catch(error){if(a===o.actor()&&pending.command?.operationId===command.operationId){pending.failed((error as {code?:string}).code,old);persist();}throw error;}finally{sync();}}
 async function load(){reset();if(pending.command){sync();return;}if(o.historical())throw Error('Return to working data to enter physical LNG cargo.');const g=epoch,a=owner,result=await o.api<Catalog>('lng/references',context());if(g!==epoch||a!==o.actor())return;catalog=result;options('book',result.books.map(b=>({value:b.id,label:b.name})));for(const [key,parties] of [['entity',result.legalEntities],['party',result.counterparties]] as const)options(key,parties.map(p=>({value:JSON.stringify([p.id,p.revision]),label:p.name})));const choices=lngChoices(result);for(const key of referenceFields)options(key,choices[key]);options('profile',result.profiles.map(p=>({value:p.code,label:p.label??p.code})));clearSource();sync();await directory();}
 const affectsSource=(target:EventTarget|null)=>target instanceof HTMLElement&&([...lngPhysicalFields,'dischargeFrom','dischargeTo'] as readonly string[]).some(k=>target.id==='lng-'+k);
 get('form').oninput=e=>{invalidate();if(affectsSource(e.target))clearSource();};
 get('form').addEventListener('change',e=>{invalidate();if(affectsSource(e.target)){clearSource();void resolveReferences();}sync();});
 get('clear-references').onclick=()=>{if(o.busy()||pending.command||!catalog)return;for(const key of referenceFields)input(key).value='';invalidate();clearSource();};
 get<HTMLFormElement>('form').onsubmit=e=>{e.preventDefault();void o.act(async()=>{invalidate();const g=epoch,a=owner,body=await request();if(g!==epoch||a!==o.actor())return;const result=await o.api<Value>('lng/preview',{...body,operationId:crypto.randomUUID()});if(g!==epoch||a!==o.actor())return;reviewed=body;render(result,'Unbooked cargo preview',body);sync();});};
 get('book-submit').onclick=()=>void o.act(async()=>{if(!reviewed)throw Error('Preview the exact current terms before booking.');pending.begin('lng/book',reviewed);persist();reviewed=undefined;await submit();});
 get('retry').onclick=()=>void o.act(submit);get('check').onclick=()=>void o.act(async()=>{const command=pending.command,a=owner;if(!command)return;const result=await o.api<Receipt|{found:false}>('lng/status',{operationId:command.operationId});if(a!==o.actor()||pending.command?.operationId!==command.operationId)return;if('tradeId'in result)await accept(result,command,a);else get('receipt').textContent='No completed booking found. Retry the original request.';});get('refresh').onclick=()=>void o.act(directory);
 return{load,inspect:async(trade:string)=>{await load();await open(trade);},reset,sync,pending:()=>!!pending.command,unsaved:()=>dirty||!!reviewed||!!pending.command};
}
