import {button,choices,element,field,group,help,input,resultTable,select,type PriceAuthoringOptions} from './price-authoring-ui.js';
import {utcMicrosecondText} from './price-authoring-model.js';

type Reference={revision:{catalogId:string;revisionId:string};code:string};
type Publication={key:{catalogId:string;id:string;version:number};implementation:string;indices:{key:Reference}[]};
type Source={id:string;name:string;provider:string;market:string;minutes:number};
type Inspection={publication:Publication['key'];index:Reference;source:{id:string};market:string;fromDate:string;throughDate:string;knownAt:string;contextId:string;
 publicationStatus:string;contractuallyFinal:boolean;bindingSaved:boolean;complete:boolean;expectedIntervals:number;missingIntervals:number;
 rows:{start:string;end:string;price:{value:string};batchId:string;revision:number|null;knownAt:string}[]};
type ProductChoice={code:string;name:string;version:number};
type ProductInspection={capture:Inspection;product:ProductChoice;block:string;averaging:string;calendar:{code:string;holidays:string[]};
 expectedIntervals:number;missingIntervals:number;complete:boolean;state:string;formattedAverage:string|null;
 days:{operatingDate:string;expectedIntervals:number;missingIntervals:number;state:string;formattedAverage:string|null}[]};

export function setupPowerPublicationPreview(host:HTMLElement,o:PriceAuthoringOptions,selection:()=>Publication|undefined){
 const fields=element('fieldset');fields.className='price-authoring-preview';
 fields.append(element('h3','Captured hourly prices'),help('Inspect a possible match to an existing PJM hourly source. This does not save a source binding or qualify prices as final settlements.'));
 const index=select('power-publication-index'),source=select('power-publication-source'),market=select('power-publication-market');
 const from=input('power-publication-from','date'),through=input('power-publication-through','date'),known=input('power-publication-known');
 known.placeholder='YYYY-MM-DDTHH:mm:ssZ';known.spellcheck=false;
 const load=button('Load captured sources',()=>void o.act(loadSources),'power-publication-load-sources');
 const check=button('Inspect captured prices',()=>void o.act(inspect),'power-publication-check');
 const product=select('power-publication-product'),block=select('power-publication-block'),averaging=select('power-publication-averaging');
 const loadProducts=button('Load product choices',()=>void o.act(readProducts),'power-publication-load-products');
 const checkProduct=button('Inspect product averages',()=>void o.act(inspectProduct),'power-publication-check-product');
 const result=element('div');result.id='power-publication-result';result.setAttribute('role','status');
 fields.append(group(field('Saved index',index),field('Captured source',source),field('Market',market)),group(field('First operating date',from),field('Last operating date',through),field('Known at · UTC',known)),group(load,check),
  help('Product diagnostics use the current published product definition and the selected price cutoff. Historical product-version replay is unavailable.'),
  group(field('Published TOU product',product),field('Hours',block),field('Price averaging',averaging)),group(loadProducts,checkProduct),result);host.append(fields);
 let generation=0,context='',products:ProductChoice[]=[];
 function contextKey(){const p=selection(),historical=o.historical();return JSON.stringify([o.identity(),historical||!p?null:o.snapshot(),historical,p]);}
 function clear(){generation++;result.replaceChildren();}
 fields.addEventListener('input',clear);fields.addEventListener('change',clear);
 function inputs(){return [index.value,source.value,market.value,from.value,through.value,known.value,product.value,block.value,averaging.value].join('|');}
 function ticket(){const at=generation,key=contextKey(),values=inputs();return ()=>at===generation&&key===contextKey()&&values===inputs();}
 function reset(){
  clear();context=contextKey();const p=selection();
  choices(index,p?.indices.map(i=>({value:i.key.code,label:i.key.code}))??[]);
  choices(source,[]);choices(market,[{value:'DA',label:'Day-ahead'},{value:'RT',label:'Real-time'}]);
  products=[];choices(product,[]);choices(block,[]);choices(averaging,[]);
  from.value=through.value=known.value='';sync();
 }
 function sync(){
  if(context!==contextKey()){reset();return;}
  const p=selection();host.hidden=!p||!['power_placeholder_v1','power_captured_hourly_v1'].includes(p.implementation);
  fields.disabled=o.busy()||o.historical()||host.hidden;
 }
 async function loadSources(){
  clear();choices(source,[]);const valid=ticket();
  const catalog=await o.api<{sources:Source[]}>('power-imports');if(!valid())return;
  choices(source,catalog.sources.filter(s=>s.provider==='PJM'&&s.minutes===60).map(s=>({value:s.id,label:`${s.name} · ${s.market}`})));
 }
 function captureRequest(){
  const p=selection();
  if(o.historical()||!p||!['power_placeholder_v1','power_captured_hourly_v1'].includes(p.implementation)||!index.value||!source.value||!market.value||!from.value||!through.value||!known.value)
   throw Error('Select a saved power publication, index, captured source, market, operating dates and explicit knowledge cutoff.');
  return {snapshotId:o.snapshot(),publication:p.key,indexCode:index.value,sourceId:source.value,market:market.value,fromDate:from.value,throughDate:through.value,knownAt:known.value};
 }
 function validateCapture(response:Inspection,request:ReturnType<typeof captureRequest>){
  const p=selection();if(!p)throw Error('The selected publication changed.');
  const member=p.indices.find(i=>i.key.code===request.indexCode)?.key;
  if(response.publication?.catalogId!==p.key.catalogId||response.publication?.id!==p.key.id||response.publication?.version!==p.key.version||
   response.index?.code!==request.indexCode||response.source?.id!==request.sourceId||response.market!==request.market||
   response.index?.revision?.catalogId!==member?.revision.catalogId||response.index?.revision?.revisionId!==member?.revision.revisionId||
   response.fromDate!==request.fromDate||response.throughDate!==request.throughDate||utcMicrosecondText(response.knownAt)!==utcMicrosecondText(request.knownAt)||
   response.publicationStatus!=='collected'||response.contractuallyFinal!==false||response.bindingSaved!==false)
   throw Error('The inspection response did not match the selected captured source and publication.');
 }
 function renderCapture(host:HTMLElement,response:Inspection){
  host.append(help(`Collected prices · not qualified final settlements. ${response.expectedIntervals} expected intervals · ${response.missingIntervals} missing. Source match inspected; no binding saved.`));
  host.append(help(`Known at · ${response.knownAt} · Knowledge context ${response.contextId}`));
  const rows=element('div');resultTable(rows,['Interval start · UTC','Interval end · UTC','Captured price','Provider revision','Batch','Known at · UTC'],response.rows.map(r=>[r.start,r.end,r.price.value,r.revision===null?'—':String(r.revision),r.batchId,r.knownAt]));host.append(rows);
 }
 async function inspect(){
  clear();const request=captureRequest(),valid=ticket();
  const response=await o.api<Inspection>('price-publications/power/preview',request);if(!valid())return;
  validateCapture(response,request);renderCapture(result,response);
 }
 async function readProducts(){
  clear();products=[];choices(product,[]);choices(block,[]);choices(averaging,[]);const valid=ticket();
  const catalog=await o.api<{products:ProductChoice[];blocks:{code:string;name:string}[];averaging:{code:string;name:string}[]}>(`price-publications/power/products?snapshotId=${encodeURIComponent(o.snapshot())}`);
  if(!valid())return;products=catalog.products;
  choices(product,products.map(p=>({value:`${p.code}:${p.version}`,label:`${p.name} · v${p.version}`})));
  choices(block,catalog.blocks.map(b=>({value:b.code,label:b.name})));choices(averaging,catalog.averaging.map(a=>({value:a.code,label:a.name})));
 }
 async function inspectProduct(){
  clear();const capture=captureRequest(),p=products.find(p=>`${p.code}:${p.version}`===product.value);
  if(!p||!block.value||!averaging.value)throw Error('Select the exact published product, hours and averaging policy.');
  const request={capture,product:p.code,productVersion:p.version,block:block.value,averaging:averaging.value},valid=ticket();
  const response=await o.api<ProductInspection>('price-publications/power/product-preview',request);if(!valid())return;
  validateCapture(response.capture,capture);
  if(response.product?.code!==request.product||response.product?.version!==request.productVersion||response.block!==request.block||response.averaging!==request.averaging)
   throw Error('The product response did not match the selected product and averaging policy.');
  result.append(help(`Selected-product coverage · ${response.expectedIntervals} expected intervals · ${response.missingIntervals} missing.`),
   help(`Native-hour average · ${response.formattedAverage??'Unavailable'}. This diagnostic is not an official monthly exchange fixing.`),
   help(`Delivery calendar · ${response.calendar.code} · Holidays in range: ${response.calendar.holidays.join(', ')||'none'}`));
  const days=element('div');resultTable(days,['Operating date','Selected intervals','Missing','State','Native-hour average'],response.days.map(d=>[d.operatingDate,String(d.expectedIntervals),String(d.missingIntervals),d.state,d.formattedAverage]));result.append(days);
  const evidence=element('details');evidence.append(element('summary','Captured hourly evidence'));renderCapture(evidence,response.capture);result.append(evidence);
 }
 return {reset,sync};
}
