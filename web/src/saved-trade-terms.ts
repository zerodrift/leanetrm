// Presentation of booked values and the backend's amendment decision. This module
// does not infer contract changes, compare economics or authorize a migration.
export type ChangeReview={action:'amend_trade'|'keep_booked_terms';bookedRevision:number;changes:{category:'contract_terms'|'forecast_source'|'trade_details';field:string;before:string|null;after:string|null}[]};
export const unavailableValueLabel=(label:string,booked=true)=>`${label} · ${booked?'booked':'entered'} value (unavailable here)`;
export function markUnavailableValue(option:HTMLOptionElement,booked=true){option.disabled=true;option.dataset.valueOrigin=booked?'booked':'entered';}
export function explainUnavailableValue(select:HTMLSelectElement){
 const id=select.id+'-booked-value-note';document.getElementById(id)?.remove();
 const origin=select.selectedOptions[0]?.dataset.valueOrigin;if(!origin)return;
 const details=document.createElement('details');details.id=id;details.className='field-help booked-value-note';
 const summary=document.createElement('summary');summary.textContent=origin==='booked'?'Why is this booked value shown?':'Why is this entered value unavailable?';
 const explanation=document.createElement('p');explanation.textContent=origin==='entered'?'This is an unsaved selection. It is not offered for the selected index, observation rules and market context. Choose an available value before previewing.':'This value is saved on the trade. It is not offered for the selected index, observation rules and market context. Your booked terms have not changed. To propose different terms, select an available choice and preview an amendment.';
 details.append(summary,explanation);select.after(details);
}
const groups={contract_terms:'Contractual terms',forecast_source:'Forecast source saved on this trade',trade_details:'Trade details'};
export function changeReviewRows(review:ChangeReview){
 if(!['amend_trade','keep_booked_terms'].includes(review.action)||review.action==='keep_booked_terms'&&review.changes.length!==0||review.action==='amend_trade'&&review.changes.length===0||!Number.isInteger(review.bookedRevision)||review.bookedRevision<1)throw Error('The amendment review is unavailable. Preview the change again.');
 return review.changes.map(change=>{
  if(!Object.hasOwn(groups,change.category))throw Error('The amendment review contains an unsupported change type.');
  return {category:groups[change.category],field:change.field,before:change.before??'None',after:change.after??'None'};
 });
}
export function changeFieldLabel(field:string){
 const parts=field.split(' / '),labels:string[]=[];
 for(let n=0;n<parts.length;n++){
  const part=parts[n];if(part==='Scheduled')continue;
  if(part==='periods'&&/^\d+$/.test(parts[n+1]??'')){labels.push(`Period ${parts[++n]}`);continue;}
  labels.push(({first:'First side',second:'Second side',amount:'Quantity'} as Record<string,string>)[part]??part.charAt(0).toUpperCase()+part.slice(1));
 }
 return labels.join(' / ');
}
export function renderChangeReview(host:HTMLElement,review:ChangeReview|null|undefined){
 host.replaceChildren();host.hidden=!review;if(!review)return;
 const rows=changeReviewRows(review),heading=document.createElement('h3'),note=document.createElement('p');
 heading.textContent=review.action==='keep_booked_terms'?'Booked terms are unchanged':`Review changes to booked trade revision ${review.bookedRevision}`;
 note.textContent=review.action==='keep_booked_terms'?'No trade-term changes were found. The booked revision is unchanged and there is no amendment to submit.':'These changes require a trade amendment. Submitting appends a new revision; the booked revision and earlier results remain available. Price or index edits have not automatically changed the booked terms. Close this editor and discard the proposal to keep them.';
 if(review.action==='keep_booked_terms'){host.append(heading,note);return;}
 const wrapper=document.createElement('div');wrapper.className='table-wrap';const table=document.createElement('table');
 const caption=document.createElement('caption');caption.textContent='Booked terms compared with the proposed amendment';table.append(caption);
 const head=document.createElement('thead'),titles=document.createElement('tr');for(const label of ['Field','Booked','Proposed']){const cell=document.createElement('th');cell.scope='col';cell.textContent=label;titles.append(cell);}head.append(titles);table.append(head);
 const body=document.createElement('tbody');
 for(const row of rows){
  const tr=document.createElement('tr'),field=document.createElement('td'),label=document.createElement('span'),category=document.createElement('small');
  field.dataset.field=row.field;label.textContent=changeFieldLabel(row.field);category.className='change-category';category.textContent=row.category;field.append(label,category);tr.append(field);
  for(const value of [row.before,row.after]){const td=document.createElement('td');td.textContent=value;tr.append(td);}body.append(tr);
 }
 table.append(body);wrapper.append(table);host.append(heading,note,wrapper);
}
