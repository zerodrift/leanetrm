type RecordValue=Record<string,unknown>;
type LoanQuantityReview={facts:[string,string][];tables:{label:string;columns:string[];rows:string[][]}[]};

// Source quantities and counterpart cash are managed facts. Display the stated
// gross quantities and recorded revisions without calculating remaining stock.
export function loanQuantityReview(details:RecordValue,selection:RecordValue,instant:(at:string)=>string):LoanQuantityReview{
 const bad=()=>Error('Reread the exact loan, recorded uses, fixed fees and bank payment review.');
 const object=(value:unknown):RecordValue=>{if(!value||typeof value!=='object'||Array.isArray(value))throw bad();return value as RecordValue;};
 const text=(value:unknown)=>{if(typeof value!=='string'||!value.trim())throw bad();return value;};
 const integer=(value:unknown)=>{if(typeof value!=='number'||!Number.isSafeInteger(value)||value<1)throw bad();return String(value);};
 const boolean=(value:unknown)=>{if(typeof value!=='boolean')throw bad();return value;};
 const rows=(value:unknown)=>{if(!Array.isArray(value))throw bad();return value.map(object);};
 const allocation=(value:unknown)=>value==='preserve_unallocated_source_quantity_v1'?'Preserve unused source quantity':
  value==='withdraw_all_source_allocations_v1'?'Withdraw every source allocation':(()=>{throw bad();})();
 const loan=object(details.loan),stock=object(details.stock),debt=object(details.returnObligation),outcome=object(details.outcome);
 const currency=text(details.currency),unit=text(details.quantityUnit),changed=boolean(details.willChange);boolean(details.alreadyRecorded);
 if(selection.feeDisposition!=='retain_fixed_fee_memberships_v1')throw bad();
 const selected=rows(selection.consumptions),uses=rows(outcome.consumptions);
 if(new Set(selected.map(row=>text(row.consumptionId))).size!==selected.length||uses.length!==selected.length||
  new Set(uses.map(row=>text(row.consumptionId))).size!==uses.length)throw bad();
 const corrections=uses.map(row=>{
  const id=text(row.consumptionId),chosen=selected.find(value=>value.consumptionId===id);if(!chosen)throw bad();
  return [id,text(chosen.quantity),boolean(chosen.retracted)?'Withdraw quantity':'Keep active quantity',allocation(chosen.sourceAllocation),
   integer(row.acknowledgedRevision),integer(row.currentRevision),text(row.currentQuantity),boolean(row.changed)?'Record correction':'Existing fact acknowledged'];
 });
 return {facts:[['Effect',changed?'Record loan and resource quantity corrections':'Existing loan quantity fact acknowledged · no new effect'],
  ['Quantity unit',unit],['Source loan revision',integer(loan.expectedRevision)],['Source gross loan quantity',text(loan.quantity)],
  ['Source stock revision',integer(stock.financialRevision)],['Stated received stock quantity',text(stock.quantity)],
  ['Source return revision',integer(debt.financialRevision)],['Source return obligation quantity',text(debt.quantity)],
  [changed?'Reviewed gross loan quantity':'Acknowledged gross loan quantity',text(selection.quantity)],
  ['Source quantity allocation',allocation(selection.sourceAllocation)],['Fixed fee treatment','Retain fixed cash memberships and recorded bank payments'],
  ['Correction evidence',text(selection.evidence)],['Acknowledged loan revision',integer(outcome.acknowledgedRevision)],
  ['Retained current loan revision',integer(outcome.currentRevision)],['Retained current gross loan quantity',text(outcome.currentQuantity)]],
  tables:[{label:'Identified source stock quantities',columns:['Stock','Stated gross quantity','Financial revision'],rows:rows(details.funding).map(row=>
    [text(row.positionId),text(row.quantity),integer(row.financialRevision)])},
   {label:'Recorded use quantities',columns:['Recorded use','Identified stock','Current recorded quantity','Financial revision','Original effective time'],rows:rows(details.consumptions).map(row=>
    [text(row.id),text(row.lot_id),text(row.quantity),integer(row.financialRevision),instant(text(row.effective_at))])},
   {label:'Recorded repayments',columns:['Repayment','Recorded use','Original repayment quantity','Current recorded use quantity','Current use revision','Identified stock','Original effective time'],rows:rows(details.repaymentFacts).map(row=>
    [text(row.repaymentId),text(row.consumptionId),text(row.originalQuantity),text(row.currentQuantity),integer(row.currentRevision),text(row.lotId),instant(text(row.effectiveAt))])},
   {label:'Fixed fee cash memberships',columns:['Cash membership','Amount','Currency','Due','Effective'],rows:rows(details.fees).map(row=>{
    if(text(row.currency_code)!==currency)throw bad();return [text(row.id),text(row.amount),currency.toUpperCase(),instant(text(row.due_at)),instant(text(row.effective_at))];})},
   {label:'Recorded bank payment history',columns:['Cash membership','Business reference','Revision','Amount','Currency','Observed payment time','Evidence'],rows:rows(details.payments).map(row=>
    [text(row.fee_id),text(row.business_key),integer(row.revision),text(row.amount),currency.toUpperCase(),instant(text(row.paid_at)),row.evidence===null?'None':text(row.evidence)])},
   {label:'Reviewed recorded use corrections',columns:['Recorded use','Reviewed quantity','Quantity instruction','Source allocation','Acknowledged revision','Current revision','Current quantity','Effect'],rows:corrections}]};
}
