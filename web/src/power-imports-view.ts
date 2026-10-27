// Delivery prices as a desk (markets-desk.md, Delivery prices): a filter bar, the range shown with how it was chosen, tiles of the
// backend's summary, and tabs for the interval prices and the collection history. The page opens on the catalog's opening; any other
// range is the person's to choose, and is read when they choose Show prices. It keeps its own knowledge cutoff, outside the As of dock.
import {zonedInput} from './user-time.js';
import {createAvailableDatePicker} from './available-date-picker.js';
import {renderRegularTable} from './regular-table.js';
import {displayDate,publicText,timeLabels} from './market-view.js';
import {deliveryPriceChoices,deliveryOpening,type DeliveryOpening,type DeliveryPriceSource} from './delivery-price-filters.js';
import {collectionTile,deliveryChartPoints,summaryTiles,type DeliveryCollection,type DeliveryRow,type DeliverySummary} from './delivery-price-desk.js';
import {overlayMenu,renderTimeChart,type OverlayCatalog,type OverlayRequest,type OverlaySeries,type TimeChart} from './time-chart.js';
import {historyOverlays} from './contract-panel.js';
import {setupTabs} from './side-panel.js';
import {renderTile,type TileSpec} from './tiles.js';

type Catalog={knownAt:string;sources:DeliveryPriceSource[];opening:DeliveryOpening|null;collection:DeliveryCollection;jobs:{name:string;schedule_enabled:boolean}[];
  attempts:{jobName:string;provider:string;fromDate:string;throughDate:string;startedAt:string;finishedAt:string|null;state:string;errorCode:string|null}[]};
type Period={priceUnit:string;rows:DeliveryRow[];summary:DeliverySummary;overlays?:Omit<OverlaySeries,'label'>[]};
/** How the range on screen was chosen: the catalog's opening, or by the person, once they change a filter, a date or the cutoff. */
type ChosenBy='opening'|'person';
const badges:Record<ChosenBy,string>={opening:'Newest delivery day',person:'Selected range'};

export function setupPowerImports(options:{zone:()=>string;api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;
  instant:(value:string)=>string;overlayCatalog:()=>Promise<OverlayCatalog>}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const known=zonedInput(get<HTMLInputElement>('delivery-price-cutoff'),options.zone,options.api);
 const filterNames=['commodity','provider','market','location','source'] as const;
 const choiceNames={commodity:'commodities',provider:'providers',market:'markets',location:'locations',source:'sources'} as const;
 const select=(name:typeof filterNames[number])=>get<HTMLSelectElement>('delivery-price-'+name);
 const from=get<HTMLInputElement>('delivery-price-from'),through=get<HTMLInputElement>('delivery-price-through');
 const overlays=get<HTMLDetailsElement>('delivery-price-overlays');
 let catalog:Catalog|undefined,generation=0,chosenBy:ChosenBy='opening',menu:OverlayCatalog|undefined,requests:OverlayRequest[]=[],chart:TimeChart|undefined;
 const firstDate=createAvailableDatePicker(get('delivery-price-from-calendar'),{id:'delivery-price-from-picker',label:'Delivery date from',busy:options.busy,onChange:date=>{from.value=date;changed();}});
 const lastDate=createAvailableDatePicker(get('delivery-price-through-calendar'),{id:'delivery-price-through-picker',label:'Delivery date to',busy:options.busy,onChange:date=>{through.value=date;changed();}});
 const show=(tab:string)=>{get('delivery-price-prices').hidden=tab!=='prices';get('delivery-price-collection').hidden=tab!=='collection';};
 const tabs=setupTabs(get('delivery-price-tabs'),[['prices','Prices'],['collection','Collection history']],{label:'Delivery prices',panel:get('delivery-price-view'),selected:'prices',select:show});
 const chosenSource=()=>catalog?.sources.find(s=>s.id===select('source').value);
 function syncDates(){
  const source=chosenSource(),dates=source?.availableDates??[];
  firstDate.set(dates,from.value);from.value=firstDate.value();
  lastDate.set(dates.filter(date=>!!from.value&&date>=from.value&&date<=(source?.availableThrough[from.value]??'')),through.value);through.value=lastDate.value();
 }
 // Each select offers what the choices above it allow, and keeps its value only while it is still offered: nothing is chosen for the person.
 function filters(opening?:DeliveryPriceSource){
  for(const name of filterNames){
   const rows=deliveryPriceChoices(catalog?.sources??[],{commodityKey:select('commodity').value,provider:select('provider').value,market:select('market').value,locationKey:select('location').value})[choiceNames[name]];
   const control=select(name),value=opening?({commodity:opening.commodityKey,provider:opening.provider,market:opening.market,location:opening.locationKey,source:opening.id})[name]:control.value;
   control.replaceChildren(new Option(`Choose ${name==='provider'?'operator':name}…`,''),...rows.map(c=>new Option(publicText(c.name),c.value)));
   control.value=rows.some(r=>r.value===value)?value:'';control.disabled=rows.length===0;
  }
  syncDates();
  const source=chosenSource();
  get('delivery-price-clock').textContent=source?`Delivery dates use ${source.marketClock}; both dates are included. Interval times use your profile time zone.`:'Choose a source to see its market clock. Both delivery dates are included.';
 }
 // What the prices tab says while it shows no prices: what is still to choose, or that the chosen range waits for Show prices.
 function pending(){
  if(!catalog)return ['',''];
  if(catalog.opening===null&&chosenBy==='opening')return ['No delivery prices yet','No source has published delivery prices. Collection history shows each attempt to collect them.'];
  if(!chosenSource())return ['Choose a source','Choose the commodity, operator, market, location and source of the prices to show.'];
  if(!from.value||!through.value)return ['Choose the delivery dates','Choose the first and last delivery dates. Only dates with saved prices can be chosen.'];
  return ['Show prices to read this range','The source, delivery dates and knowledge cutoff above are read when you choose Show prices.'];
 }
 function empty([title,help]:string[]){
  get('delivery-price-empty-title').textContent=title;get('delivery-price-empty-help').textContent=help;
  get('delivery-price-empty').hidden=!title;get('delivery-price-shown').hidden=true;
 }
 function tiles(summary:TileSpec[]){
  const tile=(spec:TileSpec,kind:string)=>{const element=renderTile(spec);element.dataset.tile=kind;element.dataset.name=spec.label;return element;};
  get('delivery-price-tiles').replaceChildren(...summary.map(spec=>tile(spec,'summary')),...(catalog?[tile(collectionTile(catalog.collection,displayDate),'collection')]:[]));
 }
 // Puts away the prices on screen; a read that was under way is no longer shown.
 function clearShown(){
  generation++;chart?.dispose();chart=undefined;
  get('delivery-price-results').replaceChildren();get('delivery-price-head').hidden=true;tiles([]);empty(pending());
 }
 // A change of the person's: the range is theirs from now on, and is read when they choose Show prices.
 function changed(){chosenBy='person';syncDates();clearShown();}
 filterNames.forEach((name,index)=>{select(name).onchange=()=>{for(const child of filterNames.slice(index+1))select(child).value='';from.value='';through.value='';filters();changed();};});
 for(const input of [from,through])input.onchange=changed;
 get('delivery-price-cutoff').addEventListener('input',()=>{changed();get('delivery-price-known').textContent='Unapplied cutoff · choose Show prices.';});
 function overlaySummary(){overlays.querySelector('summary')!.textContent=requests.length?`Overlays · ${requests.length} shown`:'Overlays';}
 async function read(){
  if(!catalog)throw Error('Load available delivery-price sources.');
  const eligible=deliveryPriceChoices(catalog.sources,{commodityKey:select('commodity').value,provider:select('provider').value,market:select('market').value,locationKey:select('location').value}).sources;
  const source=catalog.sources.find(s=>s.id===select('source').value&&eligible.some(c=>c.value===s.id));
  if(!source)throw Error('Choose the commodity, operator, market, location and source of the prices to show.');
  if(!source.availableDates.includes(from.value)||!source.availableDates.includes(through.value)||through.value<from.value||through.value>(source.availableThrough[from.value]??''))throw Error('Choose available first and last delivery dates.');
  clearShown();
  const owner=catalog,started=generation,by=chosenBy,asked=requests,shownMenu=menu!,range={sourceId:source.id,fromDate:from.value,throughDate:through.value};
  const cutoff=await known.instant();
  const result=await options.api<Period>('power-imports/period',{...range,knownAt:cutoff,...(asked.length?{overlays:asked}:{})});
  if(catalog!==owner||generation!==started)return;
  const drawn=historyOverlays(asked,result.overlays,shownMenu),zone=options.zone(),rows=result.rows;
  // Every interval's start and end, labelled together so that a clock's repeated hour keeps its instants apart.
  const labels=timeLabels([...rows.map(r=>r.start),...rows.map(r=>r.end)],zone),starts=labels.slice(0,rows.length),ends=labels.slice(rows.length);
  const startLabel=new Map(rows.map((r,i)=>[Date.parse(r.start),starts[i]]));
  const intervalStart=(at:string)=>startLabel.get(Date.parse(at))??(()=>{throw Error('The delivery-price summary names an interval the read did not return. Read it again.');})();
  get('delivery-price-known').textContent='Knowledge cutoff · '+options.instant(cutoff);
  // Styled as the As of dock's close badge: the backend's newest day as its latest close, a range of the person's as any other.
  get('delivery-price-badge').textContent=badges[by];get('delivery-price-badge').dataset.badge=by==='opening'?'latest':'selected';
  get('delivery-price-path').textContent=publicText([source.provider,source.marketName,source.locationName].join(' · '));
  get('delivery-price-title').textContent=publicText(source.name);
  get('delivery-price-range').textContent=`${range.fromDate===range.throughDate?displayDate(range.fromDate):`${displayDate(range.fromDate)} – ${displayDate(range.throughDate)}`} · ${source.minutes}-minute intervals`;
  get('delivery-price-unit').textContent=publicText(result.priceUnit);get('delivery-price-head').hidden=false;
  tiles(summaryTiles(result.summary,publicText(result.priceUnit),{start:intervalStart,instant:options.instant}));
  if(!rows.length){empty(['No prices at this cutoff','No interval of this range had been published by the knowledge cutoff.']);return;}
  get('delivery-price-empty').hidden=true;get('delivery-price-shown').hidden=false;
  chart=renderTimeChart(get('delivery-price-chart'),{title:publicText(source.name),unit:publicText(result.priceUnit),zone,points:deliveryChartPoints(rows),overlays:drawn});
  renderRegularTable(get('delivery-price-results'),'delivery-prices',JSON.stringify([source.id,range.fromDate,range.throughDate,cutoff]),['Delivery start','Delivery end',publicText(result.priceUnit),'Known at'],
   rows.map((r,i)=>[starts[i],ends[i],r.formattedPrice,options.instant(r.knownAt)]),[2],options.busy);
 }
 get<HTMLFormElement>('delivery-price-form').onsubmit=event=>{event.preventDefault();void options.act(read);};
 // The overlay menu is drawn once for each catalog, so what the person entered stays while they browse delivery prices.
 function drawMenu(next:OverlayCatalog){
  if(menu===next)return;menu=next;requests=[];overlaySummary();
  // Applying folds the menu away, so the chart it changes is in view, and reads the range on screen again.
  overlayMenu(get('delivery-price-overlay-menu'),next,chosen=>{requests=chosen;overlays.open=false;overlaySummary();void options.act(read);});
 }
 function forget(){
  catalog=undefined;clearShown();get('delivery-price-attempts').replaceChildren();get('delivery-price-jobs').textContent='';get('delivery-price-known').textContent='';
 }
 return {
  sync(){known.refresh();firstDate.sync();lastDate.sync();for(const name of filterNames)select(name).disabled=options.busy()||select(name).options.length<2;},
  reset(){known.clear();forget();menu=undefined;requests=[];get('delivery-price-overlay-menu').replaceChildren();overlays.open=false;overlaySummary();
   for(const name of filterNames)select(name).replaceChildren();filters();tabs.mark('prices');show('prices');},
  async load(){
   from.value='';through.value='';chosenBy='opening';forget();filters();const started=generation;
   const [next,overlayCatalog]=await Promise.all([options.api<Catalog>('power-imports'),options.overlayCatalog()]);
   if(generation!==started)return;
   const opening=deliveryOpening(next.sources,next.opening);
   catalog=next;known.set(catalog.knownAt);drawMenu(overlayCatalog);
   filters(opening?.source);if(opening){from.value=opening.fromDate;through.value=opening.throughDate;syncDates();}
   get('delivery-price-known').textContent='Knowledge cutoff · '+options.instant(catalog.knownAt);
   get('delivery-price-jobs').textContent=catalog.jobs.length?catalog.jobs.map(j=>publicText(j.name)+': '+(j.schedule_enabled?'scheduled':'paused')).join(' · '):'No collection jobs have been configured.';
   renderRegularTable(get('delivery-price-attempts'),'delivery-price-attempts',catalog.knownAt,['Job','Operator','Delivery date from','Delivery date to','Started','Finished','Outcome','Details'],catalog.attempts.map(a=>[publicText(a.jobName),publicText(a.provider),a.fromDate,a.throughDate,options.instant(a.startedAt),a.finishedAt?options.instant(a.finishedAt):'In progress',a.state,publicText(a.errorCode??'')]),[],options.busy);
   clearShown();
   if(opening)await read();
  },
 };
}
