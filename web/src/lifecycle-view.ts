import {cloneRead} from './read-references.js';
import {displayPaymentDate} from './payment-date.js';
import {renderDataGrid,emptyGridLayout,type GridHandle,type GridLayout} from './data-grid.js';
import {actorTransport,PendingCommand,type RequestCommand} from './pending-command.js';
import {askInPanel} from './side-panel.js';
import {zonedInput} from './user-time.js';
import {mountLifecycleSchedule} from './lifecycle-scheduling.js';
import {mountLifecycleSettings} from './lifecycle-settings.js';
import {mountGasOwnedAction,mountGasOwnedSettings,gasSetupPath,type GasOwnedSetup,type GasOwnedUiOptions} from './gas-owned-lifecycle.js';
import {mountTransportFinancialCommands,requireTransportFinancialReceipt} from './gas-transport-financial-lifecycle.js';
import {overviewTermSections,renderPhysicalSections,type PhysicalOverviewSection} from './physical-instrument.js';
import {custodyReview} from './gas-custody-review.js';
import {mountCustodyObservation,mountCustodyPayment,mountTransportPayment} from './gas-custody-lifecycle.js';
import {loanFeeReview} from './gas-loan-fee-review.js';
import {loanQuantityReview} from './gas-loan-quantity-review.js';
import {operatorQuantityReview} from './gas-operator-quantity-review.js';
import {lifecycleKey,lifecycleRows,lifecycleReason,lifecycleRequest,lifecycleChangesFinancialSource,restoreLifecycleRequest,lifecycleOperationId,type LifecycleDiscovery,type LifecycleOffer,type LifecycleDraft,type LifecycleRequest} from './lifecycle-model.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Review={hash:string;offer:LifecycleOffer;details:Record<string,unknown>};
const words=(value:string)=>value.replaceAll('_',' ').replaceAll('.',' · ').replace(/([a-z])([A-Z])/g,'$1 $2');
const title=(value:string)=>{const text=words(value);return text.charAt(0).toUpperCase()+text.slice(1);};
const gasHandler=(handler:string)=>['gas_owned','gas_park_loan','gas_park_custody','gas_transport_financial','gas_futures_delivery','gas_futures_reconciliation','gas_futures_reconciliation_batch','gas_owned_quantity_reconciliation','gas_quantity_bundle','gas_loan_fee_amendment','gas_loan_quantity_reconciliation','gas_operator_quantity_reconciliation'].includes(handler);
const knownRejection=(code:string|undefined)=>!!code&&/^(lifecycle_|option_|futures_|swap_|gas_|cash_|settlement_|historical_|job_|access_denied$|request_denied$)/.test(code);
const element=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};

export function setupLifecycle(root:HTMLElement,o:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string;context:()=>Record<string,unknown>;historical:()=>boolean;zone:()=>string;permission:(name:string)=>boolean;changed:()=>void;committed?:()=>void|Promise<void>}){
 root.dataset.ownControls='true';root.classList.add('lifecycle-workspace');
 root.innerHTML=`<div id="${root.id}-prompt" class="side-panel-prompt" role="alertdialog" hidden></div>
 <div class="operations-directory-heading"><div><h2>Lifecycle actions</h2><p>Actions and settings at the selected market close.</p></div><button data-lifecycle="refresh" type="button" class="secondary">Refresh</button></div>
 <div class="lifecycle-tools"><label><input data-lifecycle="inactive" type="checkbox"> Show closed and inactive actions</label><div><button data-lifecycle="products" type="button" class="secondary">Product settings</button> <button data-lifecycle="gas-settings" type="button" class="secondary">Gas ownership settings</button> <button data-lifecycle="settings" type="button" class="secondary">Action policies</button></div></div>
 <div data-lifecycle="pending" class="operations-recovery" hidden><p data-lifecycle="pending-text" role="status"></p><button data-lifecycle="check-status" type="button" class="secondary" hidden>Check recorded outcome</button><button data-lifecycle="retry" type="button">Retry original request</button></div>
 <p data-lifecycle="message" role="status"></p><div data-lifecycle="rows"></div><section data-lifecycle="panel" class="lifecycle-panel" aria-live="polite"></section>`;
 const get=<T extends HTMLElement>(name:string)=>root.querySelector<T>(`[data-lifecycle="${name}"]`)!;
 const panel=get('panel'),message=get('message'),host=get('rows');
 const pending=new PendingCommand(knownRejection),transports=new Map<string,ReturnType<typeof actorTransport<RequestCommand>>>();
 let identity='',generation=0,draftVersion=0,panelVersion=0,reading=false,saving=false,edited=false,discovery:LifecycleDiscovery|undefined,selected:LifecycleOffer|undefined,shown:LifecycleOffer[]=[],reviewed:{request:LifecycleRequest;review:Review}|undefined;
 let grid:GridHandle|undefined,layout:GridLayout=emptyGridLayout(),filters:{query:string;columns:(string|null)[]}|undefined,asking:AbortController|undefined;
 let timeControls:ReturnType<typeof zonedInput>[]=[];
 let productSettings:{edited:()=>boolean;refresh:()=>void}|undefined;
 const busy=()=>o.busy()||reading||saving;
 const instant=(at:string)=>new Intl.DateTimeFormat(undefined,{timeZone:o.zone(),dateStyle:'medium',timeStyle:'short'}).format(new Date(at));
 function transport(){const actor=o.identity();let value=transports.get(actor);if(!value)transports.set(actor,value=actorTransport<RequestCommand>(()=>'lifecycle-request:'+actor,()=>sessionStorage));return value;}
 function restore(){if(identity===o.identity())return;pending.clear();identity=o.identity();layout=emptyGridLayout();filters=undefined;const saved=restoreLifecycleRequest(transport().read());if(saved)pending.command={...saved,uncertain:true};}
 function store(){transport().write(pending.command);o.changed();}
 const current=(epoch:number,actor:string)=>generation===epoch&&o.identity()===actor;
 const canExecute=()=>!!selected&&!!discovery?.choices.actions.some(a=>a.code===selected!.action&&o.permission(a.executionPermission));
 function invalidate(){draftVersion++;reviewed=undefined;edited=true;o.changed();panel.querySelector('[data-lifecycle-review]')?.remove();sync();}
 function field(label:string,input:HTMLElement,parent:HTMLElement){const wrapper=element('label',label);wrapper.append(input);parent.append(wrapper);return input;}
 function choices(label:string,values:{value:string;label:string}[],parent:HTMLElement,value=''){const input=element('select');input.required=true;input.append(new Option('Choose…',''),...values.map(v=>new Option(v.label,v.value)));input.value=value;field(label,input,parent);return input;}
 function textField(label:string,parent:HTMLElement,value='',type='text'){const input=element('input');input.type=type;input.value=value;field(label,input,parent);return input;}
 function timeField(label:string,parent:HTMLElement){const input=textField(label,parent,'','datetime-local');input.step='1';const control=zonedInput(input,o.zone,o.api);timeControls.push(control);const zone=element('small',o.zone());input.parentElement!.append(zone);return{input,control};}
 function clearPanel(){draftVersion++;panelVersion++;productSettings=undefined;panel.replaceChildren();timeControls=[];reviewed=undefined;edited=false;selected=undefined;o.changed();}
 function sync(){restore();get('pending').hidden=!pending.command;if(pending.command)get('pending-text').textContent=`${pending.command.label??'Lifecycle change'} has an unconfirmed outcome. Retry sends the original request.`;
  get('check-status').hidden=!receiptStatusPath(pending.command);get<HTMLButtonElement>('check-status').disabled=busy()||!pending.command;
  get<HTMLButtonElement>('retry').disabled=busy()||!pending.command||o.historical();get<HTMLButtonElement>('refresh').disabled=busy()||!!pending.command;get<HTMLButtonElement>('settings').disabled=busy()||!!pending.command||!discovery;
  get<HTMLButtonElement>('products').disabled=busy()||!!pending.command||!discovery;
  get<HTMLButtonElement>('gas-settings').hidden=!o.permission('ReadGasOwnedHoldings');get<HTMLButtonElement>('gas-settings').disabled=busy()||!!pending.command||!discovery;
  get<HTMLInputElement>('inactive').disabled=busy()||!!pending.command;host.inert=busy()||!!pending.command;
  const denied=(node:HTMLElement)=>{const permission=node.closest<HTMLElement>('[data-lifecycle-permission]')?.dataset.lifecyclePermission;return permission!==undefined&&(!permission||!o.permission(permission));};
  for(const button of panel.querySelectorAll<HTMLButtonElement>('button'))button.disabled=busy()||!!pending.command||o.historical()||denied(button);
  const execute=panel.querySelector<HTMLButtonElement>('[data-lifecycle-execute]');if(execute)execute.disabled=busy()||!!pending.command||!reviewed||o.historical()||!canExecute();
  for(const form of panel.querySelectorAll('form'))form.inert=busy()||!!pending.command||o.historical()||denied(form);
 }
 async function confirmDiscard(){if(asking)return false;const controller=asking=new AbortController();try{return await askInPanel(root,{text:'Discard the lifecycle edits? An unconfirmed request remains retained.',keep:'Keep editing',go:'Discard edits'})(controller.signal);}finally{if(asking===controller)asking=undefined;}}
 async function leave(){if(!edited&&!productSettings?.edited())return true;const go=await confirmDiscard();if(go)clearPanel();return go;}
 function clearGrid(){if(grid){filters=grid.state();if(grid.ready())layout=grid.layout();grid.destroy();grid=undefined;}host.replaceChildren();}
 function draw(){clearGrid();shown=(discovery?.actions??[]).filter(offer=>get<HTMLInputElement>('inactive').checked||!['option_holding_closed','lifecycle_action_window','option_instruction_cutoff','option_instruction_closed','option_exercise_window'].includes(offer.reason??''));
  if(!shown.length){host.textContent='No lifecycle actions are available in this context.';return;}
  const rows=lifecycleRows(shown,instant);
  grid=renderDataGrid(host,'lifecycle-actions',o.identity(),['Holding','Product','Book','Action','Event time','Status'],rows,[],busy,{keys:['holding','product','book','action','at','status'],rowKeys:shown.map(lifecycleKey),layout,initial:filters,onLayout:next=>layout=next,onRowOpen:async index=>{if(!await leave())return false;showAction(shown[index]);return true;},cells:(index,column)=>{
   if(shown[index].eligible)return null;const text=element('span',rows[index][column]);text.className='lifecycle-inactive';text.title=lifecycleReason(shown[index].reason);return text;
  }});
 }
 function receiptStatusPath(command:RequestCommand|undefined){return command?.path==='gas/transport/financial/record'?'gas/transport/financial/status':command?.path==='gas/park-custody/evidence'?'gas/park-custody/evidence/status':command?.path==='cash-payments/record'?'cash-payments/status':undefined;}
 function requireReceipt(command:RequestCommand,outcome:Record<string,unknown>){
  if(outcome.operationId!==command.operationId)throw Error('The reply did not identify the retained request. Retry the original request.');
  if(command.path==='gas/transport/financial/record')requireTransportFinancialReceipt(command,outcome);
  if(command.path==='cash-payments/record'&&(outcome.tradeId!==command.body.tradeId||outcome.bindingId!==command.body.bindingId||outcome.allocationId!==command.body.allocationId||typeof outcome.revision!=='number'||!Number.isSafeInteger(outcome.revision)||outcome.revision<1))
   throw Error('The reply did not identify this retained cash owner and payment allocation. Retry the original request.');
  if(command.path==='gas/park-custody/evidence'){
   const target=command.body.target as LifecycleOffer['target']|undefined;
   if(target?.handler!=='gas_park_custody'||outcome.tradeId!==target.scopeId||outcome.instrumentId!==target.bindingId||outcome.action!=='evidence')throw Error('The reply did not identify this exact retained custody observation and agreement.');
  }
 }
 async function checkStatus(){
  const command=pending.command,path=receiptStatusPath(command);if(!command||!path)return;
  const financial=command.path==='gas/transport/financial/record',label=financial?'Transport financial operation':command.path==='cash-payments/record'?'Recorded cash payment':'Custody observation';
  const actor=o.identity(),epoch=generation;saving=true;sync();let confirmed=false;
  try{
   const reply=await o.api<{found:boolean;outcome?:Record<string,unknown>}>(path,financial?{operationId:command.operationId,ownerId:command.body.ownerId}:cloneRead(command.body));
   if(!current(epoch,actor)||pending.command!==command)return;
   if(reply.found===false){message.textContent='No recorded outcome was found. The original request remains retained for an authorized retry.';return;}
   if(reply.found!==true||!reply.outcome)throw Error('Check the exact retained request outcome again.');
   requireReceipt(command,financial?reply:reply.outcome);confirmed=true;pending.clear();store();clearPanel();await o.committed?.();
   if(!current(epoch,actor))return;
   message.textContent=label+' confirmed from its recorded outcome.';saving=false;await load(false);
  }catch(error){if(current(epoch,actor))message.textContent=confirmed?label+' recorded. Its updated views could not be read. '+String(error):String(error);throw error;}
  finally{if(o.identity()===actor){saving=false;sync();}}
 }
 async function send(command:RequestCommand){const actor=o.identity(),epoch=generation,wasUncertain=pending.sending();let confirmed=false;saving=true;store();sync();try{
   const outcome=await o.api<{operationId?:string;changed?:boolean;semanticAcknowledgement?:boolean;acknowledgedRevision?:number;acknowledgedVersion?:number;currentRevision?:number;currentVersion?:number}>(command.path,command.body);
   requireReceipt(command,outcome);
   if(!current(epoch,actor))return;confirmed=true;pending.clear();store();clearPanel();
   if(lifecycleChangesFinancialSource(command))await o.committed?.();
   if(!current(epoch,actor))return;
   const accepted=outcome.acknowledgedRevision??outcome.acknowledgedVersion,head=outcome.currentRevision??outcome.currentVersion;
   message.textContent=command.path==='gas/transport/financial/record'?'Transport financial operation confirmed from its recorded outcome.':outcome.semanticAcknowledgement===true||outcome.changed===false?
    `Existing fact acknowledged${accepted!==undefined?' · revision '+accepted:''}${head!==undefined?' · current revision '+head+' retained':''}.`:command.path==='jobs/lifecycle/create'?'Lifecycle task retained. Its outcome is available in Jobs.':command.path==='cash-payments/record'?'Observed payment recorded.':'Lifecycle change recorded.';
   saving=false;await load(false);
  }catch(error){if(current(epoch,actor)){if(!confirmed){pending.failed((error as {code?:string}).code,wasUncertain);store();if(!pending.command)invalidate();message.textContent=String(error);}else message.textContent='Lifecycle change recorded. Its updated views could not be read. '+String(error);}throw error;}finally{if(o.identity()===actor){saving=false;sync();}}
 }
 async function queue(path:string,body:Record<string,unknown>,label:string){restore();if(pending.command)throw Error('Reconcile the retained lifecycle request first.');pending.command={path,operationId:lifecycleOperationId(path,body)??(()=>{throw Error('Retain the exact request identity.');})(),body:cloneRead(body),label};store();await send(pending.command);}
 function showReview(review:Review,request:LifecycleRequest,references:{label:string;value:string;key?:string}[]=[]){panel.querySelector('[data-lifecycle-review]')?.remove();const section=element('section');section.dataset.lifecycleReview='';section.className='lifecycle-review';section.append(element('h3','Review '+(selected?.label??'action')));
  const list=element('dl');list.className='lifecycle-review-facts';
  const add=(label:string,value:unknown)=>{if(value===null||value===undefined||value===''||typeof value==='object')return;list.append(element('dt',label),element('dd',String(value)));};
  add('Holding',selected?.subject?.reference);add('Business date',request.context.businessDate);add('As of',instant(request.context.asOf));
  for(const reference of references)if(request.target.handler!=='gas_operator_quantity_reconciliation'||reference.label==='Action policy')add(reference.label,reference.value);
  const d=review.details;for(const [key,label] of [['decision','Decision'],['openLots','Open lots'],['lots','Lots affected'],['futureLots','Linked futures lots'],['strike','Strike'],['settlement','Settlement'],['amount','Amount'],['roundedTotal','Rounded payment'],['exactTotal','Exact payment'],['reference','Evidence reference']] as const)add(label,d[key]);
  const financialReview=request.target.handler==='gas_park_custody'?custodyReview(d,instant):
   request.target.handler==='gas_loan_fee_amendment'?loanFeeReview(d,request.selection,instant):
   request.target.handler==='gas_loan_quantity_reconciliation'?loanQuantityReview(d,request.selection,instant):
    request.target.handler==='gas_operator_quantity_reconciliation'?operatorQuantityReview(d,request.selection,instant):undefined;
  if(financialReview)for(const[label,value]of financialReview.facts)add(label,value);
  if(gasHandler(request.target.handler)&&request.target.handler!=='gas_transport_financial'&&!financialReview){
   const effect=d.effects as Record<string,unknown>|undefined;add('Effect',d.willChange===false?'Existing fact acknowledged · no new effect':'Record observed facts');
   const correction=request.target.handler==='gas_futures_reconciliation';
   if(correction){add('Acknowledged financial revision',d.acknowledgedRevision);add('Current financial revision',d.currentRevision);}
   if(effect){for(const key of ['performedQuantity','quantitySpecification','stockQuantity','returnQuantity','outstandingPrincipal','feeAmount','cashAmount','currency'])add(title(key),effect[key]);
    for(const key of ['effectiveAt','returnAt','feeDueAt','cashDueAt'])if(typeof effect[key]==='string')add(title(key),instant(effect[key]));}
   if(request.action==='gas.park_loan.fee_determine'){
    const text=(key:string)=>{const value=effect?.[key];if(typeof value!=='string'||!value.trim())throw Error('Reload the complete cumulative fee review before recording it.');return value;};
    text('currency');
    add('Observed accrual through',instant(text('accruedThrough')));
    add('Previous rounded cumulative fee',text('previousCumulativeRoundedFee'));
    add('New rounded cumulative fee',text('cumulativeRoundedFee'));
    add('Signed fee contribution',text('independentCash'));
    add('Fee due',instant(text('dueAt')));
   }
   for(const key of ['quantity','amount','businessReference','businessKey','evidence'])add(correction&&d.willChange===false&&key==='quantity'?'Acknowledged quantity':title(key),request.selection[key]);
   for(const key of ['titleAt','availableAt','performedAt','paidAt','effectiveAt'])if(typeof request.selection[key]==='string')add(title(key),instant(request.selection[key] as string));
  }
  const paymentCurrency=d.currency as {code?:string}|undefined;add('Currency',paymentCurrency?.code?.toUpperCase());
  if(typeof d.effectiveAt==='string')add('Observed payment time',instant(d.effectiveAt));
  if(request.action==='swap.determine'&&d.calculation&&typeof d.calculation==='object'){
   const calculation=d.calculation as Record<string,unknown>,currency=calculation.currency as {code?:string}|undefined;
   add('New contractual total',calculation.amount);add('Previous contractual total',d.previousTotal);add('New contribution',d.proposedContribution);add('Currency',currency?.code?.toUpperCase());
   if(typeof calculation.paymentAt==='string')add('Contractual payment date',displayPaymentDate(calculation.paymentAt));
  }
  const net=request.action==='swap.net'?d.settlement as Record<string,unknown>|undefined:undefined;
  if(net){if(net.existingFact===true)add('Effect','Already recorded · no new cash or allocation');add('Net cash',net.amount);add('Gross paid',net.grossPaid);add('Gross received',net.grossReceived);add('Reverses payment',net.reversesId);}
  section.append(list);
  if(request.target.handler==='gas_transport_financial'){
   if(!Array.isArray(d.overviewSections)||!d.overviewSections.length)throw Error('Reread the complete reviewed Transport financial effect.');
   renderPhysicalSections(section,overviewTermSections(d.overviewSections as PhysicalOverviewSection[],o.zone()));
  }
  if(financialReview)for(const part of financialReview.tables){
   section.append(element('h4',part.label));
   if(!part.rows.length){section.append(element('p','None'));continue;}
   const table=element('table');table.className='regular-table';const head=element('tr');for(const label of part.columns)head.append(element('th',label));table.append(head);
   for(const values of part.rows){const row=element('tr');for(const value of values)row.append(element('td',value));table.append(row);}
   const scroll=element('div');scroll.className='lifecycle-review-table';scroll.append(table);section.append(scroll);
  }
  const bundle=request.target.handler==='gas_quantity_bundle';
  if((request.target.handler==='gas_owned_quantity_reconciliation'||bundle)&&d.outcome&&typeof d.outcome==='object'){
   const result=d.outcome as Record<string,unknown>,outcome=(bundle?result.ordinary:result) as Record<string,unknown>;
   const selectedRoot=(bundle?request.selection.ordinary:request.selection) as Record<string,unknown>;
   for(const [family,id,label]of [['positions','positionId','Owned title'],['consumptions','consumptionId','Recorded use']] as const){
    if(!Array.isArray(outcome[family])||!(outcome[family] as unknown[]).length)continue;
    const selected=Array.isArray(selectedRoot[family])?selectedRoot[family] as Record<string,unknown>[]:[];
    const table=element('table');table.className='regular-table';const head=element('tr');
    for(const caption of [label,'Effect','Acknowledged revision','Current revision','Reviewed quantity','Current quantity'])head.append(element('th',caption));table.append(head);
    for(const fact of outcome[family] as Record<string,unknown>[]){
     const row=element('tr'),selection=selected.find(s=>s[id]===fact[id]);
     for(const value of [references.find(r=>r.key===fact[id])?.value??fact[id],fact.changed===true?'Record correction':'Existing fact acknowledged',
      fact.acknowledgedRevision,fact.currentRevision,selection?.quantity,fact.currentQuantity])row.append(element('td',String(value??'')));
     table.append(row);
    }
    const scroll=element('div');scroll.className='lifecycle-review-table';scroll.append(table);section.append(scroll);
   }
  }
  if((request.target.handler==='gas_futures_reconciliation_batch'||bundle)&&Array.isArray(d.corrections)){
   const selectedRoot=(bundle?request.selection.physical:request.selection) as Record<string,unknown>;
   const selections=Array.isArray(selectedRoot.corrections)?selectedRoot.corrections as Record<string,unknown>[]:[];
   const table=element('table');table.className='regular-table';const head=element('tr');
   for(const label of ['Observed performance','Effect','Accepted revision','Current revision','Accepted quantity','Current quantity'])head.append(element('th',label));table.append(head);
   for(const correction of d.corrections as Record<string,unknown>[]){
    const selection=selections.find(s=>s.performanceId===correction.performanceId),row=element('tr');
    for(const value of [references.find(r=>r.key===correction.performanceId)?.value??correction.performanceId,
     correction.changed===true?'Record correction':'Existing fact acknowledged',correction.acknowledgedRevision,correction.currentRevision,selection?.quantity,correction.currentQuantity])row.append(element('td',String(value??'')));
    table.append(row);
   }
   const scroll=element('div');scroll.className='lifecycle-review-table';scroll.append(table);section.append(scroll);
  }
  const allocationGroups=(Array.isArray(d.sourceAllocations)?d.sourceAllocations:[d.sourceAllocations]) as ({sources?:Record<string,unknown>[]} | undefined)[];
  for(const sourceAllocations of allocationGroups)if(sourceAllocations?.sources?.length){
   section.append(element('h4','Observed source allocations'));
   const table=element('table');table.className='regular-table';const head=element('tr');
   for(const label of ['Source reference','Previous capacity','Current capacity','Previously allocated','Currently allocated','Previously unused','Currently unused','Unit'])head.append(element('th',label));table.append(head);
   for(const source of sourceAllocations.sources){const row=element('tr');const unit=source.quantity as {code?:string}|undefined;
    for(const value of [source.sourceKey,source.oldCapacity,source.capacity,source.oldAllocated,source.allocated,source.previouslyUnused,source.unused,unit?.code])row.append(element('td',String(value??'')));table.append(row);
   }
   const scroll=element('div');scroll.className='lifecycle-review-table';scroll.append(table);section.append(scroll);
  }
  if(net&&Array.isArray(net.allocations)){
   const table=element('table');table.className='regular-table';const head=element('tr');for(const label of ['Contribution','Gross allocation','Outstanding after settlement'])head.append(element('th',label));table.append(head);
   for(const allocation of net.allocations as Record<string,unknown>[]){const row=element('tr');for(const value of [selected?.inputChoices?.netDeterminationId?.find(choice=>choice.value===allocation.determinationId)?.label??allocation.determinationId,allocation.amount,allocation.remaining])row.append(element('td',String(value??'')));table.append(row);}section.append(table);
  }
  const marginRows=Array.isArray(d.marginRows)?d.marginRows:[];
  if(marginRows.length){const table=element('table');table.className='regular-table';const head=element('tr');for(const label of ['Occurrence','Exact variation','Rounded payment','Cash adjustment','Due','Currency'])head.append(element('th',label));table.append(head);
   for(const step of marginRows as Record<string,unknown>[]){const row=element('tr');for(const value of [typeof step.at==='string'?instant(step.at):'',step.exactVariation,step.variation,step.adjustment,typeof step.dueAt==='string'?instant(step.dueAt):'',String(step.currency??'').toUpperCase()])row.append(element('td',String(value??'')));table.append(row);}const scroll=element('div');scroll.className='lifecycle-review-table';scroll.append(table);section.append(scroll);
  }
  const principalValue=d.physicalPrincipal??d.principal;
  const principals=(Array.isArray(principalValue)?principalValue:[principalValue]) as ({positionId?:string;originalQuantity?:unknown;finalPrice?:unknown;currency?:{code?:string};principalDueAt?:string;performances?:Record<string,unknown>[]} | undefined)[];
  for(const principal of principals)if(principal){
   section.append(element('h4','Physical delivery principal'));
   const parent=references.find(reference=>reference.key===principal.positionId);
   if(parent){const label=element('p',parent.value);label.dataset.physicalParent='';section.append(label);}
   const currency=principal.currency?.code?.toUpperCase();
   const terms=element('p',`Original quantity ${String(principal.originalQuantity??'')} · final price ${String(principal.finalPrice??'')}${currency?' '+currency:''} · principal due ${principal.principalDueAt?instant(principal.principalDueAt):''}`);section.append(terms);
   if(principal.performances?.length){const table=element('table');table.className='regular-table';const head=element('tr');for(const label of ['Performance','Effective','Signed quantity','Principal','Adjustment'])head.append(element('th',label));table.append(head);
    for(const performance of principal.performances){const row=element('tr');for(const value of [performance.businessReference,typeof performance.at==='string'?instant(performance.at):'',performance.quantity,performance.contribution,performance.adjustment])row.append(element('td',String(value??'')));table.append(row);}section.append(table);
   }else section.append(element('p','No accepted performance: this final creates or updates the delivery obligation without recording title.'));
  }
  const execute=element('button','Record reviewed action');execute.type='button';execute.dataset.lifecycleExecute='';execute.onclick=()=>void o.act(async()=>{if(!reviewed)return;await queue('lifecycle/execute',reviewed.request,`${selected?.label??'Lifecycle action'} · ${selected?.subject?.reference??'holding'}`);});section.append(execute);panel.append(section);reviewed={request,review};sync();
 }
 function showAction(offer:LifecycleOffer){clearPanel();selected=offer;panel.append(element('h2',`${offer.label} · ${offer.subject?.reference??'Holding'}`));panel.append(element('p',lifecycleReason(offer.reason)));
  if(gasHandler(offer.target.handler)){void o.act(()=>showGasAction(offer));sync();return;}
  if(!offer.selection){const setup=element('button','Configure lifecycle settings');setup.type='button';setup.onclick=()=>showProductSettings(offer);panel.append(setup);sync();return;}
  if(offer.scheduleSettings&&discovery?.scheduling&&o.permission('RunJobs')&&canExecute()){
   const schedule=element('button','Schedule this occurrence');schedule.type='button';schedule.className='secondary';schedule.dataset.lifecyclePermission='RunJobs';
   schedule.onclick=()=>void o.act(async()=>{if(!await leave()||!discovery)return;clearPanel();selected=offer;const actor=o.identity(),epoch=generation,view=panelVersion;
    productSettings=mountLifecycleSchedule(panel,discovery,offer,{api:o.api,act:o.act,zone:o.zone,current:()=>current(epoch,actor)&&view===panelVersion,changed:o.changed,
     queue:async(path,body,label)=>{if(current(epoch,actor)&&view===panelVersion)await queue(path,body,label);}});sync();});panel.append(schedule);
  }
  if(!canExecute()){panel.append(element('p','Your current permissions allow inspection of this action. Recording it requires the action’s execution permission.'));sync();return;}
  const form=element('form');form.className='lifecycle-form';form.dataset.lifecyclePermission=discovery!.choices.actions.find(a=>a.code===offer.action)!.executionPermission;const fields=element('div');fields.className='schedule-fields';form.append(fields);panel.append(form);
  const policies=(discovery?.policies??[]).filter(p=>p.action===offer.action);
  const policy=choices('Action policy',policies.map(p=>({value:JSON.stringify([p.id,p.revision]),label:`${title(p.mode)} · ${instant(p.from_at)} to ${instant(p.until_at)} · revision ${p.revision}`})),fields,offer.policyId&&offer.policyRevision?JSON.stringify([offer.policyId,offer.policyRevision]):'');
  const inputs=new Map<string,HTMLInputElement|HTMLSelectElement>(),times=new Map<string,ReturnType<typeof zonedInput>>();
  const netLines:{determination:HTMLSelectElement;amount:HTMLInputElement}[]=[];
  let netMode:HTMLSelectElement|undefined,netAmount:HTMLInputElement|undefined,netReversal:HTMLSelectElement|undefined,paymentReversal:HTMLSelectElement|undefined;
  if(offer.requiredInputs.includes('netSettlement')){
   netMode=choices('Settlement type',[{value:'net',label:'Observed net settlement'},{value:'reversal',label:'Reverse an original net settlement'}],fields);netMode.dataset.netMode='';
   const body=element('div');fields.append(body);
   netMode.onchange=()=>{body.replaceChildren();netLines.length=0;netAmount=undefined;netReversal=undefined;
    if(netMode!.value==='net'){
     netAmount=textField('Observed signed net cash',body);netAmount.required=true;netAmount.dataset.netAmount='';
     body.append(element('p','Enter each observed gross allocation. The backend validates eligibility and the net cash total.'));
     const add=element('button','Add gross allocation');add.type='button';add.dataset.netAdd='';add.onclick=()=>{const row=element('div');row.className='schedule-fields';row.dataset.netLine='';const determination=choices('Contribution',offer.inputChoices?.netDeterminationId??[],row),amount=textField('Observed signed gross allocation',row);amount.required=true;const line={determination,amount};netLines.push(line);const remove=element('button','Remove');remove.type='button';remove.onclick=()=>{netLines.splice(netLines.indexOf(line),1);row.remove();invalidate();};row.append(remove);body.insertBefore(row,add);invalidate();};body.append(add);
    }else if(netMode!.value==='reversal')netReversal=choices('Original net settlement',offer.inputChoices?.reversesId??[],body);
    invalidate();
   };
  }
  for(const name of offer.requiredInputs){if(['policyId','correctionDue','netSettlement'].includes(name))continue;
   if(name==='correctionDueAt'){const timing=timeField('Correction due time (leave blank for an original determination)',fields);inputs.set(name,timing.input);times.set(name,timing.control);continue;}
   if(name==='effectiveAt'){const timing=timeField('Observed payment time',fields);inputs.set(name,timing.input);times.set(name,timing.control);continue;}
   const labels:Record<string,string>={evidenceId:'Observed evidence ID',lots:`Lots${offer.availableQuantity?' · open '+offer.availableQuantity:''}`,determinationId:'Determination',amount:'Observed signed payment',reference:'Payment reference'};
   const available=offer.inputChoices?.[name],input=available?choices(labels[name]??title(name),available,fields):textField(labels[name]??title(name),fields);
   input.required=true;inputs.set(name,input);
  }
  const reversals=offer.inputChoices?.paymentReversal;
  if(offer.action==='swap.payment'&&reversals?.length){
   paymentReversal=choices('Original payment to reverse',reversals,fields);paymentReversal.required=false;paymentReversal.dataset.paymentReversal='';paymentReversal.options[0].text='Enter a new observed payment';
   paymentReversal.onchange=()=>{const preset=paymentReversal!.value?JSON.parse(paymentReversal!.value) as {determinationId:string;amount:string;reversesId:string}:null;
    const determination=inputs.get('determinationId')!,amount=inputs.get('amount')!;determination.value=preset?.determinationId??'';amount.value=preset?.amount??'';determination.disabled=!!preset;(amount as HTMLInputElement).readOnly=!!preset;invalidate();};
  }
  const correctionRows:{select:HTMLSelectElement;time:ReturnType<typeof zonedInput>}[]=[];
  if(offer.requiredInputs.includes('correctionDue')){const box=element('fieldset'),legend=element('legend','Correction due times');box.append(legend,element('p','For a correction, include each affected occurrence whose cash changes. An original run needs no correction rows.'));
   const add=element('button','Add correction');add.type='button';add.className='secondary';add.onclick=()=>{const row=element('div');row.className='schedule-fields';const options=(discovery?.actions??[]).filter(a=>a.target.scopeId===offer.target.scopeId&&a.target.bindingId===offer.target.bindingId&&a.action===offer.action);const selected=choices('Occurrence',options.map(a=>({value:a.occurrenceId,label:instant(a.at)})),row);const timing=timeField('Correction due',row);const remove=element('button','Remove');remove.type='button';remove.onclick=()=>{correctionRows.splice(correctionRows.indexOf(item),1);row.remove();invalidate();};row.append(remove);box.insertBefore(row,add);const item={select:selected,time:timing.control};correctionRows.push(item);invalidate();};box.append(add);form.append(box);
  }
  const preview=element('button','Review action');preview.type='submit';form.append(preview);form.addEventListener('input',invalidate);form.addEventListener('change',invalidate);
  form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{if(!discovery)throw Error('Refresh lifecycle actions.');const source=discovery.context,actor=o.identity(),epoch=generation,version=draftVersion;const [policyId,policyRevision]=policy.value?JSON.parse(policy.value):['',0];const draft:LifecycleDraft={policyId,policyRevision,correctionDue:[]};
   if(netMode){if(netMode.value==='net'){draft.amount=netAmount!.value;draft.allocations=netLines.map(row=>({determinationId:row.determination.value,amount:row.amount.value}));draft.reversesId=null;}
    else if(netMode.value==='reversal'){draft.amount=null;draft.allocations=[];draft.reversesId=netReversal!.value;}else throw Error('Choose the observed settlement type.');}
   if(paymentReversal)draft.reversesId=paymentReversal.value?(JSON.parse(paymentReversal.value) as {reversesId:string}).reversesId:null;
   for(const [name,input] of inputs){(draft as unknown as Record<string,unknown>)[name]=times.has(name)?input.value?await times.get(name)!.instant():null:input.value;}
   for(const row of correctionRows)draft.correctionDue!.push({occurrenceId:row.select.value,dueAt:await row.time.instant()});
   if(!current(epoch,actor)||selected!==offer||version!==draftVersion)return;const request=lifecycleRequest(source,offer,draft,crypto.randomUUID());const result=await o.api<Review>('lifecycle/preview',request);if(!current(epoch,actor)||selected!==offer||version!==draftVersion)return;
   if(!result.offer||lifecycleKey(result.offer)!==lifecycleKey(offer)||result.offer.eligible!==true)throw Error('The review did not retain the selected native action and occurrence. Refresh and review again.');
   if(!/^[a-f0-9]{64}$/.test(result.hash))throw Error('The review did not retain its source hash.');request.previewHash=result.hash;showReview(result,request);
  });};sync();
 }
 function showPolicies(){clearPanel();if(!discovery)return;panel.append(element('h2','Action policy'));const form=element('form');form.className='lifecycle-form';form.dataset.lifecyclePermission='';const fields=element('div');fields.className='schedule-fields';form.append(fields);panel.append(form);
  const available=discovery.choices.actions.filter(a=>o.permission(a.configurationPermission));const action=choices('Action',available.map(a=>({value:a.code,label:title(a.code)})),fields);
  const saved=choices('Policy',[],fields),mode=choices('Execution',[],fields),from=timeField('Available from',fields),until=timeField('Available until (exclusive)',fields);
  function policyChoices(){form.dataset.lifecyclePermission=available.find(a=>a.code===action.value)?.configurationPermission??'';saved.replaceChildren(new Option('Create a new policy','new'),...(discovery?.policies??[]).filter(p=>p.action===action.value).map(p=>new Option(`${title(p.mode)} · ${instant(p.from_at)} to ${instant(p.until_at)} · revision ${p.revision}`,JSON.stringify([p.id,p.revision]))));saved.value='new';const automatic=available.find(a=>a.code===action.value)?.automatic;mode.replaceChildren(new Option('Choose…',''),...(discovery?.choices.modes??[]).filter(m=>m!=='automatic'||automatic&&o.permission('RunJobs')).map(m=>new Option(title(m),m)));from.control.clear();until.control.clear();invalidate();}
  action.onchange=policyChoices;saved.onchange=()=>{if(saved.value==='new'){from.control.clear();until.control.clear();mode.value='';return;}const [id,revision]=JSON.parse(saved.value),p=discovery?.policies.find(p=>p.id===id&&p.revision===revision);if(p){mode.value=p.mode;from.control.set(p.from_at);until.control.set(p.until_at);}invalidate();};
  const save=element('button','Save policy');save.type='submit';form.append(save);form.addEventListener('input',invalidate);form.addEventListener('change',invalidate);
  form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{if(!discovery)throw Error('Refresh lifecycle actions.');if(!action.value||!mode.value)throw Error('Choose the action and execution policy.');const actor=o.identity(),epoch=generation,version=draftVersion,implementation=discovery.choices.policyImplementation,[policyId,expectedRevision]=saved.value==='new'?[crypto.randomUUID(),0]:JSON.parse(saved.value);const body={operationId:crypto.randomUUID(),policyId,expectedRevision,action:action.value,mode:mode.value,fromAt:await from.control.instant(),untilAt:await until.control.instant(),implementation};if(!current(epoch,actor)||version!==draftVersion)return;await queue('lifecycle/policies/save',body,'Save '+title(action.value)+' policy');});};
  panel.append(element('p','Saving a policy does not assign it to a holding. Product settings select the exact saved policy version.'));edited=false;sync();
 }
 function showProductSettings(offer?:LifecycleOffer){clearPanel();if(!discovery)return;const actor=o.identity(),epoch=generation,view=panelVersion;
  productSettings=mountLifecycleSettings(panel,discovery,{api:o.api,act:o.act,zone:o.zone,permission:o.permission,current:()=>current(epoch,actor)&&view===panelVersion,confirmDiscard,changed:o.changed,
   queue:async(path,body,label)=>{if(current(epoch,actor)&&view===panelVersion)await queue(path,body,label);}},offer);sync();
 }
 function gasOptions(actor:string,epoch:number,view:number):GasOwnedUiOptions{return{api:o.api,act:o.act,zone:o.zone,permission:o.permission,current:()=>current(epoch,actor)&&view===panelVersion,changed:invalidate,confirmDiscard,
  queue:async(path,body,label)=>{if(current(epoch,actor)&&view===panelVersion)await queue(path,body,label);}};}
 async function gasSetup(target:LifecycleOffer['target']|null,actor:string,epoch:number,view:number){
  if(!discovery)throw Error('Refresh the gas source.');const source=cloneRead(discovery.context);
  const value=await o.api<GasOwnedSetup>(gasSetupPath(target?.handler),{context:source,target});
  if(!current(epoch,actor)||view!==panelVersion)return;
  if(!value.context||value.context.contextId!==source.contextId||value.context.asOf!==source.asOf||value.context.businessDate!==source.businessDate||value.context.snapshotId!==source.snapshotId||
   (target?value.target?.handler!==target.handler||value.target.scopeId!==target.scopeId||value.target.bindingId!==target.bindingId:value.target!=null))
   throw Error('The gas fields did not retain this exact source and native target. Refresh and review again.');return value;
 }
 async function showGasAction(offer:LifecycleOffer){
  const actor=o.identity(),epoch=generation,view=panelVersion,setup=await gasSetup(offer.target,actor,epoch,view);if(!setup||selected!==offer)return;
  const completed=offer.target.handler==='gas_park_custody'&&offer.eligible===false&&offer.reason==='gas_custody_complete';
  let actionEditor:ReturnType<typeof mountGasOwnedAction>|undefined;
  const financial=offer.target.handler==='gas_transport_financial';
  if(!completed&&(!financial||offer.eligible&&setup.forms[offer.action])){
   const form=element('form');form.className='lifecycle-form';form.dataset.lifecyclePermission=setup.forms[offer.action]?.permission??'';panel.append(form);
   const fields=element('div');fields.className='schedule-fields';form.append(fields);const editor=actionEditor=mountGasOwnedAction(fields,offer,setup,gasOptions(actor,epoch,view));
   const preview=element('button','Review action');preview.type='submit';form.append(preview);
   form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{
    const version=draftVersion,draft=await editor.read();if(!current(epoch,actor)||view!==panelVersion||selected!==offer||version!==draftVersion)return;
    const request:LifecycleRequest={operationId:crypto.randomUUID(),context:cloneRead(setup.context),target:cloneRead(offer.target),action:offer.action,
     policyId:draft.policyId,policyRevision:draft.policyRevision,automatic:false,previewHash:'0'.repeat(64),selection:draft.selection};
    const references=editor.references(),result=await o.api<Review>('lifecycle/preview',request);if(!current(epoch,actor)||view!==panelVersion||selected!==offer||version!==draftVersion)return;
    if(!result.offer||lifecycleKey(result.offer)!==lifecycleKey(offer)||result.offer.eligible!==true||!/^[a-f0-9]{64}$/.test(result.hash))throw Error('Review the exact gas target, occurrence and source again.');
    request.previewHash=result.hash;showReview(result,request,references);
   });};
  }
  const observation=offer.target.handler==='gas_park_custody'&&setup.configuration.evidence?mountCustodyObservation(panel,setup,gasOptions(actor,epoch,view)):undefined;
  const payment=offer.target.handler==='gas_park_custody'&&setup.configuration.payment?mountCustodyPayment(panel,setup,gasOptions(actor,epoch,view)):financial&&setup.configuration.payment?mountTransportPayment(panel,setup,gasOptions(actor,epoch,view)):undefined;
  const commands=financial?mountTransportFinancialCommands(panel,setup,gasOptions(actor,epoch,view)):undefined;
  productSettings={edited:()=>!!actionEditor?.dirty()||!!observation?.edited()||!!payment?.edited()||!!commands?.edited(),refresh:()=>{actionEditor?.refresh();observation?.refresh();payment?.refresh();commands?.refresh();}};sync();
 }
 async function showGasSettings(){
  clearPanel();const actor=o.identity(),epoch=generation,view=panelVersion,setup=await gasSetup(null,actor,epoch,view);if(!setup)return;
  productSettings=mountGasOwnedSettings(panel,setup,gasOptions(actor,epoch,view));edited=false;sync();
 }
 async function load(announce=true){restore();const actor=o.identity(),epoch=++generation;reading=true;clearPanel();clearGrid();discovery=undefined;if(announce)message.textContent='Loading lifecycle actions…';sync();try{
   const value=await o.api<LifecycleDiscovery>('lifecycle/view',o.context());if(!current(epoch,actor))return;discovery=value;draw();if(announce)message.textContent='';
  }catch(error){if(current(epoch,actor)){message.textContent=String(error);host.textContent='';}throw error;}finally{if(current(epoch,actor)){reading=false;sync();}}
 }
 get('refresh').onclick=()=>void o.act(async()=>{if(await leave())await load();});get('settings').onclick=()=>void o.act(async()=>{if(await leave())showPolicies();});get('products').onclick=()=>void o.act(async()=>{if(await leave())showProductSettings();});get<HTMLInputElement>('inactive').onchange=draw;
 get('gas-settings').onclick=()=>void o.act(async()=>{if(await leave())await showGasSettings();});
 get('check-status').onclick=()=>void o.act(checkStatus);
 get('retry').onclick=()=>void o.act(async()=>{if(pending.command)await send(pending.command);});
 return {load,sync,leave,edited:()=>edited||!!productSettings?.edited()||!!pending.command,shown:()=>selected?{target:selected.target,action:selected.action}:undefined,
  reset(){generation++;reading=false;saving=false;asking?.abort();asking=undefined;clearGrid();clearPanel();discovery=undefined;pending.clear();identity='';message.textContent='';},
  async refresh(){for(const time of timeControls)time.refresh();productSettings?.refresh();if(await leave())await load();}};
}
