import type {RequestCommand} from './pending-command.js';

export type LifecycleContext={contextId:string;asOf:string;businessDate:string;snapshotId?:string|null};
export type LifecycleTarget={handler:string;scopeId:string;bindingId:string};
export type LifecycleChoice={value:string;label:string};
export type LifecycleOffer={target:LifecycleTarget;action:string;label:string;occurrenceId:string;at:string;before:string|null;eligible:boolean;reason:string|null;revision:number;stateVersion:number;
 subject?:{reference:string;product:string;bookId:string;book:string}|null;policyId?:string|null;policyRevision?:number|null;selection:Record<string,unknown>|null;requiredInputs:string[];availableQuantity?:string|null;inputChoices?:Record<string,LifecycleChoice[]>};
export type LifecycleRequest={operationId:string;context:LifecycleContext;target:LifecycleTarget;action:string;policyId:string;policyRevision:number;automatic:boolean;previewHash:string;selection:Record<string,unknown>};
export type LifecycleDraft={policyId:string;policyRevision:number;evidenceId?:string;lots?:string;correctionDue?:{occurrenceId:string;dueAt:string}[];correctionDueAt?:string|null;determinationId?:string;amount?:string|null;effectiveAt?:string;reference?:string;reversesId?:string|null;allocations?:{determinationId:string;amount:string}[]};
export type LifecyclePolicy={id:string;revision:number;action:string;mode:string;from_at:string;until_at:string};
export type LifecycleMarginPolicy=Record<string,unknown>&{id:string;revision:number;definition_revision_id:string;contract_id:string;series_code:string;settlement_curve_name:string;from_at:string;until_at:string};
export type LifecycleOptionSource={trade_id:string;definition_revision_id:string;underlying_contract_id:string;curve_code:string;curve_name:string;future_mark_eligible:boolean;expiry_eligible:boolean};
export type LifecycleSwapOccurrence={trade_id:string;origin_revision:number;binding_id:string;occurrence_id:string;membership_ordinal:number;payment_ordinal:number;delivery_from:string;delivery_to:string;payment_at:string;reference_catalog_id:string;reference_revision_id:string;currency_code:string;decimal_places:number};
export type LifecycleDeliveryProfile={profile:Record<string,unknown>&{id:string;revision:number;name:string;definition_revision_id:string;contract_id:string};gas:Record<string,unknown>&{account_id:string}};
export type LifecycleDeliveryAccount=Record<string,unknown>&{id:string;code:string;book_name:string;legal_entity_name:string;mark_expiry_at:string};
export type LifecycleFutureSetup={trade_id:string;binding_id:string;revision:number;book_id:string;book_name:string;trade_reference:number;lots:string;execution_price:string;execution_at:string;definition_revision_id:string;contract_id:string;mark_curve:string;reference_catalog_id:string;reference_revision_id:string;expiry_at:string;series_code:string;product_name:string;currency_code:string;adopted_policy_id:string|null;adopted_policy_revision:number|null;
 settlement_method:string;deliveryAccounts:LifecycleDeliveryAccount[];deliveryProfiles:LifecycleDeliveryProfile[];
 sources:{curve_code:string;curve_name:string;reference_catalog_id:string;reference_revision_id:string;quote_code:string;source_code:string;currency_code:string}[]};
export type LifecycleDiscovery={context:LifecycleContext;actions:LifecycleOffer[];policies:LifecyclePolicy[];optionProfiles:Record<string,unknown>[];swapProfiles:Record<string,unknown>[];swapNetProfiles?:Record<string,unknown>[];marginPolicies:Record<string,unknown>[];
 choices:{actions:{code:string;automatic:boolean;configurationPermission:string;executionPermission:string}[];modes:string[];policyImplementation:string;swapRounding:string[];swapDue:string[];swapRecognition:string[];swapCorrection:string[];swapNet?:string[];
 futures?:{policyImplementation:string;opening:string[];ties:string[];roundingStages:string[];roundingScopes:string[];phases:string[];adoption:string[];delivery?:{implementations:string[];carrying:string[];principalScopes:string[];correctionDuePolicies:string[]}}};
 optionSources?:LifecycleOptionSource[];swapOccurrences?:LifecycleSwapOccurrence[];futuresSetup?:LifecycleFutureSetup[];deliveryProfiles?:LifecycleDeliveryProfile[];deliveryAccounts?:LifecycleDeliveryAccount[]};
export const lifecycleKey=(offer:LifecycleOffer)=>JSON.stringify([offer.target.handler,offer.target.scopeId,offer.target.bindingId,offer.action,offer.occurrenceId]);
const reasons:Record<string,string>={option_holding_closed:'The option holding is closed.',lifecycle_configuration_required:'Configure an action policy and the product settings.',lifecycle_policy_required:'Choose the action policy to review margin.',lifecycle_selection_required:'Choose a market close.',lifecycle_action_window:'Outside the configured action window.',option_instruction_cutoff:'The instruction window has ended.',option_instruction_closed:'The instruction cutoff has passed.',option_exercise_window:'Outside the exercise window.'};
export const lifecycleReason=(reason:string|null)=>reason?reasons[reason]??reason.replaceAll('_',' '):'Available';
export function lifecycleRows(offers:LifecycleOffer[],instant:(value:string)=>string):string[][]{
 return offers.map(offer=>[offer.subject?.reference??'Holding',offer.subject?.product??offer.target.handler.replaceAll('_',' '),offer.subject?.book??'',offer.label,instant(offer.at),offer.eligible?'Available':offer.reason==='option_holding_closed'?'Closed':lifecycleReason(offer.reason)]);
}
/** Present explicit observed inputs over a backend-created native selection.
 * Eligibility, quantities, prices, calendars and all financial interpretation stay in the backend. */
export function lifecycleRequest(context:LifecycleContext,offer:LifecycleOffer,draft:LifecycleDraft,operationId:string):LifecycleRequest{
 if(!offer.selection)throw Error('Configure the product lifecycle settings first.');
 if(!draft.policyId||!Number.isSafeInteger(draft.policyRevision)||draft.policyRevision<1)throw Error('Choose an exact saved action policy.');
 const selection=structuredClone(offer.selection);
 for(const input of offer.requiredInputs){
  if(input==='policyId')continue;
  if(input==='correctionDue'){
   if(!Array.isArray(draft.correctionDue))throw Error('Choose explicit correction due times, or confirm that none are needed.');
   selection.correctionDue=structuredClone(draft.correctionDue);continue;
  }
  if(input==='netSettlement'){
   if(!selection.net||typeof selection.net!=='object')throw Error('Choose a supported net settlement.');
   const net=selection.net as Record<string,unknown>,preview=net.preview;
   if(!preview||typeof preview!=='object'||!Array.isArray(draft.allocations)||!Object.hasOwn(draft,'reversesId'))throw Error('Choose explicit net allocations or one original settlement to reverse.');
   if(draft.reversesId===null){if(typeof draft.amount!=='string'||!draft.amount.trim()||draft.allocations.length<2)throw Error('Enter the observed net amount and at least two gross allocations.');}
   else if(typeof draft.reversesId!=='string'||!draft.reversesId||draft.amount!==null||draft.allocations.length)throw Error('A reversal uses its exact original settlement without replacement amounts.');
   Object.assign(preview,{amount:draft.amount,allocations:structuredClone(draft.allocations),reversesId:draft.reversesId});continue;
  }
  if(input==='correctionDueAt'){
   if(!Object.hasOwn(draft,input))throw Error('Choose a correction due time, or confirm that this is an original determination.');
   if(!selection.determination||typeof selection.determination!=='object')throw Error('Choose a supported determination.');
   (selection.determination as Record<string,unknown>).correctionDueAt=draft.correctionDueAt;continue;
  }
  if(!['evidenceId','lots','determinationId','amount','effectiveAt','reference'].includes(input))throw Error('Choose an action with a supported input form.');
  const value=draft[input as keyof LifecycleDraft];if(typeof value!=='string'||!value.trim())throw Error('Enter '+input.replace(/([A-Z])/g,' $1').toLowerCase()+'.');
  const owner=['effectiveAt','reference'].includes(input)&&selection.net?selection.net:['determinationId','amount','effectiveAt','reference'].includes(input)?selection.payment:selection;
  if(!owner||typeof owner!=='object'||Array.isArray(owner))throw Error('Choose a supported native action selection.');
  (owner as Record<string,unknown>)[input]=value;
 }
 if(selection.payment&&typeof selection.payment==='object'&&Object.hasOwn(draft,'reversesId'))(selection.payment as Record<string,unknown>).reversesId=draft.reversesId;
 return {operationId,context:structuredClone(context),target:structuredClone(offer.target),action:offer.action,policyId:draft.policyId,policyRevision:draft.policyRevision,automatic:false,previewHash:'0'.repeat(64),selection};
}
export const lifecycleWritePaths=['lifecycle/execute','lifecycle/policies/save','lifecycle/options/configure','lifecycle/swaps/configure','lifecycle/swaps/net/configure','lifecycle/futures/policies/save','lifecycle/futures/adopt','lifecycle/futures/delivery/configure','lifecycle/gas/configure'] as const;
export function restoreLifecycleRequest(value:unknown):RequestCommand|undefined{
 if(!value||typeof value!=='object'||Array.isArray(value))return;
 const saved=value as RequestCommand;
 if(!lifecycleWritePaths.includes(saved.path as typeof lifecycleWritePaths[number])||typeof saved.operationId!=='string'||!saved.operationId||!saved.body||typeof saved.body!=='object'||Array.isArray(saved.body)||saved.body.operationId!==saved.operationId)return;
 return {path:saved.path,operationId:saved.operationId,body:structuredClone(saved.body),...(saved.uncertain===true?{uncertain:true}:{}),...(typeof saved.label==='string'?{label:saved.label}:{})};
}
