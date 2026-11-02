import type {Holding} from './desk-model.js';
import type {AmendState,DetailRead,TermSection} from './position-panel.js';
import {displayInstant} from './market-view.js';
import {setupTicket,ticketBookingRetained,type TicketHandle} from './ticket-controller.js';
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
const convention='physical_gas_fixed_quantity_v4';

type Row=Record<string,string|number|boolean|null>;
// Read retained terms independently of pre-delivery PV. Operational quantities
// remain available after delivery starts, without changing the booked contract.
// Every gas trade opens read-only in the deal ticket; a single-point trade opens as the composition of one physical side it is.
export async function readGasView(api:Api,context:Record<string,unknown>,h:Holding,o:{actor:()=>string;readOnly?:boolean}):Promise<DetailRead>{
 const detail=await api<{head:Row;amend:AmendState}>('trading/gas',{context:{...context,convention:'trading_workspace_v1'},tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),instrumentType:'physical_gas'});
 if(String(detail.head.revision)!==h.revision||detail.head.instrument_id!==h.bindingId)throw Error('The gas position changed. Refresh the Blotter.');
 return{amend:detail.amend,view:zone=>{
  const terms:TermSection[]=[{title:'Physical gas trade',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Value measure','Contractual PV'],['Quantity policy','Retained physical-side schedules; delivery revisions do not change the contract']]}];
  return{terms,history:[{title:`Revision ${h.revision}`,rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]}],
   schedules:(host:HTMLElement)=>{
    const box=document.createElement('div');host.append(box);
    const ticket=setupTicket(box,{api,context:()=>({...context,convention}),product:'physical_gas',instant:at=>displayInstant(at,zone),actor:o.actor,readOnly:o.readOnly,opened:{tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision)}});
    return ()=>{ticket.destroy();box.remove();};
   }};
 }};
}

/** New trade's physical gas form: the deal ticket of physical gas, in the panel the trade is entered in. */
export function setupGasTicket(o:{api:Api;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;instant:(at:string)=>string;onBooked:(tradeId:string)=>Promise<void>}){
 const root=document.getElementById('gas-page')!,host=document.createElement('div');host.id='gas-ticket';root.replaceChildren(host);
 let ticket:TicketHandle|undefined;
 const reset=()=>{ticket?.destroy();ticket=undefined;};
 return {root,reset,
  async begin(){
   reset();
   if(o.historical())throw Error('Return to working data to enter a gas trade.');
   ticket=setupTicket(host,{api:o.api,context:()=>({...o.source(),convention,knowledgeMode:'current'}),product:'physical_gas',instant:o.instant,actor:o.actor,booked:r=>void o.onBooked(r.tradeId)});
  },
  inspect:()=>Promise.reject(Error('A booked gas trade opens in the Details pane.')),
  unsaved:()=>!!ticket?.unsaved(),
  pending:()=>!!ticket?.pending()||ticketBookingRetained('physical_gas',o.actor),
  // The ticket keeps itself in step with its own requests; the page's busy state does not change it.
  sync:()=>{}};
}
