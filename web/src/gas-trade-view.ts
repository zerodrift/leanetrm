import type {Holding} from './desk-model.js';
import type {DetailRead,TermSection} from './position-panel.js';
import {displayInstant} from './market-view.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Row=Record<string,string|number|boolean|null>;
// Read retained terms independently of pre-delivery PV. Operational quantities
// remain available after delivery starts, without changing the booked contract.
export async function readGasView(api:Api,context:Record<string,unknown>,h:Holding,operations:()=>void):Promise<DetailRead>{
 const detail=await api<{head:Row;physical:Row[];components:Row[]}>('trading/gas',{context:{...context,convention:'trading_workspace_v1'},tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),instrumentType:'physical_gas'});
 if(String(detail.head.revision)!==h.revision||detail.head.instrument_id!==h.bindingId)throw Error('The gas position changed. Refresh the Blotter.');
 return{amend:{allowed:false,reason:'Gas amendments and physical settlement are not supported yet. Daily quantities are separate operational revisions.'},view:zone=>{
  const terms:TermSection[]=[{title:'Physical gas trade',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Value measure','Contractual PV'],['Quantity policy','Retained physical-side schedules; delivery revisions do not change the contract']]}];
  for(const p of detail.physical)terms.push({title:`Physical side ${Number(p.slot)+1}`,rows:[['Pipeline',String(p.pipeline_code)],['Zone',String(p.zone_code)],['Delivery location',String(p.point_code)],['Delivery',`${p.delivery_from} to ${p.delivery_to} exclusive`],['Pricing curve',String(p.curve_name)],['Native unit',String(p.unit)]]});
  for(const m of detail.components)terms.push({title:`${m.code} · ${m.role}`,rows:[['Direction',String(m.direction)==='1'?'Receive':m.role==='physical'?'Deliver':'Pay'],
   ...(m.role==='physical'?[["Quantity basis",String(m.quantity_basis)],["Independent quantity",String(m.independent_quantity??'Linked')],["Fuel rule",String(m.formula??'None')],["Fuel fraction",String(m.fraction??'0')]] as const: [["Quantity source slot",String(m.source_slot)],["Quantity stage",String(m.stage)],["Fixed price",String(m.price??'Tariff')],["Tariff",String(m.tariff_code??'None')],["Payment",displayInstant(String(m.payment_at),zone)]] as const)]});
  if(!detail.components.length)terms.push({title:'Cash and quantity terms',rows:[['Quantity basis',String(detail.head.quantity_basis)],['Contract quantity',String(detail.head.quantity)],['Fixed price',String(detail.head.fixed_price)],['Usage fee',String(detail.head.usage_fee??'None')]]});
  return{terms,history:[{title:`Revision ${h.revision}`,rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]}],
   actions:detail.head.composition?[{id:'gas-operations',label:'Daily delivery quantities',run:operations}]:[]};
 }};
}
