import {observationScheduleInput,type ObservationInspection,type ObservationOptions,type ObservationRow,type ObservationSchedule,type SamplingChoice} from './swap-observations.js';
import {displayInstant,publicText} from './market-view.js';
import {renderRegularTable} from './regular-table.js';

export function renderSwapObservationInspection(host:HTMLElement,plan:ObservationInspection,paymentDate:string,
 key:string,zone:string,units:{price:string;cash:string;quantity:string},busy:()=>boolean){
 if(plan.observations.some(row=>typeof row.isGuard!=='boolean'))throw Error('The observation schedule is missing its backend guard classification.');
 const details=document.createElement('details');details.className='swap-observation-plan';
 const summary=document.createElement('summary');summary.textContent=publicText(`Observation schedule · ${plan.deliveryFrom} to ${plan.deliveryTo} · payment ${paymentDate}`);
 const description=document.createElement('p');description.className='field-help';description.textContent=publicText(`${plan.averaging} · Denominator: ${plan.denominator} · Mapping: ${plan.mappingRule}${plan.calendar?` · Calendar: ${plan.calendar.code}`:''}. Values below come from the evaluated side schedule.`);
 const observations=document.createElement('div'),applications=document.createElement('div');observations.className=applications.className='table-wrap';observations.dataset.observationRows='';applications.dataset.applicationRows='';
 const observationTitle=document.createElement('h4'),applicationTitle=document.createElement('h4');observationTitle.textContent='Price observations';applicationTitle.textContent='Applications to delivery days';
 const publication=(value:string|null)=>value?displayInstant(value,zone):'Not applicable';
 renderRegularTable(observations,key+'-observations',JSON.stringify(plan),['Ordinal','Observation start','Observation end · exclusive','Role',`Averaging quantity · ${units.quantity}`,'Weight',`Price · ${units.price}`,`Cash contribution · ${units.cash}`,'State',`Expected publication · ${zone}`],
  plan.observations.map(row=>[String(row.ordinal),row.from,row.to,row.isGuard?'Outside delivery · guard':'Delivery observation',row.averagingQuantity?.amount??'',row.weight,row.price,row.cashContribution,row.fixingState,publication(row.expectedPublication)]),[0,4,5,6,7],busy);
 renderRegularTable(applications,key+'-applications',JSON.stringify(plan),['Payment ordinal','Delivery start','Delivery end · exclusive','Observation ordinal','Weight','Mapping rule',`Price · ${units.price}`,`Cash contribution · ${units.cash}`,'State',`Expected publication · ${zone}`],
  plan.applications.map(row=>[String(row.paymentOrdinal),row.targetFrom,row.targetTo,String(row.observationOrdinal),row.weight,row.mappingRule,row.price,row.cashContribution,row.fixingState,publication(row.expectedPublication)]),[0,3,4,6,7],busy);
 const evidence=document.createElement('details'),evidenceLabel=document.createElement('summary'),payload=document.createElement('pre');evidenceLabel.textContent='Exact schedule, price lookups and observation lineage';payload.className='swap-evidence';payload.dataset.observationEvidence='';payload.textContent=JSON.stringify(plan,null,2);evidence.append(evidenceLabel,payload);
 details.append(summary,description,observationTitle,observations,applicationTitle,applications,evidence);host.append(details);
}

export function setupSwapObservationControls(options:{prefix:string;enabled:()=>boolean;api:<T>(path:string,body:unknown)=>Promise<T>;request:()=>unknown;
 choice:()=>SamplingChoice|undefined;unit:()=>string;busy:()=>boolean;apply:(value:ObservationOptions)=>void}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const id=(suffix:string)=>`${options.prefix}-observation-${suffix}`;
 const calendar=get<HTMLSelectElement>(id('calendar')),weights=get(id('quantities')),status=get(id('status'));
 let current:ObservationOptions|undefined,requestKey='',generation=0,pending=false;
 let inputs:HTMLInputElement[]=[],rows:ObservationRow[]|null=null,retained:ObservationSchedule|undefined,lastMode='';
 const rowKey=(row:ObservationRow)=>JSON.stringify([row.from,row.to,row.quantitySpecification]);
 function sync(){
  const choice=options.choice(),enabled=options.enabled(),mode=choice?JSON.stringify([choice.resetFrequency,choice.averagingMethod]):'';
  if(mode!==lastMode){if(!choice?.requiresObservationCalendar)calendar.value='';if(!choice?.requiresQuantityWeights){inputs=[];rows=null;weights.replaceChildren();}lastMode=mode;}
  get(id('calendar-field')).hidden=!choice?.requiresObservationCalendar;
  calendar.required=enabled&&!!choice?.requiresObservationCalendar;calendar.disabled=!enabled||options.busy()||pending||!choice?.requiresObservationCalendar;
  weights.hidden=!choice?.requiresQuantityWeights;
  for(const input of inputs){input.required=enabled&&!!choice?.requiresQuantityWeights;input.disabled=!enabled||options.busy()||pending||!choice?.requiresQuantityWeights;}
 }
 function reset(){generation++;current=undefined;requestKey='';pending=false;inputs=[];rows=null;retained=undefined;calendar.replaceChildren(new Option('Choose…',''));weights.replaceChildren();status.textContent='';sync();}
 function restore(value:ObservationSchedule|undefined){retained=value?structuredClone(value):undefined;}
 async function refresh(){
  sync();
  const started=++generation,request=options.request(),scope=JSON.stringify(request);
  const baseline=new Map((rows??[]).map((row,index)=>[rowKey(row),inputs[index]?.value??'']));
  const selected=retained?.calendar?JSON.stringify(retained.calendar):calendar.value;
  pending=true;status.textContent='Checking observation compatibility…';sync();
  try{
   const result=await options.api<ObservationOptions>('swaps/observation-options',request);
   if(started!==generation||scope!==JSON.stringify(options.request()))return;
   if(!Array.isArray(result.indices)||!Array.isArray(result.samplings)||result.samplings.some(choice=>typeof choice.requiresObservationCalendar!=='boolean'||typeof choice.requiresQuantityWeights!=='boolean')||!Array.isArray(result.observationCalendars)||!Array.isArray(result.choices)||
    result.observations!==null&&!Array.isArray(result.observations))throw Error('The observation choices are incomplete. Reload the editor.');
   current=result;requestKey=scope;options.apply(result);
   calendar.replaceChildren(new Option('Choose…',''),...result.observationCalendars.map(choice=>new Option(publicText(`${choice.name} · ${choice.from} to ${choice.to}`),JSON.stringify(choice.reference))));
   calendar.value=[...calendar.options].some(option=>option.value===selected)?selected:'';
   const previous=new Map((rows??[]).map((row,index)=>[rowKey(row),inputs[index]?.value??'']));
   rows=result.observations;inputs=[];weights.replaceChildren();
   if(options.choice()?.requiresQuantityWeights&&rows){
    const label=document.createElement('p');label.className='field-help';label.textContent=`Enter a positive ${options.unit()} quantity for every backend-planned interval. The backend determines the resulting averaging weights.`;weights.append(label);
    const table=document.createElement('table'),head=table.createTHead().insertRow();
    for(const name of ['Observation start','Observation end · exclusive',`Quantity · ${options.unit()}`]){const th=document.createElement('th');th.scope='col';th.textContent=name;head.append(th);}
    const body=table.createTBody();
    for(const row of rows){const tr=body.insertRow();tr.insertCell().textContent=row.from;tr.insertCell().textContent=row.to;
     const input=document.createElement('input');input.type='text';input.inputMode='decimal';input.autocomplete='off';input.dataset.from=row.from;input.dataset.to=row.to;
     input.setAttribute('aria-label',`Positive quantity · ${row.from} to ${row.to} · ${options.unit()}`);
     const key=rowKey(row),edited=previous.has(key)&&previous.get(key)!==baseline.get(key);
     input.value=edited?previous.get(key)!:retained?.quantities?.find(value=>rowKey({...value,quantitySpecification:value.quantity.specification})===key)?.quantity.value??previous.get(key)??'';
     tr.insertCell().append(input);inputs.push(input);
    }
    const scroll=document.createElement('div');scroll.className='regular-table-scroll';scroll.append(table);weights.append(scroll);
   }
   retained=undefined;
   status.textContent=result.availability?.message??result.customProjectionUnavailableReason??'Observation choices and intervals use the selected market context.';
  }catch(error){if(started===generation){current=undefined;requestKey='';rows=null;inputs=[];weights.replaceChildren();status.textContent='Observation choices could not be verified. Reload or change the selection.';}throw error;}
  finally{if(started===generation){pending=false;sync();}}
 }
 return{reset,restore,refresh,sync,
  calendar:()=>options.choice()?.requiresObservationCalendar?(retained?.calendar??(calendar.value?JSON.parse(calendar.value):undefined)):undefined,
  read(){const choice=options.choice();if(!choice)throw Error('Choose an explicit observation sampling rule.');
   if(pending||!current||requestKey!==JSON.stringify(options.request()))throw Error('Refresh the compatible observation plan before pricing.');
   if(current.availability)throw Error(current.availability.message);
   if(!current.samplings.some(mode=>mode.resetFrequency===choice.resetFrequency&&mode.averagingMethod===choice.averagingMethod))throw Error('Choose an available observation convention for the selected index and period.');
   if(!current.observations)throw Error('Resolve the complete observation plan before pricing.');
   return observationScheduleInput(choice,calendar.value?JSON.parse(calendar.value):null,rows,inputs.map(input=>input.value));
  },
 };
}
