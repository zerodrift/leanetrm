import {renderRegularTable} from './regular-table.js';
import {publicText} from './market-view.js';

type Source={id:string;name:string;provider:string;market:string;minutes:number};
type Catalog={knownAt:string;sources:Source[];jobs:{name:string;schedule_enabled:boolean}[];attempts:{state:string;started_at:string;error_code:string|null}[]};
export function setupPowerImports(options:{api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;instant:(value:string)=>string}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 let catalog:Catalog|undefined;
 const clear=()=>get('delivery-price-results').replaceChildren();
 get<HTMLSelectElement>('delivery-price-source').onchange=clear;
 get<HTMLFormElement>('delivery-price-form').onsubmit=event=>{event.preventDefault();clear();void options.act(async()=>{
  if(!catalog)throw Error('Load available delivery-price sources.');
  const source=catalog.sources.find(s=>s.id===get<HTMLSelectElement>('delivery-price-source').value);if(!source)throw Error('Choose a delivery-price source.');
  const result=await options.api<{rows:{start:string;end:string;formattedPrice:string;knownAt:string}[]}>('power-imports/period',{sourceId:source.id,fromDate:get<HTMLInputElement>('delivery-price-from').value,throughDate:get<HTMLInputElement>('delivery-price-through').value,knownAt:catalog.knownAt});
  renderRegularTable(get('delivery-price-results'),'delivery-prices',source.id+catalog.knownAt,['Delivery start','Delivery end','USD/MWh','Known at'],result.rows.map(r=>[options.instant(r.start),options.instant(r.end),r.formattedPrice,options.instant(r.knownAt)]),[2],options.busy);
 });};
 return {reset(){catalog=undefined;clear();get<HTMLSelectElement>('delivery-price-source').replaceChildren();},async load(){clear();catalog=await options.api<Catalog>('power-imports');get<HTMLSelectElement>('delivery-price-source').replaceChildren(new Option('Choose source…',''),...catalog.sources.map(s=>new Option(publicText(`${s.provider} · ${s.name} · ${s.market} · ${s.minutes} min`),s.id)));get('delivery-price-known').textContent='Known at '+options.instant(catalog.knownAt);get('delivery-price-jobs').textContent=catalog.jobs.length?catalog.jobs.map(j=>publicText(j.name)+': '+(j.schedule_enabled?'scheduled':'paused')).join(' · '):'No collection sources have been configured.';renderRegularTable(get('delivery-price-attempts'),'delivery-price-attempts',catalog.knownAt,['Started','Outcome','Details'],catalog.attempts.map(a=>[options.instant(a.started_at),a.state,a.error_code??'']),[],options.busy);}};
}
