import {amountText as managedAmountText} from './amount-display.js';
import type {Cell} from './options-chain.js';
import {type SurfaceSensitivities} from './vol-surface-view.js';

// Booked option positions as the Options page lists them. Every figure, subtotal and total is the positions read's: this
// module keeps the backend's order and formats exact text for display, and adds nothing up.

/** One figure of the positions read: an exact amount, or the code of why there is none. */
export type Reference={code:string;revision:{catalogId:string;revisionId:string}};
export type Figure={amount:string|null;missing:string|null};
type Measures={value:Figure;delta:Figure;gamma:Figure;vega:Figure;theta:Figure};
export type PositionRead={tradeId:string;bindingId:string;tradeReference:string;bookId:string;book:string;series:string;seriesReference:Reference;currencyReference:Reference;right:'call'|'put';strike:string;openLots:string;
 state:'analysed'|'unavailable'|'notCalibrated';reason:string|null}&Measures;
/** The positions on one underlying future, with the selection their product's surface was fitted under, or none without a surface. */
export type PositionGroupRead={underlyingSeries:string;expiryAt:string;product:string;productName:string;definitionRevisionId:string;currency:string;currencyReference:Reference;priceUnit:string;quantityUnit:string;
 sensitivities:Pick<SurfaceSensitivities,'policy'|'dayTo'>|null;rows:PositionRead[];subtotal:Measures};
/** The positions read: positions by underlying future, money totals per currency, and the open lots held in each series. */
export type OptionPositionsRead={model:string;groups:PositionGroupRead[];totals:{currency:string;currencyReference:Reference;value:Figure;theta:Figure}[];held:{series:string;seriesReference:Reference;openLots:string}[]};

type Cells={value:Cell;delta:Cell;gamma:Cell;vega:Cell;theta:Cell};
export type PositionLine=Pick<PositionRead,'tradeId'|'bindingId'|'tradeReference'|'book'|'series'|'right'|'strike'|'openLots'|'state'|'reason'>&Cells;
export type PositionGroup=Pick<PositionGroupRead,'underlyingSeries'|'expiryAt'|'product'|'productName'|'definitionRevisionId'|'currency'|'currencyReference'|'priceUnit'|'quantityUnit'|'sensitivities'>&{rows:PositionLine[];subtotal:Cells};

// Cash and quantity figures use the shared managed formatter, preserving raw exact values.
const money=(figure:Figure):Cell=>figure.amount!==null?{text:managedAmountText(figure.amount,'money'),missing:null}:{text:'—',missing:figure.missing??'incomplete'};
const quantity=(figure:Figure):Cell=>figure.amount!==null?{text:managedAmountText(figure.amount,'quantity'),missing:null}:{text:'—',missing:figure.missing??'incomplete'};
const cells=(measures:Measures):Cells=>({value:money(measures.value),delta:quantity(measures.delta),gamma:quantity(measures.gamma),vega:money(measures.vega),theta:money(measures.theta)});

/** The read as the page lists it: the backend's groups and rows in its order, with each figure, subtotal and total formatted. */
export function positionTable(read:OptionPositionsRead):{groups:PositionGroup[];totals:{currency:string;currencyReference:Reference;value:Cell;theta:Cell}[]}{
 return {
  groups:read.groups.map(group=>({underlyingSeries:group.underlyingSeries,expiryAt:group.expiryAt,product:group.product,productName:group.productName,
   definitionRevisionId:group.definitionRevisionId,currency:group.currency,currencyReference:group.currencyReference,priceUnit:group.priceUnit,quantityUnit:group.quantityUnit,sensitivities:group.sensitivities,subtotal:cells(group.subtotal),
   rows:group.rows.map(row=>({tradeId:row.tradeId,bindingId:row.bindingId,tradeReference:row.tradeReference,book:row.book,series:row.series,right:row.right,strike:row.strike,
    openLots:row.openLots,state:row.state,reason:row.reason,...cells(row)}))})),
  totals:read.totals.map(total=>({currency:total.currency,currencyReference:total.currencyReference,value:money(total.value),theta:money(total.theta)}))};
}
