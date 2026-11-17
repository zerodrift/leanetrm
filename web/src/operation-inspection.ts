import {renderRegularTable} from './regular-table.js';
import {displayExactInstant} from './market-view.js';
import {renderDeliveryAssessment} from './delivery-assessment-view.js';

export type OperationInspection={assessment?:unknown;assessmentQuantityLabel?:string;
 overviewSections:{label:string;values:{label:string;value:string}[]}[];
 tables:{label:string;columns:string[];rows:string[][];instantColumns?:number[]}[]};
type Options={key:string;scope:string;label:string;zone:string;busy:()=>boolean;assessmentProfile?:string};
const node=<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};

/** Reviews and saved inspections use the same server-provided facts. Only
 * columns explicitly declared as instants are formatted for the display zone. */
export function renderOperationInspection(host:HTMLElement,value:OperationInspection,o:Options){
 host.replaceChildren();
 if(value.assessment!==undefined&&value.assessment!==null)renderDeliveryAssessment(host,value.assessment,value.assessmentQuantityLabel!,o.zone,o.busy,o.assessmentProfile,o.key);
 if(!Array.isArray(value.overviewSections)||!Array.isArray(value.tables))throw Error('The '+o.label+' inspection is incomplete. Refresh its exact source.');
 for(const section of value.overviewSections){const card=node('section'),list=node('dl');card.className='card';card.append(node('h3',section.label));list.className='lifecycle-review-facts';for(const fact of section.values){if(typeof fact.label!=='string'||typeof fact.value!=='string')throw Error('The '+o.label+' facts are incomplete.');list.append(node('dt',fact.label),node('dd',fact.value));}card.append(list);host.append(card);}
 for(const [index,table]of value.tables.entries()){
  const instants=table.instantColumns??[];
  if(!Array.isArray(instants)||instants.some(column=>!Number.isSafeInteger(column)||column<0||column>=table.columns.length))throw Error('The '+o.label+' table time columns are incomplete.');
  const card=node('section'),grid=node('div');card.className='card';grid.className='table-wrap';card.append(node('h3',table.label),grid);host.append(card);
  const displayRows=table.rows.map(row=>row.map((text,column)=>instants.includes(column)&&text!==''?displayExactInstant(text,o.zone):text));
  renderRegularTable(grid,o.key+'-'+index,o.scope,table.columns,table.rows,[],o.busy,{displayRows});
 }
}
