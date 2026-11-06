import {renderDataGrid,emptyGridLayout,type GridHandle,type GridLayout} from './data-grid.js';
import {readPhysicalDelivery,type PhysicalDeliveryView} from './physical-delivery.js';
import {renderPhysicalDeliveryGrid,renderDeliveryCapabilities} from './physical-delivery-grid.js';
import {physicalProduct,physicalTradeKey,physicalTradeGrid,operationsTag} from './physical-operations-grid.js';
import {crudeInstrumentSections,type CrudeInstrumentFacts} from './crude-trade-view.js';
import {lngInstrumentSections,type LngInstrumentFacts} from './lng-trade-view.js';
import {facilityAllocationTrack} from './facility-allocation-track.js';
import type {FacilityAllocationTrack} from './ticket-types.js';
import {setupGasOperations} from './gas-operations.js';
import {setupGasBilling,type GasBillingWorkflow} from './gas-billing.js';
import {setupCrudeOperations} from './crude-operations.js';
import {actorTransport,PendingCommand,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import {isCrudeOperation,knownTradeRejection,releasesRetained,retainedText,retryAllowed,tradeStatusPath,requirePhysicalReceipt} from './trade-requests.js';
import {askInPanel,setupTabs,clearTabs} from './side-panel.js';
import {readPositionFacts,appendPositionFacts,type PositionFact} from './position-facts.js';
import {gasInstrumentSections,renderPhysicalSections,type GasInstrumentFacts} from './physical-instrument.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Tab='overview'|'delivery'|'cash';
const tradeLayout=():GridLayout=>({...emptyGridLayout(),sort:[{key:'trade',direction:'asc' as const}]});
const commandAllowed=(c:RequestCommand)=>c.path.startsWith('gas/operations/')||['gas/quantities/record','gas/billing/determine'].includes(c.path)||isCrudeOperation(c.path);

/** A separate operational workspace. Discovery reads booking facts, never PV;
 * commodity modules own interpretation, preview, persistence and authority. */
export function setupPhysicalOperations(root:HTMLElement,o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string;context:()=>Record<string,unknown>;zone:()=>string;permission:(name:string)=>boolean;changed:()=>void}){
 root.tabIndex=-1;root.dataset.ownControls='true';root.classList.add('physical-operations');
 root.innerHTML=`<div id="${root.id}-prompt" class="side-panel-prompt" role="alertdialog" hidden></div>
 <section class="operations-directory" aria-label="Physical trades"><div class="operations-directory-heading"><div><h2>Physical trades</h2><p>Select a row to open its delivery work. Sort columns or filter the booked facts below.</p></div><button id="physical-operations-refresh" class="secondary" type="button"><span aria-hidden="true">↻</span> Refresh trades</button></div><div id="physical-operations-trades"></div><p class="operations-roadmap">Additional workflows <span>Power · TBD</span><span>NGLs · TBD</span><span>Storage · TBD</span><span>Rail / marine / truck · TBD</span></p></section>
 <div id="physical-operations-pending" class="operations-recovery" hidden><div><h3>Check the last request</h3><p id="physical-operations-pending-text" role="status"></p></div><div class="operations-recovery-actions"><button id="physical-operations-status" type="button">Check status</button><button id="physical-operations-retry" class="secondary" type="button">Retry original request</button></div></div>
 <header id="physical-operations-instrument" class="operations-instrument" hidden><div><p id="physical-operations-family" class="operations-eyebrow"></p><h2 id="physical-operations-reference"></h2><p id="physical-operations-market"></p></div><dl><div><dt>Book</dt><dd id="physical-operations-book"></dd></div><div><dt>Delivery window</dt><dd id="physical-operations-period"></dd></div></dl></header>
 <p id="physical-operations-message" role="status"></p><div id="physical-operations-tabs" class="side-panel-tabs"></div><div id="physical-operations-view"></div>`;
 const get=<T extends HTMLElement>(id:string)=>root.querySelector<T>('#physical-operations-'+id)!;
 const tradeHost=get('trades'),content=get('view'),message=get('message');
 let deliveryGrid:GridHandle|undefined;
 let grid:GridHandle|undefined,layout=tradeLayout(),filters:{query:string;columns:(string|null)[]}|undefined;
 let tabStrip:ReturnType<typeof setupTabs>|undefined;
 let positions:PositionFact[]=[],selected:PositionFact|undefined,tab:Tab='overview',generation=0,reading=false,identity='',asking:AbortController|undefined;
 let gas:ReturnType<typeof setupGasOperations>|undefined,billing:ReturnType<typeof setupGasBilling>|undefined,dispose:(()=>void)|undefined;
 const pending=new PendingCommand(knownTradeRejection),transports=new Map<string,ReturnType<typeof actorTransport<RequestCommand>>>();
 function transport(){const actor=o.identity();let t=transports.get(actor);if(!t)transports.set(actor,t=actorTransport<RequestCommand>(()=>'physical-operations-request:'+actor,()=>sessionStorage));return t;}
 function restore(){if(identity===o.identity())return;pending.clear();identity=o.identity();layout=tradeLayout();filters=undefined;const saved=restoreTradeRequest(transport().read());if(saved&&commandAllowed(saved))pending.command={...saved,uncertain:true};}
 function store(){transport().write(pending.command);o.changed();}
 function current(epoch:number,actor:string){return epoch===generation&&actor===o.identity();}
 const busy=()=>reading||o.busy();
 const gasContext=()=>({...o.context(),convention:'physical_gas_fixed_quantity_v4'});
 const writableContext=()=>{try{return o.context().knowledgeMode==='current';}catch{return false;}};
 const edited=()=>!!gas?.unsaved()||!!billing?.unsaved()||crude.unsaved();
 const workflow:GasBillingWorkflow={busy,pending:()=>pending.command,changed:o.changed,queue};
 const crude=setupCrudeOperations({api:o.api,act:o.act,busy,context:o.context,actor:o.identity,zone:o.zone,onDenied:()=>{clearWidgets();content.replaceChildren();},queue,pending:()=>pending.command,host:()=>content,unsavedChanged:o.changed});
 function clearWidgets(){deliveryGrid?.destroy();deliveryGrid=undefined;dispose?.();dispose=undefined;gas?.destroy();gas=undefined;billing?.destroy();billing=undefined;crude.reset();content.replaceChildren();}
 function clearInstrument(){get('instrument').hidden=true;for(const name of ['family','reference','market','book','period'])get(name).textContent='';clearTabs(get('tabs'),content);get('tabs').hidden=true;}
 function sync(){restore();gas?.sync();billing?.sync();crude.sync();const blocked=busy()||!!pending.command;tradeHost.inert=blocked;get<HTMLButtonElement>('refresh').disabled=blocked;
  get('pending').hidden=!pending.command;if(pending.command)get('pending-text').textContent=retainedText(pending.command);
  const allowed=pending.command&&retryAllowed(pending.command.path,{canAmendTrades:o.permission('AmendTrade'),canConfigureGasServices:o.permission('ReadGasOperations')&&o.permission('ConfigureGasServices'),canRecordGasOperations:o.permission('ReadGasOperations')&&o.permission('RecordGasOperations')&&o.permission('AmendTrade')});
  get<HTMLButtonElement>('retry').disabled=busy()||!pending.command||!allowed||!writableContext();get<HTMLButtonElement>('status').disabled=busy()||!pending.command;
  for(const b of get('tabs').querySelectorAll<HTMLButtonElement>('button'))b.disabled=busy();
 }
 async function leave(){if(!edited())return true;if(asking)return false;const controller=asking=new AbortController();try{const go=await askInPanel(root,{text:'This physical operation has unsaved edits. Leaving discards those edits; an unconfirmed request remains retained.',keep:'Keep editing',go:'Discard changes'})(controller.signal);if(go)clearWidgets();return go;}finally{if(asking===controller)asking=undefined;}}
 async function queue(command:RequestCommand){restore();if(pending.command)throw Error('Reconcile the retained physical request first.');if(!commandAllowed(command))throw Error('Choose a supported physical operation.');pending.command=structuredClone(command);store();sync();await send(pending.command);}
 async function accepted(command:RequestCommand){if(pending.command!==command)return;pending.clear();store();message.textContent='Operation recorded. The selected view is refreshed under the header context.';await mount();}
 async function send(command:RequestCommand){const actor=o.identity(),epoch=generation,uncertain=pending.sending();store();sync();try{const outcome=await o.api<{operationId?:string}>(command.path,{...command.body,operationId:command.operationId});requirePhysicalReceipt(command,outcome);if(current(epoch,actor))await accepted(command);}catch(error){if(current(epoch,actor)){if(releasesRetained(command.path,(error as {code?:string}).code,uncertain)){pending.clear();store();}message.textContent=String(error);}throw error;}finally{sync();}}
 async function check(){const command=pending.command;if(!command)return;const actor=o.identity(),epoch=generation;const outcome=await o.api<{found?:boolean}>(tradeStatusPath(command.path),{operationId:command.operationId});if(!current(epoch,actor)||pending.command!==command)return;if(outcome.found===false){message.textContent='No committed outcome is available. Retry the retained original request.';return;}requirePhysicalReceipt(command,outcome);await accepted(command);}
 function clearGrid(keep=false){if(keep&&grid){filters=grid.state();if(grid.ready())layout=grid.layout();}grid?.destroy();grid=undefined;tradeHost.replaceChildren();}
 async function chooseTrade(index:number){
  const next=positions[index],epoch=generation,actor=o.identity();if(!next||busy()||pending.command)return false;
  if(selected===next)return true;if(!await leave()||!current(epoch,actor))return false;
  selected=next;tab='overview';await mount();if(actor!==o.identity()||selected!==next)return false;grid?.selectRow(index);return true;
 }
 function drawTrades(){const trades=positions,model=physicalTradeGrid(trades);
  grid=renderDataGrid(tradeHost,'physical-operations-trades',o.identity(),model.headers,model.rows,model.numeric,()=>busy()||!!pending.command,{
   keys:model.keys,rowKeys:model.rowKeys,displayRows:model.displayRows,initial:filters,layout,onLayout:next=>layout=next,onRowOpen:chooseTrade,onRowSelect:chooseTrade,
   cells:(row,column)=>column===1?operationsTag(model.rows[row][1],physicalProduct(trades[row].instrumentType).tone):column===8?operationsTag(model.rows[row][8],trades[row].instrumentType==='physical_lng'?'tbd':'neutral'):null,
  });
 }
 function tabs(){const available:[string,string][]=[['overview','Overview']];if(selected?.instrumentType==='physical_gas')available.push(['delivery','Delivery'],['cash','Billing & corrections']);else if(selected?.instrumentType==='physical_crude')available.push(['delivery','Delivery & cash']);else if(selected?.instrumentType==='physical_lng')available.push(['delivery','Delivery']);if(!available.some(([id])=>id===tab))tab='overview';tabStrip=setupTabs(get('tabs'),available,{label:'Physical instrument operations',panel:content,selected:tab,select:id=>{tabStrip?.mark(tab);void(async()=>{const epoch=generation,actor=o.identity();if(busy()||!await leave()||!current(epoch,actor))return;tab=id as Tab;await mount();})();}});}
 function showDelivery(value:unknown,p:PositionFact){
  const view=readPhysicalDelivery(value,{tradeId:p.tradeId,bindingId:p.bindingId,revision:p.revision});
  const section=document.createElement('section');section.className='operations-deliveries';section.innerHTML='<h3>Delivery intervals</h3><div data-delivery="capabilities"></div><div data-delivery="rows"></div><p class="operations-grid-note">Exact contractual interval totals. Operational recording is available only through the supported workflows below.</p>';
  renderDeliveryCapabilities(section.querySelector('[data-delivery=capabilities]')!,view);content.append(section);
  deliveryGrid=renderPhysicalDeliveryGrid(section.querySelector('[data-delivery=rows]')!,'physical-delivery-intervals',o.identity(),view,o.zone(),busy,{layout:emptyGridLayout(),onLayout(){}});
 }
 async function mount(){clearWidgets();if(!selected){clearInstrument();message.textContent='';content.innerHTML='<div class="operations-empty"><span aria-hidden="true">⇄</span><h3>Choose an instrument to begin</h3><p>Select a trade in the grid above to view its delivery work.</p></div>';sync();return;}
  tabs();get('tabs').hidden=false;get('instrument').hidden=false;get('family').textContent=physicalProduct(selected.instrumentType).label;get('reference').textContent=selected.tradeReference;get('market').textContent=selected.marketLabel??'';get('book').textContent=selected.book;get('period').textContent=selected.deliveryFrom&&selected.deliveryTo?`${selected.deliveryFrom} → ${selected.deliveryTo} · exclusive`:'Not available';
  const p=selected,epoch=++generation,actor=o.identity();message.textContent='';reading=true;sync();
  try{const context=structuredClone(o.context());if(tab==='delivery'&&p.instrumentType==='physical_gas'){gas=setupGasOperations(content,{api:o.api,context:gasContext,tradeId:p.tradeId,actor:o.identity,zone:o.zone,workflow});const allocations=await o.api<{facilityAllocations:FacilityAllocationTrack[]}>('trading/gas',{context,tradeId:p.tradeId,bindingId:p.bindingId,revision:p.revision,instrumentType:p.instrumentType});if(current(epoch,actor)&&allocations.facilityAllocations?.length)gas.afterFlowHost.append(facilityAllocationTrack(allocations.facilityAllocations));}
   else if(tab==='cash'&&p.instrumentType==='physical_gas')billing=setupGasBilling(content,{api:o.api,context:gasContext,tradeId:p.tradeId,actor:o.identity,zone:o.zone,workflow});
   else if(tab==='delivery'&&(p.instrumentType==='physical_crude'||p.instrumentType==='physical_lng')){
    const path=p.instrumentType==='physical_crude'?'trading/crude':'trading/lng';
    const facts=await o.api<{delivery:PhysicalDeliveryView}>(path,{context,tradeId:p.tradeId,bindingId:p.bindingId,revision:p.revision,instrumentType:p.instrumentType});
    if(!current(epoch,actor))return;showDelivery(facts.delivery,p);if(p.instrumentType==='physical_crude')await crude.open(p.tradeId);
   }
   else{const sections:Parameters<typeof renderPhysicalSections>[1][number][]=[];
    if(p.instrumentType==='physical_gas'){const facts=await o.api<GasInstrumentFacts>('trading/gas',{context,tradeId:p.tradeId,bindingId:p.bindingId,revision:p.revision,instrumentType:p.instrumentType});if(!current(epoch,actor))return;if(Number(facts.head.revision)!==p.revision||facts.head.instrument_id!==p.bindingId)throw Error('The physical revision changed. Refresh the directory.');sections.push(...gasInstrumentSections(facts,o.zone()));}
    else if(p.instrumentType==='physical_crude'){const facts=await o.api<CrudeInstrumentFacts>('trading/crude',{context,tradeId:p.tradeId,bindingId:p.bindingId,revision:p.revision,instrumentType:p.instrumentType});if(!current(epoch,actor))return;sections.push(...crudeInstrumentSections(facts,o.zone()));}
    else if(p.instrumentType==='physical_lng'){const facts=await o.api<LngInstrumentFacts>('trading/lng',{context,tradeId:p.tradeId,bindingId:p.bindingId,revision:p.revision,instrumentType:p.instrumentType});if(!current(epoch,actor))return;sections.push(...lngInstrumentSections(facts,o.zone()),{title:'Operational workflow',rows:[['LNG actuals and reconciliation','TBD']]});}
    if(current(epoch,actor))dispose=renderPhysicalSections(content,sections);
   }
  }catch(error){if(current(epoch,actor)){clearWidgets();message.textContent=String(error);}}finally{if(current(epoch,actor)){reading=false;sync();}}
 }
 async function load(){restore();const previous=selected&&physicalTradeKey(selected),actor=o.identity(),epoch=++generation;clearWidgets();clearGrid(true);positions=[];selected=undefined;clearInstrument();tradeHost.textContent='Loading physical trades…';reading=true;sync();try{
   const context=structuredClone(o.context());let facts=readPositionFacts(await o.api('physical/operations/directory',{context,bookIds:[],portfolioIds:[],offset:0,limit:64}),64);if(!current(epoch,actor))return;
   while(facts.nextOffset!==null){const offset=facts.nextOffset;facts=appendPositionFacts(facts,await o.api('physical/operations/directory',{context:{...context,knowledgeMode:'retained',contextId:facts.context.contextId},bookIds:[],portfolioIds:[],offset,limit:64}),64);if(!current(epoch,actor))return;}
   positions=facts.positions;selected=positions.find(p=>physicalTradeKey(p)===previous)??(positions.length===1?positions[0]:undefined);drawTrades();reading=false;await mount();if(selected)grid?.selectRow(positions.indexOf(selected));
  }catch(error){if(current(epoch,actor)){positions=[];selected=undefined;filters=undefined;clearGrid();message.textContent=String(error);}}finally{if(current(epoch,actor)){reading=false;sync();}}}
 get('refresh').onclick=()=>void o.act(async()=>{if(await leave())await load();});get('retry').onclick=()=>void o.act(async()=>{if(pending.command)await send(pending.command);});get('status').onclick=()=>void o.act(check);
 return{load,sync,leave,edited:()=>edited()||!!pending.command,selected:()=>selected,reset(){generation++;reading=false;asking?.abort();asking=undefined;clearWidgets();positions=[];selected=undefined;tab='overview';pending.clear();identity='';clearGrid();filters=undefined;clearInstrument();message.textContent='';},async refresh(){if(await leave())await load();}};
}
