import type {LoanFeeReview} from './gas-loan-fee-review.js';

type Value=Record<string,unknown>;
// Every custody, reservation and cash amount is managed review output. Keep its
// decimal text exact; this presentation adapter never computes a balance.
export function custodyReview(details:Value,instant:(at:string)=>string):LoanFeeReview{
 const bad=()=>Error('Reload the complete custody, reservation, title and fee cash review before recording.');
 const object=(value:unknown):Value=>{if(!value||typeof value!=='object'||Array.isArray(value))throw bad();return value as Value;};
 const text=(value:unknown)=>{if(typeof value!=='string'||!value.trim())throw bad();return value;};
 const date=(value:unknown)=>value===null?'None':instant(text(value));
 const effect=object(details.effects);
 if(typeof details.willChange!=='boolean'||typeof effect.acknowledgementOutsideSource!=='boolean'||effect.acknowledgementOutsideSource&&details.willChange||effect.titleRetainedByCustomer!==true||effect.physicalActivityUnchanged!==true||
  typeof effect.feeCashUnchanged!=='boolean'||!Array.isArray(effect.allocations))throw bad();
 const facts:[string,string][]=[['Effect',details.willChange?'Record observed custody':'Existing custody fact acknowledged · no new effect'],
  ['Performed custody quantity',text(effect.performedQuantity)],['Quantity specification',text(effect.quantitySpecification)],
  ['Custody before',text(effect.custodyBefore)],['Custody after',text(effect.custodyAfter)],
  ['Reserved before',text(effect.reservedBefore)],['Reserved after',text(effect.reservedAfter)],
  ['Owned quantity change',text(effect.ownedQuantityChange)],['Title ownership','Retained by customer'],['Physical activity','Unchanged'],
  ['New signed fee cash',text(effect.feeAmount)],['Currency',text(effect.currency).toUpperCase()],
  ['Existing fee cash',effect.feeCashUnchanged?'Unchanged':'Independent fee cash recorded'],
  ['Observed custody time',instant(text(effect.effectiveAt))],['Contractual return due',instant(text(effect.returnAt))],['Fee due',date(effect.feeDueAt)]];
 if(effect.acknowledgementOutsideSource)facts.push(['Selected view','Existing fact acknowledged beyond this selected business or knowledge view; this view was not refreshed.']);
 if(!details.willChange)facts.push(['Acknowledged custody quantity',text(effect.acknowledgedQuantity)]);
 return{facts,tables:[{label:'Custody lot reservations',columns:['Owned lot','Reservation','Custody quantity','Remainder reservation','Remainder quantity'],
  rows:effect.allocations.map(value=>{const row=object(value);return[text(row.lotId),text(row.reservationId),text(row.quantity),row.remainderReservationId===null?'None':text(row.remainderReservationId),text(row.remainderQuantity)];})}]};
}
