import {mountMarkTerm,termOptions} from './curve-mark-charts.js';
import {cellPresentation,keyId,keyLabel,type DraftCell,type HistoryEntry,type LayoutCell,type Setup} from './curve-mark-model.js';

function node<K extends keyof HTMLElementTagNameMap>(tag:K,text?:string,css?:string){
 const element=document.createElement(tag);
 if(text!==undefined)element.textContent=text;
 if(css)element.className=css;
 return element;
}
export function markDetail(list:HTMLDListElement,label:string,value:string){
 list.append(node('dt',label),node('dd',value));
}
type InspectorOptions={reset:()=>void;edit:()=>void;strip:()=>void;next:()=>void;previous:()=>void;close:()=>void};
export function setupMarkInspector(host:HTMLElement,o:InspectorOptions){
 host.innerHTML=`<div class="mark-inspector-top"><h3>Cell inspector</h3><button type="button" class="secondary mark-drawer-close">Close</button></div>
 <div class="mark-inspector-cell"></div><section class="mark-term" hidden><h4>Monthly term structure</h4><div class="mark-term-chart" role="img"></div></section>
 <section class="mark-history" hidden><div class="mark-history-head"><h4>Revision history</h4><div class="mark-history-actions">
 <button type="button" class="secondary mark-history-prev" aria-label="Previous revision page">‹</button>
 <button type="button" class="secondary mark-history-next" aria-label="Next revision page">›</button></div></div><div class="mark-history-items" aria-live="polite"></div></section>`;
 const find=<T extends HTMLElement>(selector:string)=>host.querySelector<T>(selector)!;
 const detail=find('.mark-inspector-cell'),history=find('.mark-history'),items=find('.mark-history-items');
 const chartSection=find('.mark-term'),chart=find('.mark-term-chart');
 const prev=find<HTMLButtonElement>('.mark-history-prev'),next=find<HTMLButtonElement>('.mark-history-next');
 find<HTMLButtonElement>('.mark-drawer-close').onclick=o.close;prev.onclick=o.previous;next.onclick=o.next;
 let dispose:(()=>void)|undefined,chartFrame:number|undefined;
 function releaseChart(){
  if(chartFrame!==undefined)cancelAnimationFrame(chartFrame);
  chartFrame=undefined;dispose?.();dispose=undefined;chartSection.hidden=true;
 }
 function update(selection:LayoutCell|undefined,setup:Setup|undefined,edits:ReadonlyMap<string,string>,canEdit:boolean){
  releaseChart();detail.replaceChildren();
  if(!selection?.key||!setup?.draft||!setup.view){
   detail.append(node('p','Select a grid cell to inspect its exact values and revision history.','mark-muted'));
   history.hidden=true;return;
  }
  const key=selection.key,id=keyId(key),cell=setup.draft.cells.find(row=>keyId(row.state.key)===id)!;
  const curveNames=new Map(setup.curves.map(curve=>[curve.code,curve.name]));
  const pair=[key.firstCurve,key.secondCurve].flatMap(code=>code?[curveNames.get(code)??code]:[]).join(' × ');
  const presentation=cellPresentation(cell,edits.get(id));
  const title=node('h4',pair,'mark-pair-name');title.title=keyLabel(key);
  const subtitle=node('p',`${key.month} · ${selection.editable?presentation.label:'Mirrored cell · '+presentation.label}`,'mark-muted');
  detail.append(title,subtitle);
  const facts=node('dl',undefined,'mark-inspector-facts');
  markDetail(facts,'Published',cell.state.selected?.value??'Missing');
  if(cell.state.selected)markDetail(facts,'Selected revision',String(cell.state.selected.revision));
  if(cell.enteredValue===edits.get(id)&&cell.proposedValue!==null&&!cell.error){
   markDetail(facts,'Validated draft',cell.proposedValue);
   markDetail(facts,'Draft − selected',cell.isNewMark?'New mark':cell.difference??'—');
  }
  if(cell.state.writeHead)markDetail(facts,'Write head',`Revision ${cell.state.writeHead.revision} · ${cell.state.writeHead.value}`);
  if(cell.state.editBlock)detail.append(node('p',cell.state.editBlock.message,'mark-note'));
  if(presentation.state==='invalid'&&cell.error)detail.append(node('p',cell.error.message,'mark-warning'));
  detail.append(facts);
  const actions=node('div',undefined,'mark-action-row');
  function add(label:string,action:()=>void){
   const button=node('button',label,'secondary');button.type='button';button.onclick=action;actions.append(button);
  }
  if(canEdit)add('Edit cell',o.edit);
  if(edits.has(id)&&canEdit)add('Reset cell',o.reset);
  if(setup.view.mode==='grid')add('Open monthly strip',o.strip);
  detail.append(actions);
  const points=setup.draft.cells.filter(row=>row.state.key.firstCurve===key.firstCurve&&row.state.key.secondCurve===key.secondCurve)
   .sort((a,b)=>a.state.key.month.localeCompare(b.state.key.month));
  if(points.length){
   const shown=points.map((point):DraftCell=>edits.has(keyId(point.state.key))?point:{...point,enteredValue:null,proposedValue:null});
   chartSection.hidden=false;chart.setAttribute('aria-label',`Monthly term structure for ${pair}. Solid published values and dashed validated draft values.`);
   chartFrame=requestAnimationFrame(()=>{
    chartFrame=undefined;
    if(chart.clientWidth&&chart.clientHeight)dispose=mountMarkTerm(chart,termOptions(shown,edits,keyLabel(key),setup.convention,setup.view!.kind));
   });
  }
  history.hidden=false;
 }
 function revisions(entries:HistoryEntry[],offset:number,more:boolean,pending:boolean,message=''){
  prev.disabled=pending||offset===0;next.disabled=pending||!more;
  items.replaceChildren();
  if(pending){items.append(node('p','Reading revision history…','mark-muted'));return;}
  if(message||!entries.length){items.append(node('p',message||'No revisions are visible at this cutoff.','mark-muted'));return;}
  for(const {mark,previousId,actorId,reason,effectiveEligible} of entries){
   const group=node('article',undefined,'mark-revision');
   const header=node('div',undefined,'mark-revision-head');
   header.append(node('strong',`Revision ${mark.revision}`),node('strong',mark.value));group.append(header);
   if(!effectiveEligible)group.append(node('span','Later effective','mark-pill'));
   const facts=node('dl',undefined,'mark-inspector-facts');
   markDetail(facts,'Fact',mark.id);markDetail(facts,'Predecessor',previousId??'—');markDetail(facts,'Actor',actorId);
   markDetail(facts,'Effective',mark.effectiveAt);markDetail(facts,'Known',mark.knownAt);
   group.append(node('p',reason||'—','mark-revision-reason'),facts);items.append(group);
  }
 }
 return {update,revisions,releaseChart};
}
