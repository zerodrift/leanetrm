type RecordValue=Record<string,unknown>;
export type LoanFeeReview={facts:[string,string][];tables:{label:string;columns:string[];rows:string[][]}[]};

// The managed review supplies every amount and gross adjustment. This module
// formats that closed display contract without recomputing balances or fees.
export function loanFeeReview(details:RecordValue,selection:RecordValue,instant:(at:string)=>string):LoanFeeReview{
 const bad=()=>Error('Reread the exact loan fee terms, cash and recorded bank payment review.');
 const object=(value:unknown):RecordValue=>{if(!value||typeof value!=='object'||Array.isArray(value))throw bad();return value as RecordValue;};
 const text=(value:unknown)=>{if(typeof value!=='string'||!value.trim())throw bad();return value;};
 const integer=(value:unknown)=>{if(typeof value!=='number'||!Number.isSafeInteger(value)||value<1)throw bad();return String(value);};
 const date=(value:unknown)=>value===null?'None':instant(text(value));
 const rows=(value:unknown)=>{if(!Array.isArray(value))throw bad();return value.map(object);};
 const terms=object(details.terms),outcome=object(details.outcome),currency=text(outcome.currency),label=currency.toUpperCase();
 if(typeof details.willChange!=='boolean'||typeof details.alreadyRecorded!=='boolean')throw bad();
 const sourceCash=(value:RecordValue)=>{
  if(text(value.currency)!==currency)throw bad();
  return [text(value.amount),label,date(value.dueAt),instant(text(value.effectiveAt))];
 };
 const original=details.rootFee===null?null:object(details.rootFee);
 const changes=rows(outcome.cashChanges).map(value=>{
  const kind=value.kind==='reverse_prior_fee'?'Reverse prior fee':value.kind==='revised_fee'?'Revised fee':(()=>{throw bad();})();
  return [kind,text(value.amount),label,date(value.dueAt),instant(text(value.effectiveAt))];
 });
 return {facts:[['Effect',details.willChange?'Record revised fee terms':'Existing fee terms acknowledged · no new effect'],
  ['Currency',label],['Source fee revision',integer(terms.revision)],['Source total fee',text(terms.amount)],['Source fee due',date(terms.dueAt)],
  ['Source fee effective',instant(text(terms.effectiveAt))],
  [details.willChange?'Revised total fee':'Acknowledged total fee',text(selection.amount)],[details.willChange?'Revised fee due':'Acknowledged fee due',date(selection.dueAt)],
  ['Amendment effective time',instant(text(selection.effectiveAt))],['Amendment evidence',text(selection.evidence)],
  ['Acknowledged fee revision',integer(outcome.acknowledgedRevision)],['Retained current fee revision',integer(outcome.currentRevision)],
  ['Retained current total fee',text(outcome.currentAmount)],['Retained current fee due',date(outcome.currentDueAt)],
  ['Retained current fee effective',instant(text(outcome.currentEffectiveAt))],['Recorded paid amount',text(details.currentPaid)]],
  tables:[{label:'Original cash fee',columns:['Amount','Currency','Due','Effective'],rows:original?[sourceCash(original)]:[]},
   {label:'Existing cash memberships',columns:['Amount','Currency','Due','Effective'],rows:rows(details.cash).map(sourceCash)},
   {label:'Recorded bank payment history',columns:['Business reference','Revision','Amount','Currency','Observed payment time','Evidence'],rows:rows(details.payments).map(value=>
    [text(value.businessKey),integer(value.revision),text(value.amount),label,instant(text(value.paidAt)),value.evidence===null?'None':text(value.evidence)])},
   {label:details.willChange?'Cash adjustments':'Recorded cash adjustments',columns:['Adjustment','Amount','Currency','Due','Effective'],rows:changes}]};
}
