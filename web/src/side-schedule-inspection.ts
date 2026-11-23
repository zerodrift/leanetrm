import type {DeliveryQuantityEvidence} from './ticket-types.js';
import {retainedKnowledge} from './read-references.js';
import {displayPaymentDate} from './payment-date.js';
import {numberText,sourceLabels} from './schedule-grid.js';
import {lifecycleLines,quantityBasisText,words as text} from './lifecycle-text.js';
import {clearTabs,setupTabs} from './side-panel.js';
type Api=<T>(path:string,body:unknown,signal?:AbortSignal)=>Promise<T>;
export type SchedulePosition={context:Record<string,unknown>;tradeId:string;bindingId:string;revision:number;instrumentType:string};
type Lifecycle={delivery:string;due:string|null;cash:string|null;economicClass:string|null;coverage:string|null;unavailableReason:string|null;fulfillment:string|null};
export type InspectedScheduleRow={key:string;kind:string;deliveryFrom:string|null;deliveryTo:string|null;start:string|null;end:string|null;valueAt:string|null;paymentAt:string|null;paymentTiming?:'instant'|'utc_payment_day'|null;
 quantityAdjustment?:{contract:string;ratio:string};quantityEvidence?:DeliveryQuantityEvidence[];quantity:string|null;price:string|null;amount:string|null;discountFactor:string|null;presentValue:string|null;quantityState:string|null;priceStates:string[];lifecycle:Lifecycle;paid:string|null;outstanding:string|null;accrued?:string|null;paidAt?:string|null;finalDetermination?:string|null;quantitySpecification:{code:string}|null;currency:{code:string}|null;priceUnit?:{currency:unknown;quantity:unknown;multiplier:string}|null;priceUnitLabel?:string|null;currencyLabel?:string|null;quantityLabel?:string|null;quoteMultiplier:string|null;operatingDate:string|null};
type Step={unit?:unknown;currency?:unknown;quoteMultiplier?:string|null;valueUnitLabel?:string|null;key:string;role:string;operation:string;kind:string;value:string|null;presence:string;deliveryFrom:string|null;deliveryTo:string|null;start:string|null;end:string|null;observationFrom:string|null;observationTo:string|null;valueAt:string|null;
 paymentDate:string|null;contractFrom:string|null;contractTo:string|null;fixingAt:string|null;priceStates:string[];inputs:{port:string;key:string}[];bindings:{kind:string;market?:{curve:{code:string}}|null;code?:string|null}[];lineage:{id:string}[];actualBatch?:string|null;actualRevision?:number|null;actualKnownAt?:string|null;parameters?:Record<string,string>};
type Page={side:string;manifest:string;valuationAt:string;knownAt:string;offset:number;total:number;rows:InspectedScheduleRow[]};
type Calculation={side:string;manifest:string;contribution:string;valueBasis:string;tradeQuantityScale:string;membershipNumerator:string;membershipDenominator:string;offset:number;total:number;rows:Step[]};
type DeliveryWindow={id:string;label:string};
type Side={slot:number;sideId:string;family:string;manifest:string;windows:DeliveryWindow[]};
// A delivery window's rows in the table, or every row of a Side that has no windows: how many are shown, of how many.
type Part={window:DeliveryWindow|null;body:HTMLTableSectionElement;loaded:number;total:number};
type Identity={tradeId:string;bindingId:string;revision:number;contextId:string;snapshotId:string};
type Directory=Identity&{sides:Side[]};
type Reply<T>=Identity&{sideSlot:number;sideId:string;manifest:string;window?:string|null;page:T};
const invalidates=(error:unknown)=>['access_denied','position_unavailable','position_facts','side_schedule_missing'].includes((error as {code?:string})?.code??'');
const number=(value:string|null)=>value===null?'—':numberText(value);
const element=<K extends keyof HTMLElementTagNameMap>(tag:K,value?:string)=>{const node=document.createElement(tag);if(value!==undefined)node.textContent=value;return node;};
function lines(primary:string,details:string[]){
 const box=element('div');box.className='inspection-value';box.append(element('span',primary));
 details.filter(Boolean).forEach(detail=>box.append(element('small',detail)));return box;
}
function tableRow(values:(string|HTMLElement)[],numeric:number[]){
 const row=element('tr');
 values.forEach((value,n)=>{const cell=element('td');if(numeric.includes(n))cell.className='numeric';cell.append(value);row.append(cell);});
 return row;
}
function headRow(headers:string[]){const head=element('thead'),titles=element('tr');headers.forEach(title=>titles.append(element('th',title)));head.append(titles);return head;}
function table(host:HTMLElement,headers:string[],rows:(string|HTMLElement)[][],numeric:number[]){
 const grid=element('table'),body=element('tbody');body.append(...rows.map(values=>tableRow(values,numeric)));
 grid.append(headRow(headers),body);host.replaceChildren(grid);
}
const columns=['Delivery from','To · exclusive','Payment date / time','Quantity','Price','DF','Amount','PV','Lifecycle',''];
// A window opens with its first rows; the rest of it, and the calculation inputs, are read in further pages the API bounds at 128.
const pageRows=32,furtherRows=128;
let mounted=0;
// One read-only inspector for native Side values. Its rows show as soon as it is mounted: a tab for each Side, and under it one table
// of every delivery window in order, each under its own heading. It pins the directory's certified context, reads a window when the
// rows before it are reached, and requests calculation inputs only on demand.
export function mountSideScheduleInspection(host:HTMLElement,o:{api:Api;position:SchedulePosition;instant:(at:string)=>string;onDenied:()=>void;side?:{slot:number};
 /** The Side to open on where the position has it, else its first; and what is told each Side shown. */
 start?:number;chosen?:(slot:number)=>void;
 quantityEntry?:{days:ReadonlyMap<string,{value:string|null;sources:string[]}>;disabled:boolean;enter:(day:string,value:string)=>void}}){
 const root=element('section'),sides=element('div'),status=element('p'),rows=element('div'),sheet=element('div'),grid=element('table'),more=element('button'),evidence=element('section'),serial=++mounted;
 root.className='side-schedule-inspection';
 sides.id=`inspection-sides-${serial}`;sides.className='inspection-sides';sides.hidden=true;rows.id=`inspection-rows-${serial}`;rows.className='inspection-rows table-wrap';
 status.setAttribute('role','status');evidence.className='inspection-evidence';more.type='button';more.className='secondary inspection-more';more.hidden=grid.hidden=true;
 sheet.className='inspection-sheet';grid.append(headRow(columns));sheet.append(grid,more);rows.append(sheet);root.append(sides,status,rows,evidence);host.append(root);
 let directory:Directory|undefined,side:Side|undefined,parts:Part[]=[],context={...o.position.context};
 // One read of rows and one of calculation inputs at a time; a read that is no longer the current one is dropped when it answers.
 let reading:AbortController|undefined,inspecting:AbortController|undefined;
 // Why the last read failed, which stops reading on until the person asks again; and the valuation the rows shown were read under.
 let problem:string|undefined,stamp:Pick<Page,'valuationAt'|'knownAt'>|undefined;
 const refusals=new Set<string>();
 const instant=(at:string|null)=>at===null?'—':o.instant(at);
 const identity=(reply:Identity)=>{
  if(reply.tradeId!==o.position.tradeId||reply.bindingId!==o.position.bindingId||reply.revision!==o.position.revision||reply.snapshotId!==o.position.context.snapshotId||
   !reply.contextId||directory&&reply.contextId!==directory.contextId)throw Error('The schedule response belongs to another position or financial context.');
 };
 const read=<T>(slot:number|null,period:DeliveryWindow|null,contribution:string|null,offset:number,limit:number,signal:AbortSignal)=>o.api<T>('trading/side-schedules',{
  position:{...o.position,context},sideSlot:slot,contribution,offset,limit,window:period?.id??null},signal);
 const message=(error:unknown,fallback:string)=>error instanceof Error?error.message:fallback;
 // What reading on brings: the next delivery window, or more of a Side whose rows are one run.
 function following():{part?:Part;window:DeliveryWindow|null}|undefined{
  if(!side)return undefined;
  if(parts.length<Math.max(1,side.windows.length))return {window:side.windows[parts.length]??null};
  const last=parts[parts.length-1];
  return side.windows.length<2&&last.loaded<last.total?{part:last,window:last.window}:undefined;
 }
 const rowsText=(part:Part)=>part.total===0?'No rows':part.loaded<part.total?`${part.loaded} of ${part.total} rows`:`${part.total} rows`;
 function sync(){
  const next=following(),busy=reading!==undefined;
  grid.hidden=!parts.length;if(busy)rows.setAttribute('aria-busy','true');else rows.removeAttribute('aria-busy');
  // A window's heading stays in view under the column titles, tucked beneath them so no row shows between the two.
  if(parts.length)rows.style.setProperty('--inspection-head',`${Math.floor(grid.tHead!.rows[0].cells[0].getBoundingClientRect().height)}px`);
  more.hidden=directory?!next:problem===undefined;more.disabled=busy;
  more.textContent=!directory?'Retry':next?.part?'Show more rows':next?.window?`Show ${next.window.label}`:'Show rows';
  for(const button of grid.querySelectorAll<HTMLButtonElement>('.inspection-window-more button'))button.disabled=busy;
  if(problem!==undefined)status.textContent=problem;
  else if(!directory)status.textContent='Loading Side schedules…';
  else if(!side||!stamp)status.textContent='Reading the selected Side…';
  else status.textContent=`${side.windows.length>1?`${parts.length} of ${side.windows.length} delivery windows`:rowsText(parts[0])} · Valued ${instant(stamp.valuationAt)} · Known ${instant(stamp.knownAt)}. Values include the signed membership and booked quantity.${refusals.size?' Valuation unavailable: '+[...refusals].join('; '):''}`;
 }
 // Takes down the rows and inputs shown, and drops the reads under way.
 function reset(){
  reading?.abort();inspecting?.abort();reading=inspecting=undefined;
  for(const part of parts)part.body.remove();parts=[];stamp=undefined;problem=undefined;refusals.clear();
  evidence.replaceChildren();evidence.removeAttribute('aria-busy');evidence.inert=false;evidence.style.removeProperty('min-height');
 }
 function fail(error:unknown){
  if(!invalidates(error)){problem=message(error,'The Side schedule could not be read.');sync();return;}
  reset();problem=message(error,'The Side schedule could not be read.');directory=undefined;side=undefined;clearTabs(sides,rows);sides.hidden=true;sync();more.hidden=true;o.onDenied();
 }
 async function load(){
  const mine=reading=new AbortController();problem=undefined;sync();
  try{
   const value=await read<Directory>(null,null,null,0,pageRows,mine.signal);if(reading!==mine)return;
   identity(value);if(value.sides.some(s=>!Array.isArray(s.windows)||s.windows.some(w=>!w.id||!w.label)))throw Error('The Side directory has invalid delivery windows.');
   const first=o.side?value.sides.find(s=>s.slot===o.side!.slot):value.sides.find(s=>s.slot===o.start)??value.sides[0];
   if(!first)throw Error(o.side?'The selected native Side membership is unavailable.':'This position has no Side schedules.');
   directory=value;context={...context,...retainedKnowledge(value)};reading=undefined;
   if(!o.side){
    setupTabs(sides,value.sides.map(s=>[String(s.slot),`${text(s.family).replace(/^./,c=>c.toUpperCase())} · Side ${s.slot+1}`] as const),{label:'Sides',panel:rows,selected:String(first.slot),
     select:id=>{if(String(side?.slot)!==id||problem!==undefined)show(value.sides.find(s=>String(s.slot)===id)!);}});
    sides.hidden=false;
   }
   show(first);
  }catch(error){if(reading===mine){reading=undefined;fail(error);}}
 }
 function show(next:Side){reset();side=next;rows.scrollTop=0;o.chosen?.(next.slot);void fill(following()!);}
 function paymentCell(row:InspectedScheduleRow):string|HTMLElement{
  if(row.paymentTiming!=null&&!['instant','utc_payment_day'].includes(row.paymentTiming)||row.paymentAt===null&&row.paymentTiming!=null)
   throw Error('Reread the Side row with its explicit supported payment timing.');
  if(row.paymentAt===null)return '—';
  if(row.paymentTiming==='instant'){const shown=element('span',instant(row.paymentAt));shown.title=row.paymentAt;return shown;}
  return displayPaymentDate(row.paymentAt);
 }
 function cells(row:InspectedScheduleRow,period:DeliveryWindow|null){
  const inspect=element('button','Calculation');inspect.type='button';inspect.className='inspection-calculation';inspect.disabled=row.lifecycle.unavailableReason!==null;
  inspect.onclick=()=>void calculation(row,period,0);
  const state=row.lifecycle,currency=row.currencyLabel??row.currency?.code??'';
  const quantity=lines(number(row.quantity),[row.quantityLabel??row.quantitySpecification?.code??'',quantityBasisText(row.quantityState),...(row.quantityAdjustment?[`Contract: ${number(row.quantityAdjustment.contract)} · selected ratio: ${number(row.quantityAdjustment.ratio)}`]:[]),...(row.quantityEvidence??[]).map(e=>`${e.reference} · ${e.revisionId}`)]);
  const day=row.operatingDate,recorded=day?o.quantityEntry?.days.get(day):undefined;
  if(day&&recorded&&o.quantityEntry){
   const input=element('input');input.className='ticket-cell-input';input.inputMode='decimal';input.placeholder=number(recorded.value);input.disabled=o.quantityEntry.disabled;
   input.setAttribute('aria-label',`Record quantity · ${day}`);input.onchange=()=>o.quantityEntry!.enter(day,input.value);
   quantity.append(element('small',`Operational view: ${number(recorded.value)} · ${recorded.sources.map(s=>sourceLabels[s]??s).join(', ')}`),input);
  }
  return [lines(row.start===null?row.deliveryFrom??'—':instant(row.start),[text(row.kind),row.operatingDate?`Operating day: ${row.operatingDate}`:'']),
   row.end===null?row.deliveryTo??'—':instant(row.end),paymentCell(row),
   quantity,
   lines(number(row.price),[row.priceUnitLabel??'',row.priceStates.length?'Inputs: '+row.priceStates.map(text).join(', '):'',row.quoteMultiplier?`Quote multiplier: ${row.quoteMultiplier}`:'']),
   number(row.discountFactor),lines(number(row.amount),[currency]),lines(number(row.presentValue),[currency]),lines(text(state.delivery),lifecycleLines(row,instant)),inspect];
 }
 // A window's heading, and under the rows of a window that has more, what reads them.
 function frame(part:Part){
  if(!part.window)return;
  for(const row of part.body.querySelectorAll('.inspection-window-heading,.inspection-window-more'))row.remove();
  const heading=element('tr'),title=element('th'),label=element('span',part.window.label);label.append(element('small',rowsText(part)));
  heading.className='inspection-window-heading';title.colSpan=columns.length;title.scope='rowgroup';title.append(label);heading.append(title);part.body.prepend(heading);
  if(side!.windows.length<2||part.loaded>=part.total)return;
  const tail=element('tr'),cell=element('td'),rest=element('button',`Show ${Math.min(furtherRows,part.total-part.loaded)} more rows of ${part.window.label}`);
  tail.className='inspection-window-more';cell.colSpan=columns.length;rest.type='button';rest.className='secondary';rest.onclick=()=>void fill({part,window:part.window});cell.append(rest);tail.append(cell);part.body.append(tail);
 }
 async function fill(next:{part?:Part;window:DeliveryWindow|null}){
  const at=side;if(!at||reading)return;
  const mine=reading=new AbortController(),offset=next.part?.loaded??0;problem=undefined;sync();
  try{
   const value=await read<Reply<Page>>(at.slot,next.window,null,offset,next.part?furtherRows:pageRows,mine.signal);if(reading!==mine)return;
   identity(value);const page=value.page;
   if(value.sideSlot!==at.slot||value.sideId!==at.sideId||value.manifest!==at.manifest||page.manifest!==at.manifest||(value.window??null)!==(next.window?.id??null)||
    page.offset!==offset||offset+page.rows.length>page.total||!page.rows.length&&offset<page.total)throw Error('The response does not match this retained Side.');
   let part=next.part;
   if(!part){part={window:next.window,body:element('tbody'),loaded:0,total:0};if(next.window)part.body.dataset.window=next.window.id;parts.push(part);grid.append(part.body);}
   part.loaded+=page.rows.length;part.total=page.total;stamp=page;
   for(const row of page.rows){if(row.lifecycle.unavailableReason)refusals.add(text(row.lifecycle.unavailableReason));part.body.append(tableRow(cells(row,part.window),[3,4,5,6,7]));}
   frame(part);
  }catch(error){if(reading===mine){reading=undefined;fail(error);}}
  finally{if(reading===mine){reading=undefined;sync();reached.unobserve(more);reached.observe(more);}}
 }
 async function calculation(row:InspectedScheduleRow,period:DeliveryWindow|null,offset:number){
  const at=side;if(!at)return;inspecting?.abort();
  const mine=inspecting=new AbortController(),previousSteps=evidence.querySelector<HTMLElement>('.inspection-steps'),left=previousSteps?.scrollLeft??0,top=previousSteps?.scrollTop??0;
  if(previousSteps)evidence.style.minHeight=evidence.clientHeight+'px';else evidence.replaceChildren(element('p','Reading calculation inputs…'));
  evidence.setAttribute('aria-busy','true');evidence.inert=true;
  try{
   const value=await read<Reply<Calculation>>(at.slot,period,row.key,offset,pageRows,mine.signal);if(inspecting!==mine)return;identity(value);const detail=value.page;
   if(value.sideSlot!==at.slot||value.sideId!==at.sideId||value.manifest!==at.manifest||detail.manifest!==at.manifest||detail.contribution!==row.key||detail.valueBasis!=='unit_side_v1'||(value.window??null)!==(period?.id??null))throw Error('The calculation response belongs to another contribution or value basis.');
   const caption=element('p',`Unit Side calculation · booked quantity scale ${number(detail.tradeQuantityScale)} · signed membership ${detail.membershipNumerator}/${detail.membershipDenominator}. The schedule above shows the resulting holding values. ${row.quantityEvidence!==undefined?"It also applies the selected delivery quantities and accrual shown above; the retained unit calculation keeps the contract quantity.":""}`);
   const steps=element('div');steps.className='inspection-steps table-wrap';
   table(steps,['Step','Operation','Type','Value','Delivery from','To','Observation from','To','Value at','Contract from','To','Fixing at','Price state','Source','Inputs','Parameters'],detail.rows.map(step=>[
    step.key,text(step.operation).replace(/^./,c=>c.toUpperCase()),text(step.kind),lines(number(step.value),[step.valueUnitLabel??'']),step.start===null?step.deliveryFrom??'—':instant(step.start),step.end===null?step.deliveryTo??'—':instant(step.end),step.observationFrom??'—',step.observationTo??'—',step.paymentDate?displayPaymentDate(step.paymentDate):instant(step.valueAt),
    step.contractFrom??'—',step.contractTo??'—',instant(step.fixingAt),step.priceStates.map(text).join(', '),step.bindings.map(b=>b.market?.curve.code??b.code??text(b.kind)).join(', ')+(step.actualBatch?` · Actual batch ${step.actualBatch}, revision ${step.actualRevision??'—'}, known ${instant(step.actualKnownAt??null)}`:''),step.inputs.map(i=>`${i.port}: ${i.key}`).join(', '),Object.entries(step.parameters??{}).map(([key,value])=>`${text(key)}: ${value}`).join(', ')]),[3]);
   const before=element('button','Previous inputs'),after=element('button','Next inputs');before.type=after.type='button';before.disabled=offset===0;after.disabled=offset+detail.rows.length>=detail.total;
   before.onclick=()=>void calculation(row,period,Math.max(0,offset-pageRows));after.onclick=()=>void calculation(row,period,offset+pageRows);
   const buttons=element('div');buttons.className='inspection-pager';buttons.append(before,element('span',`${offset+1}–${offset+detail.rows.length} of ${detail.total} calculation cells`),after);evidence.replaceChildren(caption,steps,buttons);steps.scrollLeft=left;steps.scrollTop=top;
  }catch(error){if(inspecting!==mine)return;if(invalidates(error))fail(error);else evidence.replaceChildren(element('p',message(error,'The calculation inputs could not be read.')));}
  finally{if(inspecting===mine){inspecting=undefined;evidence.removeAttribute('aria-busy');evidence.inert=false;}}
 }
 // Reads on when the end of the rows shown is reached inside their box, wherever the box sits on the page, unless the last read
 // failed: then only when asked.
 const reached=new IntersectionObserver(entries=>{const next=following();if(entries.some(e=>e.isIntersecting)&&next&&problem===undefined)void fill(next);},{root:rows});
 reached.observe(more);
 more.onclick=()=>{const next=following();if(!directory)void load();else if(next)void fill(next);};
 void load();
 return()=>{reset();side=undefined;reached.disconnect();root.remove();};
}
