import {gasInstrumentSections,type GasInstrumentFacts} from './physical-instrument.js';
import {renderRegularTable} from './regular-table.js';
import type {GasQuantityView} from './gas-daily-quantities.js';
import type {Holding} from './desk-model.js';
import type {AmendState,DetailRead} from './position-panel.js';
import {displayInstant} from './market-view.js';
import {setupTicket,ticketBookingRetained,type TicketHandle} from './ticket-controller.js';
type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
const convention='physical_gas_fixed_quantity_v4';

// Read retained terms independently of pre-delivery PV. Operational quantities
// remain available after delivery starts, without changing the booked contract.
// Every gas trade opens read-only in the deal ticket; a single-point trade opens as the composition of one physical side it is.
export async function readGasView(api:Api,context:Record<string,unknown>,h:Holding,o:{actor:()=>string;readOnly?:boolean}):Promise<DetailRead>{
 const detail=await api<GasInstrumentFacts&{amend:AmendState}>('trading/gas',{context:{...context,convention:'trading_workspace_v1'},tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),instrumentType:'physical_gas'});
 if(String(detail.head.revision)!==h.revision||detail.head.instrument_id!==h.bindingId)throw Error('The gas position changed. Refresh the Blotter.');
 let ticket:TicketHandle|undefined;
 const mount=(host:HTMLElement,zone:string,presentation:'schedules')=>{
  ticket=setupTicket(host,{api,context:()=>({...context,convention}),product:'physical_gas',instant:at=>displayInstant(at,zone),zone:()=>zone,actor:o.actor,readOnly:true,presentation,opened:{tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision)}});
  return()=>{ticket?.destroy();ticket=undefined;};
 };
 return{amend:detail.amend,view:zone=>({termsLabel:'Physical overview',terms:[{title:'Physical gas instrument',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Value measure','Contractual PV']]},...gasInstrumentSections(detail,zone)],
  history:[{title:`Revision ${h.revision}`,rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]}],
  schedules:host=>{
   if(h.unavailableReason===null)return mount(host,zone,'schedules');
   let active=true;const box=document.createElement('div');host.append(box);
   void api<GasQuantityView>('gas/quantities/read',{context:{...context,convention},tradeId:h.tradeId,selectionPolicy:'operational_projection_v1',includeHistory:false}).then(read=>{
    if(!active)return;renderRegularTable(box,'gas-side-schedules:'+h.tradeId,h.tradeId,['Side','Gas day','Start','End','Contract quantity','Selected quantity','Unit','Input track'],read.rows.map(r=>[r.sideCode,r.day,displayInstant(r.start,zone),displayInstant(r.end,zone),r.contractual.numerator+'/'+r.contractual.denominator,r.selected.numerator+'/'+r.selected.denominator,r.quantitySpecification.code,r.kind]),[],()=>false);
   }).catch(error=>{if(active)box.textContent=String(error);});
   for(const c of detail.components.filter(c=>c.role!=='physical')){const p=document.createElement('p');p.textContent=`${c.code} · Payment ${displayInstant(String(c.payment_at),zone)} · fixed unit price ${c.price} · source ${detail.components.find(m=>m.slot===c.source_slot)?.code??'Unavailable'} / ${c.stage}`;host.append(p);}
   return()=>{active=false;host.replaceChildren();};
  }})};
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
  inspect:()=>Promise.reject(Error('A booked gas trade opens in the Details pane.')),
  unsaved:()=>!!ticket?.unsaved(),
  pending:()=>!!ticket?.pending()||ticketBookingRetained('physical_gas',o.actor),
  // The ticket keeps itself in step with its own requests; the page's busy state does not change it.
  sync:()=>{}};
}
