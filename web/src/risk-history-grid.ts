import type {HistoryManifest,HistoryEvidence} from './factor-history.js';
import {node} from './risk-report-elements.js';

export {historyPriceLabels} from './factor-history.js';
import {historyPriceLabels} from './factor-history.js';
export const historyGridState=()=>({row:0,column:0,metric:'prices' as 'prices'|'changes'});
/** A bounded view of backend-selected endpoints; no price selection or financial arithmetic occurs here. */
export function renderHistoryGrid(host:HTMLElement,manifest:HistoryManifest,factors:{id:string;label:string;unit:string}[],state:ReturnType<typeof historyGridState>,redraw:()=>void){
 const endpoints=new Map([...manifest.samples,...manifest.rejected].flatMap(s=>[[s.from.at,s.from],[s.to.at,s.to]] as const));
 const evidence=new Map(manifest.evidence.map(e=>[e.observation.key.id,e]));
 const prices=new Map(manifest.dayPrices?.map(d=>[d.at,d.choices]));
 const controls=node('div');controls.className='risk-result-controls';
 const choose=node('select');choose.setAttribute('aria-label','Aligned factor grid values');choose.append(new Option('Daily prices','prices'),new Option('Complete daily changes','changes'));choose.value=state.metric;
 choose.onchange=()=>{state.metric=choose.value as typeof state.metric;state.row=0;redraw();};const label=node('label','Grid values ');label.append(choose);controls.append(label);
 const allRows=state.metric==='prices'?manifest.sessions:manifest.samples.map(s=>({date:s.id,close:s.to.at}));
 for(const [key,text,size,total] of [['row','Session rows',24,allRows.length],['column','Factor columns',16,factors.length]] as const){
  if(state[key]>=total)state[key]=0;
  const wrap=node('label',text+' '),select=node('select');select.setAttribute('aria-label',text);
  for(let start=0;start<total;start+=size)select.append(new Option(`${start+1}–${Math.min(start+size,total)} of ${total}`,String(start)));
  select.value=String(state[key]);select.onchange=()=>{state[key]=Number(select.value);redraw();};wrap.append(select);controls.append(wrap);
 }
 host.append(node('h3','Aligned factor grid'),node('p','Green: official daily close. Amber: an explicitly allowed alternative. Grey: unavailable. Each cell shows its selected type, actual mark time and available alternatives in its details. Paging changes the display only.'),controls);
 const wrap=node('div'),table=node('table'),head=node('thead'),tr=node('tr');wrap.className='table-wrap history-matrix';table.setAttribute('aria-label','Aligned dated-factor prices and coverage');
 tr.append(node('th','Business session'));for(const f of factors.slice(state.column,state.column+16))tr.append(node('th',f.label+' · '+f.unit));head.append(tr);table.append(head);
 const body=node('tbody');
 for(const row of allRows.slice(state.row,state.row+24)){
  const tr=node('tr');tr.append(node('th',row.date));const endpoint=endpoints.get(row.close),choices=prices.get(row.close);
  for(let i=state.column;i<Math.min(state.column+16,factors.length);i++){
   const td=node('td'),mark=endpoint?.marks[i],choice=choices?.[i],selected=choice?.selectedType;
   const sample=state.metric==='changes'?manifest.samples.find(s=>s.to.at===row.close):undefined;
   td.dataset.priceType=mark?(selected==='official_close_v1'?'official':selected?'alternative':'exact'):'missing';
   const detail=node('details'),summary=node('summary',state.metric==='changes'?sample?.changes[i]??'Unavailable':mark?.value??'Unavailable');
   detail.append(summary);
   const source:HistoryEvidence|undefined=mark?evidence.get((mark.observation as {id:string}).id):undefined;
   if(mark)detail.append(node('p',(selected?historyPriceLabels[selected]??selected:'Exact captured close')+' · '+(source?.observation.observedAt??row.close)),node('p','Pinned source: '+(source?.observation.source.code??'retained observation')));
   if(choice)detail.append(node('p','Official close: '+choice.officialCloseStatus.replaceAll('_',' ')),node('p','Available types: '+(choice.availableTypes.map(t=>historyPriceLabels[t]??t).join(', ')||'none')));
   if(sample){const from=prices.get(sample.from.at)?.[i]?.selectedType;if(from&&selected&&from!==selected)detail.append(node('p','Price type changes between endpoints: '+(historyPriceLabels[from]??from)+' → '+(historyPriceLabels[selected]??selected)));}
   td.append(detail);tr.append(td);
  }
  body.append(tr);
 }
 table.append(body);wrap.append(table);host.append(wrap);
 if(!allRows.length)host.append(node('p','No complete changes are available. Daily prices retain missing-data diagnostics.'));
}
