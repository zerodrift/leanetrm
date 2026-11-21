import type {setupFacilityQuantities} from './facility-quantities-view.js';
import {setupTabs} from './side-panel.js';
import {renderRegularTable} from './regular-table.js';
import {displayExactInstant} from './market-view.js';

type Path={pipeline:string;zone:string;point:string;product:string;unit:string;quantityCode:string;direction:string;referenceCatalogId:string;referenceRevisionId:string};
type Stage={reported:number;provisional:number;withdrawn:number;quantity:string|null;reportedQuantity:string|null};
type Row={path:Path;period:string;occurrences:number;contracted:string;nominated:Stage;submitted:Stage;confirmed:Stage;scheduled:Stage;allocated:Stage;submittedLessScheduled:string|null};
type Fact={quantity:string|null;status:string}|null;
type Source={path:Path;day:string;start:string;end:string;reference:string;side:string;tradeId:string;revision:number;contracted:string;nominated:Fact;submitted:Fact;requests:{externalReference:string;submittedAt:string}[]|null;confirmed:Fact;scheduled:Fact;allocated:Fact};
type Balance={pipeline:string;account:string;unit:string;day:string;amount:string|null;reason:string|null;referenceRevisionId:string;referenceCatalogId:string};
type Report={context:{knownAt:string};at:string;pipelines:Row[];rows:Row[];sources:Source[];balances:Balance[];entitlements:{id:string;label:string}[];capacity:{ready:boolean;unit:string;tables:{columns:string[];rows:string[][];instantColumns:number[]}[]}|null};
type Options=Parameters<typeof setupFacilityQuantities>[0];
function el<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){const n=document.createElement(tag);if(text)n.textContent=text;return n;}
function button(label:string,action:()=>void){const b=el('button',label);b.type='button';b.className='secondary';b.onclick=action;return b;}
function field(root:HTMLElement,name:string,control:HTMLElement){const label=el('label',name);label.append(control);root.append(label);}
function monthStart(day:string,offset=0){const d=new Date(day.slice(0,7)+'-01T00:00:00Z');d.setUTCMonth(d.getUTCMonth()+offset);return d.toISOString().slice(0,10);}
function valueCell(value:string,detail?:string,tone?:string){
 const cell=el('span');cell.className='outlook-amount';if(tone)cell.dataset.tone=tone;
 cell.append(el('span',value));if(detail)cell.append(document.createTextNode(' '),el('small',detail));return cell;
}
function stage(s:Stage,count:number){
 const notes=[];
 if(s.reportedQuantity!==null&&s.quantity===null)notes.push(s.reported+'/'+count+' reported');
 if(s.provisional)notes.push(s.provisional+' provisional');
 if(s.withdrawn&&s.withdrawn!==count)notes.push(s.withdrawn+' withdrawn');
 const quantity=s.quantity??s.reportedQuantity;
 return valueCell(quantity??(s.withdrawn===count?'Withdrawn':'Awaiting'),notes.join(' · '),quantity===null?'muted':undefined);
}
function fact(f:Fact){return f&&f.quantity!==null?valueCell(f.quantity,f.status):valueCell(f?.status==='withdrawn'?'Withdrawn':'Awaiting',undefined,'muted');}
const pipelineKey=(p:Pick<Path,'pipeline'|'referenceCatalogId'|'referenceRevisionId'>)=>JSON.stringify([p.referenceCatalogId,p.referenceRevisionId,p.pipeline]);
const views=[['volumes','Volumes'],['sources','Booked Sides'],['capacity','Capacity'],['balances','Balances']] as const;
type Lens=typeof views[number][0];

export function setupPipelineOutlook(root:HTMLElement,o:Options,open:(section:'transport'|'gas-delivery')=>void){
 let generation=0,from='',to='',level='day',selected='',entitlementId='',capFrom='',capTo='',contextDay='',scope='',lens:Lens='volumes';
 const reset=()=>{generation++;from=to=selected=entitlementId=capFrom=capTo=contextDay=scope='';level='day';lens='volumes';root.replaceChildren();};
 async function load(){
  const g=++generation,context=o.context(),identity=o.identity();
  const selectionScope=JSON.stringify([identity,context]);
  if(scope!==selectionScope){selected=entitlementId=capFrom=capTo='';scope=selectionScope;}
  const current=()=>g===generation&&selectionScope===JSON.stringify([o.identity(),o.context()]);
  if(contextDay!==context.effectiveOn){from=monthStart(context.effectiveOn);to=monthStart(context.effectiveOn,1);contextDay=context.effectiveOn;}
  root.classList.add('pipeline-outlook');
  const head=el('header'),heading=el('h2','Pipeline outlook'),ranges=el('div');head.className='outlook-heading';ranges.className='outlook-ranges';
  const bar=el('form'),body=el('div'),tabs=el('div'),tables=el('section'),status=el('p');
  bar.id='outlook-filters';bar.className='outlook-filters';body.id='outlook-panel';tabs.id='outlook-views';tabs.className='gas-view-tabs';tables.className='outlook-report';
  status.className='outlook-asof';status.setAttribute('role','status');
  const first=el('input'),end=el('input'),period=el('select'),pipeline=el('select'),entitlement=el('select');first.type=end.type='date';first.required=end.required=true;
  first.id='outlook-from';end.id='outlook-to';period.id='outlook-period';first.value=from;end.value=to;period.add(new Option('Gas day','day'));period.add(new Option('Month','month'));period.value=level;
  field(bar,'Pipeline',pipeline);field(bar,'From gas day',first);field(bar,'To gas day (exclusive)',end);field(bar,'Group by',period);
  const read=el('button','Show usage');read.type='submit';read.id='outlook-read';bar.append(read);
  function range(offset:number,count:number,by:string){first.value=monthStart(context.effectiveOn,offset);end.value=monthStart(context.effectiveOn,offset+count);period.value=by;bar.requestSubmit();}
  ranges.append(button('Current month',()=>range(0,1,'day')),button('Next three months',()=>range(1,3,'month')));head.append(heading,ranges);
  const capacityControls=el('div'),capacityWindow=el('div'),capacityFirst=el('input'),capacityEnd=el('input');
  capacityControls.className='outlook-capacity-controls';capacityWindow.className='outlook-capacity-window';capacityWindow.hidden=!entitlementId;
  capacityFirst.type=capacityEnd.type='date';capacityFirst.value=capFrom;capacityEnd.value=capTo;
  field(capacityControls,'Transport entitlement',entitlement);entitlement.setAttribute('aria-label','Transport entitlement');
  field(capacityWindow,'Capacity from (UTC)',capacityFirst);field(capacityWindow,'Capacity to (UTC, exclusive)',capacityEnd);
  capacityWindow.append(button('Apply dates',()=>bar.requestSubmit()));capacityControls.append(capacityWindow);
  const panelHead=el('div');panelHead.className='outlook-panel-head';panelHead.append(tabs);
  const nav=el('div');nav.className='outlook-links';nav.append(button('Agreement capacity',()=>open('transport')),button('Route plans and nominations',()=>open('gas-delivery')));
  const filters=el('section');filters.className='outlook-scope';filters.append(head,bar);body.append(capacityControls,tables);
  root.replaceChildren(filters,panelHead,body,status,nav);
  let request=0,result:Report|undefined;
  setupTabs(tabs,views,{label:'Pipeline report',panel:body,selected:lens,select:id=>{lens=id as Lens;draw();}});
  capacityControls.hidden=lens!=='capacity';
  function table(host:HTMLElement,name:string,headers:string[],rows:(string|HTMLElement)[][],numeric:number[]){
   const frame=el('section');frame.className='outlook-table';const target=el('div');frame.append(el('h3',name),target);host.append(frame);
   renderRegularTable(target,'outlook-'+name,JSON.stringify([g,request,selected,from,to,entitlementId]),headers,rows,numeric,o.busy);
  }
  function periodCell(r:Row){
   if(level==='day')return r.period;
   const month=button(r.period.slice(0,7),()=>{first.value=from>r.period?from:r.period;const after=monthStart(r.period,1);end.value=to<after?to:after;period.value='day';bar.requestSubmit();});
   month.className='outlook-drill';month.title='Open gas days in '+r.period.slice(0,7);return month;
  }
  function draw(){
   if(!current())return;
   capacityControls.hidden=lens!=='capacity';tables.replaceChildren();if(!result)return;
   const match=(p:Pick<Path,'pipeline'|'referenceCatalogId'|'referenceRevisionId'>)=>!selected||pipelineKey(p)===selected;
   const rows=result.rows.filter(r=>match(r.path));
   if(lens==='volumes'){
    const gross=result.pipelines.filter(r=>match(r.path));
    if(!gross.length){tables.append(el('p','No booked gas Sides in this window.'));return;}
    table(tables,'Gross pipeline volumes',['Period','Pipeline / product','Flow / unit','Contracted','Nominated','Submitted','Confirmed','Scheduled','Allocated'],
     gross.map(r=>[periodCell(r),valueCell(r.path.pipeline,r.path.product),valueCell(r.path.direction==='receive'?'Receipts':'Deliveries',r.path.unit,r.path.direction),
      r.contracted,stage(r.nominated,r.occurrences),stage(r.submitted,r.occurrences),stage(r.confirmed,r.occurrences),stage(r.scheduled,r.occurrences),stage(r.allocated,r.occurrences)]),[3,4,5,6,7,8]);
    const points=el('details');points.className='outlook-points';points.append(el('summary','Volumes by point'));tables.append(points);
    table(points,'Contract and operating quantities',['Period','Pipeline','Zone','Point','Product','Side','Unit','Contracted','Nominated','Submitted','Confirmed','Scheduled','Allocated','Submitted less scheduled'],
     rows.map(r=>{const p=r.path;return [periodCell(r),p.pipeline,p.zone,p.point,p.product,p.direction==='receive'?'Receipt':'Delivery',p.unit,
      r.contracted,stage(r.nominated,r.occurrences),stage(r.submitted,r.occurrences),stage(r.confirmed,r.occurrences),stage(r.scheduled,r.occurrences),stage(r.allocated,r.occurrences),r.submittedLessScheduled??'Awaiting complete evidence'];}),[7,8,9,10,11,12,13]);
   }else if(lens==='sources'){
    const sources=result.sources.filter(s=>match(s.path));
    if(!sources.length){tables.append(el('p',level==='month'?'Select a month in Volumes or group by gas day to see booked Sides.':'No booked Sides in this window.'));return;}
    table(tables,'Booked Side detail',['Gas day','Trade','Side','Point','Native start','Native end','Unit','Contracted','Nominated','Submitted','Confirmed','Scheduled','Allocated'],sources.map(s=>{
     const trade=button(s.reference,()=>{const reason=o.tradeInspection();if(reason)void o.act(async()=>{throw Error(reason);});else void o.openTrade(s.tradeId,s.revision);});trade.className='outlook-drill';
     return [s.day,trade,s.side,s.path.point,displayExactInstant(s.start,o.zone()),displayExactInstant(s.end,o.zone()),s.path.unit,s.contracted,fact(s.nominated),
      s.submitted?valueCell(s.submitted.quantity!,s.requests!.map(r=>r.externalReference+' · '+displayExactInstant(r.submittedAt,o.zone())).join(', ')):fact(null),fact(s.confirmed),fact(s.scheduled),fact(s.allocated)];}),[7,8,9,10,11,12]);
   }else if(lens==='capacity'){
    const cap=result.capacity;
    if(!cap){tables.append(el('p',result.entitlements.length?'Choose an agreement to see its hourly limits and chosen plans.':'No visible transport entitlements at this business time.'));return;}
    tables.append(el('p',cap.ready?'Commercial capacity in '+cap.unit+' per elapsed hour.':'This entitlement has a future-effective revision. Capacity assessment is unavailable at this business time.'));
    for(const t of cap.tables){if(!t.rows.length){tables.append(el('p','No capacity intervals in this window.'));continue;}
     table(tables,'Native capacity intervals',t.columns,t.rows.map(row=>row.map((v,i)=>t.instantColumns.includes(i)?displayExactInstant(v,o.zone()):v)),t.columns.flatMap((label,i)=>label.endsWith(' / hour')?[i]:[]));}
   }else{
    const balances=result.balances.filter(match);
    if(!balances.length)tables.append(el('p','No eligible service accounts or statements in this window.'));
    else table(tables,'Service account balances',['Gas day','Pipeline','Service account','Unit','Statement and accepted movements'],balances.map(b=>[b.day,b.pipeline,b.account,b.unit,b.amount??b.reason??'Awaiting statement']),[4]);
   }
  }
  async function report(){
   const serial=++request;from=first.value;to=end.value;level=period.value;capFrom=capacityFirst.value;capTo=capacityEnd.value;result=undefined;tables.replaceChildren();status.textContent='Reading pipeline usage…';
   const data=await o.api<Report>('gas/pipeline-outlook',{context,from,to,period:level,entitlementId:entitlementId||null,capacityFrom:entitlementId?capFrom:null,capacityTo:entitlementId?capTo:null});
   if(!current()||serial!==request)return;
   result=data;entitlement.replaceChildren(new Option('Choose an agreement',''));for(const e of data.entitlements)entitlement.add(new Option(e.label,e.id));entitlement.value=entitlementId;
   const pipelines=new Map<string,string>();for(const p of [...data.rows.map(r=>r.path),...data.balances])pipelines.set(pipelineKey(p),p.pipeline);
   pipeline.replaceChildren(new Option('All visible pipelines',''));
   for(const [id,name]of pipelines){const duplicate=[...pipelines.values()].filter(n=>n===name).length>1;pipeline.add(new Option(duplicate?name+' · '+JSON.parse(id)[1]:name,id));}
   if(selected&&!pipelines.has(selected)){const [,revision,name]=JSON.parse(selected);pipeline.add(new Option(name+' · '+revision,selected));}
   pipeline.value=selected;
   status.textContent='Business time '+displayExactInstant(data.at,o.zone())+' · Known at '+displayExactInstant(data.context.knownAt,o.zone());
   draw();
  }
  entitlement.onchange=()=>{entitlementId=entitlement.value;capacityWindow.hidden=!entitlementId;
   if(entitlementId){capacityFirst.value=capFrom||first.value;capacityEnd.value=capTo||end.value;}bar.requestSubmit();};
  pipeline.onchange=()=>{selected=pipeline.value;draw();};
  bar.onsubmit=e=>{e.preventDefault();void o.act(report);};await report();
 }
 return {load,reset,sync:()=>{},refresh:()=>{},edited:()=>false,unconfirmed:()=>false,leave:()=>true};
}
