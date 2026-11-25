import {publicText} from './market-view.js';

export function node<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string){const n=document.createElement(tag);if(text!==undefined)n.textContent=publicText(text);return n;}
export type Cells=(string|HTMLElement)[];
export function table(headers:string[],rows:(Cells|(()=>Cells))[]){
 const wrap=node('div');wrap.className='table-wrap';const t=node('table'),head=node('tr');headers.forEach(h=>head.append(node('th',h)));const thead=node('thead');thead.append(head);t.append(thead);
 const body=node('tbody');t.append(body);wrap.append(t);let page=0;
 const previous=node('button','Previous rows'),next=node('button','Next rows'),count=node('span');previous.type=next.type='button';previous.className=next.className='secondary';
 function draw(){body.replaceChildren();for(const item of rows.slice(page*100,page*100+100)){const row=node('tr'),cells=typeof item==='function'?item():item;for(const value of cells){const cell=node('td');cell.append(typeof value==='string'?document.createTextNode(publicText(value)):value);row.append(cell);}body.append(row);}previous.disabled=page===0;next.disabled=(page+1)*100>=rows.length;count.textContent=`Rows ${rows.length?page*100+1:0}–${Math.min(rows.length,(page+1)*100)} of ${rows.length}`;}
 previous.onclick=()=>{page--;draw();};next.onclick=()=>{page++;draw();};draw();
 if(rows.length>100){const paging=node('div');paging.className='actions';paging.append(previous,count,next);wrap.append(paging);}return wrap;
}

export type SwingRiskSample={right:number;counted:string;work:{evaluatedActions:number}};
export function swingRiskSamples<Lane extends string>(positions:readonly {tradeReference:string;values:{currency:string};
 swingRights?:Record<Lane,SwingRiskSample[]>}[],lanes:readonly [Lane,string][]){
 const rows=positions.flatMap(p=>lanes.flatMap(([lane,label])=>(p.swingRights?.[lane]??[]).map(r=>[
  p.tradeReference,String(r.right+1),label,r.counted,p.values.currency,String(r.work.evaluatedActions)])));
 if(!rows.length)return null;
 const detail=node('details');detail.className='swing-risk-samples';
 detail.append(node('summary','Swing right repricing'),table(['Trade','Right','Scenario','Remaining-right value','Currency','Evaluated actions'],rows));return detail;
}
