import type {Holding} from './desk-model.js';
import type {DetailRead,TermSection} from './position-panel.js';
import {displayInstant} from './market-view.js';
import {setupTicket} from './ticket-controller.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Row=Record<string,string|number|boolean|null>;
// Read retained terms independently of pre-delivery PV. Operational quantities
// remain available after delivery starts, without changing the booked contract.
// Every gas trade opens read-only in the deal ticket; a single-point trade opens as the composition of one physical side it is.
export async function readGasView(api:Api,context:Record<string,unknown>,h:Holding,operations:()=>void):Promise<DetailRead>{
 const detail=await api<{head:Row}>('trading/gas',{context:{...context,convention:'trading_workspace_v1'},tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),instrumentType:'physical_gas'});
 if(String(detail.head.revision)!==h.revision||detail.head.instrument_id!==h.bindingId)throw Error('The gas position changed. Refresh the Blotter.');
 return{amend:{allowed:false,reason:'Gas amendments and physical settlement are not supported yet. Daily quantities are separate operational revisions.'},view:zone=>{
  const terms:TermSection[]=[{title:'Physical gas trade',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Value measure','Contractual PV'],['Quantity policy','Retained physical-side schedules; delivery revisions do not change the contract']]}];
  return{terms,history:[{title:`Revision ${h.revision}`,rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]}],
   actions:detail.head.composition?[{id:'gas-operations',label:'Daily delivery quantities',run:operations}]:[],
   schedules:(host:HTMLElement)=>{
    const box=document.createElement('div');host.append(box);
    const ticket=setupTicket(box,{api,context:()=>({...context,convention:'physical_gas_fixed_quantity_v4'}),product:'physical_gas',instant:at=>displayInstant(at,zone),opened:{tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision)}});
    return ()=>{ticket.destroy();box.remove();};
   }};
 }};
}
