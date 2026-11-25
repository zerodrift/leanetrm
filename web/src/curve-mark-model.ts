import {type CatalogueSource} from './market-navigation.js';
import {type RequestCommand} from './pending-command.js';
import {cloneRead} from './read-references.js';

export const markPolicy='curve_mark_batch_v1';
export const markConvention='curve_marks_log_return_held_v1';
export const markPath='curve-marks/editor/';
export type MarkKind='volatility'|'correlation';
export type MarkContext={contextId:string;asOf:string;businessDate:string;snapshotId?:string|null};
export type ReadMarkContext={selected:MarkContext;knownAt:string;mode:'working'|'historical'};
export type MarkView={kind:MarkKind;mode:'grid'|'strip';curves:string[];fromMonth:string;toMonth:string};
export type CellKey={firstCurve:string;secondCurve:string|null;month:string};
export type Address={row:number;column:number};
export type LayoutCell={address:Address;key:CellKey|null;editable:boolean};
export type MarkValue={id:string;revision:number;value:string;effectiveAt:string;knownAt:string};
type Problem={code:string;message:string};
export type DraftCell={
 state:{key:CellKey;selected:MarkValue|null;writeHead:MarkValue|null;expectedRevision:number|null;editBlock:Problem|null};
 enteredValue:string|null;proposedValue:string|null;difference:string|null;isChanged:boolean;isNewMark:boolean;error:Problem|null;
};
export type Draft={cells:DraftCell[];coverage:{marked:number;missing:number;changed:number;invalid:number}};
export type MarkChoice={code:string;name:string;currency:string};
export type MarkScope={context:MarkContext;source:CatalogueSource};
export type SetupRequest=MarkScope&{view:MarkView|null;search:string;offset:number;limit:number};
export type Setup={
 source:CatalogueSource;context:ReadMarkContext;ownerId:string;canPublish:boolean;convention:string;
 limits:{gridCurves:number;volatilityGridMonths:number;stripMonths:number;batchCells:number;pageSize:number;reasonCharacters:number};
 curves:MarkChoice[];hasMoreCurves:boolean;view:MarkView|null;layout:{rowCount:number;columnCount:number;cells:LayoutCell[]}|null;draft:Draft|null;
};
export type MarkEdit={key:CellKey;value:string};
export type PasteChange={address:Address;key:CellKey;value:string};
export type PasteRequest={mode:'rectangle'|'full_matrix';start:Address|null;text:string};
export type Validation={source:CatalogueSource;context:ReadMarkContext;view:MarkView;draft:Draft;addressed:PasteChange[]};
export type HistoryEntry={mark:MarkValue;previousId:string|null;actorId:string;reason:string;effectiveEligible:boolean};
export type History={source:CatalogueSource;context:ReadMarkContext;kind:MarkKind;key:CellKey;revisions:HistoryEntry[];hasMore:boolean};
export type Change=CellKey&{expectedRevision:number;value:string};
export type Batch=MarkScope&{policy:typeof markPolicy;ownerId:string;operationId:string;kind:MarkKind;reason:string;changes:Change[]};
type ReviewedHead={revision:number;value:string;effectiveAt:string};
export type Review={batch:Batch;changes:{key:CellKey;selectedValue:string|null;proposedValue:string;expectedRevision:number;wouldChange:boolean;acknowledgedRevision:number|null;headAfterReview:ReviewedHead|null}[];alreadyRecorded:boolean};
export type Receipt={
 policy:typeof markPolicy;ownerId:string;operationId:string;kind:MarkKind;source:CatalogueSource;effectiveAt:string;reason:string;
 cells:{key:CellKey;markId:string;revision:number;value:string;changed:boolean;headAtPublication:ReviewedHead&{markId:string}}[];
};
export type MarkCommand=RequestCommand&{body:Batch};
export type MarkFence=Readonly<{actor:string;context:string;source:string;generation:number}>;

export const markFence=(actor:string,context:string,source:string,generation:number):MarkFence=>({actor,context,source,generation});
export const currentMarkFence=(a:MarkFence,b:MarkFence)=>
 a.actor===b.actor&&a.context===b.context&&a.source===b.source&&a.generation===b.generation;
export const keyId=(key:CellKey)=>JSON.stringify([key.firstCurve,key.secondCurve,key.month]);
export const sourceId=(source:CatalogueSource)=>JSON.stringify([source.definitionRevisionId,source.referenceCatalogId,source.referenceRevisionId]);
export const addressId=({row,column}:Address)=>row+':'+column;
export const keyLabel=(key:CellKey)=>[key.firstCurve,key.secondCurve].filter(Boolean).join(' × ');
export function moveCell(cells:Address[],from:Address,direction:'previous'|'next'|'down'){
 const index=cells.findIndex(cell=>addressId(cell)===addressId(from));
 if(index<0)return;
 if(direction==='down')return cells.find(cell=>cell.row>from.row&&cell.column===from.column);
 return cells[index+(direction==='next'?1:-1)];
}
export function cellPresentation(cell:DraftCell,edit:string|undefined,pending?:string){
 if(pending!==undefined)return{state:'pending',value:pending,label:'Publication pending'};
 if(edit!==undefined){
  if(edit!==cell.enteredValue)return{state:'unvalidated',value:edit,label:'Unvalidated'};
  if(cell.error)return{state:'invalid',value:edit,label:'Invalid'};
  return{state:'draft',value:cell.proposedValue??edit,label:'Validated draft'};
 }
 if(cell.state.selected)return{state:'published',value:cell.state.selected.value,label:'Published'};
 return{state:'missing',value:'—',label:'Missing'};
}

const record=(value:unknown):value is Record<string,unknown>=>!!value&&typeof value==='object'&&!Array.isArray(value);
function shape(value:unknown,fields:string,optional:string[]=[]):value is Record<string,unknown>{
 const keys=fields.split(' ');
 return record(value)&&keys.every(key=>Object.hasOwn(value,key))&&Object.keys(value).every(key=>keys.includes(key)||optional.includes(key));
}
const text=(value:unknown):value is string=>typeof value==='string';
const nonblank=(value:unknown):value is string=>text(value)&&!!value.trim();
const bool=(value:unknown):value is boolean=>typeof value==='boolean';
const count=(value:unknown):value is number=>typeof value==='number'&&Number.isSafeInteger(value)&&value>=0;
const positive=(value:unknown):value is number=>count(value)&&value>0;
const uuid=(value:unknown):value is string=>text(value)&&/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value)&&value!=='00000000-0000-0000-0000-000000000000';
const date=(value:unknown):value is string=>text(value)&&/^\d{4}-\d{2}-\d{2}$/.test(value);
const month=(value:unknown):value is string=>text(value)&&/^\d{4}-(0[1-9]|1[0-2])$/.test(value);
export const isUtcInstant=(value:unknown):value is string=>text(value)&&/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$/.test(value);
const canonicalInstant=(value:string)=>value.replace(/(?:Z|\+00:00)$/,'').replace(/(\.\d*?)0+$/,'$1').replace(/\.$/,'');
export const sameInstant=(a:string,b:string)=>canonicalInstant(a)===canonicalInstant(b);
const decimal=(value:unknown):value is string=>text(value)&&/^-?(?:0|[1-9]\d*)(?:\.\d{1,12})?$/.test(value);
const nullable=<T>(value:unknown,check:(value:unknown)=>value is T):value is T|null=>value===null||check(value);
const array=<T>(value:unknown,check:(value:unknown)=>value is T):value is T[]=>Array.isArray(value)&&value.every(check);
const isKind=(v:unknown):v is MarkKind=>v==='volatility'||v==='correlation';
const isSource=(v:unknown):v is CatalogueSource=>shape(v,'definitionRevisionId referenceCatalogId referenceRevisionId')&&uuid(v.definitionRevisionId)&&uuid(v.referenceCatalogId)&&uuid(v.referenceRevisionId);
const isContext=(v:unknown):v is MarkContext=>shape(v,'contextId asOf businessDate',['snapshotId'])&&uuid(v.contextId)&&isUtcInstant(v.asOf)&&date(v.businessDate)&&(v.snapshotId===undefined||nullable(v.snapshotId,uuid));
const isReadContext=(v:unknown):v is ReadMarkContext=>shape(v,'selected knownAt mode')&&isContext(v.selected)&&isUtcInstant(v.knownAt)&&(v.mode==='working'||v.mode==='historical');
const isView=(v:unknown):v is MarkView=>shape(v,'kind mode curves fromMonth toMonth')&&isKind(v.kind)&&(v.mode==='grid'||v.mode==='strip')&&array(v.curves,nonblank)&&month(v.fromMonth)&&month(v.toMonth);
const isKey=(v:unknown):v is CellKey=>shape(v,'firstCurve secondCurve month')&&nonblank(v.firstCurve)&&nullable(v.secondCurve,nonblank)&&month(v.month);
const isAddress=(v:unknown):v is Address=>shape(v,'row column')&&count(v.row)&&count(v.column);
const isLayoutCell=(v:unknown):v is LayoutCell=>shape(v,'address key editable')&&isAddress(v.address)&&nullable(v.key,isKey)&&bool(v.editable)&&(!v.editable||v.key!==null);
const isLayout=(v:unknown):v is NonNullable<Setup['layout']>=>shape(v,'rowCount columnCount cells')&&positive(v.rowCount)&&positive(v.columnCount)&&array(v.cells,isLayoutCell)&&v.cells.length===v.rowCount*v.columnCount&&v.cells.every((cell,i)=>cell.address.row===Math.floor(i/(v.columnCount as number))&&cell.address.column===i%(v.columnCount as number));
const isValue=(v:unknown):v is MarkValue=>shape(v,'id revision value effectiveAt knownAt')&&uuid(v.id)&&positive(v.revision)&&decimal(v.value)&&isUtcInstant(v.effectiveAt)&&isUtcInstant(v.knownAt);
const isProblem=(v:unknown):v is Problem=>shape(v,'code message')&&nonblank(v.code)&&nonblank(v.message);
type CellState=DraftCell['state'];
const isState=(v:unknown):v is CellState=>shape(v,'key selected writeHead expectedRevision editBlock')&&isKey(v.key)&&nullable(v.selected,isValue)&&nullable(v.writeHead,isValue)&&nullable(v.expectedRevision,count)&&nullable(v.editBlock,isProblem);
function isDraftCell(v:unknown):v is DraftCell{
 if(!shape(v,'state enteredValue proposedValue difference isChanged isNewMark error')||!isState(v.state)||!nullable(v.enteredValue,text)||
  !nullable(v.proposedValue,decimal)||!nullable(v.difference,decimal)||!bool(v.isChanged)||!bool(v.isNewMark)||!nullable(v.error,isProblem))return false;
 if(v.enteredValue===null||v.error)return v.proposedValue===null&&v.difference===null&&!v.isNewMark&&!v.isChanged&&(v.enteredValue!==null||!v.error);
 return v.proposedValue!==null&&v.isNewMark===(v.state.selected===null)&&(v.difference===null)===v.isNewMark;
}
const isCoverage=(v:unknown):v is Draft['coverage']=>shape(v,'marked missing changed invalid')&&count(v.marked)&&count(v.missing)&&count(v.changed)&&count(v.invalid);
const isChoice=(v:unknown):v is MarkChoice=>shape(v,'code name currency')&&nonblank(v.code)&&nonblank(v.name)&&nonblank(v.currency);
const isLimits=(v:unknown):v is Setup['limits']=>shape(v,'gridCurves volatilityGridMonths stripMonths batchCells pageSize reasonCharacters')&&Object.values(v).every(positive);
const isAddressed=(v:unknown):v is PasteChange=>shape(v,'address key value')&&isAddress(v.address)&&isKey(v.key)&&decimal(v.value);
const isHistoryEntry=(v:unknown):v is HistoryEntry=>shape(v,'mark previousId actorId reason effectiveEligible')&&isValue(v.mark)&&nullable(v.previousId,uuid)&&uuid(v.actorId)&&text(v.reason)&&bool(v.effectiveEligible);
function isChange(v:unknown):v is Change{
 if(!shape(v,'firstCurve secondCurve month expectedRevision value'))return false;
 const {expectedRevision,value,...key}=v;
 return isKey(key)&&count(expectedRevision)&&decimal(value);
}
const isBatch=(v:unknown):v is Batch=>shape(v,'policy ownerId operationId kind source context reason changes')&&v.policy===markPolicy&&nonblank(v.ownerId)&&uuid(v.operationId)&&isKind(v.kind)&&isSource(v.source)&&isContext(v.context)&&nonblank(v.reason)&&array(v.changes,isChange)&&v.changes.length>0&&v.changes.length<=240&&new Set(v.changes.map(keyId)).size===v.changes.length;
const isHead=(v:unknown):v is ReviewedHead=>shape(v,'revision value effectiveAt')&&positive(v.revision)&&decimal(v.value)&&isUtcInstant(v.effectiveAt);
const isReviewed=(v:unknown):v is Review['changes'][number]=>shape(v,'key selectedValue proposedValue expectedRevision wouldChange acknowledgedRevision headAfterReview')&&isKey(v.key)&&nullable(v.selectedValue,decimal)&&decimal(v.proposedValue)&&count(v.expectedRevision)&&bool(v.wouldChange)&&nullable(v.acknowledgedRevision,positive)&&nullable(v.headAfterReview,isHead);
function isReceiptCell(v:unknown):v is Receipt['cells'][number]{
 if(!shape(v,'key markId revision value changed headAtPublication')||!isKey(v.key)||!uuid(v.markId)||!positive(v.revision)||!decimal(v.value)||!bool(v.changed)||!record(v.headAtPublication))return false;
 const {markId,...head}=v.headAtPublication;
 return uuid(markId)&&isHead(head);
}
const failure=()=>Error('The curve mark response was incomplete or did not match this request. Refresh or check the original publication status.');
const sameContext=(a:MarkContext,b:MarkContext)=>a.contextId===b.contextId&&sameInstant(a.asOf,b.asOf)&&a.businessDate===b.businessDate&&(a.snapshotId??null)===(b.snapshotId??null);
const sameView=(a:MarkView|null,b:MarkView|null)=>a===null||b===null?a===b:
 a.kind===b.kind&&a.mode===b.mode&&a.fromMonth===b.fromMonth&&a.toMonth===b.toMonth&&a.curves.length===b.curves.length&&a.curves.every((curve,i)=>curve===b.curves[i]);
const matches=(source:unknown,context:unknown,scope:MarkScope,mode:ReadMarkContext['mode'])=>
 isSource(source)&&sourceId(source)===sourceId(scope.source)&&isReadContext(context)&&sameContext(context.selected,scope.context)&&context.mode===mode;
export function requireDraft(v:unknown,keys:CellKey[]):Draft{
 if(!shape(v,'cells coverage')||!array(v.cells,isDraftCell)||!isCoverage(v.coverage))throw failure();
 const ids=new Set(keys.map(keyId));
 if(ids.size!==v.cells.length||v.cells.some(cell=>!ids.delete(keyId(cell.state.key))))throw failure();
 return {cells:v.cells,coverage:v.coverage};
}
function layoutMatches(layout:NonNullable<Setup['layout']>,view:MarkView,limits:Setup['limits']){
 const matrix=view.kind==='correlation'&&view.mode==='grid',strip=view.mode==='strip';
 if(matrix){
  if(layout.rowCount!==view.curves.length||layout.columnCount!==view.curves.length)return false;
 }else if(strip){
  if(layout.columnCount!==1||layout.rowCount>limits.stripMonths)return false;
 }else if(layout.rowCount!==view.curves.length||layout.columnCount>limits.volatilityGridMonths)return false;
 const months=matrix?[view.fromMonth]:layout.cells.filter(cell=>strip||cell.address.row===0).map(cell=>cell.key?.month??'');
 if(months[0]!==view.fromMonth||months.at(-1)!==view.toMonth||months.some((month,index)=>index>0&&month<=months[index-1]))return false;
 return layout.cells.every(({address:{row,column},key,editable})=>{
  if(matrix&&row===column)return key===null&&!editable;
  if(!key||editable!==(!matrix||row<column)||key.month!==months[matrix?0:strip?row:column])return false;
  const first=view.curves[strip?0:row],second=view.kind==='volatility'?null:view.curves[matrix?column:1];
  return key.firstCurve===first&&key.secondCurve===second||second!==null&&key.firstCurve===second&&key.secondCurve===first;
 });
}
const layoutKeys=(layout:NonNullable<Setup['layout']>)=>layout.cells.flatMap(cell=>cell.editable&&cell.key?[cell.key]:[]);
export function requireCapture(value:unknown,businessDate:string){
 if(!record(value)||!record(value.context)||!uuid(value.context.contextId)||!isUtcInstant(value.context.knownAt)||value.context.effectiveOn!==businessDate)throw failure();
 return value.context as {contextId:string;knownAt:string;effectiveOn:string};
}
export function requireSetup(v:unknown,request:SetupRequest,mode:ReadMarkContext['mode']):Setup{
 if(!shape(v,'source context ownerId canPublish convention limits curves hasMoreCurves view layout draft')||!matches(v.source,v.context,request,mode)||
  !nonblank(v.ownerId)||!bool(v.canPublish)||mode==='historical'&&v.canPublish||v.convention!==markConvention||!isLimits(v.limits)||!array(v.curves,isChoice)||
  !bool(v.hasMoreCurves)||!nullable(v.view,isView)||!sameView(v.view,request.view))throw failure();
 if(v.view===null){if(v.layout!==null||v.draft!==null)throw failure();}
 else{
  if(!isLayout(v.layout)||!layoutMatches(v.layout,v.view,v.limits))throw failure();
  const draft=requireDraft(v.draft,layoutKeys(v.layout));
  if(draft.cells.some(cell=>cell.enteredValue!==null||!v.canPublish&&(cell.state.expectedRevision!==null||cell.state.writeHead!==null)))throw failure();
 }
 return v as Setup;
}
export function requireValidation(v:unknown,scope:MarkScope,view:MarkView,layout:NonNullable<Setup['layout']>,clock:ReadMarkContext):Validation{
 if(!shape(v,'source context view draft addressed')||!matches(v.source,v.context,scope,clock.mode)||!sameInstant((v.context as ReadMarkContext).knownAt,clock.knownAt)||!isView(v.view)||!sameView(v.view,view)||!array(v.addressed,isAddressed))throw failure();
 requireDraft(v.draft,layoutKeys(layout));
 const addresses=new Map(layout.cells.filter(cell=>cell.editable).map(cell=>[addressId(cell.address),cell.key]));
 const seen=new Set<string>();
 for(const change of v.addressed){
  const position=addressId(change.address),key=addresses.get(position);
  if(!key||keyId(key)!==keyId(change.key)||seen.has(position))throw failure();
  seen.add(position);
 }
 return v as Validation;
}
export function requireHistory(v:unknown,scope:MarkScope,kind:MarkKind,key:CellKey,clock:ReadMarkContext):History{
 if(!shape(v,'source context kind key revisions hasMore')||!matches(v.source,v.context,scope,clock.mode)||!sameInstant((v.context as ReadMarkContext).knownAt,clock.knownAt)||v.kind!==kind||!isKey(v.key)||keyId(v.key)!==keyId(key)||!array(v.revisions,isHistoryEntry)||!bool(v.hasMore))throw failure();
 return v as History;
}
function matchingChanges<T>(rows:T[],batch:Batch,matches:(row:T,change:Change)=>boolean,keyOf:(row:T)=>CellKey){
 const remaining=new Map(batch.changes.map(change=>[keyId(change),change]));
 return rows.length===remaining.size&&rows.every(row=>{
  const key=keyId(keyOf(row)),change=remaining.get(key);
  return change&&matches(row,change)&&remaining.delete(key);
 });
}
function sameBatch(a:Batch,b:Batch){
 return a.policy===b.policy&&a.ownerId===b.ownerId&&a.operationId===b.operationId&&a.kind===b.kind&&a.reason===b.reason&&
  sourceId(a.source)===sourceId(b.source)&&sameContext(a.context,b.context)&&
  matchingChanges(a.changes,b,(row,change)=>row.expectedRevision===change.expectedRevision&&row.value===change.value,row=>row);
}
export function requireReview(v:unknown,batch:Batch):Review{
 if(!shape(v,'batch changes alreadyRecorded')||!isBatch(v.batch)||!sameBatch(v.batch,batch)||!array(v.changes,isReviewed)||!bool(v.alreadyRecorded)||
  !matchingChanges(v.changes,batch,(row,change)=>row.expectedRevision===change.expectedRevision&&row.proposedValue===change.value,row=>row.key))throw failure();
 for(const change of v.changes){
  const head=change.headAfterReview;
  if(v.alreadyRecorded?(change.wouldChange||change.acknowledgedRevision===null||change.headAfterReview!==null):
   head===null||change.wouldChange!==(change.acknowledgedRevision===null))throw failure();
  if(head&&change.wouldChange&&(head.revision!==change.expectedRevision+1||head.value!==change.proposedValue||!sameInstant(head.effectiveAt,batch.context.asOf)))throw failure();
  if(head&&change.acknowledgedRevision!==null&&head.revision<change.acknowledgedRevision)throw failure();
 }
 return v as Review;
}
// Compare the supported UTC wire instants at exact microsecond precision. Date
// checks the calendar only, with the fraction kept outside its millisecond clock.
function receiptInstant(value:string){
 const [seconds,fraction='']=canonicalInstant(value).split('.');
 if(fraction.length>6||seconds.startsWith('0000-')||seconds>='9999-12-31T00:00:00')return;
 const calendar=new Date(seconds+'Z');
 if(Number.isNaN(calendar.getTime())||calendar.toISOString().slice(0,19)!==seconds)return;
 return `${seconds}.${fraction.padEnd(6,'0')}`;
}

// These patterns verify canonical evidence returned by the server. Draft values
// are always sent verbatim for backend validation and normalization.
function receiptValue(kind:MarkKind,value:string){
 return kind==='volatility'?/^(?:10|[0-9](?:\.\d{0,11}[1-9])?)$/.test(value):
  /^(?:0|-?1|-?0\.\d{0,11}[1-9])$/.test(value);
}
const ordinal=(a:string,b:string)=>a<b?-1:a>b?1:0;
const receiptOrder=(a:CellKey,b:CellKey)=>ordinal(a.firstCurve,b.firstCurve)||
 ordinal(a.secondCurve??'',b.secondCurve??'')||ordinal(a.month,b.month);
export function requireReceipt(v:unknown,batch:Batch):Receipt{
 if(!shape(v,'policy ownerId operationId kind source effectiveAt reason cells')||v.policy!==batch.policy||v.ownerId!==batch.ownerId||
  v.operationId!==batch.operationId||v.kind!==batch.kind||v.reason!==batch.reason||!isSource(v.source)||sourceId(v.source)!==sourceId(batch.source)||
  !isUtcInstant(v.effectiveAt)||!sameInstant(v.effectiveAt,batch.context.asOf)||!array(v.cells,isReceiptCell)||
  v.cells.length!==batch.changes.length)throw failure();
 const effective=receiptInstant(v.effectiveAt);
 if(!effective)throw failure();
 const orderedChanges=[...batch.changes].sort(receiptOrder);
 const ids=new Set<string>();
 const maxRevision=2_147_483_647;
 for(const [index,cell] of v.cells.entries()){
  const change=orderedChanges[index],head=cell.headAtPublication,headEffective=receiptInstant(head.effectiveAt);
  const acceptedId=cell.markId.toLowerCase(),headId=head.markId.toLowerCase();
  const acceptedIsHead=head.revision===cell.revision;
  if(keyId(cell.key)!==keyId(change)||cell.value!==change.value||!receiptValue(batch.kind,cell.value)||ids.has(acceptedId)||ids.has(headId)||
   cell.revision>maxRevision||head.revision>maxRevision||head.revision<cell.revision||
   !headEffective||headEffective<effective||!receiptValue(batch.kind,head.value)||
   acceptedIsHead!==(acceptedId===headId)||acceptedIsHead&&(head.value!==cell.value||headEffective!==effective)||
   cell.changed&&(!acceptedIsHead||cell.revision!==change.expectedRevision+1))throw failure();
  ids.add(acceptedId);ids.add(headId);
 }
 return v as Receipt;
}
export function requireStatus(v:unknown,batch:Batch):Receipt|null{
 if(shape(v,'status receipt')){
  if(v.status==='absent'&&v.receipt===null)return null;
  if(v.status==='committed')return requireReceipt(v.receipt,batch);
 }
 throw failure();
}
export function restoreMarkCommand(value:unknown):MarkCommand|undefined{
 if(!shape(value,'path operationId body',['uncertain','label'])||value.path!==markPath+'record'||!isBatch(value.body)||value.operationId!==value.body.operationId)return;
 return{path:value.path,operationId:value.body.operationId,body:cloneRead(value.body),uncertain:true};
}
