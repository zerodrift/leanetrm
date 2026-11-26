import type {DeliveryQuantityEvidence,TicketChoice} from './ticket-types.js';
import {numberText} from './schedule-grid.js';

export type SwingContract={source:string;referenceQuantity:string;priceUnit:string;days:{day:string;exerciseAt:string}[]};
type SwingPeriod={from:string;through:string;days:number;limitPeriod:string;totalBasis:string|null;changeCount:number|null;countRule:string|null;
 appliedLower:string|null;appliedUpper:string|null;remainingLower:string|null;remainingUpper:string|null;elected:string;changesUsed:number;
 remainingMinimum:string;remainingMaximum:string;missingDays:number;blockedDays:number;nextDeadline:string|null;planned:string|null;plannedChanges:number|null};
export type SwingDay={day:string;exerciseAt:string;state:string;electedQuantity:string|null;selectedQuantity:string|null;
 revisionId:string|null;commercialEvidence:string|null;quantityEvidence:DeliveryQuantityEvidence[];operationalRecords:{kind:string;status:string;quantity:string;evidence:DeliveryQuantityEvidence}[];
 forward:string|null;physicalPerUnit:string|null;fixedPerUnit:string|null;plannedQuantity:string|null};
export type SwingReport={knownAt:string;right:number;holder:string;referenceQuantity:string;planningStep:string;counted:string|null;scope:string;modeledDays:number;
 unassessedDays:number;comparison:{model:string;amount:string;plannedDays:number;unassessedDays:number}|null;
 periods:SwingPeriod[];dailyMinimum:string;dailyMaximum:string;nextDeadline:string|null;
 pricing:{model:string;dayCount:string;numericalPolicy:string;marksConvention:string};
 forwardInputs:{market:{curve:{code:string};contract:{id:string}};from:string;to:string;price:string;observations:{id:string}[]}[];
 marks:{curve:{code:string;revision:{revisionId:string}};month:string;revisionId:string;volatility:string}[];
 days:SwingDay[];work:{factorSteps:number;maximumStates:number;operations:number;peakBytes:number};
 estimate:{operations:number;peakBytes:number;maximumPriceNodes:number}};
export type SwingAction={allowed:boolean;reason:string|null;label:string;preparePath:string;recordPath:string;tradeId:string;instrumentId:string;
 expectedRevision:number;readContextId:string|null;right:number;holder:string;planningStep:string;dailyMinimum:string;dailyMaximum:string;
 referenceQuantity:string;periods:SwingPeriod[];
 parties:TicketChoice[];decisions:TicketChoice[];
 days:{day:string;exerciseAt:string;head:string|null;electedQuantity:string|null;effectiveRevision:string|null;evidence:string|null}[]};

const el=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const node=document.createElement(tag);if(text!==undefined)node.textContent=text;return node;};
const exact=(value:string|null)=>value===null?'—':numberText(value);
const range=(lo:string|null,hi:string|null)=>`${exact(lo)} – ${exact(hi)}`;
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
function budgets(periods:SwingPeriod[],unit:string,instant:(at:string)=>string){
 return table('Period limits and elections',['Period','Total basis',`Applied limits (${unit})`,`Elected (${unit})`,
  `Remaining limits (${unit})`,`Remaining feasible (${unit})`,'Changes used / allowed',`Forward plan (${unit})`,
  'Planned changes','Missed / blocked days','Next deadline'],periods.map(p=>[
   `${p.from} – ${p.through} · ${p.limitPeriod}`,p.totalBasis??'—',range(p.appliedLower,p.appliedUpper),exact(p.elected),range(p.remainingLower,p.remainingUpper),
   range(p.remainingMinimum,p.remainingMaximum),`${p.changesUsed} / ${p.changeCount??'unlimited'}${p.countRule?' · '+p.countRule:''}`,exact(p.planned),String(p.plannedChanges??'—'),
   `${p.missingDays} / ${p.blockedDays}`,p.nextDeadline?instant(p.nextDeadline):'—']),[2,3,4,5,6,7,8,9]);
}
export function swingDetails(right:SwingReport,unit:string,currency:string,instant:(at:string)=>string,knownAt?:string,contract?:SwingContract){
 const section=el('section');section.className='ticket-swing-detail';
 const head=el('div');head.className='swing-heading';head.append(el('h3','Remaining swing right'),el('strong',`${exact(right.counted)} ${currency}`));section.append(head);
 const cutoff=right.knownAt??knownAt;
 if(cutoff)section.append(el('small',`Knowledge cutoff · ${instant(cutoff)}`));
 section.append(facts([['Reference daily quantity',`${exact(right.referenceQuantity)} ${unit}`],['Modeled days',String(right.modeledDays)],
  ['Unassessed days',String(right.unassessedDays)],['Next deadline',right.nextDeadline?instant(right.nextDeadline):'—']]),budgets(right.periods,unit,instant));
 const assessment=(state:string)=>state==='missing_election'?'Missing election':state==='period_unassessed'?'Unassessed period':state==='undecided'?'Undecided · contingent':state==='elected'?'Elected':state;
 const commercial=(day:SwingDay)=>{
  if(day.electedQuantity!==null)return `Elected · ${exact(day.electedQuantity)}`;
  if(day.revisionId)return 'Withdrawn';
  return assessment(day.state);
 };
 const commercialEvidence=(d:SwingDay)=>{
  return [d.revisionId,d.commercialEvidence].filter(Boolean).join(' · ')||'—';
 };
 section.append(table('Daily elections and delivery', ['Gas day','Election deadline',`Commercial (${unit})`,`Selected (${unit})`,'Commercial evidence','Delivery evidence','Assessment'],
  right.days.map(d=>[d.day,instant(d.exerciseAt),commercial(d),exact(d.selectedQuantity),
   commercialEvidence(d),
   d.operationalRecords.map(o=>`${o.kind} · ${o.status} · ${exact(o.quantity)} · ${o.evidence.reference} · ${o.evidence.revisionId}`).join('\n')||'—',assessment(d.state)]),[2,3]));
 const drawer=el('details');drawer.className='swing-quant';drawer.append(el('summary','Quant details'));
 drawer.append(facts([['Model',right.pricing.model],['Price assumption','One common lognormal price factor'],['Day count',right.pricing.dayCount],
  ['Numerical grid',right.pricing.numericalPolicy],['Volatility convention',right.pricing.marksConvention],['Scenario','Base market · no scenario bump'],
  ['Daily limits',`${range(right.dailyMinimum,right.dailyMaximum)} ${unit}`],['Planning step',`${exact(right.planningStep)} ${unit}`],
  ['Time steps',String(right.work.factorSteps)],['Maximum price nodes',String(right.estimate.maximumPriceNodes)],
  ['Maximum volume states',String(right.work.maximumStates)],['Counted operations',String(right.work.operations)],
  ['Estimated operations',String(right.estimate.operations)],['Modeled peak bytes',String(right.estimate.peakBytes)]]));
 if(right.forwardInputs.length)drawer.append(table('Forward inputs',['From','To','Curve','Contract','Price','Observations'],
  right.forwardInputs.map(f=>[f.from,f.to,f.market.curve.code,f.market.contract.id,exact(f.price),f.observations.map(o=>o.id).join(', ')]),[4]));
 if(right.marks.length)drawer.append(table('Monthly volatility marks',['Month','Curve','Definition revision','Volatility','Mark revision'],
  right.marks.map(m=>[m.month,m.curve.code,m.curve.revision.revisionId,exact(m.volatility),m.revisionId]),[3]));
 drawer.append(el('h4','Intrinsic comparison'),el('p',`Current-curve plan for ${right.comparison?.plannedDays??0} days · ${exact(right.comparison?.amount??null)} ${currency}`),
  table('Current forward strip and intrinsic plan',['Gas day',contract?`Forward (${contract.priceUnit})`:'Forward',`Physical per unit (${currency})`,`Fixed per unit (${currency})`,`Intrinsic plan (${unit})`],
   right.days.map(d=>[d.day,exact(d.forward),exact(d.physicalPerUnit),exact(d.fixedPerUnit),exact(d.plannedQuantity)]),[1,2,3,4]));
 section.append(drawer);return section;
}

export function swingContractDetails(contract:SwingContract,unit:string,instant:(at:string)=>string,knownAt?:string,action?:SwingAction){
 const detail=el('section');detail.className='ticket-swing-detail';
 detail.append(el('h3',`Swing elections · ${contract.source}`));
 if(knownAt)detail.append(el('small',`Knowledge cutoff · ${instant(knownAt)}`));
 if(action)detail.append(...retainedElections(action,unit,instant));
 else detail.append(el('p',`Reference daily quantity · ${exact(contract.referenceQuantity)} ${unit}`),
  table('Contract deadlines',['Gas day','Election deadline'],contract.days.map(d=>[d.day,instant(d.exerciseAt)])));
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
 return retainedElections(selected,unit,at=>at);
}

function retainedElections(selected:SwingAction,unit:string,instant:(at:string)=>string){
 return [facts([['Reference daily quantity',`${exact(selected.referenceQuantity)} ${unit}`],['Daily limits',`${range(selected.dailyMinimum,selected.dailyMaximum)} ${unit}`],
  ['Planning step',`${exact(selected.planningStep)} ${unit}`]]),budgets(selected.periods,unit,instant),
  table('Retained election days',['Gas day','Deadline',`Elected (${unit})`,'Effective revision','Evidence','Latest known head'],selected.days.map(d=>[d.day,instant(d.exerciseAt),
   d.electedQuantity!==null?`Elected · ${exact(d.electedQuantity)}`:d.effectiveRevision?'Withdrawn':'Undecided',d.effectiveRevision??'—',d.evidence??'—',d.head??'—']),[2])];
}
