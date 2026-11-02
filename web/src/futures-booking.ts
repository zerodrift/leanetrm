import {amountText} from './amount-display.js';
import {priceSourceElement,priceSourceNote,renderCloseOut,type CloseOutShown,type FuturesCloseOut} from './close-out-draft.js';
import {draftValuation} from './draft-valuation.js';
import {trackEdits} from './edit-tracking.js';
import {futuresContractDescription,futuresContractKey,type FuturesContract} from './futures-contract.js';
import {futuresInstrumentEditor} from './futures-instrument-editor.js';
import type {CurveShape} from './desk-model.js';
import type {DraftMarket} from './curve-shapes.js';
import {publicText} from './market-view.js';
import type {RequestCommand} from './pending-command.js';
import {tradeAnnotations,type PartyCatalog,type Trader} from './trade-annotations.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
/** What the form of a new future needs of the Blotter's catalog: the people and books to book for, and, once the priced catalog is read, the contracts. */
export type FuturesBookingCatalog=PartyCatalog&{bookingActor:Trader;books:{id:string;name:string;portfolio:string}[];canBook:boolean}&({kind:'directory'}|{kind:'priced_contracts';contracts:FuturesContract[]});

/**
 * The market a priced futures draft is for: the one of the contract it was priced with, with that contract's delivery period. The contract
 * is found in the catalog by the identity the draft carries (its curve and id), not by what the form offers now, which the person can have
 * changed since the price was made; one the catalog does not hold has no delivery period to mark, and is said so.
 */
export function futuresDraftMarkets(contracts:readonly Pick<FuturesContract,'id'|'curve'|'deliveryFrom'|'deliveryTo'>[],draft:{curve:string;contractId:string}):DraftMarket[]{
  const contract=contracts.find(c=>c.curve===draft.curve&&c.id===draft.contractId);
  if(!contract)throw Error('The contract this price was made for is not in the catalog.');
  return [{curve:contract.curve,deliveryFrom:contract.deliveryFrom,deliveryTo:contract.deliveryTo}];
}

/**
 * The form of a new futures trade, which the page puts in the side panel's form part (blotter-desk.md, Side panel, New trade). Its
 * booking facts are the server's and its instrument is the person's: an exact dated contract, lots and an execution price, priced
 * before booking. What it sends is the body of a request, which the page keeps exactly as first sent (`book`).
 */
export function setupFuturesBooking(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;
 /** The market context the page reads in, as every read and command states it. */
 context:()=>{snapshotId:string;convention:string}&Record<string,unknown>;
 /** The catalog of the read on screen, with its contracts once the priced catalog has been read. */
 catalog:()=>FuturesBookingCatalog|undefined;
 /** A request of the person's whose outcome is not known, which must be settled before another is entered. */
 pending:()=>RequestCommand|undefined;
 /** Sends the booking whose body this is. */
 book:(body:Record<string,unknown>)=>Promise<void>;
 onDenied:()=>void;host:()=>HTMLElement;
 /** The shape of a market's curve at the close, for Price before booking. */
 shape:(market:DraftMarket)=>Promise<CurveShape|string>;
 /** Hears each time the form starts or stops holding unsaved edits. */
 unsavedChanged:()=>void}){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const field=(id:string)=>get<HTMLInputElement|HTMLSelectElement>(id);
 const text=(id:string,value:string)=>{get(id).textContent=publicText(value);};
 // The form is kept on the page's shelf until the panel takes it, and goes back there when it is reset.
 const root=get('futures-new-trade'),shelf=root.parentElement!,form=get<HTMLFormElement>('futures-form'),edits=trackEdits(form,[],options.unsavedChanged);
 const mounted=()=>root.parentElement!==shelf;
 const priced=()=>{const catalog=options.catalog();return catalog?.kind==='priced_contracts'?catalog:undefined;};
 const chosen=()=>priced()?.contracts.find(c=>futuresContractKey(c)===field('futures-contract').value);
 const annotations=tradeAnnotations('futures',options.zone);
 const instrument=futuresInstrumentEditor(get('futures-instrument'),{contract:'futures-contract',lots:'futures-lots',price:'futures-execution',terms:'futures-contract-terms'},{edited:()=>edits.mark()});
 const draft=draftValuation(get('futures-draft-valuation'),{product:'futures',api:options.api,act:options.act,busy:options.busy,denied:options.onDenied,key:()=>JSON.stringify(options.context()),shape:options.shape,markets:made=>futuresDraftMarkets(priced()?.contracts??[],made),
  draft:async()=>{
   const c=chosen();if(!c)throw Error('Choose an exact dated futures contract.');
   return {context:options.context(),curve:c.curve,contractId:c.id,lots:field('futures-lots').value,executionPrice:field('futures-execution').value};
  }});
 form.addEventListener('input',draft.reset);
 // A close-out draft (blotter-workbench.md, Row context menu) heads the form, and its price is labelled with its source until the price, or
 // the contract it is the curve price of, is changed.
 const closeOut=document.createElement('div');closeOut.id='futures-close-out';closeOut.hidden=true;root.prepend(closeOut);
 const priceNote=priceSourceElement('futures-price-source');field('futures-execution').parentElement!.append(priceNote);
 const priceSource=priceSourceNote(form,target=>target===field('futures-execution')||target===field('futures-contract'),priceNote,options.zone);
 // The contract the person chose, kept across a read of the catalog that offers it again.
 let selected='';
 function terms(){
  const c=chosen();text('futures-terms',c?futuresContractDescription(c,options.zone()):'Select a dated contract to see its terms.');
  instrument.render(c,options.zone(),(value,tick)=>amountText(value,'price',tick));
 }
 function choose(id:string,rows:{id:string;name:string}[],value:string){
  const select=get<HTMLSelectElement>(id);select.replaceChildren(new Option('Choose…',''),...rows.map(r=>new Option(publicText(r.name),r.id)));select.value=rows.some(r=>r.id===value)?value:'';
 }
 field('futures-contract').onchange=()=>{selected=field('futures-contract').value;terms();};
 form.onsubmit=event=>{event.preventDefault();void options.act(async()=>{
  const catalog=priced();if(!catalog?.canBook)throw Error('Return to current inputs with trading authority before booking.');
  const c=chosen();if(!c)throw Error('Choose the exact dated contract.');
  const {snapshotId,convention}=options.context();
  await options.book({...annotations.read(),snapshotId,convention,knowledgeMode:'current',bookId:field('futures-book').value,curve:c.curve,contractId:c.id,lots:field('futures-lots').value,executionPrice:field('futures-execution').value});
 });};
 function sync(){
  const catalog=priced(),held=!!options.pending();
  instrument.sync(options.busy()||!catalog);draft.sync();
  get<HTMLFieldSetElement>('futures-fields').disabled=options.busy()||!catalog||held;
  form.hidden=!catalog;
  get<HTMLButtonElement>('futures-book-submit').disabled=options.busy()||!catalog?.canBook||held;
 }
 return {
  /** Fills the form from the catalog of the read on screen, keeping what the person has chosen or typed that it still offers. */
  populate(){
   const catalog=options.catalog();
   if(!catalog)return;
   annotations.populate(catalog.bookingActor,catalog,annotations.read());
   choose('futures-book',catalog.books.map(b=>({id:b.id,name:`${b.portfolio} · ${b.name}`})),field('futures-book').value);
   instrument.populate(priced()?.contracts??[],selected);terms();
   if(priced())selected=field('futures-contract').value;
  },
  /** Puts the form in the panel's form part, empty of what an earlier one held. */
  open(){options.host().append(root);root.hidden=false;edits.clear();sync();},
  /** Takes the form out of the panel and forgets everything it held: its values, its price and the person's edits. */
  reset(){shelf.append(root);root.hidden=true;instrument.clear();draft.reset();form.reset();edits.clear();selected='';closeOut.replaceChildren();closeOut.hidden=true;priceSource.clear();text('futures-terms','Select a dated contract to see its terms.');sync();},
  /**
   * Fills the open form with a close-out the backend drafted: its book, contract, parties, lots and price, each as drafted, for the person to
   * review and book. A draft the form cannot hold as drafted, such as a contract the priced catalog does not offer, is refused, never altered.
   */
  closeOut(drafted:FuturesCloseOut,shown:CloseOutShown){
   const catalog=priced(),t=drafted.terms;if(!catalog)throw Error('The priced futures contracts are not read, so the close-out cannot be shown.');
   const key=futuresContractKey({curve:t.curve,id:t.contractId});
   if(!catalog.contracts.some(c=>futuresContractKey(c)===key))throw Error('The drafted contract is not offered for booking at this close.');
   if(!catalog.books.some(b=>b.id===t.bookId))throw Error('The drafted book is not one you can book for.');
   selected=key;instrument.populate(catalog.contracts,key);terms();field('futures-book').value=t.bookId;
   annotations.populate(catalog.bookingActor,catalog,t as Parameters<typeof annotations.populate>[2]);
   field('futures-lots').value=t.lots;field('futures-execution').value=t.executionPrice;
   renderCloseOut(closeOut,drafted,shown);priceSource.show(drafted.price);draft.reset();edits.clear();sync();
  },
  sync,
  /** The time zone changed: the form's times are written again, and its price, which they were written beside, is cleared. */
  refresh(){annotations.refresh();priceSource.refresh();draft.reset();terms();},
  /** Whether the form is in the panel, and has edits that have not been sent. */
  unsaved:()=>mounted()&&edits.edited(),
  mounted,
 };
}
