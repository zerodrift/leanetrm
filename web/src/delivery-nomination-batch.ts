import {PendingCommand,actorTransport,restoreTradeRequest,type RequestCommand} from './pending-command.js';
import {renderOperationInspection,type OperationInspection} from './operation-inspection.js';
import {zonedInput,sameUtcInstant} from './user-time.js';

type Account={id:string;versionId:string;label:string;expectedPreviousId:string|null};
type Entry={obligationId:string;tradeId:string;expectedRevision:number;day:string;quantity:string;label:string;accounts:Account[]};
type Source={profile:'complete_allocation_nomination_batch_v1';allocationPlanId:string;entries:Entry[];channels:{value:string;label:string}[]};
type Input={context:Record<string,unknown>;allocationPlanId:string;profile:string;entries:{obligationId:string;tradeId:string;expectedRevision:number;day:string;details:Record<string,unknown>}[]};
type Review=OperationInspection&{operationId:string;ownerId:string;allocationPlanId:string;input:Input;requestedInput:Input;previewHash:string};
type Options={api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;pendingElsewhere?:()=>boolean;
 zone:()=>string;identity:()=>string;permission:(name:string)=>boolean;financialContext:()=>Record<string,unknown>|null;changed:()=>void;recorded:()=>Promise<void>};
const path='delivery/workbench/gas-transport/nominations',profile='complete_allocation_nomination_batch_v1';
const canonical=(value:unknown):string=>JSON.stringify(value,(_key,item)=>item&&typeof item==='object'&&!Array.isArray(item)?Object.fromEntries(Object.entries(item).sort(([a],[b])=>a.localeCompare(b))):item);
const text=(value:unknown):value is string=>typeof value==='string'&&value.trim().length>0;
const node=<K extends keyof HTMLElementTagNameMap>(tag:K,value?:string)=>{const n=document.createElement(tag);if(value!==undefined)n.textContent=value;return n;};

function readSource(value:unknown):Source{
 const source=value as Source;
 if(!source||source.profile!==profile||!text(source.allocationPlanId)||!Array.isArray(source.entries)||!source.entries.length||!Array.isArray(source.channels)||!source.channels.length||
  source.channels.some(c=>!c||!text(c.value)||!text(c.label))||source.entries.some(e=>!e||!text(e.obligationId)||!text(e.tradeId)||!Number.isSafeInteger(e.expectedRevision)||e.expectedRevision<1||!text(e.day)||!text(e.quantity)||!text(e.label)||!Array.isArray(e.accounts)||
   e.accounts.some(a=>!a||!text(a.id)||!text(a.versionId)||!text(a.label)||a.expectedPreviousId!==null&&!text(a.expectedPreviousId)))||
  new Set(source.entries.map(e=>e.obligationId+':'+e.day)).size!==source.entries.length)throw Error('The nomination choices did not retain the exact committed delivery allocations. Refresh the source.');
 return structuredClone(source);
}

/** Records new external request streams under the existing gas nomination
 * contract. Source quantities stay read-only; this control transmits nothing. */
export function setupDeliveryNominationBatch(root:HTMLElement,o:Options){
 root.innerHTML=`<section class="card"><h3>Record submitted nomination batch</h3><p class="field-help">Use the exact committed delivery allocations to record new external request references. Counterparty and pipeline responses, schedule adoption and corrections remain separate native gas operations. This screen does not transmit nominations.</p>
 <p data-delivery-nominations="message" role="status"></p><section data-delivery-nominations="pending" class="operations-recovery" hidden><p>The original nomination batch has an unconfirmed outcome. Its allocation, source quantities, financial context and request identity are retained.</p><button type="button" data-delivery-nominations="status">Check original batch status</button><button type="button" data-delivery-nominations="retry">Retry original batch</button></section>
 <form data-delivery-nominations="form" hidden><fieldset><div data-delivery-nominations="entries"></div><div class="schedule-fields"><label>Actual submission time<input name="submittedAt" type="datetime-local" step="1" required></label><label>External channel<select name="channel" required></select></label><label>Reason<input name="reason" required autocomplete="off"></label><label>Evidence<input name="evidence" required autocomplete="off"></label></div><button type="submit" data-delivery-nominations="review">Review submitted requests</button><button type="button" class="secondary" data-delivery-nominations="discard">Discard nomination inputs</button></fieldset></form><section data-delivery-nominations="review-host"></section></section>`;
 const get=<T extends HTMLElement=HTMLElement>(name:string)=>root.querySelector<T>(`[data-delivery-nominations="${name}"]`)!;
 const form=get<HTMLFormElement>('form'),field=form.querySelector<HTMLFieldSetElement>('fieldset')!,channel=form.elements.namedItem('channel') as HTMLSelectElement;
 const input=(name:string)=>form.elements.namedItem(name) as HTMLInputElement;
 const time=zonedInput(input('submittedAt'),o.zone,o.api),accounts:HTMLSelectElement[]=[],references:HTMLInputElement[]=[];
 const pending=new PendingCommand(code=>!!code&&(/^(delivery_|gas_operations_|temporal_)/.test(code)||['access_denied','historical_read_only','request_format','revision_conflict'].includes(code)));
 const storage=actorTransport<RequestCommand>(()=>'openetrm.delivery.nominations.request.'+o.identity(),()=>sessionStorage);
 let actor='',owner='',source:Source|undefined,dirty=false,generation=0,version=0,reviewed:Review|undefined,lastReason:string|null=null;
 const financial=()=>{try{return o.financialContext();}catch{return null;}};
 const stamp=()=>o.identity()+canonical(financial()),fence=()=>({generation,stamp:stamp()}),current=(at:ReturnType<typeof fence>)=>at.generation===generation&&at.stamp===stamp();
 function restore(){if(actor===o.identity())return;actor=o.identity();pending.clear();const saved=restoreTradeRequest(storage.read());if(saved?.path===path+'/record')pending.command={...saved,uncertain:true};}
 function retain(){storage.write(pending.command);o.changed();}
 function invalidate(){version++;reviewed=undefined;get('review-host').replaceChildren();dirty=true;o.changed();sync();}
 function clear(){version++;reviewed=undefined;dirty=false;form.reset();time.clear();get('review-host').replaceChildren();o.changed();}
 function unavailable(){
  if(!source)return 'Select a committed current delivery allocation plan to prepare nominations.';
  const context=financial();if(!context||!text(context.snapshotId)||context.knowledgeMode!=='current'||context.contextId)return 'Choose an explicit working financial close in the Blotter before reviewing nominations. The operating date does not select a price close.';
  if(!['ReadMarketData','ReadTrades','ReadGasOperations','RecordGasOperations','AmendTrade'].every(o.permission))return 'Current gas recording and trade amendment permissions are required.';
  if(source.entries.some(e=>!e.accounts.length))return 'Configure an eligible owned service account for each delivery in Physical operations before reviewing this batch.';
  return null;
 }
 function sync(){restore();const blocked=o.busy()||!!o.pendingElsewhere?.()||!!pending.command,reason=unavailable();field.disabled=blocked||!!reason;get<HTMLButtonElement>('review').disabled=blocked||!!reason;get<HTMLButtonElement>('discard').disabled=blocked;get('form').hidden=!source||!!pending.command;get('pending').hidden=!pending.command;get<HTMLButtonElement>('status').disabled=o.busy()||!pending.command;get<HTMLButtonElement>('retry').disabled=o.busy()||!pending.command;
  for(const button of get('review-host').querySelectorAll<HTMLButtonElement>('button'))button.disabled=blocked||!!reason;
  if(!pending.command){if(reason)get('message').textContent=reason;else if(get('message').textContent===lastReason)get('message').textContent='';lastReason=reason;}
 }
 function show(value:unknown|null){generation++;restore();clear();source=undefined;owner='';accounts.length=references.length=0;get('entries').replaceChildren();
  const data=value as {ownerId?:unknown;nominationBatch?:unknown}|null;
  if(data?.nominationBatch){if(!text(data.ownerId))throw Error('Retain the exact owning account for nomination review.');owner=data.ownerId;source=readSource(data.nominationBatch);
   for(const [index,e]of source.entries.entries()){
    const row=node('fieldset');row.append(node('legend',`${e.label} · ${e.day} · allocated ${e.quantity}`));
    const accountLabel=node('label','Service account'),account=node('select');account.name='account_'+index;account.required=true;account.append(new Option('Choose a service account',''),...e.accounts.map(a=>new Option(a.label,a.id)));accountLabel.append(account);accounts.push(account);
    const referenceLabel=node('label','New external request reference'),reference=node('input');reference.name='reference_'+index;reference.required=true;reference.autocomplete='off';referenceLabel.append(reference);references.push(reference);row.append(accountLabel,referenceLabel);get('entries').append(row);
   }
   channel.replaceChildren(new Option('Choose the external channel',''),...source.channels.map(c=>new Option(c.label,c.value)));
  }
  get('message').textContent='';sync();
 }
 function sameTargets(actual:Input,expected:Input){
  const targets=(value:Input)=>value.entries.map(e=>({obligationId:e.obligationId,tradeId:e.tradeId,expectedRevision:e.expectedRevision,day:e.day,accountId:e.details.accountId,serviceVersionId:e.details.serviceVersionId,expectedPreviousId:e.details.expectedPreviousId,quantity:e.details.quantity,channel:e.details.channel}));
  return !!actual&&actual.profile===expected.profile&&actual.allocationPlanId===expected.allocationPlanId&&canonical(actual.context)===canonical(expected.context)&&Array.isArray(actual.entries)&&canonical(targets(actual))===canonical(targets(expected))&&actual.entries.every((e,i)=>typeof e.details.submittedAt==='string'&&sameUtcInstant(e.details.submittedAt,String(expected.entries[i].details.submittedAt)));
 }
 function requireReceipt(command:RequestCommand,value:unknown){const v=value as {found?:boolean;ownerId:string;operationId:string;allocationPlanId:string;input:Input;previewHash:string;outcome:unknown},expected=command.body as {ownerId:string;input:Input;previewHash:string};
  if(!v||v.found!==true||v.ownerId!==expected.ownerId||v.operationId!==command.operationId||v.allocationPlanId!==expected.input.allocationPlanId||canonical(v.input)!==canonical(expected.input)||v.previewHash!==expected.previewHash||!v.outcome||typeof v.outcome!=='object')throw Error('The reply did not identify the exact original nomination batch. Keep the request and check its status.');
 }
 async function confirmed(command:RequestCommand,value:unknown){requireReceipt(command,value);pending.clear();retain();clear();sync();try{await o.recorded();}finally{get('message').textContent='The submitted nomination batch is recorded. Operator responses and schedule adoption remain separate.';}}
 async function send(command:RequestCommand){const at=fence(),previous=pending.sending();retain();sync();try{const value=await o.api(path+'/record',{...command.body,operationId:command.operationId});if(current(at)&&pending.command===command)await confirmed(command,value);}catch(error){if(current(at)&&pending.command===command){pending.failed((error as {code?:string}).code,previous);retain();sync();}throw error;}}
 form.addEventListener('input',invalidate);form.addEventListener('change',invalidate);
 form.onsubmit=event=>{event.preventDefault();void o.act(async()=>{
  restore();if(!source||unavailable()||pending.command||!form.reportValidity())throw Error(unavailable()??'Reconcile the original batch before reviewing another request.');
  const at=fence(),serial=version,selected=source,ownerId=owner,context={...structuredClone(financial()!),contextId:null},submittedAt=await time.instant();if(!current(at)||serial!==version)return;
  const inputValue:Input={context,allocationPlanId:selected.allocationPlanId,profile:selected.profile,entries:selected.entries.map((e,index)=>{
   const account=e.accounts.find(a=>a.id===accounts[index].value);if(!account)throw Error('Select the exact service account for each allocated delivery.');
   return{obligationId:e.obligationId,tradeId:e.tradeId,expectedRevision:e.expectedRevision,day:e.day,details:{accountId:account.id,serviceVersionId:account.versionId,expectedPreviousId:account.expectedPreviousId,externalReference:references[index].value,quantity:e.quantity,submittedAt,channel:channel.value,reason:input('reason').value,evidence:input('evidence').value}};
  })};
  const operationId=crypto.randomUUID(),value=await o.api<Review>(path+'/preview',{operationId,ownerId,input:inputValue});if(!current(at)||serial!==version||source!==selected)return;
  if(value.operationId!==operationId||value.ownerId!==ownerId||value.allocationPlanId!==selected.allocationPlanId||canonical(value.requestedInput)!==canonical(inputValue)||!sameTargets(value.input,inputValue)||!text(value.previewHash))throw Error('Review the exact original nomination inputs and committed allocations again.');
  reviewed=structuredClone(value);const review=node('section');review.append(node('h4','Review submitted nomination batch'));const facts=node('div');renderOperationInspection(facts,value,{key:'delivery-nomination-review',scope:operationId,label:'nomination batch',zone:o.zone(),busy:o.busy});review.append(facts);
  const record=node('button','Record reviewed nomination batch');record.type='button';record.dataset.deliveryNominations='record';record.onclick=()=>void o.act(async()=>{if(!reviewed||reviewed.operationId!==operationId||serial!==version||!current(at)||unavailable())throw Error('Review the current exact nomination batch before recording.');const r=reviewed,command=pending.begin(path+'/record',{ownerId:r.ownerId,input:r.input,previewHash:r.previewHash},()=>r.operationId,'Submitted nomination batch');retain();sync();await send(command);});review.append(record);get('review-host').replaceChildren(review);sync();
 });};
 get('discard').onclick=()=>{if(pending.command)return;clear();sync();};
 get('status').onclick=()=>void o.act(async()=>{restore();const command=pending.command;if(!command)return;const at=fence(),value=await o.api<{found?:boolean}>(path+'/status',{ownerId:command.body.ownerId,operationId:command.operationId});if(!current(at)||pending.command!==command)return;if(value.found===false){get('message').textContent='No recorded outcome is available yet. Retry the retained original batch.';return;}await confirmed(command,value);});
 get('retry').onclick=()=>void o.act(async()=>{restore();if(pending.command)await send(pending.command);});
 return{show,sync,unconfirmed:()=>{restore();return !!pending.command;},edited:()=>dirty||!!pending.command,leave(){if(pending.command||!dirty)return true;if(!confirm('Discard unsaved nomination inputs?'))return false;clear();sync();return true;},refresh(){time.refresh();if(reviewed){reviewed=undefined;get('review-host').replaceChildren();}sync();},reset(){generation++;source=undefined;owner='';actor='';pending.clear();clear();get('entries').replaceChildren();form.hidden=true;sync();}};
}
