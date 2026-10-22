import {dealFormatEditor} from './deal-format-editor.js';
import {tradeAnnotations,type PartyCatalog,type Trader,type Annotation} from './trade-annotations.js';
import {futuresInstrumentEditor} from './futures-instrument-editor.js';
import {renderRegularTable} from './regular-table.js';
import {publicText,displayInstant,timeLabels,curveName,deliveryLabel} from './market-view.js';
import {futuresContractKey,type FuturesContract} from './futures-contract.js';
import type {RequestCommand} from './pending-command.js';
import {trackEdits} from './edit-tracking.js';
type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type TradeState=Annotation&{tradeReference:string;instrumentReference:string;tradeId:string;revision:number;bookId:string;bindingId:string;instrumentId:string;curve:string;contractId:string;lots:string;executionPrice:string;executedAt:string;effectiveAt:string;recordedAt:string;kind:string;reason:string;settlement:string};
type Detail={state:TradeState;history:TradeState[];catalog:PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string}[];contracts:FuturesContract[]};asOf:string;canAmend:boolean;amendUnavailableReason:string|null;amendmentConvention:string};
type Preview={before:{quantity:string;unit:string;currency:string;executionPnl:string};after:{quantity:string;unit:string;currency:string;executionPnl:string};change:string};
export function setupTradeEditor(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;context:()=>Record<string,unknown>;queue:(command:RequestCommand)=>Promise<void>;pending:()=>RequestCommand|undefined;retry:()=>void;reconcile:()=>void;onDenied:()=>void}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 const text=(id:string,value:string)=>{get(id).textContent=publicText(value);};
 const dialog=get<HTMLDialogElement>('trade-editor'),amendEdits=trackEdits(get('trade-amend-form'));
 const annotations=tradeAnnotations('trade',options.zone);
 const instrument=futuresInstrumentEditor(get('trade-instrument'),{contract:'trade-contract',lots:'trade-lots',price:'trade-price',terms:'trade-side-fields'});
 const display=dealFormatEditor(get('trade-amount-format'),{api:options.api,act:options.act,busy:options.busy,changed:async()=>{if(detail)await display.prepare(detail,'trade',detail.catalog.contracts.find(c=>c.id===detail!.state.contractId)?.clearingIncrement);terms();}});
 let detail:Detail|undefined,prepared:RequestCommand|undefined,selection:{tradeId:string;revision:number|null}|undefined;
 let generation=0,previewGeneration=0;
 let failedLiveSelection:{tradeId:string;revision:number|null}|undefined;
 const contextKey=()=>JSON.stringify(options.context());
 function invalidate(){previewGeneration++;prepared=undefined;get('trade-preview-result').hidden=true;get<HTMLInputElement>('trade-confirm').checked=false;sync();}
 function reset(){failedLiveSelection=undefined;display.reset();annotations.reset();generation++;if(dialog.open)dialog.close();detail=undefined;selection=undefined;amendEdits.clear();invalidate();get('trade-history').replaceChildren();for(const id of ['trade-revision','trade-book','trade-contract'])field(id).replaceChildren();for(const id of ['trade-preview-values','trade-effective'])text(id,'');get<HTMLFormElement>('trade-amend-form').reset();text('trade-editor-reference','');text('trade-editor-terms','');get('trade-side-fields').replaceChildren();text('trade-editor-mode','');text('trade-error','');get('trade-error').hidden=true;}
 async function safe(work:()=>Promise<void>){const started=generation;try{get('trade-error').hidden=true;await work();}catch(error){if(started!==generation)return;text('trade-error',error instanceof Error?error.message:'The trade operation failed.');get('trade-error').hidden=false;if((error as {code?:string}).code==='access_denied'){reset();options.onDenied();}throw error;}}
 function sync(){display.sync();
  const waiting=options.pending()?.path==='futures/amend';
  get<HTMLFieldSetElement>('trade-amend-fields').disabled=options.busy()||!detail?.canAmend||!!options.pending();
  field('trade-revision').disabled=options.busy()||!!options.pending();
  get<HTMLButtonElement>('trade-reload').disabled=options.busy()||!!options.pending();
  get<HTMLButtonElement>('trade-submit').disabled=options.busy()||!prepared||!get<HTMLInputElement>('trade-confirm').checked||!!options.pending();
  get<HTMLInputElement>('trade-confirm').disabled=options.busy()||!prepared||!!options.pending();
  get('trade-editor-pending').hidden=!waiting;
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
  field('trade-contract').replaceChildren(...detail.catalog.contracts.map(c=>new Option(publicText(`${curveName(c.curve)} · ${deliveryLabel(c.deliveryFrom,c.deliveryTo)}`),futuresContractKey(c))));
  const key=JSON.stringify([state.curve,state.contractId]);if(!detail.catalog.contracts.some(c=>futuresContractKey(c)===key))field('trade-contract').append(new Option('Retained dated contract',key));field('trade-contract').value=key;
  field('trade-lots').value=state.lots;field('trade-price').value=state.executionPrice;field('trade-reason').value='';amendEdits.clear();
  text('trade-editor-mode',detail.canAmend?`Current revision ${state.revision} · unprocessed futures`:detail.amendUnavailableReason??'Read-only trade revision');
  text('trade-effective',`Amendment effective time: ${displayInstant(detail.asOf,options.zone())} · ${options.zone()}. Earlier revisions remain unchanged.`);
  const labels=timeLabels(detail.history.flatMap(h=>[h.effectiveAt,h.recordedAt]),options.zone());
  renderRegularTable(get('trade-history'),'trade-history',JSON.stringify([state.tradeId,state.revision,detail.asOf]),['Revision','Event','Effective','Recorded','Book','Lots','Execution price','Trader ID','Memo','Trade date','Legal entity','Counterparty','Reason'],detail.history.map((h,i)=>[String(h.revision),h.kind==='booked'?'Booked':'Amended',labels[2*i],labels[2*i+1],books.find(b=>b.id===h.bookId)?.name??'Unavailable book',h.lots,h.executionPrice,h.traderCode??'',h.memo??'',h.booking?.tradeDate??'',h.booking?.legalEntity??'',h.booking?.counterparty??'',h.reason]),[0,5,6],options.busy);
  terms();invalidate();
 }
 async function open(tradeId:string,revision:number|null){
  failedLiveSelection=undefined;
  const started=++generation,key=contextKey();
  invalidate();detail=undefined;selection={tradeId,revision};
  try{
   const response=await options.api<Detail>('futures/editor',{context:options.context(),tradeId,revision});
   if(started!==generation||key!==contextKey())return;
   detail=response;render();await display.load(response.state.tradeId,options.context(),response.canAmend,['trade']);await display.prepare(response,'trade',response.catalog.contracts.find(c=>c.id===response.state.contractId)?.clearingIncrement);if(started!==generation||key!==contextKey())return;terms();if(!dialog.open)dialog.showModal();
  }catch(error){if(started!==generation||key!==contextKey())return;reset();if((error as {code?:string}).code==='access_denied')options.onDenied();throw error;}
 }
 get('trade-reload').onclick=()=>{if(selection)void options.act(()=>open(selection!.tradeId,null));};
 get('trade-editor-close').onclick=()=>{if(!options.busy())reset();};
 dialog.addEventListener('cancel',()=>reset());
 field('trade-revision').onchange=()=>{if(selection)void options.act(()=>safe(()=>open(selection!.tradeId,Number(field('trade-revision').value))));};
 get<HTMLFormElement>('trade-amend-form').addEventListener('input',invalidate);
 field('trade-contract').onchange=()=>{invalidate();terms();};
 get<HTMLFormElement>('trade-amend-form').onsubmit=event=>{event.preventDefault();void options.act(()=>safe(async()=>{
  if(!detail?.canAmend)throw Error('Only the current eligible trade revision can be amended.');
  const c=detail.catalog.contracts.find(c=>futuresContractKey(c)===field('trade-contract').value);if(!c)throw Error('Choose an available dated contract.');
  const context=options.context(),key=contextKey(),started=generation,previewStarted=previewGeneration;
  if(started!==generation||previewStarted!==previewGeneration||key!==contextKey())return;
  const command:RequestCommand={path:'futures/amend',operationId:crypto.randomUUID(),body:{...annotations.read(),snapshotId:context.snapshotId,tradeId:detail.state.tradeId,expectedRevision:detail.state.revision,bookId:field('trade-book').value,curve:c.curve,contractId:c.id,lots:field('trade-lots').value,executionPrice:field('trade-price').value,effectiveAt:detail.asOf,reason:field('trade-reason').value,convention:context.convention,amendmentConvention:detail.amendmentConvention,knowledgeMode:'current'}};
  const result=await options.api<Preview>('futures/amend-preview',{...command.body,operationId:command.operationId});
  if(started!==generation||previewStarted!==previewGeneration||key!==contextKey())return;
  prepared=command;text('trade-preview-values',`Quantity before: ${result.before.quantity} ${result.before.unit}; after: ${result.after.quantity} ${result.after.unit}. Execution P&L: ${result.before.executionPnl} → ${result.after.executionPnl} ${result.after.currency}; change ${result.change} ${result.after.currency}.`);get('trade-preview-result').hidden=false;get<HTMLInputElement>('trade-confirm').checked=false;sync();
 }));};
 get<HTMLInputElement>('trade-confirm').onchange=sync;
 get('trade-submit').onclick=()=>void options.act(()=>safe(async()=>{if(!prepared||!get<HTMLInputElement>('trade-confirm').checked)throw Error('Preview and confirm the exact amendment first.');await options.queue(prepared);}));
 get('trade-editor-retry').onclick=options.retry;get('trade-editor-reconcile').onclick=options.reconcile;
 return{open,reset,sync,async liveRefresh(){const requested=failedLiveSelection??(dialog.open?selection:undefined);if(!requested)return;try{await open(requested.tradeId,requested.revision);}catch(error){if((error as {code?:string}).code!=='access_denied')failedLiveSelection=requested;throw error;}},refresh(){annotations.refresh();if(detail){terms();invalidate();}},
  // The futures trade the dialog shows, and amendment edits not yet sent.
  shown:()=>detail&&dialog.open?{tradeId:detail.state.tradeId,instrumentType:'future' as const,revision:detail.state.revision,reference:detail.state.tradeReference}:undefined,
  unsaved:()=>dialog.open&&amendEdits.edited()};
}
