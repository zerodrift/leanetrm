import {cloneRead} from './read-references.js';
import {mountOperationFields} from './operation-fields.js';
import {overviewTermSections,renderPhysicalSections,type PhysicalOverviewSection} from './physical-instrument.js';
import {sameUtcInstant} from './user-time.js';
import type {GasOwnedSetup,GasOwnedUiOptions} from './gas-owned-lifecycle.js';
import type {LifecycleContext} from './lifecycle-model.js';
import type {RequestCommand} from './pending-command.js';

const object=(value:unknown):value is Record<string,unknown>=>!!value&&typeof value==='object'&&!Array.isArray(value);
const canonical=(value:unknown):string=>JSON.stringify(value,(_key,item)=>object(item)?Object.fromEntries(Object.entries(item).sort(([a],[b])=>a<b?-1:a>b?1:0)):item);
const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const element=document.createElement(tag);if(text!==undefined)element.textContent=text;return element;};
const sameContext=(left:LifecycleContext,right:LifecycleContext)=>left?.contextId===right.contextId&&left.snapshotId===right.snapshotId&&left.businessDate===right.businessDate&&sameUtcInstant(left.asOf,right.asOf);
type Preview={operationId:string;ownerId:string;tradeId:string;kind:string;context:LifecycleContext;command:Record<string,unknown>;effectiveAt:string;requestedCommand:Record<string,unknown>;requestedEffectiveAt:string;acknowledgement:boolean;overviewSections:PhysicalOverviewSection[]};

/** The financial receipt attests the original canonical command, independently
 * of whether a later view can still admit a new source selection. */
export function requireTransportFinancialReceipt(request:RequestCommand,value:unknown){
 const body=request.body,expected=body.command;
 if(!object(expected)||!object(value)||value.found!==true||value.operationId!==request.operationId||value.ownerId!==body.ownerId||value.tradeId!==expected.tradeId||
  value.kind!==expected.kind||!object(value.command)||canonical(value.command)!==canonical(expected)||typeof value.effectiveAt!=='string'||typeof body.effectiveAt!=='string'||
  !sameUtcInstant(value.effectiveAt,body.effectiveAt)||!object(value.outcome)||value.outcome.tradeId!==expected.tradeId||value.outcome.kind!==expected.kind||typeof value.outcome.changed!=='boolean')
  throw Error('The reply did not identify the exact retained Transport command and source. Keep the request and check its status.');
}

/** Backend forms select exact financial state and published-source tuples. The
 * UI reviews the backend result and queues that canonical command unchanged. */
export function mountTransportFinancialCommands(root:HTMLElement,setup:GasOwnedSetup,o:GasOwnedUiOptions){
 if(setup.target?.handler!=='gas_transport_financial'||typeof setup.ownerId!=='string'||!setup.ownerId)throw Error('Read the exact financial Transport target and owning account.');
 const target=setup.target,ownerId=setup.ownerId;
 const editors:ReturnType<typeof mountOperationFields>[]=[];
 let edited=false;const refreshers:(()=>void)[]=[];
 for(const kind of ['adopt_capacity_version','adopt_rights_mark'] as const){
  const definition=setup.configuration[kind];if(!definition)continue;
  const section=node('details'),heading=node('summary',definition.label),form=node('form');section.append(heading,form);root.append(section);
  form.className='lifecycle-form';form.dataset.transportFinancialCommand=kind;form.dataset.lifecyclePermission=definition.permission;
  const fields=node('fieldset');fields.className='schedule-fields';fields.disabled=!o.permission(definition.permission);form.append(fields);
  const reviewHost=node('div');let version=0,reviewed:Record<string,unknown>|undefined;
  const invalidate=()=>{version++;reviewed=undefined;reviewHost.replaceChildren();};
  const editor=mountOperationFields(fields,definition.fields,{...o,changed:()=>{edited=true;invalidate();o.changed();}});editors.push(editor);
  const review=node('button','Review '+definition.label.toLowerCase());review.type='submit';form.append(review,reviewHost);
  form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{
   invalidate();const serial=version;
   if(!o.permission(definition.permission))throw Error('Restore the current financial amendment permission.');
   const {effectiveAt,...command}=await editor.read();if(!o.current()||serial!==version)return;
   if(typeof effectiveAt!=='string'||command.kind!==kind||command.tradeId!==target.scopeId)throw Error('Select this exact Transport agreement and financial command.');
   const body={operationId:crypto.randomUUID(),ownerId,context:cloneRead(setup.context),effectiveAt,command};
   const result=await o.api<Preview>('gas/transport/financial/preview',body);if(!o.current()||serial!==version)return;
   if(result.operationId!==body.operationId||result.ownerId!==ownerId||result.tradeId!==target.scopeId||result.kind!==kind||!sameContext(result.context,setup.context)||
    canonical(result.requestedCommand)!==canonical(command)||result.requestedEffectiveAt!==effectiveAt||!object(result.command)||result.command.kind!==kind||result.command.tradeId!==target.scopeId||
    typeof result.effectiveAt!=='string'||!sameUtcInstant(result.effectiveAt,effectiveAt)||typeof result.acknowledgement!=='boolean'||!Array.isArray(result.overviewSections)||!result.overviewSections.length)
    throw Error('Review the exact financial command, selected source and original inputs again.');
   const sections=overviewTermSections(result.overviewSections,o.zone());
   const accepted=reviewed={operationId:body.operationId,ownerId,context:cloneRead(setup.context),effectiveAt:result.effectiveAt,command:cloneRead(result.command)};
   const box=node('section');box.className='lifecycle-review';box.dataset.transportFinancialReview='';box.append(node('h3','Review '+definition.label.toLowerCase()));
   renderPhysicalSections(box,sections);
   const record=node('button','Record reviewed '+definition.label.toLowerCase());record.type='button';record.dataset.transportFinancialRecord='';box.append(record);reviewHost.replaceChildren(box);
   record.onclick=()=>void o.act(async()=>{
    if(!o.current()||version!==serial||reviewed!==accepted||!o.permission(definition.permission))throw Error('Review the current financial command before recording.');
    await o.queue('gas/transport/financial/record',cloneRead(accepted),definition.label);
   });
  });};
  refreshers.push(()=>{editor.refresh();invalidate();});
 }
 return{edited:()=>edited||editors.some(editor=>editor.dirty()),refresh:()=>refreshers.forEach(refresh=>refresh())};
}
