import type {Holding} from './desk-model.js';
import type {DetailRead,TermSection} from './position-panel.js';
import {displayInstant,deliveryLabel} from './market-view.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Row=Record<string,string|number|null>;
type Terms={assignmentCode:string;direction:string;considerationDirection:string;quantity:string;fixedPrice:string;handlingFee:string|null;handlingFeeDirection:string|null;paymentAt:string;deliveryFrom:string;deliveryTo:string;profile:string;tolerance:string};
const direction=(value:string)=>value==='receive'?'Receive':value==='deliver'?'Deliver':value==='pay'?'Pay':value;
// Retained contractual facts remain inspectable when delivery has started and PV is unavailable.
export async function readCrudeView(api:Api,context:Record<string,unknown>,h:Holding,operations?:()=>void):Promise<DetailRead>{
 const detail=await api<{head:{revision:number;instrument_id:string;request:Terms};physical:Row}>('trading/crude',{context:{...context,convention:'trading_workspace_v1'},tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),instrumentType:'physical_crude'});
 if(String(detail.head.revision)!==h.revision||detail.head.instrument_id!==h.bindingId)throw Error('The crude position changed. Refresh the Blotter.');
 const t=detail.head.request,p=detail.physical;
 return{amend:{allowed:false,reason:'Crude amendments and custody measurement conversion are not supported. Accepted certified receipts and invoice cash are separate operational facts.'},view:zone=>{
  const terms:TermSection[]=[{title:'Physical crude trade',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Value measure','Contractual PV']]},
   {title:'Physical delivery side',rows:[['Assignment',t.assignmentCode],['Commodity',String(p.commodity_name)],['Grade',String(p.grade_name)],['Facility',String(p.facility_name)],['Custody point',String(p.custody_point_name)],['Physical direction',direction(t.direction)],['Contract volume',`${t.quantity} ${p.unit}`],['Standard temperature',`${p.standard_temperature} ${p.temperature_scale}`],['Delivery',deliveryLabel(t.deliveryFrom,t.deliveryTo)],['Pricing curve',String(p.curve_name)],['Profile',t.profile],['Tolerance',t.tolerance]]},
   {title:'Cash consideration side',rows:[['Consideration direction',direction(t.considerationDirection)],['Fixed price',t.fixedPrice],['Price unit',`${p.currency} / ${p.multiplier} ${p.unit}`],['Payment',displayInstant(t.paymentAt,zone)]]},
   {title:'Handling fee side',rows:t.handlingFee===null?[['Handling fee','None']]:[['Handling fee',t.handlingFee],['Price unit',`${p.currency} / ${p.multiplier} ${p.unit}`],['Handling fee direction',direction(t.handlingFeeDirection!)],['Payment',displayInstant(t.paymentAt,zone)]]}];
  return{terms,actions:operations?[{id:'crude-operations',label:'Delivery and cash',run:operations}]:undefined,history:[{title:`Revision ${h.revision}`,rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]}]};
 }};
}
