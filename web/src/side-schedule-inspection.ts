import {numberText,sourceLabels} from './schedule-grid.js';
type Api=<T>(path:string,body:unknown,signal?:AbortSignal)=>Promise<T>;
export type SchedulePosition={context:Record<string,unknown>;tradeId:string;bindingId:string;revision:number;instrumentType:string};
type Lifecycle={delivery:string;due:string|null;cash:string|null;economicClass:string|null;coverage:string|null;unavailableReason:string|null;fulfillment:string|null};
export type InspectedScheduleRow={key:string;kind:string;deliveryFrom:string|null;deliveryTo:string|null;start:string|null;end:string|null;valueAt:string|null;paymentAt:string|null;
 quantity:string|null;price:string|null;amount:string|null;discountFactor:string|null;presentValue:string|null;quantityState:string|null;priceStates:string[];lifecycle:Lifecycle;paid:string|null;outstanding:string|null;quantitySpecification:{code:string}|null;currency:{code:string}|null;quoteMultiplier:string|null;operatingDate:string|null};
type Step={key:string;role:string;operation:string;kind:string;value:string|null;presence:string;deliveryFrom:string|null;deliveryTo:string|null;start:string|null;end:string|null;observationFrom:string|null;observationTo:string|null;valueAt:string|null;
 contractFrom:string|null;contractTo:string|null;fixingAt:string|null;priceStates:string[];inputs:{port:string;key:string}[];bindings:{kind:string;market?:{curve:{code:string}}|null;code?:string|null}[];lineage:{id:string}[];actualBatch?:string|null;actualRevision?:number|null;actualKnownAt?:string|null;parameters?:Record<string,string>};
type Page={side:string;manifest:string;valuationAt:string;knownAt:string;offset:number;total:number;rows:InspectedScheduleRow[]};
type Calculation={side:string;manifest:string;contribution:string;valueBasis:string;tradeQuantityScale:string;membershipNumerator:string;membershipDenominator:string;offset:number;total:number;rows:Step[]};
type Side={slot:number;sideId:string;family:string;manifest:string;windows:{id:string;label:string}[]};
type Identity={tradeId:string;bindingId:string;revision:number;contextId:string;snapshotId:string};
type Directory=Identity&{sides:Side[]};
type Reply<T>=Identity&{sideSlot:number;sideId:string;manifest:string;window?:string|null;page:T};
const text=(value:string|null|undefined)=>{if(!value)return '—';const words=value.replace(/_/g,' ').replace(/([a-z])([A-Z])/g,'$1 $2');return words[0]+words.slice(1).toLowerCase();};
const invalidates=(error:unknown)=>['access_denied','position_unavailable','position_facts','side_schedule_missing'].includes((error as {code?:string})?.code??'');
const number=(value:string|null)=>value===null?'—':numberText(value);
const element=<K extends keyof HTMLElementTagNameMap>(tag:K,value?:string)=>{const node=document.createElement(tag);if(value!==undefined)node.textContent=value;return node;};
function lines(primary:string,details:string[]){
 const box=element('div');box.className='inspection-value';box.append(element('span',primary));
 details.filter(Boolean).forEach(detail=>box.append(element('small',detail)));return box;
}
function table(host:HTMLElement,headers:string[],rows:(string|HTMLElement)[][],numeric:number[]){
 const grid=element('table'),head=element('thead'),titles=element('tr'),body=element('tbody');
 headers.forEach(title=>titles.append(element('th',title)));head.append(titles);
 for(const values of rows){const row=element('tr');values.forEach((value,n)=>{const cell=element('td');if(numeric.includes(n))cell.className='numeric';cell.append(typeof value==='string'?document.createTextNode(value):value);row.append(cell);});body.append(row);}
 grid.append(head,body);host.replaceChildren(grid);
}
// One read-only inspector for native Side values. It pins the first directory's
// certified context, pages rows, and requests calculation inputs only on demand.
export function mountSideScheduleInspection(host:HTMLElement,o:{api:Api;position:SchedulePosition;instant:(at:string)=>string;onDenied:()=>void;side?:{slot:number};
 quantityEntry?:{days:ReadonlyMap<string,{value:string|null;sources:string[]}>;disabled:boolean;enter:(day:string,value:string)=>void}}){
 const root=element('details'),summary=element('summary','Pricing & lifecycle details'),controls=element('div'),select=element('select'),windowSelect=element('select'),status=element('p'),rows=element('div'),pager=element('div'),evidence=element('section');
 root.className='side-schedule-inspection';if(o.side){root.classList.add('inspection-inline');summary.hidden=true;root.open=true;}controls.className='inspection-controls';select.className='inspection-side';status.setAttribute('role','status');rows.className='inspection-rows table-wrap';evidence.className='inspection-evidence';
 const previous=element('button','Previous'),next=element('button','Next');previous.type=next.type='button';pager.className='inspection-pager';pager.append(previous,next);
 const label=element('label','Side ');label.append(select);const windowLabel=element('label','Delivery window ');windowSelect.className='inspection-window';windowLabel.append(windowSelect);windowLabel.hidden=true;controls.append(label,windowLabel);label.hidden=!!o.side;controls.hidden=pager.hidden=true;
 root.append(summary,controls,status,rows,pager,evidence);host.append(root);
 let directoryLoading=false;
 let alive=true,epoch=0,controller:AbortController|undefined,directory:Directory|undefined,page:Page|undefined,context={...o.position.context};
 const instant=(at:string|null)=>at===null?'—':o.instant(at);
 const current=(at:number)=>alive&&at===epoch;
 const begin=()=>{controller?.abort();controller=new AbortController();return {at:++epoch,signal:controller.signal};};
 const selected=()=>directory?.sides.find(s=>String(s.slot)===select.value);
 const identity=(reply:Identity)=>{
  if(reply.tradeId!==o.position.tradeId||reply.bindingId!==o.position.bindingId||reply.revision!==o.position.revision||reply.snapshotId!==o.position.context.snapshotId||
   !reply.contextId||directory&&reply.contextId!==directory.contextId)throw Error('The schedule response belongs to another position or financial context.');
 };
 const read=<T>(slot:number|null,contribution:string|null,offset:number,signal:AbortSignal)=>o.api<T>('trading/side-schedules',{
  position:{...o.position,context},sideSlot:slot,contribution,offset,limit:32,window:slot===null?null:windowSelect.value||null},signal);
 function fail(error:unknown,at:number){
  if(!current(at))return;
  rows.replaceChildren();evidence.replaceChildren();pager.hidden=true;page=undefined;
  status.textContent=error instanceof Error?error.message:'The Side schedule could not be read.';
  if(invalidates(error)){
   begin();directory=undefined;select.replaceChildren();controls.hidden=true;root.ontoggle=null;o.onDenied();
  }
 }
 async function load(){
  if(directory||directoryLoading)return;directoryLoading=true;const {at,signal}=begin();status.textContent='Loading Side schedules…';
  try{
   const value=await read<Directory>(null,null,0,signal);if(!current(at))return;identity(value);if(value.sides.some(s=>!Array.isArray(s.windows)||s.windows.some(w=>!w.id||!w.label)))throw Error('The Side directory has invalid delivery windows.');directory=value;
   context={...context,knowledgeMode:'retained',contextId:value.contextId};
   select.replaceChildren(new Option('Choose a Side…',''),...value.sides.map(s=>new Option(`${text(s.family)} · Side ${s.slot+1}`,String(s.slot))));
   controls.hidden=!!o.side;status.textContent='Choose a Side to inspect its evaluated rows. Dates do not establish fulfillment or payment.';
   if(o.side){if(!value.sides.some(s=>s.slot===o.side!.slot))throw Error('The selected native Side membership is unavailable.');select.value=String(o.side.slot);sideChanged();}
   else if(value.sides.length===1){select.value=String(value.sides[0].slot);sideChanged();}
  }catch(error){fail(error,at);}finally{directoryLoading=false;}
 }
 async function show(offset:number){
  const side=selected();if(!side||side.windows.length&&!windowSelect.value){begin();rows.replaceChildren();evidence.replaceChildren();pager.hidden=true;status.textContent='Choose a delivery window to inspect.';return;}
  const {at,signal}=begin();rows.replaceChildren();evidence.replaceChildren();pager.hidden=true;status.textContent='Reading the selected Side…';
  try{
   const value=await read<Reply<Page>>(side.slot,null,offset,signal);if(!current(at))return;identity(value);
   if(value.sideSlot!==side.slot||value.sideId!==side.sideId||value.manifest!==side.manifest||value.page.manifest!==side.manifest||(value.window??null)!==(windowSelect.value||null))throw Error('The response does not match this retained Side.');
   page=value.page;
   table(rows,['Delivery from','To · exclusive','Payment at','Quantity','Price','DF','Amount','PV','Lifecycle',''],
    page.rows.map(row=>{
     const inspect=element('button','Calculation');inspect.type='button';inspect.className='inspection-calculation';inspect.disabled=row.lifecycle.unavailableReason!==null;
     inspect.onclick=()=>void calculation(row,0);
     const state=row.lifecycle,currency=row.currency?.code??'';
     const quantity=lines(number(row.quantity),[row.quantitySpecification?.code??'',text(row.quantityState)]);
     const day=row.operatingDate,recorded=day?o.quantityEntry?.days.get(day):undefined;
     if(day&&recorded&&o.quantityEntry){
      const input=element('input');input.className='ticket-cell-input';input.inputMode='decimal';input.placeholder=number(recorded.value);input.disabled=o.quantityEntry.disabled;
      input.setAttribute('aria-label',`Record quantity · ${day}`);input.onchange=()=>o.quantityEntry!.enter(day,input.value);
      quantity.append(element('small',`Operational view: ${number(recorded.value)} · ${recorded.sources.map(s=>sourceLabels[s]??s).join(', ')}`),input);
     }
     const lifecycle=[state.due&&`Payment: ${text(state.due)}`,state.cash&&`Cash: ${text(state.cash)}`,
      row.paid!==null&&`Paid: ${number(row.paid)}`,row.outstanding!==null&&`Outstanding: ${number(row.outstanding)}`,
      state.fulfillment&&`Fulfillment: ${text(state.fulfillment)}`,state.economicClass&&text(state.economicClass),state.coverage&&text(state.coverage)].filter((v):v is string=>!!v);
     return [lines(row.start===null?row.deliveryFrom??'—':instant(row.start),[text(row.kind),row.operatingDate?`Operating day: ${row.operatingDate}`:'']),
      row.end===null?row.deliveryTo??'—':instant(row.end),instant(row.paymentAt),
      quantity,
      lines(number(row.price),[row.priceStates.length?'Inputs: '+row.priceStates.map(text).join(', '):'',row.quoteMultiplier?`Quote multiplier: ${row.quoteMultiplier}`:'']),
      number(row.discountFactor),lines(number(row.amount),[currency]),lines(number(row.presentValue),[currency]),lines(text(state.delivery),lifecycle),inspect];
    }),[3,4,5,6,7]);
   const refusals=[...new Set(page.rows.flatMap(r=>r.lifecycle.unavailableReason?[text(r.lifecycle.unavailableReason)]:[]))];
   status.textContent=`${page.rows.length?`${offset+1}–${offset+page.rows.length}`:"No"} of ${page.total} rows · Valued ${instant(page.valuationAt)} · Known ${instant(page.knownAt)}. Values include the signed membership and booked quantity.${refusals.length?' Valuation unavailable: '+refusals.join('; '):''}`;
   previous.disabled=offset===0;next.disabled=offset+page.rows.length>=page.total;pager.hidden=false;
  }catch(error){fail(error,at);}
 }
 async function calculation(row:InspectedScheduleRow,offset:number){
  const side=selected();if(!side)return;const {at,signal}=begin();evidence.replaceChildren(element('p','Reading calculation inputs…'));
  try{
   const value=await read<Reply<Calculation>>(side.slot,row.key,offset,signal);if(!current(at))return;identity(value);const detail=value.page;
   if(value.sideSlot!==side.slot||value.sideId!==side.sideId||value.manifest!==side.manifest||detail.manifest!==side.manifest||detail.contribution!==row.key||detail.valueBasis!=='unit_side_v1'||(value.window??null)!==(windowSelect.value||null))throw Error('The calculation response belongs to another contribution or value basis.');
   const caption=element('p',`Unit Side calculation · booked quantity scale ${number(detail.tradeQuantityScale)} · signed membership ${detail.membershipNumerator}/${detail.membershipDenominator}. The schedule above shows the resulting holding values.`);
   const steps=element('div');steps.className='inspection-steps table-wrap';
   table(steps,['Step','Operation','Type','Value','Delivery from','To','Observation from','To','Value at','Contract from','To','Fixing at','Price state','Source','Inputs','Parameters'],detail.rows.map(step=>[
    step.key,text(step.operation).replace(/^./,c=>c.toUpperCase()),text(step.kind),number(step.value),step.start===null?step.deliveryFrom??'—':instant(step.start),step.end===null?step.deliveryTo??'—':instant(step.end),step.observationFrom??'—',step.observationTo??'—',instant(step.valueAt),
    step.contractFrom??'—',step.contractTo??'—',instant(step.fixingAt),step.priceStates.map(text).join(', '),step.bindings.map(b=>b.market?.curve.code??b.code??text(b.kind)).join(', ')+(step.actualBatch?` · Actual batch ${step.actualBatch}, revision ${step.actualRevision??'—'}, known ${instant(step.actualKnownAt??null)}`:''),step.inputs.map(i=>`${i.port}: ${i.key}`).join(', '),Object.entries(step.parameters??{}).map(([key,value])=>`${text(key)}: ${value}`).join(', ')]),[3]);
   const before=element('button','Previous inputs'),after=element('button','Next inputs');before.type=after.type='button';before.disabled=offset===0;after.disabled=offset+detail.rows.length>=detail.total;
   before.onclick=()=>void calculation(row,Math.max(0,offset-32));after.onclick=()=>void calculation(row,offset+32);
   const buttons=element('div');buttons.className='inspection-pager';buttons.append(before,element('span',`${offset+1}–${offset+detail.rows.length} of ${detail.total} calculation cells`),after);evidence.replaceChildren(caption,steps,buttons);
  }catch(error){if(invalidates(error))fail(error,at);else if(current(at))evidence.replaceChildren(element('p',error instanceof Error?error.message:'The calculation inputs could not be read.'));}
 }
 function sideChanged(){
  const side=selected(),windows=side?.windows??[];windowSelect.replaceChildren(new Option('Choose a window…',''),...windows.map(w=>new Option(w.label,w.id)));
  if(windows.length===1)windowSelect.value=windows[0].id;windowLabel.hidden=windows.length===0;controls.hidden=!!o.side&&windows.length===0;void show(0);
 }
 root.ontoggle=()=>{if(root.open)void load();};if(o.side)void load();select.onchange=sideChanged;windowSelect.onchange=()=>void show(0);previous.onclick=()=>void show(Math.max(0,(page?.offset??0)-32));next.onclick=()=>void show((page?.offset??0)+32);
 return()=>{alive=false;epoch++;controller?.abort();root.remove();};
}
