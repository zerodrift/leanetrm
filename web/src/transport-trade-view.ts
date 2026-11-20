import {retainedKnowledge} from './read-references.js';
import type {Holding} from './desk-model.js';
import {displayInstant} from './market-view.js';
import type {DetailRead} from './position-panel.js';
import {renderPhysicalSections,overviewTermSections,type PhysicalOverviewSection} from './physical-instrument.js';
import {setupTicket,ticketBookingRetained,type TicketHandle} from './ticket-controller.js';
import type {TicketDraft,TicketTrade} from './ticket-types.js';
import {valuationReason,type ValuationReason} from './valuation-status.js';

type Api=<T>(path:string,body?:unknown,signal?:AbortSignal)=>Promise<T>;
const product='physical_gas_transport',convention='physical_gas_fixed_quantity_v4';
type TransportTrade=Omit<TicketTrade,'draft'>&{draft:TicketDraft|null;lifecycleTarget:{handler:string;scopeId:string;bindingId:string};measureLabel:string;ownershipNote:string;overviewSections:PhysicalOverviewSection[];unavailableReason:ValuationReason|null;sideGroupsUnavailableReason?:ValuationReason|null};

export async function readTransportView(api:Api,context:Record<string,unknown>,h:Holding,o:{actor:()=>string;lifecycle?:()=>void}):Promise<DetailRead>{
 if(h.instrumentType!==product)throw Error('Select an exact Transport holding.');
 const selected={tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),product},requested={...context,convention};
 const detail=await api<TransportTrade>('tickets/trade',{context:requested,...selected});
 if(detail.snapshotId!==context.snapshotId||!detail.context?.contextId||!detail.context.effectiveOn||!detail.context.knownAt||
  context.knowledgeMode==='retained'&&detail.context.contextId!==context.contextId||
  detail.lifecycleTarget?.handler!=='gas_transport_financial'||detail.lifecycleTarget.scopeId!==h.tradeId||detail.lifecycleTarget.bindingId!==h.bindingId||
  detail.draft!==null&&(detail.draft?.product!==product||detail.draft.trade.profile!=='physical_gas_transport_direct_rights_v1'))
  throw Error('The Transport response does not match its selected agreement, profile, snapshot and context. Refresh the Blotter.');
 if(typeof detail.measureLabel!=='string'||!detail.measureLabel||typeof detail.ownershipNote!=='string'||!detail.ownershipNote||
  !Array.isArray(detail.overviewSections)||!detail.overviewSections.length||detail.draft===null&&(typeof detail.unavailableReason!=='string'||!detail.unavailableReason))
  throw Error('The Transport response did not identify its financial state, value measure and availability. Refresh the Blotter.');
 overviewTermSections(detail.overviewSections,'UTC');
 if(detail.draft===null&&(detail.sideGroupsUnavailableReason!==detail.unavailableReason||detail.sideGroups!=null||detail.sideGroupsReason!=null)||
  detail.draft!==null&&detail.sideGroupsUnavailableReason!=null)
  throw Error('Reread the explicit unavailable Side composition; it cannot be replaced by financial absence or an obsolete state.');
 const unavailable=detail.unavailableReason===null?null:valuationReason(detail.unavailableReason,product);
 const compositionUnavailable=detail.sideGroupsUnavailableReason==null?null:valuationReason(detail.sideGroupsUnavailableReason,product);
 const certified={...requested,...retainedKnowledge(detail.context)};
 return {amend:{allowed:false,reason:detail.actions.amend.reason},view:zone=>({
  termsLabel:'Transport terms',
  terms:[{title:'Transport',rows:[['Trade',h.tradeReference],['Instrument',h.instrumentReference],['Book',h.book],['Value measure',detail.measureLabel],['Ownership',detail.ownershipNote],
   ...unavailable?[['Value unavailable',unavailable] as [string,string]]:[],...compositionUnavailable?[['Side composition unavailable',compositionUnavailable] as [string,string]]:[]]}],
  termsContent:host=>{
   const content=document.createElement('div');host.append(content);let ticket:TicketHandle|undefined;
   if(detail.draft!==null){const ticketHost=document.createElement('div');content.append(ticketHost);
    ticket=setupTicket(ticketHost,{api,context:()=>certified,product,opened:selected,readOnly:true,requireRetainedChoices:true,retainedTarget:{handler:'gas_transport_financial',scopeId:selected.tradeId,bindingId:selected.bindingId},presentation:'terms',instant:at=>displayInstant(at,zone),zone:()=>zone,actor:o.actor});}
   renderPhysicalSections(content,overviewTermSections(detail.overviewSections,zone));
   return ()=>{ticket?.destroy();content.remove();};
  },
  history:[{title:`Revision ${h.revision}`,rows:[['Booked',displayInstant(h.executedAt,zone)],['Booker',h.trader??''],['Trade date',h.booking.tradeDate??'Unavailable']]}],
  actions:o.lifecycle?[{id:'transport-lifecycle',label:'Open Transport actions',run:o.lifecycle}]:[],
 })};
}

export function setupTransportTicket(o:{api:Api;source:()=>{snapshotId:string};historical:()=>boolean;actor:()=>string;instant:(at:string)=>string;zone:()=>string;onBooked:(tradeId:string)=>Promise<void>}){
 const root=document.getElementById('transport-page')!,host=document.createElement('div');host.id='transport-ticket';root.replaceChildren(host);
 let ticket:TicketHandle|undefined;
 const reset=()=>{ticket?.destroy();ticket=undefined;};
 return {root,reset,
  async begin(){
   reset();if(o.historical())throw Error('Return to working data to enter a Transport agreement.');
   ticket=setupTicket(host,{api:o.api,context:()=>({...o.source(),convention,knowledgeMode:'current'}),product,instant:o.instant,zone:o.zone,actor:o.actor,booked:r=>void o.onBooked(r.tradeId)});
  },
  async inspect(){throw Error('Inspect retained Transport terms from the Blotter; use its reviewed financial actions for supported changes.');},
  unsaved:()=>!!ticket?.unsaved(),pending:()=>!!ticket?.pending()||ticketBookingRetained(product,o.actor),sync:()=>{},
 };
}
