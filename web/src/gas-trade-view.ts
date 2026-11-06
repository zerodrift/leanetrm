import {gasInstrumentSections,type GasInstrumentFacts} from './physical-instrument.js';
import {mountSideScheduleInspection} from './side-schedule-inspection.js';
import type {Holding} from './desk-model.js';
import type {AmendState,DetailRead,TermSection} from './position-panel.js';
import {displayInstant} from './market-view.js';
import {setupTicket,ticketBookingRetained,type TicketHandle} from './ticket-controller.js';
import type {ProductSelection} from './product-tickets.js';
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
const convention='physical_gas_fixed_quantity_v4';

// Read retained terms independently of pre-delivery PV. Operational quantities
// remain available after delivery starts, without changing the booked contract.
// Every gas trade opens read-only in the deal ticket; a single-point trade opens as the composition of one physical side it is.
export async function readGasView(api:Api,context:Record<string,unknown>,h:Holding,o:{actor:()=>string;onDenied:()=>void;readOnly?:boolean;amend?:()=>void}):Promise<DetailRead>{
 const detail=await api<GasInstrumentFacts&{amend:AmendState;history?:Record<string,string|number|null>[]}>('trading/gas',{context:{...context,convention:'trading_workspace_v1'},tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),instrumentType:'physical_gas'});
 if(String(detail.head.revision)!==h.revision||detail.head.instrument_id!==h.bindingId)throw Error('The gas position changed. Refresh the Blotter.');
 let ticket:TicketHandle|undefined;
 const amend:AmendState=detail.amend.allowed&&(!o.amend||o.readOnly)?{allowed:false,reason:'Open the Blotter in the main window to amend this gas trade.'}:detail.amend;
 return{amend,updatesBlocked:()=>!!ticket&&(ticket.unsaved()||ticket.pending()),view:zone=>{
  const terms:TermSection[]=[{title:'Physical gas instrument',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Value measure','Contractual PV']]},...gasInstrumentSections(detail,zone)];
  return{termsLabel:'Physical overview',terms,history:[{title:'Original booking',rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]},
   ...(detail.history??[]).map(row=>({title:`Revision ${row.revision}`,rows:[['Effective',displayInstant(String(row.effective_at),zone)],['Reason',row.reason===null?'Original booking':String(row.reason)],['Actor',String(row.actor_id)]] as [string,string][]}))],
   schedules:(host:HTMLElement)=>{
    const position={context:{...context,convention:'trading_workspace_v1'},tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),instrumentType:h.instrumentType};
    if(h.unavailableReason!==null){
     return mountSideScheduleInspection(host,{api,onDenied:o.onDenied,position,instant:at=>displayInstant(at,zone)});
    }
    ticket=setupTicket(host,{api,context:()=>({...context,convention}),product:'physical_gas',instant:at=>displayInstant(at,zone),zone:()=>zone,actor:o.actor,readOnly:true,presentation:'schedules',
     opened:{tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision)},
     sideSchedule:(target,_side,slot,_view,certified)=>mountSideScheduleInspection(target,{api,onDenied:o.onDenied,position:{...position,context:{...certified,convention:'trading_workspace_v1'}},side:{slot},instant:at=>displayInstant(at,zone)})});
    return ()=>{ticket?.destroy();ticket=undefined;};
   }};
 }};
}

/** New trade's physical gas form: the deal ticket of physical gas, in the panel the trade is entered in. */
export function setupGasTicket(o:{api:Api;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;instant:(at:string)=>string;zone:()=>string;onBooked:(tradeId:string)=>Promise<void>}){
 const root=document.getElementById('gas-page')!,host=document.createElement('div');host.id='gas-ticket';root.replaceChildren(host);
 let ticket:TicketHandle|undefined;
 const reset=()=>{ticket?.destroy();ticket=undefined;};
 return {root,reset,
  async begin(){
   reset();
   if(o.historical())throw Error('Return to working data to enter a gas trade.');
   ticket=setupTicket(host,{api:o.api,context:()=>({...o.source(),convention,knowledgeMode:'current'}),product:'physical_gas',instant:o.instant,zone:o.zone,actor:o.actor,booked:r=>void o.onBooked(r.tradeId)});
  },
  async inspect(tradeId:string,selection?:ProductSelection){
   reset();
   if(o.historical())throw Error('Return to working data to amend a gas trade.');
   if(!selection)throw Error('Open a gas amendment from its exact selected Blotter revision.');
   ticket=setupTicket(host,{api:o.api,context:()=>({...o.source(),convention,knowledgeMode:'current'}),product:'physical_gas',instant:o.instant,zone:o.zone,actor:o.actor,
    amending:{tradeId,...selection},booked:r=>void o.onBooked(r.tradeId)});
  },
  unsaved:()=>!!ticket?.unsaved(),
  pending:()=>!!ticket?.pending()||ticketBookingRetained('physical_gas',o.actor),
  // The ticket keeps itself in step with its own requests; the page's busy state does not change it.
  sync:()=>{}};
}
