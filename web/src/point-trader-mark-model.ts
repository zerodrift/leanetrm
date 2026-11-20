import {cloneRead} from './read-references.js';
import type {CatalogueSource,FrameSummary} from './market-navigation.js';
import type {PhysicalOverviewSection} from './physical-instrument.js';
import type {RequestCommand} from './pending-command.js';
import {sameUtcInstant} from './user-time.js';

export type PointContext={contextId:string;asOf:string;businessDate:string;snapshotId?:string|null};
export type PointTarget=CatalogueSource&{curveCode:string;contractId:string;seriesId:string;source:string;quote:string;observedAt:string;
 sourceEventId:string;sourceRevisionToken:string;expectedPreviousObservationId:string|null};
export type OriginalPointInputs={snapshotId:string;fingerprint:string};
export type PointCommand={policy:string;target:PointTarget;value:string;evidence:string;originalInputs:OriginalPointInputs|null};
export type PointRequest=PointCommand&{operationId:string;ownerId:string;context:PointContext};
export type PointQuoteFilter={quantityCode:string;currencyCode:string;multiplier:string};
export type PointSetupRequest={context:PointContext;source:CatalogueSource;curveCode?:string|null;contractId?:string|null;search:string;offset:number;limit:number;quoteFilter:PointQuoteFilter|null;eligibility:string|null};
export type PointChoice={label:string;target:PointTarget;valueUnit:string;currency:string;previous:null|{observationId:string;value:string;sourceRevisionId:string}};
export type PointSetup={ownerId:string;context:PointContext;source:CatalogueSource;policy:{code:string;label:string};choices:PointChoice[];
 unavailable:{label:string;reason:string}[];canPublish:boolean;page:{offset:number;limit:number;hasMore:boolean;search:string};originalInputs:OriginalPointInputs|null};
export type PointOutcome=Omit<PointTarget,'curveCode'|'expectedPreviousObservationId'|'sourceRevisionToken'>&{operationId:string;observationId:string;changed:boolean;
 sourceRevisionId:string;previousObservationId:string|null;value:string;knowledgeEventOrder:number;publicationXid:string};
export type PointReply={operationId:string;ownerId:string;context:PointContext;requestedCommand:PointCommand;command:PointCommand;
 acknowledgement:boolean;outcome:PointOutcome;overviewSections:PhysicalOverviewSection[];found?:true;editedSelection?:unknown};
export type PointEditedSelection={state:'available';selectionId:string;originalSnapshotId:string;fingerprint:string}|{state:'unavailable';reason:string};
export type AvailablePointSelection=Extract<PointEditedSelection,{state:'available'}>;
// This local stamp fences view application; it never enters the business body.
export type PointPending=RequestCommand&{scopeStamp?:string};
const publicationRejections=new Set(['input','source','context','stale','conflict','denied','owner_changed','policy','reference'].map(suffix=>'point_trader_mark_'+suffix));
// PendingCommand applies these only to the first send, never an uncertain retry.
export const pointPublicationRejection=(code:string|undefined):boolean=>code!==undefined&&publicationRejections.has(code);

const object=(value:unknown):value is Record<string,unknown>=>!!value&&typeof value==='object'&&!Array.isArray(value);
const text=(value:unknown):value is string=>typeof value==='string'&&value.length>0;
const closed=(value:unknown,required:readonly string[],optional:readonly string[]=[]):value is Record<string,unknown>=>object(value)&&required.every(key=>Object.hasOwn(value,key))&&Object.keys(value).every(key=>required.includes(key)||optional.includes(key));
const sourceKeys=['definitionRevisionId','referenceCatalogId','referenceRevisionId'] as const;
const targetKeys=[...sourceKeys,'curveCode','contractId','seriesId','source','quote','observedAt','sourceEventId','sourceRevisionToken','expectedPreviousObservationId'] as const;
const commandKeys=['policy','target','value','evidence','originalInputs'] as const;
const canonical=(value:unknown):string=>JSON.stringify(value,(_key,item)=>object(item)?Object.fromEntries(Object.entries(item).sort(([a],[b])=>a<b?-1:a>b?1:0)):item);
const same=(left:unknown,right:unknown)=>canonical(left)===canonical(right);
const original=(value:unknown):value is OriginalPointInputs=>closed(value,['snapshotId','fingerprint'])&&text(value.snapshotId)&&typeof value.fingerprint==='string'&&/^[0-9a-f]{64}$/.test(value.fingerprint);
const source=(value:unknown):value is CatalogueSource=>closed(value,sourceKeys)&&sourceKeys.every(key=>text(value[key]));
const context=(value:unknown):value is PointContext=>closed(value,['contextId','asOf','businessDate'],['snapshotId'])&&text(value.contextId)&&
 typeof value.asOf==='string'&&sameUtcInstant(value.asOf,value.asOf)&&typeof value.businessDate==='string'&&/^\d{4}-\d{2}-\d{2}$/.test(value.businessDate)&&
 (value.snapshotId===undefined||value.snapshotId===null||text(value.snapshotId));
const target=(value:unknown):value is PointTarget=>closed(value,targetKeys)&&targetKeys.filter(key=>key!=='expectedPreviousObservationId').every(key=>text(value[key]))&&
 (value.expectedPreviousObservationId===null||text(value.expectedPreviousObservationId))&&sameUtcInstant(value.observedAt,value.observedAt);
const command=(value:unknown):value is PointCommand=>closed(value,commandKeys)&&text(value.policy)&&target(value.target)&&text(value.value)&&text(value.evidence)&&
 (value.originalInputs===null||original(value.originalInputs));
const sameContext=(left:PointContext,right:PointContext)=>context(left)&&context(right)&&left.contextId===right.contextId&&left.businessDate===right.businessDate&&
 (left.snapshotId??null)===(right.snapshotId??null)&&sameUtcInstant(left.asOf,right.asOf);
const sameTarget=(left:PointTarget,right:PointTarget)=>target(left)&&target(right)&&targetKeys.every(key=>key==='observedAt'?sameUtcInstant(left[key],right[key]):left[key]===right[key]);
const request=(value:unknown):value is PointRequest=>closed(value,['operationId','ownerId','context',...commandKeys])&&text(value.operationId)&&text(value.ownerId)&&context(value.context)&&
 command(Object.fromEntries(commandKeys.map(key=>[key,value[key]])))&&sameUtcInstant((value.target as PointTarget).observedAt,(value.context as PointContext).asOf);
const body=(value:PointRequest):PointCommand=>Object.fromEntries(commandKeys.map(key=>[key,cloneRead(value[key])])) as PointCommand;

export function requirePointSetup(expected:PointSetupRequest,value:unknown):PointSetup{
 const fail=()=>{throw Error('The point setup did not identify the exact source, context and search page.');};
 if(!context(expected.context)||!source(expected.source)||!object(value)||!text(value.ownerId)||!sameContext(value.context as PointContext,expected.context)||
  !source(value.source)||!same(value.source,expected.source)||!closed(value.policy,['code','label'])||!text(value.policy.code)||!text(value.policy.label)||
  !Array.isArray(value.choices)||!Array.isArray(value.unavailable)||typeof value.canPublish!=='boolean'||
  !closed(value.page,['offset','limit','hasMore','search'])||value.page.offset!==expected.offset||value.page.limit!==expected.limit||value.page.search!==expected.search||typeof value.page.hasMore!=='boolean'||
  !Object.hasOwn(value,'originalInputs')||value.originalInputs!==null&&(!original(value.originalInputs)||value.originalInputs.snapshotId!==expected.context.snapshotId))return fail();
 for(const choice of value.choices){
  if(!closed(choice,['label','target','valueUnit','currency','previous'])||!text(choice.label)||!text(choice.valueUnit)||!text(choice.currency)||!target(choice.target))return fail();
  const selected=choice.target;
  if(!sourceKeys.every(key=>selected[key]===expected.source[key])||!sameUtcInstant(selected.observedAt,expected.context.asOf)||
   expected.curveCode!=null&&selected.curveCode!==expected.curveCode||expected.contractId!=null&&selected.contractId!==expected.contractId)return fail();
  const previous=choice.previous;
  if(previous===null?selected.expectedPreviousObservationId!==null:
   !closed(previous,['observationId','value','sourceRevisionId'])||!text(previous.observationId)||!text(previous.value)||!text(previous.sourceRevisionId)||previous.observationId!==selected.expectedPreviousObservationId)return fail();
 }
 if(new Set(value.choices.map(choice=>canonical(choice.target))).size!==value.choices.length||value.unavailable.some(row=>!closed(row,['label','reason'])||!text(row.label)||!text(row.reason)))return fail();
 return value as PointSetup;
}

function outcomeMatches(expected:PointRequest,value:unknown):value is PointOutcome{
 if(!object(value))return false;
 const sameRevision=value.sourceRevisionId===expected.target.sourceRevisionToken&&value.previousObservationId===expected.target.expectedPreviousObservationId;
 const acceptedPredecessor=expected.target.expectedPreviousObservationId!==null&&value.observationId===expected.target.expectedPreviousObservationId;
 return value.operationId===expected.operationId&&text(value.observationId)&&typeof value.changed==='boolean'&&text(value.sourceRevisionId)&&
  (value.previousObservationId===null||text(value.previousObservationId))&&(value.changed?sameRevision:sameRevision||acceptedPredecessor)&&
  targetKeys.filter(key=>!['curveCode','sourceRevisionToken','expectedPreviousObservationId','observedAt'].includes(key)).every(key=>value[key]===expected.target[key])&&
  sameUtcInstant(value.observedAt,expected.target.observedAt)&&value.value===expected.value&&
  typeof value.knowledgeEventOrder==='number'&&Number.isSafeInteger(value.knowledgeEventOrder)&&value.knowledgeEventOrder>0&&
  typeof value.publicationXid==='string'&&/^\d+$/.test(value.publicationXid);
}

/** The backend owns value normalization. The UI attests the raw request and
 * exact source point before retaining its reviewed canonical command. */
export function reviewedPointRequest(expected:PointRequest,value:unknown):PointRequest{
 const fail=()=>{throw Error('Review the exact point publication and original source again.');};
 if(!request(expected)||!object(value)||value.operationId!==expected.operationId||value.ownerId!==expected.ownerId||!sameContext(value.context as PointContext,expected.context)||
  !same(value.requestedCommand,body(expected))||!command(value.command)||value.command.policy!==expected.policy||!sameTarget(value.command.target,expected.target)||
  !same(value.command.originalInputs,expected.originalInputs)||typeof value.acknowledgement!=='boolean'||!Array.isArray(value.overviewSections)||!value.overviewSections.length)return fail();
 const accepted={...cloneRead(expected),...cloneRead(value.command)};
 if(!outcomeMatches(accepted,value.outcome)||value.acknowledgement===false&&value.outcome.changed===false)return fail();
 return accepted;
}

/** Optional edited-view metadata is intentionally outside business certainty. */
export function requirePointReceipt(expected:PointRequest,value:unknown):asserts value is PointReply{
 if(!request(expected)||!object(value)||value.operationId!==expected.operationId||value.ownerId!==expected.ownerId||!sameContext(value.context as PointContext,expected.context)||
  !same(value.requestedCommand,body(expected))||!same(value.command,body(expected))||typeof value.acknowledgement!=='boolean'||!outcomeMatches(expected,value.outcome)||
  value.acknowledgement===false&&value.outcome.changed===false)throw Error('The receipt did not identify the exact retained point publication. Keep its original request and check status.');
}

export function pointEditedSelection(expected:PointRequest,value:unknown):PointEditedSelection{
 if(closed(value,['state','reason'])&&value.state==='unavailable'&&text(value.reason))return value as PointEditedSelection;
 if(closed(value,['state','selectionId','originalSnapshotId','fingerprint'])&&value.state==='available'&&text(value.selectionId)&&original(expected.originalInputs)&&
  value.originalSnapshotId===expected.originalInputs.snapshotId&&value.fingerprint===expected.originalInputs.fingerprint)return value as PointEditedSelection;
 throw Error('The edited inputs do not identify the original selection. Publication remains recorded.');
}

export function requirePointInputs(expected:PointRequest,offer:AvailablePointSelection,value:unknown):FrameSummary{
 const fail=()=>{throw Error('The edited inputs did not identify the exact retained operation and working selection.');};
 pointEditedSelection(expected,offer);
 if(!closed(value,['operationId','ownerId','selectionId','originalSnapshotId','fingerprint','frame'])||value.operationId!==expected.operationId||value.ownerId!==expected.ownerId||
  value.selectionId!==offer.selectionId||value.originalSnapshotId!==offer.originalSnapshotId||value.fingerprint!==offer.fingerprint||
  !closed(value.frame,['id','name','at','businessDate'],['originCloseId','close'])||value.frame.id!==offer.selectionId||!text(value.frame.name)||
  !sameUtcInstant(value.frame.at,expected.context.asOf)||value.frame.businessDate!==expected.context.businessDate||value.frame.originCloseId!=null||value.frame.close!=null)return fail();
 return value.frame as FrameSummary;
}

export function restorePointRequest(value:unknown):PointPending|undefined{
 if(!object(value)||value.path!=='market-values/record'||!request(value.body)||value.operationId!==value.body.operationId||
  value.uncertain!==undefined&&typeof value.uncertain!=='boolean'||value.label!==undefined&&typeof value.label!=='string')return;
 return{path:value.path,operationId:value.operationId,body:cloneRead(value.body),...(value.uncertain===true?{uncertain:true}:{}),...(typeof value.label==='string'?{label:value.label}:{}),...(text(value.scopeStamp)?{scopeStamp:value.scopeStamp}:{})};
}
