import type {setupFacilityQuantities} from './facility-quantities-view.js';
import {renderRegularTable} from './regular-table.js';
import {displayInstant} from './market-view.js';

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
function stage(s:Stage,count:number){
 const coverage=s.reportedQuantity===null?(s.withdrawn===count?'Withdrawn':'Awaiting'):s.quantity??s.reportedQuantity+' ('+s.reported+'/'+count+' reported)';
 const note=[s.provisional?s.provisional+' provisional':'',s.withdrawn&&s.withdrawn!==count?s.withdrawn+' withdrawn':''].filter(Boolean);
 return [coverage,...note].join(' · ');
}
function fact(f:Fact){return f?f.status==='withdrawn'?'Withdrawn':f.quantity+' · '+f.status:'Awaiting';}
const pipelineKey=(p:Pick<Path,'pipeline'|'referenceCatalogId'|'referenceRevisionId'>)=>JSON.stringify([p.referenceCatalogId,p.referenceRevisionId,p.pipeline]);

export function setupPipelineOutlook(root:HTMLElement,o:Options,open:(section:'transport'|'gas-delivery')=>void){
 let generation=0,from='',to='',level='day',selected='',entitlementId='',capFrom='',capTo='',contextDay='';
 const reset=()=>{generation++;from=to=selected=entitlementId=capFrom=capTo=contextDay='';level='day';root.replaceChildren();};
 async function load(){
  const g=++generation,context=o.context(),identity=o.identity();
  const current=()=>g===generation&&identity===o.identity()&&JSON.stringify(context)===JSON.stringify(o.context());
  if(contextDay!==context.effectiveOn){from=monthStart(context.effectiveOn);to=monthStart(context.effectiveOn,1);contextDay=context.effectiveOn;}
  const bar=el('form'),tables=el('section'),status=el('p');bar.className='schedule-fields';status.setAttribute('role','status');
  const first=el('input'),end=el('input'),period=el('select'),pipeline=el('select'),entitlement=el('select');first.type=end.type='date';first.required=end.required=true;
  first.id='outlook-from';end.id='outlook-to';period.id='outlook-period';first.value=from;end.value=to;period.add(new Option('Gas day','day'));period.add(new Option('Month','month'));period.value=level;
  field(bar,'From gas day',first);field(bar,'To gas day (exclusive)',end);field(bar,'Show by',period);field(bar,'Pipeline',pipeline);field(bar,'Compare a transport entitlement',entitlement);entitlement.setAttribute('aria-label','Transport entitlement');
  const capacityWindow=el('div'),capacityFirst=el('input'),capacityEnd=el('input');capacityWindow.className='schedule-fields';capacityWindow.hidden=!entitlementId;
  capacityFirst.type=capacityEnd.type='date';capacityFirst.value=capFrom;capacityEnd.value=capTo;
  field(capacityWindow,'Capacity from (UTC)',capacityFirst);field(capacityWindow,'Capacity to (UTC, exclusive)',capacityEnd);bar.append(capacityWindow);
  const read=el('button','Show usage');read.type='submit';read.id='outlook-read';bar.append(read);
  function range(offset:number,count:number,by:string){first.value=monthStart(context.effectiveOn,offset);end.value=monthStart(context.effectiveOn,offset+count);period.value=by;bar.requestSubmit();}
  const ranges=el('div');ranges.className='schedule-actions';ranges.append(button('Current month by day',()=>range(0,1,'day')),button('Next three months',()=>range(1,3,'month')));
  const nav=el('div');nav.className='schedule-actions';nav.append(button('Agreement capacity by elapsed hour',()=>open('transport')),button('Route plans and nominations',()=>open('gas-delivery')));
  root.replaceChildren(el('h2','Pipeline outlook'),bar,ranges,status,tables,nav);let request=0;
  async function report(){
   const serial=++request;from=first.value;to=end.value;level=period.value;capFrom=capacityFirst.value;capTo=capacityEnd.value;tables.replaceChildren();status.textContent='Reading pipeline usage…';
   const result=await o.api<Report>('gas/pipeline-outlook',{context,from,to,period:level,entitlementId:entitlementId||null,capacityFrom:entitlementId?capFrom:null,capacityTo:entitlementId?capTo:null});
   if(!current()||serial!==request)return;
   entitlement.replaceChildren(new Option('Choose an agreement',''));for(const e of result.entitlements)entitlement.add(new Option(e.label,e.id));
   entitlement.value=entitlementId;entitlement.onchange=()=>{entitlementId=entitlement.value;capacityWindow.hidden=!entitlementId;
    if(entitlementId){capacityFirst.value=capFrom||first.value;capacityEnd.value=capTo||end.value;}bar.requestSubmit();};
   const pipelines=new Map<string,string>();for(const p of [...result.rows.map(r=>r.path),...result.balances])pipelines.set(pipelineKey(p),p.pipeline);
   pipeline.replaceChildren(new Option('All visible pipelines',''));
   for(const [id,name]of pipelines){const duplicate=[...pipelines.values()].filter(n=>n===name).length>1;pipeline.add(new Option(duplicate?name+' · '+JSON.parse(id)[1]:name,id));}
   if(pipelines.has(selected))pipeline.value=selected;else selected='';
   const table=(name:string,headers:string[],rows:(string|HTMLElement)[][])=>{
    tables.append(el('h3',name));const target=el('div');tables.append(target);renderRegularTable(target,'outlook-'+name,JSON.stringify([g,serial,selected,from,to]),headers,rows,[],o.busy);
   };
   function draw(){
    selected=pipeline.value;const match=(p:Pick<Path,'pipeline'|'referenceCatalogId'|'referenceRevisionId'>)=>!selected||pipelineKey(p)===selected;
    const rows=result.rows.filter(r=>match(r.path));tables.replaceChildren();
    status.textContent='Business time '+displayInstant(result.at,o.zone())+' · Known at '+displayInstant(result.context.knownAt,o.zone());
    const gross=result.pipelines.filter(r=>match(r.path));
    if(gross.length)table('Gross pipeline volumes',['Period','Pipeline','Product','Side','Unit','Contracted','Nominated','Submitted','Confirmed','Scheduled','Allocated'],
      gross.map(r=>[level==='month'?r.period.slice(0,7):r.period,r.path.pipeline,r.path.product,r.path.direction==='receive'?'Receipts':'Deliveries',r.path.unit,r.contracted,stage(r.nominated,r.occurrences),stage(r.submitted,r.occurrences),stage(r.confirmed,r.occurrences),stage(r.scheduled,r.occurrences),stage(r.allocated,r.occurrences)]));
    if(!rows.length)tables.append(el('p','No booked gas Sides in this window.'));
    else table('Contract and operating quantities',['Period','Pipeline','Zone','Point','Product','Side','Unit','Contracted','Nominated','Submitted','Confirmed','Scheduled','Allocated','Submitted less scheduled'],
      rows.map(r=>{const p=r.path;return [level==='month'?button(r.period.slice(0,7),()=>{first.value=from>r.period?from:r.period;const after=monthStart(r.period,1);end.value=to<after?to:after;period.value='day';bar.requestSubmit();}):r.period,
       p.pipeline,p.zone,p.point,p.product,p.direction==='receive'?'Receipt':'Delivery',p.unit,r.contracted,stage(r.nominated,r.occurrences),stage(r.submitted,r.occurrences),stage(r.confirmed,r.occurrences),stage(r.scheduled,r.occurrences),stage(r.allocated,r.occurrences),r.submittedLessScheduled??'Awaiting complete evidence'];}));
    const sources=result.sources.filter(s=>match(s.path));
    if(sources.length)table('Booked Side detail',['Gas day','Trade','Side','Point','Native start','Native end','Unit','Contracted','Nominated','Submitted','Confirmed','Scheduled','Allocated'],sources.map(s=>[
     s.day,button(s.reference,()=>void o.act(async()=>{const reason=o.tradeInspection();if(reason)throw Error(reason);await o.openTrade(s.tradeId,s.revision);})),s.side,s.path.point,displayInstant(s.start,o.zone()),displayInstant(s.end,o.zone()),s.path.unit,s.contracted,fact(s.nominated),s.submitted?s.submitted.quantity+' · '+s.requests!.map(r=>r.externalReference+' · '+displayInstant(r.submittedAt,o.zone())).join(', '):'Awaiting',fact(s.confirmed),fact(s.scheduled),fact(s.allocated)]));
    if(result.capacity){
     const cap=result.capacity;tables.append(el('h3','Selected transport entitlement'),el('p',cap.ready?'Commercial rates in '+cap.unit+' per elapsed hour. Intervals overlap the selected UTC date window.':'This entitlement has a future-effective revision. Capacity assessment is unavailable at this business time.'));
     for(const t of cap.tables){if(!t.rows.length){tables.append(el('p','No capacity intervals in this window.'));continue;}
      table('Native capacity intervals',t.columns,t.rows.map(row=>row.map((v,i)=>t.instantColumns.includes(i)?displayInstant(v,o.zone()):v)));}
    }
    const balances=result.balances.filter(match);
    if(!balances.length)tables.append(el('p','No eligible service accounts or statements in this window.'));
    else table('Service account balances',['Gas day','Pipeline','Service account','Unit','Statement and accepted movements'],balances.map(b=>[b.day,b.pipeline,b.account,b.unit,b.amount??b.reason??'Awaiting statement']));
   }
   pipeline.onchange=draw;draw();
  }
  bar.onsubmit=e=>{e.preventDefault();void o.act(report);};await report();
 }
 return {load,reset,sync:()=>{},refresh:()=>{},edited:()=>false,unconfirmed:()=>false,leave:()=>true};
}
