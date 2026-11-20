import {cloneRead} from './read-references.js';
import {setupDescribedOperations,type DescribedOperationsOptions,type OperationProtocol} from './described-operations-view.js';
import {setupDeliveryNominationBatch} from './delivery-nomination-batch.js';
import type {PhysicalDeliveryTarget} from './physical-operations.js';

export type DeliveryWorkbenchProfile='gas-transport'|'crude-receipt';
export type DeliveryWorkbenchOptions=DescribedOperationsOptions&{nominationContext?:()=>Record<string,unknown>|null;nativeDeliveryReason?:()=>string|null;openNativeDelivery?:(target:PhysicalDeliveryTarget)=>Promise<void>};
const profiles={
 'gas-transport':{path:'delivery/workbench/gas-transport',data:'delivery-workbench-gas',profile:'gas_transport_delivery_v1',assessmentProfile:'gas_transport_interval_v1',
  label:'Gas transport delivery',plural:'gas transport routes',boundary:'Chosen route quantities and their explicit allocations to booked delivery obligations. Nominations, operator responses, physical evidence, title and cash retain their own operations.'},
 'crude-receipt':{path:'delivery/workbench/crude-receipt',data:'delivery-workbench-crude',profile:'crude_receipt_delivery_v1',assessmentProfile:'crude_receipt_transfer_v1',
  label:'Crude receipt delivery',plural:'crude receipt scopes',boundary:'Retained native receipt and dispatch evidence. Record supported receipts, dispatches, title and cash through Physical operations; this view adds no second ledger.'},
} as const;

/** Each installed native adapter supplies the shared operation and assessment
 * contracts. A new commodity label alone does not install a delivery profile. */
export function setupDeliveryWorkbench(root:HTMLElement,o:DeliveryWorkbenchOptions,profile:DeliveryWorkbenchProfile){
 const selected=profiles[profile];if(!selected)throw Error('Choose an installed delivery workbench profile.');
 const protocol:OperationProtocol={...selected,kind:'delivery',scopeKey:'scopeId',listKey:'scopes',commandData:'deliveryCommand',
  storage:'openetrm.delivery.workbench.'+profile+'.request.',configurationPermission:'ConfigureGasServices',configurationHeading:selected.label,
  operationsHeading:'Delivery operations',selectionPlaceholder:'Choose a delivery scope'};
 const operationsHost=document.createElement('div'),nativeHost=document.createElement('section'),nominationHost=document.createElement('div');root.append(operationsHost,nativeHost);
 nativeHost.hidden=true;nativeHost.className='card operations-native-navigation';let nativeButtons:HTMLButtonElement[]=[],nativeReason:HTMLElement|undefined;
 function syncNative(){const reason=o.nativeDeliveryReason?.()??(!o.openNativeDelivery?'Native operations navigation is unavailable.':null);if(nativeReason)nativeReason.textContent=reason??'Uses the selected financial close and knowledge context. The exact source trade revision must be visible there.';for(const button of nativeButtons)button.disabled=o.busy()||!!reason;}
 function showNative(value:unknown){nativeHost.replaceChildren();nativeHost.hidden=true;nativeButtons=[];nativeReason=undefined;
  const view=value as {scopeId?:string;nativeActions?:unknown[]}|null;if(!view?.nativeActions?.length)return;
  const heading=document.createElement('h3');heading.textContent='Native delivery commands';nativeHost.append(heading);nativeHost.hidden=false;
  for(const raw of view.nativeActions){const action=raw as PhysicalDeliveryTarget&{label:string};
   if(!action||typeof action.label!=='string'||!action.label||typeof action.tradeId!=='string'||!action.tradeId||typeof action.bindingId!=='string'||!action.bindingId||!Number.isSafeInteger(action.revision)||action.revision<1||action.tab!=='delivery'||action.instrumentType!==(profile==='crude-receipt'?'physical_crude':'physical_gas')||profile==='crude-receipt'&&action.tradeId!==view.scopeId)throw Error('Native delivery navigation does not identify the exact installed source.');
   const target:PhysicalDeliveryTarget={tradeId:action.tradeId,bindingId:action.bindingId,revision:action.revision,instrumentType:action.instrumentType,tab:'delivery'};
   const button=document.createElement('button');button.type='button';button.dataset.nativeDelivery='';button.textContent=action.label;button.onclick=()=>void(async()=>{if(button.disabled)return;try{await o.openNativeDelivery?.(cloneRead(target));}catch(error){if(nativeReason)nativeReason.textContent=String(error);}})();nativeButtons.push(button);nativeHost.append(button);
  }
  nativeReason=document.createElement('p');nativeReason.setAttribute('role','status');nativeHost.append(nativeReason);syncNative();
 }
 if(profile==='gas-transport')root.append(nominationHost);
 let batch:ReturnType<typeof setupDeliveryNominationBatch>|undefined;
 const operations=setupDescribedOperations(operationsHost,{...o,busy:()=>o.busy()||!!batch?.edited()&&!batch.unconfirmed(),pendingElsewhere:()=>!!o.pendingElsewhere?.()||!!batch?.unconfirmed(),inspected:value=>{o.inspected?.(value);batch?.show(value);showNative(value);}},protocol);
 if(profile==='gas-transport')batch=setupDeliveryNominationBatch(nominationHost,{...o,pendingElsewhere:()=>!!o.pendingElsewhere?.()||operations.edited(),financialContext:()=>o.nominationContext?.()??null,recorded:()=>operations.load(operations.selected()??undefined)});
 return{...operations,async load(id?:string){batch?.sync();await operations.load(id);},sync(){batch?.sync();operations.sync();syncNative();},unconfirmed:()=>operations.unconfirmed()||!!batch?.unconfirmed(),edited:()=>operations.edited()||!!batch?.edited(),async leave(){return await operations.leave()&&(batch?.leave()??true);},refresh(){operations.refresh();batch?.refresh();syncNative();},reset(){operations.reset();batch?.reset();showNative(null);}};
}
