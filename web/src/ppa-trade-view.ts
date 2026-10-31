import type {Holding} from './desk-model.js';
import type {DetailRead,TermSection} from './position-panel.js';
import {displayInstant} from './market-view.js';
import type {Terms} from './ppa-contract.js';
import {mountPpaPositionSchedule} from './ppa-position-schedule.js';

// Retained database cutoffs have seven fractional digits, with an exact trailing zero.
export const ppaCutoff=(value:string)=>value.replace(/(\.\d{6})0(?=Z$|\+00:00$)/,'$1');

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Member={role:string;ordinal:number;coefficient:string;side:{kind:string;quantity:{kind:string};location:string|null;indexPrice:{curve:string}|null;fixedPrice:{kind:string;price:string;quantity:string}|null}};
/** A PPA trade's retained terms and history under the Blotter's exact cutoff. */
export async function readPpaView(api:Api,context:Record<string,unknown>,h:Holding,knownAt:string,manage?:()=>void):Promise<DetailRead>{
 const policy={estimate_until_actual_v1:'Estimate until the meter deadline, then require actuals',require_actuals_v1:'Require actuals for ended intervals'}[h.pendingActuals??''];
 if(!policy)throw Error('The PPA row has no supported pending-actuals policy.');
 const detail=await api<{head:{revision:number};bindings:{binding_id:string;instrument_id:string;coverage_from:string;coverage_to:string}[];
  instruments:{instrumentId:string;terms:Terms&{memberships:Member[]}}[];
  history:{revision:number;event_kind:string;effective_at:string;reason:string}[]}>('ppa/editor',{snapshotId:context.snapshotId,tradeId:h.tradeId,knownAt:ppaCutoff(knownAt)});
 const binding=detail.bindings.find(b=>b.binding_id===h.bindingId),instrument=detail.instruments.find(i=>i.instrumentId===binding?.instrument_id);
 if(!binding||!instrument||String(detail.head.revision)!==h.revision)throw Error('The PPA binding changed. Refresh the Blotter.');
 return{amend:{allowed:false,reason:'Use Manage PPA for forward amendments and termination in this trade workspace.'},view:zone=>{
  const terms:TermSection[]=[{title:'PPA trade',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Quantity',`${h.quantity} ${h.unit}`],
   ['Delivery',`${instrument.terms.delivery.from} to ${instrument.terms.delivery.to}`],['Binding coverage',`${displayInstant(binding.coverage_from,zone)} to ${displayInstant(binding.coverage_to,zone)}`],
   ['Value measure','Value excluding overdue cash'],['Pending meter readings',policy]]}];
  for(const m of instrument.terms.memberships)terms.push({title:`${m.role.replaceAll('_',' ')} · ${m.coefficient==='1'?'Receive':'Pay'}`,rows:[['Side kind',m.side.kind],['Quantity rule',m.side.quantity.kind],
   ...(m.side.location?[["Location",m.side.location] as const]:[]),...(m.side.indexPrice?[["Price curve",m.side.indexPrice.curve] as const]:[]),
   ...(m.side.fixedPrice?[["Fixed price",`${m.side.fixedPrice.price} / ${m.side.fixedPrice.quantity}`] as const]:[])]});
  return{terms,actions:manage?[{id:'ppa-manage',label:'Manage PPA',run:manage}]:[],history:detail.history.map(r=>({title:`Revision ${r.revision} · ${r.event_kind}`,rows:[['Effective at',displayInstant(r.effective_at,zone)],['Reason',r.reason]]})),
   schedules:host=>mountPpaPositionSchedule(host,api,context,h,instrument.terms,ppaCutoff(knownAt),zone)};
 }};
}
