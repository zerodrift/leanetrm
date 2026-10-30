import {mountPriceSettlement,type PriceSettlement} from './price-settlement.js';
import {explainUnavailableValue,markUnavailableValue,unavailableValueLabel} from './saved-trade-terms.js';
import type {Index} from './swap-editor.js';
import {setupSwapObservationControls} from './swap-observations-view.js';
import {mountObservationControls} from './swap-side-editor.js';
import {projectionReference,type CustomProjection} from './swap-projections.js';
import type {ObservationOptions,ObservationSchedule,SamplingChoice} from './swap-observations.js';

export type ScheduledFloatingTerms={settlement:PriceSettlement;curve:string;index:string;currency:string;deliveryFrom:string;deliveryTo:string;resetFrequency:string;averagingMethod:string;spread:string;observationSchedule?:ObservationSchedule|null;customProjection?:CustomProjection|null};
export function mountScheduledFloatingEditor(options:{host:HTMLElement;from:string;to:string;initial?:ScheduledFloatingTerms|null;indices:()=>Index[];
 context:()=>Record<string,unknown>;enabled:()=>boolean;busy:()=>boolean;api:<T>(path:string,body:unknown)=>Promise<T>;run:(work:()=>Promise<void>)=>void;changed:()=>void;quoteChanged:()=>void}){
 const prefix='scheduled-observations-'+crypto.randomUUID();
 const select=(label:string,id?:string)=>{const wrap=document.createElement('div'),caption=document.createElement('label'),field=document.createElement('select');field.id=id??prefix+'-'+crypto.randomUUID();caption.htmlFor=field.id;caption.textContent=label;wrap.append(caption,field);options.host.append(wrap);return field;};
 const key=(i:Pick<Index,'definitionRevisionId'|'curve'|'index'>)=>JSON.stringify([i.definitionRevisionId,i.curve,i.index]);
 const reset=select('Price observation frequency'),average=select('Price averaging',prefix+'-averaging');
 const index=select('Index and projection curve');index.dataset.role='period-index';
 const source=select('Projected price source');source.dataset.role='period-price-source';
 const spreadWrap=document.createElement('div'),spreadLabel=document.createElement('label'),spread=document.createElement('input');spread.id=prefix+'-spread';spread.inputMode='decimal';spreadLabel.htmlFor=spread.id;spreadLabel.textContent='Agreed floating spread';spreadWrap.append(spreadLabel,spread);options.host.append(spreadWrap);
 mountObservationControls(options.host,prefix);
 const note=document.createElement('p');note.className='field-help';options.host.append(note);
 const initial=options.initial;
 const unsupported=!!initial?.observationSchedule&&initial.observationSchedule.implementation!=='side_observation_schedule_v1';
 let qualified:ObservationOptions|undefined;
 let selectedRequirements:SamplingChoice|null|undefined;
 const samplings=()=>qualified?.samplings??(quote()?.samplings??options.indices().flatMap(i=>i.samplings));
 const quote=()=>options.indices().find(i=>key(i)===index.value);
 const settlement=mountPriceSettlement(options.host,prefix,{enabled:()=>options.enabled()&&!unsupported,indices:()=>quote()?.settlementIndices??[],changed:()=>change(()=>{sources();})});
 // Requirements remain visible for an unavailable entered mode; admission uses the qualified response.
 const choice=():SamplingChoice|undefined=>[...(selectedRequirements?[selectedRequirements]:[]),...(qualified?.samplings??[]),...(quote()?.samplings??options.indices().flatMap(i=>i.samplings))].find(s=>s.resetFrequency===reset.value&&s.averagingMethod===average.value);
 const projections=()=>settlement.override()?[]:quote()?.customProjections.filter(p=>p.samplings.some(s=>s.resetFrequency===reset.value&&s.averagingMethod===average.value))??[];
 function isBookedValue(field:HTMLSelectElement,value:string){
  if(!initial)return false;
  if(field===index){const saved=options.indices().find(i=>i.curve===initial.curve&&i.index===initial.index);return !!saved&&key(saved)===value;}
  if(field===reset)return value===initial.resetFrequency;
  if(field===average)return value===initial.averagingMethod;
  return value===(initial.customProjection?JSON.stringify(initial.customProjection):'ordinary');
 }
 const fill=(field:HTMLSelectElement,rows:{value:string;label:string}[],selected:string)=>{
  field.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(r.label,r.value)));
  if(selected&&!rows.some(r=>r.value===selected)){
   const booked=isBookedValue(field,selected),unavailable=new Option(unavailableValueLabel(selected,booked),selected);
   markUnavailableValue(unavailable,booked);field.append(unavailable);
  }
  field.value=selected;explainUnavailableValue(field);
 };
 function sources(selected=source.value){fill(source,[{value:'ordinary',label:'Selected index curve'},...projections().map(p=>({value:JSON.stringify(p.reference),label:p.label}))],selected);}
 function averaging(selected=average.value){fill(average,[...new Map(samplings().filter(s=>reset.value&&s.resetFrequency===reset.value).map(s=>[s.averagingMethod,{value:s.averagingMethod,label:s.label}])).values()],selected);sources();}
 function frequencies(selected=reset.value){fill(reset,[...new Set(samplings().map(s=>s.resetFrequency))].map(value=>({value,label:value})),selected);averaging();}
 function indices(selected=index.value){fill(index,options.indices().filter(i=>(i.settlementIndices?.length>0&&(!settlement.selected()||key(i)!==index.value))||(qualified?qualified.indices.some(candidate=>key(candidate)===key(i)):i.samplings.some(s=>(!reset.value||s.resetFrequency===reset.value)&&(!average.value||s.averagingMethod===average.value)))).map(i=>({value:key(i),label:`${i.index} · ${i.curve} · ${i.currency}/${i.unit} × ${i.quoteMultiplier}`})),selected);}
 const observations=setupSwapObservationControls({prefix,enabled:()=>options.enabled()&&options.host.isConnected,api:options.api,busy:options.busy,choice,unit:()=>quote()?.unit??'',
  request:()=>{const i=quote(),unambiguous=(values:string[])=>{const distinct=[...new Set(values)];return distinct.length===1?distinct[0]:undefined;},custom=projections().find(p=>JSON.stringify(p.reference)===source.value);return{context:options.context(),definitionRevisionId:i?.definitionRevisionId??unambiguous(options.indices().map(i=>i.definitionRevisionId)),curve:i?.curve,index:i?.index,settlement:settlement.selected(),commodity:i?.commodity??unambiguous(options.indices().map(i=>i.commodity)),
   resetFrequency:reset.value||undefined,averagingMethod:average.value||undefined,deliveryFrom:options.from,deliveryTo:options.to,observationCalendar:observations.calendar(),...(custom?{customProjection:custom.reference}:{})};},
  apply:result=>{selectedRequirements=result.selectedSampling;qualified=result;indices();frequencies();note.textContent=result.availability?.message??result.customProjectionUnavailableReason??'Observation dates and weights use the selected exact backend plan.';}});
 async function refresh(){if(unsupported||!options.indices().length||!options.host.isConnected)return;await observations.refresh();}
 const change=(work:()=>void)=>{options.changed();qualified=undefined;work();indices();frequencies();for(const field of [index,reset,average,source])explainUnavailableValue(field);options.quoteChanged();options.run(refresh);};
 index.onchange=()=>change(()=>{settlement.reset();observations.reset();spread.value='';});
 reset.onchange=()=>change(()=>{observations.reset();});
 average.onchange=()=>change(()=>{observations.reset();sources();});
 source.onchange=()=>change(()=>{});
 options.host.querySelector<HTMLSelectElement>('#'+prefix+'-observation-calendar')!.onchange=()=>change(()=>{});
 indices();if(initial){const saved=options.indices().find(i=>i.curve===initial.curve&&i.index===initial.index);if(saved){index.value=key(saved);indices();}}
 settlement.restore(initial?.settlement??null);frequencies(initial?.resetFrequency??'');averaging(initial?.averagingMethod??'');sources(initial?initial.customProjection?JSON.stringify(initial.customProjection):'ordinary':'');spread.value=initial?.spread??'';observations.restore(initial?.observationSchedule??undefined);
 return{refresh,spread:()=>spread.value,sync:()=>{observations.sync();settlement.sync();if(unsupported)for(const field of [index,reset,average,source])field.disabled=true;},setSpread:(value:string)=>{spread.value=value;},dispose:()=>observations.reset(),
  read():ScheduledFloatingTerms{if(unsupported)throw Error('These retained observation terms require explicit period replacement; they cannot be simplified by this editor.');const i=quote();if(!i||!choice()||!qualified?.indices.some(candidate=>key(candidate)===key(i)))throw Error('Choose a qualified index, reset frequency and averaging convention for every floating period.');
   const customProjection=projectionReference(source.value,projections()),observationSchedule=observations.read();
   return{curve:i.curve,index:i.index,settlement:settlement.read(),currency:i.currencyCode,deliveryFrom:options.from,deliveryTo:options.to,resetFrequency:reset.value,averagingMethod:average.value,spread:spread.value,customProjection,observationSchedule:observationSchedule??null};},
  async apply(value:ScheduledFloatingTerms){if(unsupported)throw Error('Replace unsupported retained terms explicitly before applying common observation terms.');const i=options.indices().find(i=>i.curve===value.curve&&i.index===value.index);if(!i)throw Error('The selected common price source is unavailable for this period.');qualified=undefined;indices(key(i));settlement.restore(value.settlement);frequencies(value.resetFrequency);averaging(value.averagingMethod);sources(value.customProjection?JSON.stringify(value.customProjection):'ordinary');observations.restore(value.observationSchedule??undefined);await refresh();}
 };
}
