import {zonedInput} from './user-time.js';
import {createAvailableDatePicker} from './available-date-picker.js';
import {renderRegularTable} from './regular-table.js';
import {publicText} from './market-view.js';
import {deliveryPriceChoices,latestDeliverySource,type DeliveryPriceSource} from './delivery-price-filters.js';

type Catalog={knownAt:string;sources:DeliveryPriceSource[];jobs:{name:string;schedule_enabled:boolean}[];attempts:{jobName:string;provider:string;fromDate:string;throughDate:string;startedAt:string;finishedAt:string|null;state:string;errorCode:string|null}[]};
export function setupPowerImports(options:{zone:()=>string;api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;instant:(value:string)=>string}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const known=zonedInput(get<HTMLInputElement>('delivery-price-cutoff'),options.zone,options.api);
 const filterNames=['commodity','provider','market','location','source'] as const;
 const select=(name:typeof filterNames[number])=>get<HTMLSelectElement>('delivery-price-'+name);
 let catalog:Catalog|undefined,generation=0;
 const clear=()=>{generation++;get('delivery-price-results').replaceChildren();};
 const from=get<HTMLInputElement>('delivery-price-from'),through=get<HTMLInputElement>('delivery-price-through');
 const firstDate=createAvailableDatePicker(get('delivery-price-from-calendar'),{id:'delivery-price-from-picker',label:'Delivery date from',busy:options.busy,onChange:date=>{from.value=date;clear();syncDates();}});
 const lastDate=createAvailableDatePicker(get('delivery-price-through-calendar'),{id:'delivery-price-through-picker',label:'Delivery date to',busy:options.busy,onChange:date=>{through.value=date;clear();}});
 function syncDates(){
  const source=catalog?.sources.find(s=>s.id===select('source').value),dates=source?.availableDates??[];
  firstDate.set(dates,from.value);from.value=firstDate.value();
  lastDate.set(dates.filter(date=>!!from.value&&date>=from.value&&date<=(source?.availableThrough[from.value]??'')),through.value);through.value=lastDate.value();
 }
 function filters(preferred?:DeliveryPriceSource){
  for(const name of filterNames){const choices=deliveryPriceChoices(catalog?.sources??[],{commodityKey:select('commodity').value,provider:select('provider').value,market:select('market').value,locationKey:select('location').value});const control=select(name),value=preferred?({commodity:preferred.commodityKey,provider:preferred.provider,market:preferred.market,location:preferred.locationKey,source:preferred.id})[name]:control.value,rows=choices[name==='commodity'?'commodities':name==='provider'?'providers':name==='market'?'markets':name==='location'?'locations':'sources'];control.replaceChildren(new Option(`Choose ${name==='provider'?'operator':name}…`,''),...rows.map(c=>new Option(publicText(c.name),c.value)));control.value=rows.some(r=>r.value===value)?value:rows[0]?.value??'';control.disabled=rows.length===0;}
  const selected=catalog?.sources.find(s=>s.id===select('source').value);
  if(!from.value&&selected?.availableDates.length){from.value=[...selected.availableDates].sort().at(-1)!;through.value=from.value;}
  syncDates();
  const source=catalog?.sources.find(s=>s.id===select('source').value);
  get('delivery-price-clock').textContent=source?`Delivery dates use ${source.marketClock}; both dates are included. Interval timestamps use your profile time zone.`:'Choose a source to see its market clock. Both delivery dates are included.';
 }
 filterNames.forEach((name,index)=>{select(name).onchange=()=>{clear();from.value='';through.value='';for(const child of filterNames.slice(index+1))select(child).value='';filters();void options.act(read);};});
 for(const name of ['from','through'])get<HTMLInputElement>('delivery-price-'+name).onchange=()=>{clear();syncDates();};
 async function read(){
  clear();
  if(!catalog)throw Error('Load available delivery-price sources.');
  const eligible=deliveryPriceChoices(catalog.sources,{commodityKey:select('commodity').value,provider:select('provider').value,market:select('market').value,locationKey:select('location').value}).sources;
  const source=catalog.sources.find(s=>s.id===select('source').value&&eligible.some(c=>c.value===s.id));if(!source){get('delivery-price-results').textContent='No delivery-price records available.';return;}
  if(!source.availableDates.includes(from.value)||!source.availableDates.includes(through.value)||through.value<from.value||through.value>(source.availableThrough[from.value]??''))throw Error('Choose available first and last delivery dates.');
  const owner=catalog,started=generation,cutoff=await known.instant();
  const result=await options.api<{priceUnit:string;rows:{start:string;end:string;formattedPrice:string;knownAt:string}[]}>('power-imports/period',{sourceId:source.id,fromDate:get<HTMLInputElement>('delivery-price-from').value,throughDate:get<HTMLInputElement>('delivery-price-through').value,knownAt:cutoff});
  if(catalog!==owner||generation!==started)return;
  get('delivery-price-known').textContent='Knowledge cutoff · '+options.instant(cutoff);
  if(!result.rows.length){get('delivery-price-results').textContent='No delivery-price records available at this cutoff.';return;}
  renderRegularTable(get('delivery-price-results'),'delivery-prices',JSON.stringify([source.id,from.value,through.value,cutoff]),['Delivery start','Delivery end',publicText(result.priceUnit),'Known at'],result.rows.map(r=>[options.instant(r.start),options.instant(r.end),r.formattedPrice,options.instant(r.knownAt)]),[2],options.busy);
 }
 get('delivery-price-cutoff').addEventListener('input',()=>{clear();get('delivery-price-known').textContent='Unapplied cutoff · choose Show prices.';});
 get<HTMLFormElement>('delivery-price-form').onsubmit=event=>{event.preventDefault();void options.act(read);};
 return {
  sync(){known.refresh();firstDate.sync();lastDate.sync();for(const name of filterNames)select(name).disabled=options.busy()||select(name).options.length<2;},
  reset(){known.clear();catalog=undefined;clear();for(const name of filterNames)select(name).replaceChildren();filters();get('delivery-price-attempts').replaceChildren();get('delivery-price-jobs').textContent='';get('delivery-price-known').textContent='';},
  async load(){
   clear();from.value='';through.value='';const started=generation;catalog=undefined;filters();get('delivery-price-attempts').replaceChildren();get('delivery-price-jobs').textContent='';get('delivery-price-known').textContent='';
   const next=await options.api<Catalog>('power-imports');if(generation!==started)return;catalog=next;known.set(catalog.knownAt);from.value='';through.value='';filters(latestDeliverySource(catalog.sources));
   get('delivery-price-known').textContent='Knowledge cutoff · '+options.instant(catalog.knownAt);
   get('delivery-price-jobs').textContent=catalog.jobs.length?catalog.jobs.map(j=>publicText(j.name)+': '+(j.schedule_enabled?'scheduled':'paused')).join(' · '):'No collection jobs have been configured.';
   renderRegularTable(get('delivery-price-attempts'),'delivery-price-attempts',catalog.knownAt,['Job','Operator','Delivery date from','Delivery date to','Started','Finished','Outcome','Details'],catalog.attempts.map(a=>[publicText(a.jobName),publicText(a.provider),a.fromDate,a.throughDate,options.instant(a.startedAt),a.finishedAt?options.instant(a.finishedAt):'In progress',a.state,publicText(a.errorCode??'')]),[],options.busy);
   await read();
  },
 };
}
