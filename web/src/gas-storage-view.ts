import type {Holding} from './desk-model.js';
import {drawPath,drawRatchets,type PathDay,type RatchetBand} from './gas-storage-charts.js';
import {displayInstant,publicText} from './market-view.js';
import type {DetailRead,TermSection} from './position-panel.js';
import {valuationReason,type ValuationReason} from './valuation-status.js';

// A booked gas storage contract's Details: its entitlement and ratchets drawn, the plan in force with its inventory path, and the
// controls that propose a plan from the forward curve and record it. Every figure, limit and refusal is the backend's.
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
const product='gas_storage',convention='gas_storage_trade_v1';
type Terms={unit:string;currency:string;commodity:string;injection_curve_code:string;withdrawal_curve_code:string;minimum_inventory:string;maximum_inventory:string;
 injection_fuel:string;withdrawal_fuel:string;ratchet_basis:string;flow_policy:string;fuel_basis:string;opening_inventory:string;terminal_minimum:string;terminal_maximum:string;
 injection_charge:string;withdrawal_charge:string;monthly_capacity_charge:string;term_from:string;term_to:string;clock_code:string;day_start_minute:number|string;payment_rule:{days:number|string}};
type Band={lower:string;upper:string;maximum_daily_injection:string;maximum_daily_withdrawal:string};
type Path=(PathDay&{maximumDailyInjection:string;maximumDailyWithdrawal:string;injectionFuel:string;withdrawalFuel:string})[];
type Read={position:{tradeId:string;bindingId:string;instrumentType:string;measureLabel:string;unavailableReason:ValuationReason|null};terms:Terms;ratchets:Band[];
 days:{gas_day:string}[];plan:{revision:number|string;source:string;inventory_step:string|null;effective_at:string}|null;path:Path|null;planRefusal:ValuationReason|null};
type Proposal={implementation:string;inventoryStep:string;objective:string;currency:string;path:Path};
/** What the view needs of its host: something to hear that a plan was recorded, so the position is read and valued again, and the
 *  working context a plan is recorded under, which a view reading at retained knowledge does not itself hold: null where the window
 *  has none, which then proposes and records nothing. */
export type StorageHost={recorded:()=>void;working:Record<string,unknown>|null};
// A version this page has no words for is shown by its identifier, never hidden.
const names:Readonly<Record<string,string>>={gas_day_opening_inventory_band_v1:'By the band a gas day opens in',single_direction_per_gas_day_v1:'One direction per gas day',
 in_kind_fuel_at_storage_point_v1:'In kind, at the storage point',supplied:'Entered by the desk',gas_storage_inventory_lattice_search_v1:'Proposed from the forward curve'};
const named=(code:string)=>names[code]??code;
const reason=(error:unknown)=>error instanceof Error?error.message:String(error);
function el<K extends keyof HTMLElementTagNameMap>(tag:K,className?:string,text?:string){
 const node=document.createElement(tag);if(className)node.className=className;if(text!==undefined)node.textContent=publicText(text);return node;
}
const bands=(rows:Band[]):RatchetBand[]=>rows.map(b=>({lower:b.lower,upper:b.upper,maximumDailyInjection:b.maximum_daily_injection,maximumDailyWithdrawal:b.maximum_daily_withdrawal}));
const start=(minute:number|string)=>{const m=Number(minute);return `${String(Math.floor(m/60)).padStart(2,'0')}:${String(m%60).padStart(2,'0')}`;};

export async function readGasStorageView(api:Api,context:Record<string,unknown>,h:Holding,host:StorageHost):Promise<DetailRead>{
 if(h.instrumentType!==product)throw Error('Select an exact gas storage contract.');
 const selected={context:{...context,convention},tradeId:h.tradeId};
 const read=await api<Read>('gas-storage/get',selected),t=read.terms,p=read.position;
 if(p?.tradeId!==h.tradeId||p.bindingId!==h.bindingId||p.instrumentType!==product||!Array.isArray(read.ratchets)||!read.ratchets.length||!read.days?.length)
  throw Error('The storage response does not match its selected trade. Refresh the Blotter.');
 const unavailable=p.unavailableReason===null?null:valuationReason(p.unavailableReason,product);
 let busy=false;
 return {amend:{allowed:false,reason:'Amending a booked storage contract is Coming soon.'},updatesBlocked:()=>busy,view:zone=>{
  const terms:TermSection[]=[
   {title:'Gas storage contract',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Commodity',t.commodity],
    ['Contracted capacity',`${t.maximum_inventory} ${t.unit}`],['Minimum inventory',`${t.minimum_inventory} ${t.unit}`],
    ['Term',`${t.term_from} to ${t.term_to} (gas days from ${start(t.day_start_minute)}, ${t.clock_code})`],['Gas days',String(read.days.length)],
    ['Value measure',p.measureLabel],...unavailable?[['Value unavailable',unavailable] as [string,string]]:[]]},
   {title:'Inventory',rows:[['Opening inventory',`${t.opening_inventory} ${t.unit}`],['Terminal inventory',`${t.terminal_minimum} to ${t.terminal_maximum} ${t.unit}`]]},
   {title:'Ratchets and fuel',rows:[['Ratchets apply',named(t.ratchet_basis)],['Daily flow',named(t.flow_policy)],
    ...read.ratchets.map((b,i)=>[`Band ${i+1} · ${b.lower} to ${b.upper} ${t.unit}`,`in ${b.maximum_daily_injection} · out ${b.maximum_daily_withdrawal} ${t.unit}/day`] as [string,string]),
    ['Injection fuel',t.injection_fuel],['Withdrawal fuel',t.withdrawal_fuel],['Fuel retained',named(t.fuel_basis)]]},
   {title:'Prices and charges',rows:[['Injection priced at',t.injection_curve_code],['Withdrawal priced at',t.withdrawal_curve_code],
    ['Injection charge',`${t.injection_charge} ${t.currency}/${t.unit}`],['Withdrawal charge',`${t.withdrawal_charge} ${t.currency}/${t.unit}`],
    ['Capacity charge',`${t.monthly_capacity_charge} ${t.currency}/${t.unit} of capacity each month`],['Paid',`${t.payment_rule.days} calendar days after each month`]]},
   ...(read.path?[{title:`Plan in force · revision ${read.plan!.revision}`,rows:read.path.map(d=>[d.gasDay,
     `${Number(d.injection)>0?`inject ${d.injection}`:Number(d.withdrawal)>0?`withdraw ${d.withdrawal}`:'no flow'} · inventory ${d.opening} → ${d.closing} · band ${d.band+1}`] as [string,string])}]:[])];
  const history:TermSection[]=[{title:`Revision ${h.revision}`,rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]},
    ...(read.plan?[{title:`Plan revision ${read.plan.revision}`,rows:[['Source',named(read.plan.source)] as [string,string],['Effective',displayInstant(read.plan.effective_at,zone)] as [string,string],
     ...(read.plan.inventory_step?[['Inventory step',`${read.plan.inventory_step} ${t.unit}`] as [string,string]]:[])]}]:[])];
  return {termsLabel:'Contract',terms,history,
   termsContent:(box:HTMLElement)=>{
    const root=el('section','storage-panel');root.dataset.ownControls='';
    const ratchets=el('div'),path=el('div'),state=el('p','field-help'),drawing={minimum:t.minimum_inventory,maximum:t.maximum_inventory,edges:read.ratchets.map(b=>b.upper),unit:t.unit};
    root.append(el('h3','option-section-title','Ratchets'),ratchets,el('h3','option-section-title',read.plan?`Plan in force · revision ${read.plan.revision}`:'Plan'),state,path);
    drawRatchets(ratchets,{bands:bands(read.ratchets),unit:t.unit,opening:t.opening_inventory,terminal:[t.terminal_minimum,t.terminal_maximum]});
    if(read.path){state.textContent=publicText(named(read.plan!.source));drawPath(path,{...drawing,days:read.path});}
    else state.textContent=read.planRefusal?valuationReason(read.planRefusal,product):'No plan in force. Propose one from the forward curve, then record it.';
    const working=host.working;
    if(working===null){root.append(el('p','field-help','Propose and record a plan from the Blotter in the main window.'));box.append(root);return()=>{root.remove();};}
    // Propose a plan at this close's forwards, review its path, and record it as the plan in force.
    const step=el('input'),propose=el('button','secondary','Propose plan'),proposed=el('div'),summary=el('p','field-help'),why=el('input'),adopt=el('button',undefined,'Record as plan'),said=el('p','field-help');
    step.id='storage-plan-step';step.inputMode='decimal';why.id='storage-plan-reason';propose.type=adopt.type='button';propose.id='storage-propose';adopt.id='storage-adopt';said.setAttribute('role','status');
    const stepLabel=el('label',undefined,`Inventory step · ${t.unit}`);stepLabel.htmlFor=step.id;const whyLabel=el('label',undefined,'Reason');whyLabel.htmlFor=why.id;
    const stepRow=el('div','option-row'),whyRow=el('div','option-row');stepRow.append(stepLabel,step);whyRow.append(whyLabel,why);
    const adoption=el('div','storage-adoption');adoption.hidden=true;adoption.append(summary,proposed,whyRow,adopt);
    root.append(el('h3','option-section-title','Propose a plan'),el('p','field-help','Searches plans that move inventory in whole steps from the opening inventory, at this close\'s forward prices net of the commodity charges. A smaller step searches more plans.'),
     stepRow,propose,adoption,said);
    let proposal:Proposal|undefined,operation:string|undefined,alive=true;
    const sync=()=>{propose.disabled=adopt.disabled=busy;};
    propose.onclick=()=>void(async()=>{
     if(busy)return;if(!step.value.trim()){said.textContent='Enter the inventory step.';return;}
     busy=true;said.textContent='Searching…';sync();
     try{
      const result=await api<Proposal>('gas-storage/propose',{...selected,inventoryStep:step.value.trim()});if(!alive)return;
      proposal=result;operation=undefined;adoption.hidden=false;said.textContent='';
      summary.textContent=publicText(`Proposed plan worth ${result.objective} ${result.currency} at the forwards, before the capacity charge, on a step of ${result.inventoryStep} ${t.unit}.`);
      drawPath(proposed,{...drawing,days:result.path});
     }catch(error){if(alive){adoption.hidden=true;proposal=undefined;said.textContent=`No plan was proposed: ${reason(error)}`;}}
     finally{busy=false;if(alive)sync();}
    })();
    adopt.onclick=()=>void(async()=>{
     if(busy||!proposal)return;if(!why.value.trim()){said.textContent='Enter the reason for recording this plan.';return;}
     // One request identity per proposal: a retry after an unanswered request sends the same command.
     operation??=crypto.randomUUID();busy=true;said.textContent='Recording…';sync();
     try{
      await api('gas-storage/plan',{context:{...working,convention},tradeId:h.tradeId,operationId:operation,expectedRevision:read.plan?Number(read.plan.revision):0,reason:why.value.trim(),
       source:{kind:proposal.implementation,inventoryStep:proposal.inventoryStep},days:proposal.path.map(d=>({gasDay:d.gasDay,injection:d.injection,withdrawal:d.withdrawal}))});
      if(!alive)return;said.textContent='Plan recorded.';busy=false;host.recorded();
     }catch(error){if(alive)said.textContent=`The plan was not recorded: ${reason(error)}`;}
     finally{busy=false;if(alive)sync();}
    })();
    box.append(root);
    return()=>{alive=false;root.remove();};
   }};
 }};
}
