import {actorTransport} from './pending-command.js';
import {setupGasDailyQuantities} from './gas-daily-quantities.js';
import type {GasCatalog} from './physical-gas-editor.js';
import type {TicketLink} from './ticket-types.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Fields=Record<string,string>;
type Quantity={numerator:string;denominator:string};
type SideValue={code:string;role:string;direction:string;source:string|null;stage:string|null;pipeline:string|null;zone:string|null;deliveryPoint:string|null;presentValue:string;quantity:Quantity;deliveryRows:{day:string;start:string;end:string;quantity:Quantity;meter:{identifier:string}|null}[]};
type EntryTerms={cashSides:{code:string;price:string|null;tariffCode:string|null;paymentAt:string}[]};
type Value={presentValue:string;currency:string;quantityUnit:string;sides:SideValue[];links:TicketLink[]};
type Receipt={operationId:string;tradeId:string;instrumentId:string;revision:number};
export function setupGasComposer(o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;busy:()=>boolean;onBooked?:(tradeId:string)=>Promise<void>}){
 const root=document.getElementById('gas-composer')!;
 root.innerHTML=`<section class="card"><h2>Compose a gas instrument</h2><p>Each physical side has its own direction, location, delivery terms and quantity. Link matching gas-day profiles with an explicit fuel rule, then add independently directed cash and fee sides.</p><p class="field-help">Fixed contractual quantities only · one to four physical sides and up to four cash sides · one native unit and currency · explicit no discounting. Transport rights, actuals and settlement require separate qualification.</p>
 <form id="gc-form"><fieldset><legend>Trade details</legend><div class="schedule-fields"><label>Book<select id="gc-book" required></select></label><label>Legal entity<select id="gc-entity" required></select></label><label>Counterparty<select id="gc-party" required></select></label><label>Valuation policy<select id="gc-policy" required><option value="">Choose policy</option><option value="no_discounting">No discounting</option></select></label></div></fieldset>
 <div id="gc-physical"></div><button id="gc-add-physical" type="button" class="secondary">Add physical side</button><div id="gc-cash"></div><button id="gc-add-cash" type="button" class="secondary">Add cash / fee side</button>
 <div class="schedule-actions"><button id="gc-preview" type="submit">Preview composition</button><button id="gc-book-submit" type="button" disabled>Book reviewed composition</button></div></form>
 <div id="gc-pending" hidden><p>The original booking awaits confirmation. Its complete sides and links have been retained.</p><button id="gc-check" type="button">Check original booking</button><button id="gc-retry" type="button">Retry original booking</button></div><p id="gc-receipt" role="status"></p></section>
 <section class="card" id="gc-result" hidden><h3 id="gc-result-title"></h3><p id="gc-total"></p><div id="gc-values" class="table-wrap"></div><h3>Quantity links and fuel reconciliation</h3><div id="gc-links" class="table-wrap"></div><div id="gc-schedules"></div></section>
 <div id="gc-daily"></div>
 <section class="card" id="gc-book-directory"><h3>Composed gas bookings</h3><button id="gc-refresh" type="button" class="secondary">Refresh composed trades</button><div id="gc-directory" class="table-wrap"></div></section>
 <dialog id="gc-leave"><p>Discard unsaved gas composition changes?</p><div class="schedule-actions"><button type="button" id="gc-keep">Keep editing</button><button type="button" id="gc-discard">Discard changes</button></div></dialog>`;
 const daily=setupGasDailyQuantities({...o,context:()=>context()});
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const input=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 const value=(id:string)=>input(id).value;
 const exact=(q:Quantity)=>q.denominator==='1'?q.numerator:`${q.numerator}/${q.denominator}`;
 let catalog:GasCatalog|undefined,physical:Fields[]=[],cash:Fields[]=[],serial=0,epoch=0,dirty=false,reviewed:Record<string,unknown>|undefined;
 let pending:{body:Record<string,unknown>;uncertain:boolean}|undefined;
 const key=()=>`gas-composition-request:${o.actor()}`;
 const transport=actorTransport<{body:Record<string,unknown>;uncertain:boolean}>(key,()=>sessionStorage);
 function retain(){transport.write(pending);}
 function restore(){const p=transport.read();if(p?.body?.profile==='fixed_gas_composition_v1'&&typeof p.body.operationId==='string')pending={body:p.body,uncertain:true};}
 const context=()=>({...o.source(),convention:'physical_gas_fixed_quantity_v4',knowledgeMode:'current'});
 function invalidate(){dirty=true;reviewed=undefined;epoch++;get('gc-result').hidden=true;sync();}
 function options(id:string,rows:{value:string;label:string}[],selected=''){get<HTMLSelectElement>(id).replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(r.label,r.value)));input(id).value=selected;}
 function location(p:Fields){return catalog?.locations.find(l=>l.pipeline===p.pipeline&&l.zone===p.zone&&l.deliveryPoint===p.deliveryPoint&&l.product===p.product);}
 function field(parent:HTMLElement,p:Fields,key:string,label:string,rows?:{value:string;label:string}[],kind='text'){
  const holder=document.createElement('label');holder.textContent=label;let node:HTMLInputElement|HTMLSelectElement;
  if(rows){const select=document.createElement('select');select.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(r.label,r.value)));node=select;}else{const text=document.createElement('input');text.type=kind;if(kind==='datetime-local')text.step='1';node=text;}
  node.dataset.field=key;node.value=p[key]??'';node.required=true;node.oninput=()=>{p[key]=node.value;invalidate();};holder.append(node);parent.append(holder);return node;
 }
 function choice(values:string[],labels:string[]){return values.map((v,n)=>({value:v,label:labels[n]}));}
 function refreshPhysicalChoices(card:HTMLElement,p:Fields){
  const paths=catalog?.locations??[];
  const rows=(key:'product'|'pipeline'|'zone'|'deliveryPoint',label:'productName'|'pipelineName'|'zoneName'|'deliveryPointName',selected:typeof paths)=>[...new Map(selected.map(l=>[l[key],{value:l[key],label:l[label]}])).values()];
  const select=(key:string,values:{value:string;label:string}[])=>{const node=card.querySelector<HTMLSelectElement>(`[data-field=${key}]`)!;node.replaceChildren(new Option('Choose…',''),...values.map(v=>new Option(v.label,v.value)));node.value=p[key]??'';};
  select('product',rows('product','productName',paths));select('pipeline',rows('pipeline','pipelineName',paths.filter(l=>l.product===p.product)));select('zone',rows('zone','zoneName',paths.filter(l=>l.product===p.product&&l.pipeline===p.pipeline)));select('deliveryPoint',rows('deliveryPoint','deliveryPointName',paths.filter(l=>l.product===p.product&&l.pipeline===p.pipeline&&l.zone===p.zone)));
  const l=location(p),note=card.querySelector<HTMLElement>('[data-valuation]')!;
  note.textContent=l?l.valuation?`Valuation index: ${l.valuation.index} · curve: ${l.valuation.curve} · price adjustment: ${l.valuation.adjustment} ${l.valuation.priceUnit} · quantity: ${l.valuation.quantityUnit}`:'This physical location has no valuation mapping. Configure it before pricing.':'Select the physical location to resolve its valuation index.';
 }
 function drawPhysical(){
  get('gc-physical').replaceChildren();
  physical.forEach((p,n)=>{
   const card=document.createElement('fieldset');card.dataset.physical=String(n);const legend=document.createElement('legend');legend.textContent=`Physical side ${n+1}`;card.append(legend);const grid=document.createElement('div');grid.className='schedule-fields';card.append(grid);
   field(grid,p,'code','Side code');field(grid,p,'direction','Direction',choice(['receive','deliver'],['Receive gas','Deliver gas']));
   for(const [key,label] of [['product','Product'],['pipeline','Pipeline'],['zone','Zone'],['deliveryPoint','Delivery point']])field(grid,p,key,label,[]);
   field(grid,p,'deliveryFrom','Delivery from',undefined,'date');field(grid,p,'deliveryTo','Delivery to · exclusive',undefined,'date');
   field(grid,p,'quantityBasis','Quantity basis',choice(['period_total_equal_days','per_operating_day'],['Period total · equal gas days','Per operating day']));
   field(grid,p,'mode','Quantity source',choice(['independent','linked'],['Independent contractual quantity','Link to another physical side']));
   field(grid,p,'quantity','Independent quantity · native unit');
   field(grid,p,'source','Linked source side',physical.filter(x=>x!==p).map(x=>({value:x.code,label:x.code})));
   field(grid,p,'formula','Link fuel rule',choice(['none','gross_less_fraction','gross_divided_by_one_plus_fraction'],['Same quantity · no fuel','Delivery = receipt × (1 − fuel fraction)','Delivery = receipt ÷ (1 + fuel fraction)']));
   field(grid,p,'fraction','Fuel fraction · e.g. 0.02');
   const note=document.createElement('p');note.className='field-help';note.dataset.valuation='';card.append(note);
   const remove=document.createElement('button');remove.type='button';remove.className='secondary';remove.textContent='Remove physical side';remove.onclick=()=>{physical.splice(n,1);invalidate();draw();};card.append(remove);get('gc-physical').append(card);
   for(const [key,children] of [['product',['pipeline','zone','deliveryPoint']],['pipeline',['zone','deliveryPoint']],['zone',['deliveryPoint']],['deliveryPoint',[]]] as const){const node=card.querySelector<HTMLSelectElement>(`[data-field=${key}]`)!;node.onchange=()=>{for(const child of children)p[child]='';refreshPhysicalChoices(card,p);drawCash();sync();};}
   card.querySelector<HTMLSelectElement>('[data-field=mode]')!.onchange=()=>sync();
   card.querySelector<HTMLInputElement>('[data-field=code]')!.onchange=()=>{drawPhysical();drawCash();};
   refreshPhysicalChoices(card,p);
  });sync();
 }
 function drawCash(){
  get('gc-cash').replaceChildren();
  cash.forEach((p,n)=>{
   const card=document.createElement('fieldset');card.dataset.cash=String(n);const legend=document.createElement('legend');legend.textContent=`Cash side ${n+1}`;card.append(legend);const grid=document.createElement('div');grid.className='schedule-fields';card.append(grid);
   field(grid,p,'code','Side code');field(grid,p,'role','Cash role',choice(['consideration','fee'],['Commodity consideration','Usage fee']));field(grid,p,'direction','Direction',choice(['pay','receive'],['Pay cash','Receive cash']));
   field(grid,p,'source','Physical quantity source',physical.map(x=>({value:x.code,label:x.code})));field(grid,p,'stage','Quantity stage',choice(['receipt','delivery','fuel'],['Receipt · before this link’s fuel','Delivery · after this link’s fuel','Fuel · this link’s reduction']));
   const source=physical.find(x=>x.code===p.source),l=source?location(source):undefined;
   const tariffs=(catalog?.tariffs??[]).filter(t=>t.pipeline===source?.pipeline&&t.product===source?.product);
   field(grid,p,'tariffCode','Price source',[{value:'manual',label:'Manual fixed price'},...tariffs.map(t=>({value:t.code,label:`${t.name} · ${t.rate}`}))]);
   field(grid,p,'price',`Price · ${l?.valuation?.priceUnit??'source quote units'}`);field(grid,p,'paymentAt','Payment instant · UTC',undefined,'datetime-local');
   for(const key of ['source','role','stage','tariffCode'])card.querySelector<HTMLSelectElement>(`[data-field=${key}]`)!.onchange=()=>{drawCash();sync();};
   const note=document.createElement('p');note.className='field-help';note.textContent='Configured usage tariffs require the Fee role and Delivery stage. Every cash side has its own direction.';card.append(note);
   const remove=document.createElement('button');remove.type='button';remove.className='secondary';remove.textContent='Remove cash side';remove.onclick=()=>{cash.splice(n,1);invalidate();drawCash();};card.append(remove);get('gc-cash').append(card);
  });sync();
 }
 function draw(){drawPhysical();drawCash();}
 function sync(){
  const locked=o.busy()||!!pending||daily.pending()||o.historical()||!catalog;
  for(const node of root.querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))node.disabled=locked;
  for(const card of get('gc-physical').querySelectorAll<HTMLElement>('[data-physical]')){const p=physical[Number(card.dataset.physical)];for(const key of ['quantity','source','formula','fraction']){const node=card.querySelector<HTMLInputElement|HTMLSelectElement>(`[data-field=${key}]`)!;node.disabled=locked||(key==='quantity'?p.mode!=='independent':p.mode!=='linked');node.required=!node.disabled;}}
  for(const card of get('gc-cash').querySelectorAll<HTMLElement>('[data-cash]')){const p=cash[Number(card.dataset.cash)];const node=card.querySelector<HTMLInputElement>('[data-field=price]')!;node.disabled=locked||p.tariffCode!=='manual';node.required=!node.disabled;}
  for(const node of root.querySelectorAll<HTMLButtonElement>('button'))if(!['gc-keep','gc-discard'].includes(node.id))node.disabled=locked;
  get<HTMLButtonElement>('gc-add-physical').disabled=locked||physical.length>=4;get<HTMLButtonElement>('gc-add-cash').disabled=locked||cash.length>=4;
  get<HTMLButtonElement>('gc-book-submit').disabled=locked||!reviewed||!catalog?.canBook;
  daily.sync();
  get('gc-pending').hidden=!pending;for(const id of ['gc-check','gc-retry'])get<HTMLButtonElement>(id).disabled=o.busy()||!pending||o.historical();
 }
 function reset(){daily.reset();catalog=undefined;epoch++;dirty=false;reviewed=undefined;pending=undefined;serial=1;physical=[{code:'gas_1'}];cash=[];restore();get<HTMLFormElement>('gc-form').reset();get('gc-result').hidden=true;get('gc-values').replaceChildren();get('gc-links').replaceChildren();get('gc-schedules').replaceChildren();get('gc-receipt').textContent='';get('gc-directory').replaceChildren();for(const id of ['gc-book','gc-entity','gc-party'])options(id,[]);draw();}
 async function setCatalog(c:GasCatalog){catalog=c;options('gc-book',c.books.map(b=>({value:b.id,label:b.name})));for(const [id,parties] of [['gc-entity',c.legalEntities],['gc-party',c.counterparties]] as const)options(id,parties.map(p=>({value:JSON.stringify([p.id,p.revision]),label:p.name})));draw();await directory();await daily.resume();}
 function body(){
  if(!catalog||value('gc-policy')!=='no_discounting')throw Error('Select the current catalog and explicit valuation policy.');
  const [legalEntityId,legalEntityRevision]=JSON.parse(value('gc-entity')),[counterpartyId,counterpartyRevision]=JSON.parse(value('gc-party'));
  return {...context(),operationId:crypto.randomUUID(),profile:'fixed_gas_composition_v1',bookId:value('gc-book'),definitionRevisionId:catalog.locations[0].definitionRevisionId,gasSetupId:catalog.gasSetupId,discounting:false,legalEntityId,legalEntityRevision,counterpartyId,counterpartyRevision,
   physicalSides:physical.map(p=>{if(!['independent','linked'].includes(p.mode))throw Error('Select each physical quantity source.');return{code:p.code,pipeline:p.pipeline,zone:p.zone,deliveryPoint:p.deliveryPoint,product:p.product,direction:p.direction,deliveryFrom:p.deliveryFrom,deliveryTo:p.deliveryTo,quantityBasis:p.quantityBasis,quantity:p.mode==='independent'?p.quantity:null,link:p.mode==='linked'?{source:p.source,formula:p.formula,fraction:p.fraction}:null};}),
   cashSides:cash.map(p=>({code:p.code,role:p.role,direction:p.direction,source:p.source,stage:p.stage,price:p.tariffCode==='manual'?p.price:null,tariffCode:p.tariffCode==='manual'?null:p.tariffCode,paymentAt:p.paymentAt+(p.paymentAt.length===16?':00Z':'Z')}))};
 }
 function table(host:HTMLElement,headers:string[],rows:string[][]){const t=document.createElement('table'),head=t.createTHead().insertRow();for(const label of headers){const th=document.createElement('th');th.textContent=label;head.append(th);}const b=t.createTBody();for(const values of rows){const r=b.insertRow();for(const v of values)r.insertCell().textContent=v;}host.replaceChildren(t);}
 function render(v:Value,title:string,terms?:EntryTerms){const prices=new Map(terms?.cashSides.map(c=>[c.code,c]));get('gc-result-title').textContent=title;get('gc-total').textContent=`Present value: ${v.presentValue} ${v.currency.toUpperCase()}`;table(get('gc-values'),['Side','Role','Direction','Quantity source','Stage',`Quantity · ${v.quantityUnit}`,'Cash price source','Payment · UTC','Signed PV'],v.sides.map(s=>[s.code,s.role,s.direction,s.source??[s.pipeline,s.zone,s.deliveryPoint].join(' / '),s.stage??'physical delivery',exact(s.quantity),prices.get(s.code)?.price??prices.get(s.code)?.tariffCode??'',prices.get(s.code)?.paymentAt??'',s.presentValue]));table(get('gc-links'),['Source','Destination','Rule','Fraction','Receipt','Delivery','Fuel'],v.links.map(l=>[l.source,l.target,l.formula,l.fraction,l.receipt,l.delivery,l.fuel]));get('gc-schedules').replaceChildren();for(const s of v.sides.filter(s=>s.role==='physical')){const details=document.createElement('details'),summary=document.createElement('summary');summary.textContent=`${s.code} · retained contractual delivery schedule`;details.append(summary);const host=document.createElement('div');host.className='table-wrap';table(host,['Gas day','Start · UTC','End · UTC',`Quantity · ${v.quantityUnit}`,'Meter identifier'],s.deliveryRows.map(r=>[r.day,r.start,r.end,exact(r.quantity),r.meter?.identifier??'Unassigned']));details.append(host);get('gc-schedules').append(details);}get('gc-result').hidden=false;}
 async function directory(){if(!catalog)return;const e=epoch,result=await o.api<{trades:{trade_id:string;book:string;physical_count:string}[]}>('gas/composition/directory',context());if(e!==epoch)return;const list=document.createElement('div');for(const trade of result.trades){const button=document.createElement('button');button.type='button';button.className='text-button';button.textContent=`${trade.book} · ${trade.physical_count} physical sides · ${trade.trade_id}`;button.onclick=()=>void o.act(()=>open(trade.trade_id));list.append(button);const quantities=document.createElement('button');quantities.type='button';quantities.className='secondary';quantities.textContent='Daily delivery quantities';quantities.onclick=()=>void o.act(()=>daily.open(trade.trade_id));list.append(quantities);}get('gc-directory').replaceChildren(list);}
 async function open(id:string){const e=epoch,result=await o.api<{valuation:Value;terms:EntryTerms}>('gas/composition/editor',{context:context(),tradeId:id});if(e!==epoch)return;render(result.valuation,'Booked composition · retained side schedules',result.terms);await daily.open(id);}
 function receipt(r:Receipt){if(!pending||r.operationId!==pending.body.operationId||r.revision!==1||typeof r.tradeId!=='string'||typeof r.instrumentId!=='string')throw Error('The booking response is unconfirmed. Check its original status.');pending=undefined;reviewed=undefined;dirty=false;retain();get('gc-receipt').textContent=`Booked composition ${r.tradeId}`;sync();}
 async function send(){if(!pending)return;const command=pending,actor=o.actor(),e=epoch,wasUncertain=command.uncertain;command.uncertain=true;retain();try{const r=await o.api<Receipt>('gas/composition/book',command.body);if(actor!==o.actor()||e!==epoch)return;receipt(r);await open(r.tradeId);await directory();await o.onBooked?.(r.tradeId);}catch(error){if(actor===o.actor()&&e===epoch){const code=(error as {code?:string}).code;if(!wasUncertain&&code&&(code.startsWith('gas_')||['access_denied','trade_party','request_format','side_schedule_request_conflict'].includes(code))){pending=undefined;retain();}}throw error;}finally{sync();}}
 get('gc-form').oninput=e=>{if((e.target as HTMLElement).closest('#gc-physical,#gc-cash'))return;invalidate();};
 get<HTMLFormElement>('gc-form').onsubmit=e=>{e.preventDefault();void o.act(async()=>{invalidate();const command=body(),g=epoch,result=await o.api<Value>('gas/composition/preview',command);if(g!==epoch)return;reviewed=command;render(result,'Unbooked gas composition',command);sync();});};
 get('gc-add-physical').onclick=()=>{physical.push({code:`gas_${++serial}`});invalidate();draw();};get('gc-add-cash').onclick=()=>{cash.push({code:`cash_${++serial}`});invalidate();drawCash();};
 get('gc-book-submit').onclick=()=>void o.act(async()=>{if(!reviewed)throw Error('Preview the exact current composition first.');pending={body:structuredClone(reviewed),uncertain:false};reviewed=undefined;retain();sync();await send();});
 get('gc-retry').onclick=()=>void o.act(send);get('gc-check').onclick=()=>void o.act(async()=>{if(!pending)return;const r=await o.api<Receipt|{found:false}>('gas/composition/status',{operationId:pending.body.operationId});if('tradeId'in r){receipt(r);await open(r.tradeId);await directory();await o.onBooked?.(r.tradeId);}else get('gc-receipt').textContent='No completed booking found. Retry the original request.';});get('gc-refresh').onclick=()=>void o.act(directory);
 let leaving:((go:boolean)=>void)|undefined;const prompt=get<HTMLDialogElement>('gc-leave');
 function finish(go:boolean){if(go){dirty=false;daily.discard();}prompt.close();const resolve=leaving;leaving=undefined;resolve?.(go);}
 get('gc-keep').onclick=()=>finish(false);get('gc-discard').onclick=()=>finish(true);prompt.oncancel=e=>{e.preventDefault();finish(false);};
 function leave():Promise<boolean>{if((!dirty&&!daily.unsaved())||pending)return Promise.resolve(true);if(leaving)return Promise.resolve(false);prompt.showModal();return new Promise(resolve=>{leaving=resolve;});}
 return{reset,setCatalog,sync,leave,quantities:(tradeId:string)=>daily.open(tradeId),pending:()=>!!pending||daily.pending(),unsaved:()=>dirty||!!pending||daily.unsaved()};
}
