import {createAvailableDatePicker} from './available-date-picker.js';
import {renderRegularTable} from './regular-table.js';
import {publicText} from './market-view.js';

type Source={id:string;name:string;provider:string;market:string;minutes:number;availableDates:string[];availableThrough:Record<string,string>};
type Catalog={knownAt:string;sources:Source[];jobs:{name:string;schedule_enabled:boolean}[];attempts:{state:string;started_at:string;error_code:string|null}[]};
export function setupPowerImports(options:{api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;instant:(value:string)=>string}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 let catalog:Catalog|undefined;
 const clear=()=>get('delivery-price-results').replaceChildren();
 const from=get<HTMLInputElement>('delivery-price-from'),through=get<HTMLInputElement>('delivery-price-through');
 const first=createAvailableDatePicker(get('delivery-price-from-calendar'),{id:'delivery-price-from-picker',label:'First operating date',busy:options.busy,onChange:date=>{from.value=date;clear();syncDates();}});
 const last=createAvailableDatePicker(get('delivery-price-through-calendar'),{id:'delivery-price-through-picker',label:'Last operating date',busy:options.busy,onChange:date=>{through.value=date;clear();}});
 function syncDates(){const source=catalog?.sources.find(s=>s.id===get<HTMLSelectElement>('delivery-price-source').value),dates=source?.availableDates??[];first.set(dates,from.value);from.value=first.value();last.set(dates.filter(d=>!!from.value&&d>=from.value&&d<=(source?.availableThrough[from.value]??'')),through.value);through.value=last.value();}
 get<HTMLSelectElement>('delivery-price-source').onchange=()=>{from.value='';through.value='';clear();syncDates();};
 get<HTMLFormElement>('delivery-price-form').onsubmit=event=>{event.preventDefault();clear();void options.act(async()=>{
  if(!catalog)throw Error('Load available delivery-price sources.');
  const source=catalog.sources.find(s=>s.id===get<HTMLSelectElement>('delivery-price-source').value);if(!source)throw Error('Choose a delivery-price source.');
  if(!source.availableDates.includes(from.value)||!source.availableDates.includes(through.value)||through.value<from.value||through.value>(source.availableThrough[from.value]??''))throw Error('Choose available first and last operating dates.');
  const result=await options.api<{rows:{start:string;end:string;formattedPrice:string;knownAt:string}[]}>('power-imports/period',{sourceId:source.id,fromDate:get<HTMLInputElement>('delivery-price-from').value,throughDate:get<HTMLInputElement>('delivery-price-through').value,knownAt:catalog.knownAt});
  renderRegularTable(get('delivery-price-results'),'delivery-prices',source.id+catalog.knownAt,['Delivery start','Delivery end','USD/MWh','Known at'],result.rows.map(r=>[options.instant(r.start),options.instant(r.end),r.formattedPrice,options.instant(r.knownAt)]),[2],options.busy);
 });};
 return {sync(){first.sync();last.sync();},reset(){catalog=undefined;clear();get<HTMLSelectElement>('delivery-price-source').replaceChildren();from.value='';through.value='';syncDates();},async load(){clear();catalog=await options.api<Catalog>('power-imports');get<HTMLSelectElement>('delivery-price-source').replaceChildren(new Option(catalog.sources.length?'Choose source…':'No sources with saved prices',''),...catalog.sources.filter(s=>s.availableDates.length>0).map(s=>new Option(publicText(`${s.provider} · ${s.name} · ${s.market} · ${s.minutes} min`),s.id)));from.value='';through.value='';syncDates();get('delivery-price-known').textContent='Known at '+options.instant(catalog.knownAt);get('delivery-price-jobs').textContent=catalog.jobs.length?catalog.jobs.map(j=>publicText(j.name)+': '+(j.schedule_enabled?'scheduled':'paused')).join(' · '):'No collection sources have been configured.';renderRegularTable(get('delivery-price-attempts'),'delivery-price-attempts',catalog.knownAt,['Started','Outcome','Details'],catalog.attempts.map(a=>[options.instant(a.started_at),a.state,a.error_code??'']),[],options.busy);}};
}
