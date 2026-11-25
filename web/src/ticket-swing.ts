import type {DeliveryQuantityEvidence,TicketChoice} from './ticket-types.js';
import {numberText} from './schedule-grid.js';

export type SwingContract={source:string;referenceQuantity:string;priceUnit:string;days:{day:string;exerciseAt:string}[]};
export type SwingDay={day:string;exerciseAt:string;state:string;electedQuantity:string|null;selectedQuantity:string|null;
 revisionId:string|null;quantityEvidence:DeliveryQuantityEvidence[];forward:string|null;uncertainPerLot:string|null;certainPerLot:string|null;intrinsicQuantity:string|null};
export type SwingReport={right:number;holder:string;increment:string;counted:string;intrinsic:string;committedQuantity:string;
 minimumQuantity:string;maximumQuantity:string;remainingMinimumQuantity:string;remainingMaximumQuantity:string;dailyMinimumQuantity:string;dailyMaximumQuantity:string;
 nextDeadline:string|null;pricing:{model:string;dayCount:string;numericalPolicy:string;marksConvention:string};
 marks:{curve:{code:string;revision:{revisionId:string}};month:string;revisionId:string;volatility:string}[];
 days:SwingDay[];work:{timeSteps:number;maximumPriceNodes:number;maximumVolumeStates:number;evaluatedActions:number}};
export type SwingAction={allowed:boolean;reason:string|null;label:string;preparePath:string;recordPath:string;tradeId:string;instrumentId:string;
 expectedRevision:number;readContextId:string|null;right:number;holder:string;increment:string;dailyMinimum:string;dailyMaximum:string;
 referenceQuantity:string;totalMinimum:string;totalMaximum:string;committed:string;remainingMinimum:string;remainingMaximum:string;
 parties:TicketChoice[];decisions:TicketChoice[];
 days:{day:string;exerciseAt:string;head:string|null;electedQuantity:string|null;effectiveRevision:string|null;evidence:string|null}[]};

const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};
const exact=(value:string|null)=>value===null?'—':numberText(value);
const range=(lo:string,hi:string)=>`${exact(lo)} – ${exact(hi)}`;
function table(caption:string,labels:string[],rows:string[][],numeric:number[]=[]){
 const wrap=el('div'),grid=el('table'),head=el('thead'),titles=el('tr'),body=el('tbody');
 wrap.className='swing-table table-wrap';wrap.tabIndex=0;wrap.setAttribute('role','region');wrap.setAttribute('aria-label',caption);
 labels.forEach((label,i)=>{const th=el('th',label);th.scope='col';if(numeric.includes(i))th.className='numeric';titles.append(th);});
 head.append(titles);grid.append(el('caption',caption),head,body);wrap.append(grid);
 for(const values of rows){const row=el('tr');values.forEach((value,i)=>{const td=el('td',value);if(numeric.includes(i))td.classList.add('numeric');
   if(value.startsWith('Elected · '))td.classList.add('elected');else if(value.startsWith('Undecided'))td.classList.add('undecided');row.append(td);});body.append(row);}
 return wrap;
}
function facts(rows:[string,string][]){
 const list=el('dl');list.className='swing-facts';
 for(const [label,value] of rows){const cell=el('div');cell.append(el('dt',label),el('dd',value));list.append(cell);}
 return list;
}
export function swingDetails(right:SwingReport,unit:string,currency:string,instant:(at:string)=>string,knownAt?:string,contract?:SwingContract,action?:SwingAction){
 const section=el('section');section.className='ticket-swing-detail';
 const head=el('div');head.className='swing-heading';head.append(el('h3','Remaining swing right'),el('strong',`${exact(right.counted)} ${currency}`));section.append(head);
 if(knownAt)section.append(el('small',`Knowledge cutoff · ${instant(knownAt)}`));
 section.append(facts([...(contract?[['Reference daily quantity',`${exact(contract.referenceQuantity)} ${unit}`]] as [string,string][]:[]),['Term budget',`${range(right.minimumQuantity,right.maximumQuantity)} ${unit}`],
  ['Committed',`${exact(right.committedQuantity)} ${unit}`],['Remaining feasible',`${range(right.remainingMinimumQuantity,right.remainingMaximumQuantity)} ${unit}`],
  ['Next deadline',right.nextDeadline?instant(right.nextDeadline):'All decisions recorded']]));
 const commercial=(day:SwingDay)=>day.electedQuantity===null?(action?.days.find(d=>d.day===day.day)?.effectiveRevision?'Withdrawn · contingent':'Undecided · contingent'):`Elected · ${exact(day.electedQuantity)}`;
 section.append(table('Daily elections and delivery', ['Gas day','Election deadline',`Commercial (${unit})`,`Selected (${unit})`,'Commercial evidence','Delivery evidence'],
  right.days.map(d=>[d.day,instant(d.exerciseAt),commercial(d),exact(d.selectedQuantity),
   [d.revisionId,...d.quantityEvidence.filter(e=>e.revisionId===d.revisionId).map(e=>e.reference)].filter(Boolean).join(' · ')||'—',
   d.quantityEvidence.filter(e=>e.revisionId!==d.revisionId).map(e=>`${e.source} · ${e.reference} · ${e.revisionId}`).join('\n')||'—']),[2,3]));
 const drawer=el('details');drawer.className='swing-quant';drawer.append(el('summary','Quant details'));
 drawer.append(facts([['Model',right.pricing.model],['Price assumption','One common lognormal price factor'],['Day count',right.pricing.dayCount],
  ['Numerical grid',right.pricing.numericalPolicy],['Volatility convention',right.pricing.marksConvention],['Scenario','Base market · no scenario bump'],
  ['Daily limits',`${range(right.dailyMinimumQuantity,right.dailyMaximumQuantity)} ${unit}`],['Increment',`${exact(right.increment)} ${unit}`],
  ['Time steps',String(right.work.timeSteps)],['Maximum price nodes',String(right.work.maximumPriceNodes)],
  ['Maximum volume states',String(right.work.maximumVolumeStates)],['Evaluated actions',String(right.work.evaluatedActions)]]));
 if(right.marks.length)drawer.append(table('Monthly volatility marks',['Month','Curve','Definition revision','Volatility','Mark revision'],
  right.marks.map(m=>[m.month,m.curve.code,m.curve.revision.revisionId,exact(m.volatility),m.revisionId]),[3]));
 drawer.append(el('h4','Intrinsic comparison'),el('p',`Current-curve plan value · ${exact(right.intrinsic)} ${currency}`),
  table('Current forward strip and intrinsic plan',['Gas day',contract?`Forward (${contract.priceUnit})`:'Forward',`Uncertain per increment (${currency})`,`Certain per increment (${currency})`,`Intrinsic plan (${unit})`],
   right.days.map(d=>[d.day,exact(d.forward),exact(d.uncertainPerLot),exact(d.certainPerLot),exact(d.intrinsicQuantity)]),[1,2,3,4]));
 section.append(drawer);return section;
}

export function swingContractDetails(contract:SwingContract,unit:string,instant:(at:string)=>string,knownAt?:string){
 const detail=el('details');detail.className='swing-quant';
 detail.append(el('summary',`Resolved election deadlines · ${contract.source}`),
  el('p',`Reference daily quantity · ${exact(contract.referenceQuantity)} ${unit}`));
 if(knownAt)detail.append(el('small',`Knowledge cutoff · ${instant(knownAt)}`));
 detail.append(table('Contract deadlines',['Gas day','Election deadline'],contract.days.map(d=>[d.day,instant(d.exerciseAt)])));
 return detail;
}

export function swingRangeFields(selected:SwingAction,unit:string,field:<T extends HTMLInputElement|HTMLSelectElement>(name:string,label:string,control:T)=>T,
 options:(control:HTMLSelectElement,choices:TicketChoice[])=>void){
 const days=selected.days.map(d=>({value:d.day,label:`${d.day} · deadline ${d.exerciseAt}`}));
 options(field('firstDay','First gas day',el('select')),days);
 options(field('lastDay','Last gas day',el('select')),days);
 const decision=field('decision','Instruction',el('select'));options(decision,selected.decisions);
 const quantity=field('quantity',`Quantity (${unit})`,el('input'));quantity.inputMode='decimal';
 const update=()=>{quantity.disabled=decision.value!=='elect';quantity.required=!quantity.disabled;};
 decision.onchange=update;update();
 options(field('exercisingParty','Exercising party',el('select')),selected.parties);
 field('submittedAt','Submitted instant · UTC',el('input')).placeholder='YYYY-MM-DDTHH:mm:ssZ';
 return [facts([['Reference daily quantity',`${exact(selected.referenceQuantity)} ${unit}`],['Daily limits',`${range(selected.dailyMinimum,selected.dailyMaximum)} ${unit}`],
  ['Increment',`${exact(selected.increment)} ${unit}`],['Term budget',`${range(selected.totalMinimum,selected.totalMaximum)} ${unit}`],
  ['Committed',`${exact(selected.committed)} ${unit}`],['Remaining feasible',`${range(selected.remainingMinimum,selected.remainingMaximum)} ${unit}`]]),
  table('Retained election days',['Gas day','Deadline',`Elected (${unit})`,'Evidence','Latest known head'],selected.days.map(d=>[d.day,d.exerciseAt,
   d.electedQuantity!==null?`Elected · ${exact(d.electedQuantity)}`:d.effectiveRevision?'Withdrawn':'Undecided',d.evidence??'—',d.head??'—']),[2])];
}
