import type {Holding} from './desk-model.js';
import type {DetailRead} from './position-panel.js';
import {displayExactInstant} from './market-view.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Terms={loadFrom:string;loadTo:string;dischargeFrom:string;dischargeTo:string;quantity:string;quantityCode:string;quantityUnit:string;conversionCode:string|null;direction:string;considerationDirection:string;fixedPrice:string;paymentAt:string;profile:string;incoterms:{edition:string;rule:string;namedPlace:string}};
export async function readLngView(api:Api,context:Record<string,unknown>,h:Holding):Promise<DetailRead>{
 const detail=await api<{head:{revision:number;instrument_id:string;request:Terms;energy_quantity:string};physical:Record<string,string>}>('trading/lng',{context:{...context,convention:'trading_workspace_v1'},tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),instrumentType:'physical_lng'});
 if(String(detail.head.revision)!==h.revision||detail.head.instrument_id!==h.bindingId)throw Error('The LNG position changed. Refresh the Blotter.');
 const t=detail.head.request,p=detail.physical;
 return{amend:{allowed:false,reason:'This LNG profile supports retained pre-loading valuation. Amendments and operational actuals require a separate contract.'},view:zone=>({terms:[
  {title:'Physical LNG cargo',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Value measure','Contractual PV'],['Product',p.product_name],['Profile',t.profile]]},
  {title:'Loading',rows:[['Terminal',p.load_facility_name],['Custody point',p.load_custody_name],['Window',`${displayExactInstant(t.loadFrom,zone)} to ${displayExactInstant(t.loadTo,zone)} · exclusive`]]},
  {title:'Discharge',rows:[['Terminal',p.discharge_facility_name],['Custody point',p.discharge_custody_name],['Window',`${displayExactInstant(t.dischargeFrom,zone)} to ${displayExactInstant(t.dischargeTo,zone)} · exclusive`],['Physical direction',t.direction],['Original quantity',`${t.quantity} · ${t.quantityUnit}`],['Input specification',t.quantityCode],['Gross discharge energy',`${detail.head.energy_quantity} ${p.unit}`],['Mass-energy conversion',t.conversionCode??'Direct energy'],['Pricing source · derived',p.curve_name]]},
  {title:'Incoterms',rows:[['Edition',p.edition_name],['Rule',p.rule_name],['Named place',p.place_name]]},
  {title:'Fixed consideration',rows:[['Cash direction',t.considerationDirection],['Fixed price',t.fixedPrice],['Price unit',`${p.currency} / ${p.unit} · multiplier ${p.multiplier}`],['Payment',displayExactInstant(t.paymentAt,zone)]]}
 ],history:[{title:`Revision ${h.revision}`,rows:[['Booked',displayExactInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]}]})};
}
