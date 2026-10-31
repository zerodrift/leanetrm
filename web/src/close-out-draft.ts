// A close-out draft (blotter-workbench.md, Row context menu): the backend's offsetting trade of one position revision, which the booking
// and swap forms open for review. Every term, side, quantity and price is the backend's; the browser only reads, labels and shows them.
import {displayInstant} from './market-view.js';

/** The close the drafted price was read at, as the backend states it: its instant, and a UTC label of its own. */
export type CloseOutPrice={source:'curve_price';closeAt:string;closeLabel:string};
export type FuturesCloseOut={kind:'future';terms:Record<string,unknown>&{bookId:string;curve:string;contractId:string;lots:string;executionPrice:string};price:CloseOutPrice&{value:string;unit:string}};
/** A remaining period of a swap's draft, its quantity the exact decimal or fraction the backend states. */
export type CloseOutPeriod={from:string;to:string;quantity:string;price:string};
export type SwapCloseOut={kind:'financial_swap';terms:Record<string,unknown>;periods:CloseOutPeriod[];settledPeriodsExcluded:number;price:CloseOutPrice};
export type CloseOutDraft=FuturesCloseOut|SwapCloseOut;

/** What a swap's form says of its drafted fixed price, which the backend sets at the curve price and not at par. */
export const swapCloseOutNote='Each fixed price is the floating side’s curve price at this close, without the floating side’s spread, so the offsetting swap is not at par.';

/** The draft read of a position revision, in the context the Blotter's rows on screen were read in. */
export function closeOutRequest(read:{snapshotId:string;contextId:string},h:{tradeId:string;bindingId:string;revision:string;instrumentType:string}){
  return {context:{snapshotId:read.snapshotId,convention:'trading_workspace_v1',knowledgeMode:'retained' as const,contextId:read.contextId},
    tradeId:h.tradeId,bindingId:h.bindingId,revision:Number(h.revision),instrumentType:h.instrumentType};
}

/** The price's source and close, in the zone and words the market date selector uses; the backend's own label for a close the browser cannot read. */
export function closeOutPriceLabel(price:Pick<CloseOutPrice,'closeAt'|'closeLabel'>,zone:string){
  const at=Number.isFinite(Date.parse(price.closeAt))?displayInstant(price.closeAt,zone):price.closeLabel;
  return `Curve price at ${at} close`;
}

const text=(value:unknown)=>typeof value==='string'&&value.length>0;
/** The draft the backend returned, if it is the draft of the position's own instrument with the parts the forms show; otherwise why not. */
export function readCloseOutDraft(value:unknown,kind:'future'|'financial_swap'):CloseOutDraft{
  const draft=value as {kind?:unknown;terms?:unknown;price?:Partial<CloseOutPrice>;periods?:Partial<CloseOutPeriod>[];settledPeriodsExcluded?:unknown}|null;
  if(draft?.kind!==kind)throw Error('The close-out draft is of another instrument than the position it closes.');
  const price=draft.price;
  if(price?.source!=='curve_price'||!text(price.closeAt)||!text(price.closeLabel))throw Error('The close-out draft does not state the curve price and close it was drafted at.');
  if(!draft.terms||typeof draft.terms!=='object'||Array.isArray(draft.terms))throw Error('The close-out draft states no terms.');
  const terms=draft.terms as Record<string,unknown>;
  if(kind==='future'&&!['bookId','curve','contractId','lots','executionPrice'].every(key=>text(terms[key])))throw Error('The close-out draft does not state the future’s book, contract, lots and price.');
  if(kind==='financial_swap'){
    const periods=draft.periods;
    if(!Array.isArray(periods)||!periods.length||periods.some(p=>![p?.from,p?.to,p?.quantity,p?.price].every(text))||!Number.isSafeInteger(draft.settledPeriodsExcluded))
      throw Error('The close-out draft does not state each remaining period exactly.');
  }
  return draft as unknown as CloseOutDraft;
}

/**
 * A failed draft read as the details say it: the backend's own close-out refusal, which the details show in place of a form, marked
 * `shown` so that no banner repeats it, though the operation still rejects with it. Any other failure is not a refusal to show there.
 */
export function closeOutRefusal(error:unknown,reference:string):(Error&{code:'close_out';shown:true})|undefined{
  if(!(error instanceof Error)||(error as {code?:unknown}).code!=='close_out')return undefined;
  return Object.assign(Error(`Close-out of ${reference} was not drafted. ${error.message}`),{code:'close_out' as const,shown:true as const});
}

/** What a form shows of the draft it was opened with: whose close-out it is. */
export type CloseOutShown={reference:string};

/**
 * The label beside a drafted price, `note`, saying where it came from: the curve price at its close, written in the zone shown, and again
 * when that changes (`refresh`). It stays until the person changes a price: any input or change in `form` whose target `isPrice` names.
 * Changing it back does not bring the label back; the price is then the person's.
 */
export function priceSourceNote(form:Pick<EventTarget,'addEventListener'>,isPrice:(target:EventTarget|null)=>boolean,note:Pick<HTMLElement,'hidden'|'textContent'>,zone:()=>string){
  let shown:Pick<CloseOutPrice,'closeAt'|'closeLabel'>|undefined;
  const clear=()=>{shown=undefined;note.hidden=true;note.textContent='';};
  const refresh=()=>{if(shown)note.textContent=closeOutPriceLabel(shown,zone());};
  for(const type of ['input','change'])form.addEventListener(type,event=>{if(shown&&isPrice(event.target))clear();});
  return {show(price:Pick<CloseOutPrice,'closeAt'|'closeLabel'>){shown=price;note.hidden=false;refresh();},refresh,clear};
}
/** The element a price's label is written in, beside the price. */
export function priceSourceElement(id:string){const note=document.createElement('p');note.id=id;note.className='field-help price-source';note.hidden=true;return note;}

/**
 * The head of a form opened on a draft: the position it closes, and what of the draft stays true however the person edits the form. A
 * swap's remaining periods are listed as drafted, for reference, with the settled ones left out and why its price is not par.
 */
export function renderCloseOut(host:HTMLElement,draft:CloseOutDraft,shown:CloseOutShown){
  const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
  const section=node('section');section.className='close-out-draft';section.setAttribute('aria-label','Close-out draft');
  section.append(node('h3',`Close-out of ${shown.reference}`));
  if(draft.kind==='future')section.append(node('p','The offsetting trade of the same contract, in the same book with the same parties. Nothing is booked until you press Book.'));
  else{
    const table=node('table'),head=node('tr');table.className='close-out-periods';
    for(const label of ['Delivery from','Delivery to (exclusive)','Open quantity','Curve price'])head.append(node('th',label));
    table.append(node('thead'),node('tbody'));table.tHead!.append(head);
    for(const p of draft.periods){const row=node('tr');for(const value of [p.from,p.to,p.quantity,p.price])row.append(node('td',value));table.tBodies[0].append(row);}
    const settled=draft.settledPeriodsExcluded,count=draft.periods.length;
    section.append(node('p',`${count===1?'The remaining period':`The ${count} remaining periods`} of the swap, reversed, as drafted:`),table,
      node('p',`${settled?`${settled} settled ${settled===1?'period is':'periods are'} left untouched. `:''}${swapCloseOutNote} Nothing is booked until you book the reviewed terms.`));
  }
  host.replaceChildren(section);host.hidden=false;
}
