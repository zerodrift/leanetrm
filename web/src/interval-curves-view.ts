import {mountIntervalDataWidget} from './interval-data-widget.js';
import {renderRegularTable} from './regular-table.js';
import {displayInstant,friendly,timeLabels} from './market-view.js';
import {zonedInput} from './user-time.js';
import {type ChartSpec} from './chart-view.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Block={curve:string;membership:string;touProduct:string|null;touVersion:number|null};
type Curve={code:string;name:string;kind:'marked'|'shaped';quote:string;clock:string;intervalMinutes:number;resolvable?:boolean;method?:string;blocks?:Block[]};
// A curve whose definition cannot be resolved is listed with its reason and cannot be read.
type Unavailable={code:string;kind:'marked'|'shaped';unavailable:string};
type Catalog={knownAt:string;curves:(Curve|Unavailable)[]};
const available=(c:Curve|Unavailable):c is Curve=>!('unavailable' in c);
type Price={numerator:string;denominator:string;display:string};
type Labelled={start:string;end:string;operatingDate:string;hourLabel:number;minute:number;occurrence:number};
type Point={interval:Labelled;price:Price|null;block?:{curve:string}|null;shapeRevision?:number;state?:'actual'|'projected'|'modeled'|'publishedfixing'|'unresolved';dueAt?:string;publication?:string;liveMarketRiskCoverage?:string;
 estimate?:Price|null;actual?:{providerRevision:number|null;knownAt:string}|null};
type Aggregate={start:string;end:string;mean:Price|null;constituents:string[]};
export type IntervalValues={curve:string;kind:'marked'|'shaped';resolved?:boolean;points:Point[];aggregates?:Aggregate[]|null;knownAt?:string;
 shapeSeries?:{code:string;head:number}};

// Columns follow what a read carries: shaped points name their block and shape revision, resolved points their state and due time.
export function intervalTable(values:IntervalValues,zone:string){
 const points=values.points,count=points.length,shaped=values.kind==='shaped',resolved=!!values.resolved;
 const labels=timeLabels([...points.map(p=>p.interval.start),...points.map(p=>p.interval.end)],zone),due=resolved?timeLabels(points.map(p=>p.dueAt!),zone):[];
 return {headers:['Start','End','Price',...(shaped?['Block','Shape revision']:[]),...(resolved?['State','Due']:[])],
  rows:points.map((p,i)=>[labels[i],labels[count+i],p.price?.display??'Unresolved',...(shaped?[p.block?friendly(p.block.curve):'—',p.shapeRevision==null?'—':String(p.shapeRevision)]:[]),
   ...(resolved?[friendly(p.state!),due[i]]:[])]),numeric:shaped?[2,4]:[2]};
}
// Where one point's price came from, in words; observation and batch identities stay internal.
export function describePoint(values:IntervalValues,index:number,label:string,zone:string){
 const p=values.points[index],shown=`${label} · ${p.price?.display??'no price'} · `;
 if(p.state==='unresolved')return shown+`no eligible actual yet; it is due ${displayInstant(p.dueAt!,zone)}`+(p.estimate?`; estimate ${p.estimate.display} from the ${friendly(p.block?.curve)} ${p.publication==='final_settlement_anchor'?'frozen final settlement; live market-risk coverage is missing':'live block anchor'}`:'');
 if(p.state==='actual')return shown+`the collected actual, provider revision ${p.actual!.providerRevision??'none'}, known at ${displayInstant(p.actual!.knownAt,zone,true)}`;
 if(p.publication==='final_settlement_anchor')return shown+`modeled from the ${friendly(p.block?.curve)} frozen final settlement; live market-risk coverage is missing`;
 if(p.state==='publishedfixing')return shown+'the qualified published period fixing';
 if(values.kind==='shaped')return shown+`the ${friendly(p.block!.curve)} block mark, shaped by shape revision ${p.shapeRevision}`;
 return shown+'the pinned mark of this interval'+(p.state==='projected'?`; its actual is due ${displayInstant(p.dueAt!,zone)}`:'');
}
export function aggregateTable(values:IntervalValues,zone:string){
 const aggregates=values.aggregates??[],count=aggregates.length,labels=timeLabels([...aggregates.map(a=>a.start),...aggregates.map(a=>a.end)],zone);
 return {headers:['Start','End','Mean','Intervals'],rows:aggregates.map((a,i)=>[labels[i],labels[count+i],a.mean?.display??'None · an interval is unresolved',String(a.constituents.length)]),numeric:[2,3]};
}
// An aggregate's native intervals, labelled as the values table labels them.
export function describeAggregate(values:IntervalValues,index:number,zone:string){
 const aggregate=values.aggregates![index],[start,end]=aggregateTable(values,zone).rows[index],rows=intervalTable(values,zone).rows;
 const byStart=new Map(values.points.map((p,i)=>[p.interval.start,i]));
 return `${start} to ${end} · ${aggregate.constituents.length} intervals: `+aggregate.constituents.map(s=>{const i=byStart.get(s)!;return `${rows[i][0]} ${values.points[i].price?.display??'unresolved'}`;}).join(', ');
}
export function intervalChart(values:IntervalValues,title:string,unit:string,zone:string):ChartSpec{
 const labels=timeLabels(values.points.map(p=>p.interval.start),zone);
 return {title,axis:'delivery',axisName:'Delivery interval',unit,dense:true,
  series:[{name:title,points:values.points.map((p,i)=>({x:p.interval.start,label:labels[i],y:p.price?.display??null}))}]};
}

export function setupIntervalCurves(o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;frame:()=>string;unit:(quote:string)=>Promise<string>}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement>(id),choice=(id:string)=>get<HTMLSelectElement>(id);
 const from=zonedInput(field('interval-curves-from'),o.zone,o.api),to=zonedInput(field('interval-curves-to'),o.zone,o.api),known=zonedInput(field('interval-curves-known'),o.zone,o.api);
 const widget=mountIntervalDataWidget({table:get('interval-curves-values'),chart:get('interval-curves-chart'),key:'interval-curves-values',busy:o.busy});
 let catalog:Catalog|undefined,shown:{values:IntervalValues;scope:string;title:string;unit:string}|undefined,generation=0;
 const chosen=()=>catalog?.curves.find(c=>c.code===choice('interval-curves-select').value);
 const curve=()=>{const c=chosen();return c&&available(c)?c:undefined;};
 const resolving=()=>!!curve()?.resolvable&&field('interval-curves-resolve').checked;
 // A shaped curve reads its shape, and a resolved one its collected actuals, as known at an explicit time.
 const needsKnownAt=()=>curve()?.kind==='shaped'||resolving();

 function clearResults(){
  shown=undefined;widget.clear();
  for(const id of ['interval-curves-values','interval-curves-aggregates'])get(id).replaceChildren();
  for(const id of ['interval-curves-note','interval-curves-lineage'])get(id).textContent='';
 }
 function describe(){
  const c=chosen(),summary=get('interval-curves-summary');
  if(!c||!available(c)){summary.textContent=c?`Unavailable · ${c.unavailable}`:'';return;}
  const blocks=(c.blocks??[]).map(b=>`${friendly(b.curve)}${b.touProduct?` (${friendly(b.membership)} · ${b.touProduct} v${b.touVersion})`:''}`).join(' and ');
  summary.textContent=[c.kind==='shaped'?`Shaped ${friendly(c.method)} from ${blocks}`:'Directly marked',friendly(c.clock),`${c.intervalMinutes}-minute intervals`].join(' · ');
 }
 function render(){
  if(!shown)return;
  const {values,scope,title,unit}=shown,zone=o.zone(),table=intervalTable(values,zone),count=values.points.length;
  get('interval-curves-note').textContent=[`${count} ${count===1?'interval':'intervals'}`,values.knownAt?`known at ${displayInstant(values.knownAt,zone,true)}`:'',
   values.shapeSeries?`shape ${values.shapeSeries.code} through revision ${values.shapeSeries.head}`:'',
   values.points.some(p=>p.liveMarketRiskCoverage==='frozen_anchor_only')?'Frozen-anchor coverage includes missing live market-risk coverage':''].filter(Boolean).join(' · ');
  const lineage=(text:string)=>{get('interval-curves-lineage').textContent=text;};
  widget.show({scope,title,caption:'Prices from the selected market inputs',headers:table.headers,rows:table.rows,numeric:table.numeric,
   chart:intervalChart(values,title,unit,zone),noColumnFilter:[0,1,2],onRowActivate:i=>lineage(describePoint(values,i,table.rows[i][0],zone))});
  if(values.aggregates?.length){const aggregates=aggregateTable(values,zone);renderRegularTable(get('interval-curves-aggregates'),'interval-curves-aggregates',scope,
   aggregates.headers,aggregates.rows,aggregates.numeric,o.busy,{noColumnFilter:[0,1,2],onRowActivate:i=>lineage(describeAggregate(values,i,zone))});}
  else get('interval-curves-aggregates').replaceChildren();
  sync();
 }
 async function read(){
  const selected=curve();if(!selected)throw Error('Choose a curve.');
  const owner=catalog,started=generation,resolve=resolving(),aggregate=choice('interval-curves-aggregate').value;
  const request={snapshotId:o.frame(),curve:selected.code,from:await from.instant(),to:await to.instant(),
   knownAt:needsKnownAt()?await known.instant():null,resolve,aggregate:resolve&&aggregate?aggregate:null};
  const values=await o.api<IntervalValues>('interval-curves/values',request),unit=await o.unit(selected.quote);
  if(owner!==catalog||started!==generation)return;
  if(values?.curve!==selected.code||!Array.isArray(values.points))throw Error('The curve values are missing or inconsistent. Show them again.');
  shown={values,scope:JSON.stringify(request),title:selected.name,unit};
  render();
 }
 function sync(){
  const busy=o.busy(),c=curve();
  choice('interval-curves-select').disabled=busy||!catalog;
  get<HTMLFieldSetElement>('interval-curves-window').disabled=busy||!c;
  field('interval-curves-resolve').disabled=!c?.resolvable;
  choice('interval-curves-aggregate').disabled=!resolving();
  field('interval-curves-known').disabled=!needsKnownAt();
  if(needsKnownAt())field('interval-curves-known').dataset.contextCoordinate='Knowledge cutoff';else delete field('interval-curves-known').dataset.contextCoordinate;
 }
 function unload(){generation++;catalog=undefined;clearResults();choice('interval-curves-select').replaceChildren();describe();sync();}

 choice('interval-curves-select').onchange=()=>{field('interval-curves-resolve').checked=false;choice('interval-curves-aggregate').value='';clearResults();describe();sync();};
 field('interval-curves-resolve').onchange=()=>{if(!resolving())choice('interval-curves-aggregate').value='';sync();};
 for(const event of ['input','change'])get('interval-curves-form').addEventListener(event,()=>{generation++;clearResults();});
 get<HTMLFormElement>('interval-curves-form').onsubmit=event=>{event.preventDefault();if(o.busy())return;void o.act(async()=>{clearResults();await read();});};

 return{
  assistant:()=>shown?{operation:'interval-curves.values_v1',inputJson:JSON.stringify({body:JSON.parse(shown.scope),parameters:{}})}:undefined,
  sync,
  // A failed read, such as one refused in a historical view, leaves an empty page, never the previous catalog.
  async load(){
   const selected=curve()?.code;unload();
   const started=generation,next=await o.api<Catalog>('interval-curves/catalog',{snapshotId:o.frame()});
   if(started!==generation)return;
   catalog=next;
   const control=choice('interval-curves-select');
   control.replaceChildren(new Option(next.curves.length?'Choose a curve…':'No interval curves in this market',''),...next.curves.map(c=>new Option(available(c)?`${c.name} · ${friendly(c.kind)}`:`${friendly(c.code)} · unavailable`,c.code)));
   control.value=next.curves.some(c=>c.code===selected)?selected!:'';
   known.set(next.knownAt);describe();sync();
  },
  reset(){unload();from.clear();to.clear();known.clear();field('interval-curves-resolve').checked=false;choice('interval-curves-aggregate').value='';},
  refresh(){from.refresh();to.refresh();known.refresh();render();},
 };
}
