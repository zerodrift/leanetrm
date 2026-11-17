import {renderRegularTable} from './regular-table.js';
import {displayExactInstant,friendly} from './market-view.js';

type Reference={revision:{catalogId:string;revisionId:string};code:string};
export type DeliveryGeometry={kind:'interval';from:string;through:string}|{kind:'transfer';from:string;through:null};
export type DeliveryAssessment={profile:string;quantitySpecification:Reference;
 activities:{sourceId:string;stage:string;label:string;geometry:DeliveryGeometry;location:Reference;quantity:string}[];
 measures:{code:string;label:string;value:string}[];
 constraints:{code:string;label:string;state:'within_limit'|'breached'|'unassessed';sourceId:string|null;geometry:DeliveryGeometry|null;observed:string|null;limit:string|null;residual:string|null;basis:'quantity'|'per_elapsed_hour'|'timing'}[];
 capabilities:{code:string;label:string}[]};
const text=(value:unknown):value is string=>typeof value==='string'&&value.trim().length>0;
const reference=(value:Reference)=>!!value&&text(value.code)&&!!value.revision&&text(value.revision.catalogId)&&text(value.revision.revisionId);
const geometry=(value:DeliveryGeometry)=>!!value&&text(value.from)&&(value.kind==='interval'?text(value.through):value.kind==='transfer'&&value.through===null);
const invalid=()=>Error('The delivery assessment did not retain its complete installed profile, geometry and exact quantities. Refresh its source.');

/** Presentation validation only. Source selection, quantities, conservation and
 * constraint states belong to the managed profile; no value is recalculated. */
export function readDeliveryAssessment(value:unknown,profile?:string):DeliveryAssessment{
 const v=value as DeliveryAssessment;
 if(!v||!text(v.profile)||profile!==undefined&&v.profile!==profile||!reference(v.quantitySpecification)||![v.activities,v.measures,v.constraints,v.capabilities].every(Array.isArray))throw invalid();
 if(v.activities.some(a=>!a||!text(a.sourceId)||!text(a.stage)||!text(a.label)||!geometry(a.geometry)||!reference(a.location)||!text(a.quantity))||
  v.measures.some(m=>!m||!text(m.code)||!text(m.label)||!text(m.value))||
  v.capabilities.some(c=>!c||!text(c.code)||!text(c.label))||
  v.constraints.some(c=>!c||!text(c.code)||!text(c.label)||!['within_limit','breached','unassessed'].includes(c.state)||
   !['quantity','per_elapsed_hour','timing'].includes(c.basis)||c.geometry!==null&&!geometry(c.geometry)||c.sourceId!==null&&!text(c.sourceId)||
   [c.observed,c.limit,c.residual].some(n=>n!==null&&!text(n))))throw invalid();
 return structuredClone(v);
}

const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
export function renderDeliveryAssessment(host:HTMLElement,value:unknown,quantityLabel:string,zone:string,busy:()=>boolean,profile?:string,key='delivery'){
 const assessment=readDeliveryAssessment(value,profile);
 if(!text(quantityLabel))throw invalid();
 const card=node('section');card.className='card delivery-assessment';card.append(node('h3','Delivery assessment'),node('p',quantityLabel));
 const timeline=node('div');timeline.dataset.deliveryTimeline='';
 renderRegularTable(timeline,key+'-timeline',assessment.profile+JSON.stringify(assessment.activities),['Activity','Track','Geometry','From / at','Through · excluded','Quantity'],assessment.activities.map(a=>[
  a.label,friendly(a.stage),a.geometry.kind==='interval'?'Interval flow':'Transfer event',displayExactInstant(a.geometry.from,zone),a.geometry.through===null?'—':displayExactInstant(a.geometry.through,zone),a.quantity]),[5],busy);
 card.append(timeline);
 if(assessment.measures.length){const measures=node('dl');measures.className='gas-terms';for(const m of assessment.measures)measures.append(node('dt',m.label),node('dd',m.value));card.append(measures);}
 const states={within_limit:'Within limit',breached:'Breached',unassessed:'Unassessed'};
 if(assessment.constraints.length){
  const constraints=node('div');constraints.dataset.deliveryConstraints='';
  renderRegularTable(constraints,key+'-constraints',assessment.profile+JSON.stringify(assessment.constraints),['Constraint','Assessment','Basis','Observed','Limit','Residual'],assessment.constraints.map(c=>[
   c.label,states[c.state],c.basis==='per_elapsed_hour'?'Per elapsed hour':c.basis==='timing'?'Timing':'Native quantity',c.observed??'—',c.limit??'—',c.residual??'—']),[3,4,5],busy);
  card.append(constraints);
 }
 if(assessment.capabilities.length){const supported=node('div');supported.append(node('h4','Supported profile behavior'));const list=node('ul');for(const c of assessment.capabilities)list.append(node('li',c.label));supported.append(list);card.append(supported);}
 const details=node('details');details.append(node('summary','Exact source references'));const source=node('pre');source.className='ppa-evidence';source.textContent=JSON.stringify({profile:assessment.profile,quantitySpecification:assessment.quantitySpecification,activities:assessment.activities.map(a=>({sourceId:a.sourceId,stage:a.stage,geometry:a.geometry,location:a.location})),constraints:assessment.constraints.map(c=>({code:c.code,sourceId:c.sourceId,geometry:c.geometry}))},null,2);details.append(source);card.append(details);host.append(card);
}
