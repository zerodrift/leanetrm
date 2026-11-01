import {dealFormatEditor} from './deal-format-editor.js';
import {tradeAnnotations,bookingRows,historyBooking,type PartyCatalog,type Trader,type Annotation} from './trade-annotations.js';
import {futuresInstrumentEditor,futuresTermRows} from './futures-instrument-editor.js';
import {publicText,displayInstant,timeLabels} from './market-view.js';
import {futuresContractKey,type FuturesContract} from './futures-contract.js';
import type {RequestCommand} from './pending-command.js';
import {trackEdits} from './edit-tracking.js';
import {minus,priceUnit,type Holding,type HoldingFormats} from './desk-model.js';
import type {AmendState,DetailRead,DetailView,TermSection} from './position-panel.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type TradeState=Annotation&{tradeReference:string;instrumentReference:string;tradeId:string;revision:number;bookId:string;bindingId:string;instrumentId:string;curve:string;contractId:string;lots:string;executionPrice:string;executedAt:string;effectiveAt:string;recordedAt:string;kind:string;reason:string;settlement:string};
type Detail={state:TradeState;history:TradeState[];catalog:PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string}[];contracts:FuturesContract[]};asOf:string;canAmend:boolean;amendUnavailableReason:string|null;amendmentConvention:string};
export type TradeDetail=Detail;
type Preview={before:{quantity:string;unit:string;currency:string;executionPnl:string};after:{quantity:string;unit:string;currency:string;executionPnl:string};change:string};
/** The editor's read of one trade revision (null: the latest), in the market context the page reads in. */
export const readTradeDetail=(api:Api,context:Record<string,unknown>,tradeId:string,revision:number|null)=>api<Detail>('futures/editor',{context,tradeId,revision});
/**
 * The revisions of a trade as the History tab lists them: each with the instrument it was of, when it took effect and was recorded,
 * and its facts, its numbers written as Terms writes them and its price in the unit of that revision's own contract. An amendment can
 * move a trade to another curve of the same currency, so the position's unit is not every revision's: `positionUnit` is the unit of
 * the contract the position is of, which is that of `detail.state`, and it is the unit of every revision of that contract, whether or
 * not the editor's catalog holds it (it does not hold one that is not yet eligible, or has expired). Any other revision takes its
 * contract's unit from the catalog, and a contract the catalog does not hold leaves the unit unstated, which is said, never assumed.
 */
export function tradeHistory(detail:Detail,zone:string,formats:HoldingFormats,positionUnit?:string):TermSection[]{
 const books=detail.catalog.books,labels=timeLabels(detail.history.flatMap(h=>[h.effectiveAt,h.recordedAt]),zone),own=detail.state;
 const price=(h:TradeState)=>{
  const contract=detail.catalog.contracts.find(c=>futuresContractKey(c)===JSON.stringify([h.curve,h.contractId]));
  const unit=positionUnit!==undefined&&h.curve===own.curve&&h.contractId===own.contractId?positionUnit:contract?priceUnit(contract):undefined;
  return `${minus(formats.price(h.executionPrice))} ${unit??'· unit unavailable: the contract of this revision is not in the catalog'}`;
 };
 return detail.history.map((h,i)=>({title:`Revision ${h.revision} · ${h.kind==='booked'?'Booked':'Amended'}`,
  rows:[['Effective',labels[2*i]],['Recorded',labels[2*i+1]],['Book',books.find(b=>b.id===h.bookId)?.name??'Unavailable book'],['Instrument',h.instrumentReference],['Lots',minus(h.lots)],['Execution price',price(h)],...historyBooking(h),['Reason',h.reason]]}));
}
/**
 * The terms of a trade revision as a read view lists them: its booking facts, its position, with its price in `unit`, and its
 * dated contract's terms.
 */
export function tradeTerms(detail:Detail,zone:string,formats:HoldingFormats,unit:string):TermSection[]{
 const s=detail.state,book=detail.catalog.books.find(b=>b.id===s.bookId),key=JSON.stringify([s.curve,s.contractId]),contract=detail.catalog.contracts.find(c=>futuresContractKey(c)===key);
 return [
  {title:'Trade',rows:[['Trade',`${s.tradeReference} · ${s.instrumentReference}`],['Revision',`${s.revision} · ${s.kind==='booked'?'Booked':'Amended'}`],['Book',book?`${book.portfolio} · ${book.name}`:'Unavailable book'],...bookingRows(s,zone)]},
  {title:'Position',rows:[['Lots',minus(s.lots)],['Execution price',`${minus(formats.price(s.executionPrice))} ${unit}`]]},
  {title:'Contract',rows:contract?futuresTermRows(contract,zone,value=>formats.mark(value)):[['Contract terms','Retained contract terms are unavailable in this selectable catalog.']]},
 ];
}
/** What the position panel's Terms and History tabs show of a trade revision: the same read the editor makes. */
export const tradeDetailView=(detail:Detail,zone:string,formats:HoldingFormats,unit:string):DetailView=>({terms:tradeTerms(detail,zone,formats,unit),history:tradeHistory(detail,zone,formats,unit)});
export const tradeAmendState=(detail:Detail):AmendState=>detail.canAmend?{allowed:true,reason:null}:{allowed:false,reason:detail.amendUnavailableReason??'Read-only trade revision'};
/** A trade's other bindings are other positions: an editor read must hold the binding of the position it is read for. */
export function holdsBinding(detail:Detail,h:Pick<Holding,'bindingId'>){
 if(detail.state.bindingId!==h.bindingId)throw Error('The editor read holds another binding of the trade than the position.');
}
const anotherInstrument="This revision belongs to another instrument of the trade than this position, so only the position's current revision can be amended.";
/**
 * What a revision read for a position may be used for. The position's own revision must be of its binding, and a read of another is
 * refused. An earlier revision may be of another binding, which an amendment of the instrument gives a trade, and is read as it is;
 * only the position's binding can be amended from the position.
 */
export function tradeAtPosition(detail:Detail,position:Pick<Holding,'bindingId'|'revision'>,requested:number|null):Detail{
 if(requested===null||requested===Number(position.revision))holdsBinding(detail,position);
 return detail.state.bindingId===position.bindingId?detail:{...detail,canAmend:false,amendUnavailableReason:detail.amendUnavailableReason??anotherInstrument};
}
/** The editor's read for a position of the Blotter, which must hold the binding that position is. */
export async function readTradeView(api:Api,context:Record<string,unknown>,h:Pick<Holding,'tradeId'|'bindingId'|'revision'|'currency'|'unit'|'quoteMultiplier'>,formats:HoldingFormats):Promise<DetailRead>{
 const detail=await readTradeDetail(api,context,h.tradeId,Number(h.revision));
 holdsBinding(detail,h);
 const unit=priceUnit(h);
 return {amend:tradeAmendState(detail),view:zone=>tradeDetailView(detail,zone,formats,unit)};
}
type Position=Pick<Holding,'tradeId'|'bindingId'|'revision'>;
export function setupTradeEditor(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Record<string,unknown>;queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;onDenied:()=>void;host:()=>HTMLElement;
 /** Asks whether to give up what the panel holds unsaved, when it holds any: false means the person kept it. */
 leave:()=>Promise<boolean>;
 /** Hears each time the form starts or stops holding unsaved edits. */
 unsavedChanged:()=>void}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 const text=(id:string,value:string)=>{get(id).textContent=publicText(value);};
 // The form is kept on the page's shelf until a host takes it, and goes back there when it is reset.
 const root=get('trade-editing'),shelf=root.parentElement!,amendEdits=trackEdits(get('trade-amend-form'),[],options.unsavedChanged);
 const mounted=()=>root.parentElement!==shelf;
 const annotations=tradeAnnotations('trade',options.zone);
 const instrument=futuresInstrumentEditor(get('trade-instrument'),{contract:'trade-contract',lots:'trade-lots',price:'trade-price',terms:'trade-side-fields'},{edited:()=>amendEdits.mark()});
 const display=dealFormatEditor(get('trade-amount-format'),{api:options.api,act:options.act,busy:options.busy,changed:async()=>{if(detail)await display.prepare(detail,'trade',detail.catalog.contracts.find(c=>c.id===detail!.state.contractId)?.clearingIncrement);terms();}});
 let detail:Detail|undefined,prepared:RequestCommand|undefined,selection:{position:Position;revision:number|null}|undefined;
 let generation=0,previewGeneration=0;
 const contextKey=()=>JSON.stringify(options.context());
 function invalidate(){previewGeneration++;prepared=undefined;get('trade-preview-result').hidden=true;get<HTMLInputElement>('trade-confirm').checked=false;sync();}
 function reset(){instrument.clear();display.reset();annotations.reset();generation++;shelf.append(root);root.hidden=true;detail=undefined;selection=undefined;amendEdits.clear();invalidate();for(const id of ['trade-revision','trade-book','trade-contract'])field(id).replaceChildren();for(const id of ['trade-preview-values','trade-effective'])text(id,'');get<HTMLFormElement>('trade-amend-form').reset();text('trade-editor-reference','');text('trade-editor-terms','');get('trade-side-fields').replaceChildren();text('trade-editor-mode','');text('trade-error','');get('trade-error').hidden=true;}
 async function safe(work:()=>Promise<void>){const started=generation;try{get('trade-error').hidden=true;await work();}catch(error){if(started!==generation)return;text('trade-error',error instanceof Error?error.message:'The trade operation failed.');get('trade-error').hidden=false;get('trade-error').scrollIntoView({block:'nearest'});if((error as {code?:string}).code==='access_denied'){reset();options.onDenied();}throw error;}}
 function sync(){instrument.sync(options.busy()||!detail?.canAmend||!!options.pending());display.sync();
  const confirmed=get<HTMLInputElement>('trade-confirm').checked;
  get<HTMLButtonElement>('trade-preview').disabled=options.busy()||!detail?.canAmend||!!options.pending();
  get('trade-preview').classList.toggle('secondary',!!prepared);get('trade-submit').classList.toggle('secondary',!prepared);
  text('trade-action-status',detail&&!detail.canAmend?(detail.amendUnavailableReason??'This revision is read-only.'):prepared?(confirmed?'Ready to save this amendment.':'Review the preview below and tick the confirmation to enable saving.'):'Change the terms, enter an amendment reason, then preview changes.');
  get<HTMLFieldSetElement>('trade-amend-fields').disabled=options.busy()||!detail?.canAmend||!!options.pending();
  field('trade-revision').disabled=options.busy()||!!options.pending();
  get<HTMLButtonElement>('trade-reload').disabled=options.busy()||!!options.pending();
  get<HTMLButtonElement>('trade-submit').disabled=options.busy()||!prepared||!get<HTMLInputElement>('trade-confirm').checked||!!options.pending();
  get<HTMLInputElement>('trade-confirm').disabled=options.busy()||!prepared||!!options.pending();
 }
 function terms(){
  const c=detail?.catalog.contracts.find(c=>futuresContractKey(c)===field('trade-contract').value);
  instrument.render(c,options.zone(),(value,tick)=>display.text(value,'price','trade',tick));
  text('trade-editor-terms',c?'Standard contract terms are shared. Execution P&L and open sensitivity only; daily/final settlement is not processed.':'Retained contract terms are unavailable in this selectable catalog. Choose a supported dated contract to preview an amendment.');
 }
 function render(){
  if(!detail)return;
  const state=detail.state,books=detail.catalog.books;annotations.populate(detail.catalog.bookingActor,detail.catalog,state);
  text('trade-editor-reference',`${state.tradeReference} · ${state.instrumentReference} · Revision ${state.revision}`);
  field('trade-revision').replaceChildren(...detail.history.map(h=>new Option(`Revision ${h.revision} · ${h.kind==='booked'?'Booked':'Amended'}`,String(h.revision))));field('trade-revision').value=String(state.revision);
  field('trade-book').replaceChildren(...books.map(b=>new Option(publicText(`${b.portfolio} · ${b.name}`),b.id)));field('trade-book').value=state.bookId;
  const key=JSON.stringify([state.curve,state.contractId]);instrument.populate(detail.catalog.contracts,key);
  if(!detail.catalog.contracts.some(c=>futuresContractKey(c)===key)){const retained=new Option('Retained dated contract',key);retained.disabled=true;field('trade-contract').append(retained);field('trade-contract').value=key;}
  field('trade-lots').value=state.lots;field('trade-price').value=state.executionPrice;field('trade-reason').value='';amendEdits.clear();
  text('trade-editor-mode',detail.canAmend?`Current revision ${state.revision} · unprocessed futures`:detail.amendUnavailableReason??'Read-only trade revision');
  text('trade-effective',`Amendment effective time: ${displayInstant(detail.asOf,options.zone())} · ${options.zone()}. Earlier revisions remain unchanged.`);
  terms();invalidate();
 }
 /** Opens a position's trade at one of its revisions, the latest for none, in the host. The read must hold the position's binding. */
 async function open(position:Position,revision:number|null){
  const started=++generation,key=contextKey();
  invalidate();detail=undefined;selection={position,revision};
  try{
   const read=await readTradeDetail(options.api,options.context(),position.tradeId,revision);
   if(started!==generation||key!==contextKey())return;
   const response=tradeAtPosition(read,position,revision);
   detail=response;render();await display.load(response.state.tradeId,options.context(),response.canAmend,['trade']);await display.prepare(response,'trade',response.catalog.contracts.find(c=>c.id===response.state.contractId)?.clearingIncrement);if(started!==generation||key!==contextKey())return;terms();options.host().append(root);root.hidden=false;
  }catch(error){if(started!==generation||key!==contextKey())return;reset();if((error as {code?:string}).code==='access_denied')options.onDenied();throw error;}
 }
 // Another revision replaces what the form holds, which is asked about first, before the page is busy, as every other way out of it is.
 function replacing(revision:()=>number|null){
  if(!selection||options.busy())return;
  const at=selection.position;
  void (async()=>{if(await options.leave())await options.act(()=>open(at,revision()));else field('trade-revision').value=String(detail?.state.revision??'');})();
 }
 get('trade-reload').onclick=()=>replacing(()=>null);
 field('trade-revision').onchange=()=>replacing(()=>Number(field('trade-revision').value));
 get<HTMLFormElement>('trade-amend-form').addEventListener('input',invalidate);
 field('trade-contract').onchange=()=>{invalidate();terms();};
 get<HTMLFormElement>('trade-amend-form').onsubmit=event=>{event.preventDefault();void options.act(()=>safe(async()=>{
  if(!detail?.canAmend)throw Error('Only the current eligible trade revision can be amended.');
  const c=detail.catalog.contracts.find(c=>futuresContractKey(c)===field('trade-contract').value);if(!c)throw Error('Choose an available dated contract.');
  const context=options.context(),key=contextKey(),started=generation,previewStarted=previewGeneration;
  if(started!==generation||previewStarted!==previewGeneration||key!==contextKey())return;
  const command:RequestCommand={path:'futures/amend',operationId:crypto.randomUUID(),label:`Futures amendment of ${detail.state.tradeReference}`,body:{...annotations.read(),snapshotId:context.snapshotId,tradeId:detail.state.tradeId,expectedRevision:detail.state.revision,bookId:field('trade-book').value,curve:c.curve,contractId:c.id,lots:field('trade-lots').value,executionPrice:field('trade-price').value,effectiveAt:detail.asOf,reason:field('trade-reason').value,convention:context.convention,amendmentConvention:detail.amendmentConvention,knowledgeMode:'current'}};
  const result=await options.api<Preview>('futures/amend-preview',{...command.body,operationId:command.operationId});
  if(started!==generation||previewStarted!==previewGeneration||key!==contextKey())return;
  prepared=command;text('trade-preview-values',`Quantity before: ${result.before.quantity} ${result.before.unit}; after: ${result.after.quantity} ${result.after.unit}. Execution P&L: ${result.before.executionPnl} → ${result.after.executionPnl} ${result.after.currency}; change ${result.change} ${result.after.currency}.`);get('trade-preview-result').hidden=false;get<HTMLInputElement>('trade-confirm').checked=false;sync();get('trade-preview-result').scrollIntoView({block:'nearest'});
 }));};
 get<HTMLInputElement>('trade-confirm').onchange=sync;
 get('trade-submit').onclick=()=>void options.act(()=>safe(async()=>{if(!prepared||!get<HTMLInputElement>('trade-confirm').checked)throw Error('Preview and confirm the exact amendment first.');await options.queue(prepared);}));
 return{open,reset,sync,refresh(){annotations.refresh();if(detail){terms();invalidate();}},
  // The position whose amendment form is in the host, the revision that form shows, and amendment edits not yet sent.
  shown:()=>mounted()?selection?.position:undefined,
  displayedRevision:()=>mounted()?detail?.state.revision:undefined,
  /** An amendment the backend refused for good: what was previewed for it is dropped, and the form, with the edits in it, is kept to be corrected and previewed again. */
  unprepare:invalidate,
  unsaved:()=>mounted()&&amendEdits.edited()};
}
