import {previewTable,type CurveDefinition,type CurveNode} from './custom-curve-model.js';
import {renderChart,clearChart,type ChartSpec} from './chart-view.js';
import {renderRegularTable} from './regular-table.js';
import {displayInstant} from './market-view.js';

type Contract={policy:string;label:string;kinds:string[];selectionModes:string[];sampleSelectionPort:string;sampling:string;corrections:string;description:string};
export type HistoryCatalog={componentHistory?:Contract;calendars:{code:string;name:string}[];operators:{kind:string;label:string}[]};
type Preview={snapshotId:string;at:string;context:{effectiveOn:string;knownAt:string};columns:string[];rows:(string|null)[][];chart:ChartSpec};
type Selection={definition:CurveDefinition;snapshotId:string;id?:string;version?:number};
/** Apply the server's eligibility metadata to authoring controls, without evaluating a curve. */
export function offersComponentHistory(catalog:HistoryCatalog|undefined,definition:CurveDefinition,node:CurveNode):boolean{
 const contract=catalog?.componentHistory;if(!contract?.kinds.includes(node.kind))return false;
 const selection=node.kind==='sample'?definition.nodes.find(n=>n.id===node.inputs[contract.sampleSelectionPort]):node;
 return !!selection&&contract.kinds.includes(selection.kind)&&selection.kind!=='sample'&&(selection.kind!=='select'||contract.selectionModes.includes(String(selection.parameters.mode)));
}
/** A read-only view: sends the chosen window and graph; renders only server-assembled history. */
export function setupComponentHistory(o:{api:<T>(path:string,body:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;selection:()=>Selection;valid:()=>boolean}){
 const element=document.createElement('section');element.id='custom-curves-tab-history';element.className='component-history';
 const form=document.createElement('form');form.className='component-history-controls';
 const select=(id:string,title:string,choices:[string,string][]=[]):HTMLSelectElement=>{
  const label=document.createElement('label');label.textContent=title;const input=document.createElement('select');input.id=id;input.required=true;
  input.append(new Option('Choose…',''),...choices.map(([value,text])=>new Option(text,value)));label.append(input);form.append(label);return input;
 };
 const component=select('component-history-node','Component');
 const mode=select('component-history-mode','Sampling dates',[['prior_calendar','Prior calendar dates'],['prior_business','Prior publication business dates']]);
 const countLabel=document.createElement('label');countLabel.textContent='Number of dates';const count=document.createElement('input');count.id='component-history-count';count.type='number';count.min='1';count.max='366';count.step='1';count.required=true;countLabel.append(count);form.append(countLabel);
 const include=select('component-history-include','Context date',[['true','Include'],['false','Exclude']]);
 const calendar=select('component-history-calendar','Publication calendar');
 const read=document.createElement('button');read.type='submit';read.id='component-history-read';read.textContent='Show history';form.append(read);
 const policy=document.createElement('p');policy.className='field-help';
 const status=document.createElement('p');status.id='component-history-status';status.setAttribute('role','status');
 const chart=document.createElement('div');chart.id='component-history-chart';chart.className='chart';
 const table=document.createElement('div');table.id='component-history-values';table.className='table-wrap';
 element.append(policy,form,status,chart,table);
 let catalog:HistoryCatalog|undefined,token=0;
 function clear(){token++;clearChart(chart);table.replaceChildren();status.textContent='Choose a component and an explicit date window, then show its raw history.';}
 function sync(){
  const business=mode.value==='prior_business';calendar.required=business;calendar.parentElement!.hidden=!business;
  for(const control of [component,mode,count,include,calendar])control.disabled=o.busy()||!catalog?.componentHistory;
  read.disabled=o.busy()||!o.valid()||!catalog?.componentHistory;
 }
 for(const input of [component,mode,count,include,calendar])input.addEventListener('input',()=>{clear();sync();});
 form.onsubmit=event=>{event.preventDefault();if(!form.reportValidity()||read.disabled)return;void o.act(load);};
 async function load(){
  clear();const contract=catalog?.componentHistory;if(!contract)throw Error('Component history is unavailable. Refresh the catalog.');
  const started=token,selection=o.selection(),node=component.value;
  const parameters:Record<string,string>={mode:mode.value,count:count.value,includeContext:include.value,sampling:contract.sampling,corrections:contract.corrections};
  if(mode.value==='prior_business')parameters.calendar=calendar.value;
  const dates:CurveNode={id:'history_dates',kind:'dates',parameters,inputs:{}};
  status.textContent='Reading component history…';
  try{
   const answer=await o.api<{node:string;policy:string;preview:Preview}>('custom-curves/component-history',{...selection,node,dates,policy:contract.policy});
   if(started!==token)return;
   if(answer.node!==node||answer.policy!==contract.policy||answer.preview.snapshotId!==selection.snapshotId)throw Error('The component history does not match the requested selection.');
   const shown=answer.preview;
   const values=previewTable(shown);renderChart(chart,shown.chart);renderRegularTable(table,'component-history',JSON.stringify([selection,node,dates]),values.headers,values.rows,[],o.busy);
   status.textContent=`${contract.label} · ${component.selectedOptions[0].textContent} · As of ${displayInstant(shown.at,o.zone())} · Known ${displayInstant(shown.context.knownAt,o.zone())}`;
  }catch(error){if(started!==token)return;clearChart(chart);table.replaceChildren();status.textContent='History unavailable: '+(error instanceof Error?error.message:String(error));throw error;}
 }
 return {element,clear,sync,
  choose(node:string){component.value=node;clear();sync();},
  reset(){component.value='';mode.value='';count.value='';include.value='';calendar.value='';clear();sync();},
  update(next:HistoryCatalog|undefined,definition:CurveDefinition){
   catalog=next;const node=component.value,calendarCode=calendar.value;
   const choices=definition.nodes.filter(n=>offersComponentHistory(next,definition,n));
   component.replaceChildren(new Option('Choose…',''),...choices.map(n=>new Option(`${next?.operators.find(op=>op.kind===n.kind)?.label??n.kind} · ${n.id}`,n.id)));component.value=node;
   calendar.replaceChildren(new Option('Choose…',''),...(next?.calendars??[]).map(c=>new Option(c.name,c.code)));calendar.value=calendarCode;
   policy.textContent=next?.componentHistory?`${next.componentHistory.label}. ${next.componentHistory.description}`:'Component history is unavailable.';sync();
  }
 };
}
