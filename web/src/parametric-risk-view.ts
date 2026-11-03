import {historyGridState,renderHistoryGrid} from './risk-history-grid.js';
import {node,table} from './risk-report-elements.js';
import {renderChart} from './chart-view.js';
import {parametricCharts,type ParametricView} from './parametric-risk.js';

export const newParametricView=()=>({partition:0,metric:'valueAtRisk' as 'valueAtRisk'|'expectedShortfall',rows:0,columns:0,matrix:'correlation' as 'correlation'|'covariance',history:historyGridState()});
/** Shared Risk/Blotter rendering. Every risk amount, contribution and matrix cell is a backend result. */
export function renderParametric(host:HTMLElement,view:ParametricView,state:ReturnType<typeof newParametricView>,redraw:()=>void,plots:HTMLElement[]){
 const result=view.result,c=result.calibration;
 host.append(node('p',`Delta-normal · one adjacent business-session market move · signed upper loss tail · confidence ${result.policy.confidence} · ${result.policy.drift==='zero_drift_v1'?'explicit zero drift':'sample-mean drift'}.`),
  node('p','This method uses local delta. Gamma, time passage and full-repricing tail outcomes are outside this approximation.'),
  node('p',`${c.samples} common changes · ${c.columns.length} dated factors · covariance rank ${c.rank} · ${c.conditioning.replaceAll('_',' ')}${c.correlationCondition===null?'':` · correlation condition ${c.correlationCondition}`}.`));
 if(!result.available){host.append(node('p','Parametric risk unavailable: '+result.unavailableReasons.map(v=>v.replaceAll('_',' ')).join('; ')));alignedGrid();diagnostics();return;}
 if(state.partition>=result.partitions.length)state.partition=0;
 const p=result.partitions[state.partition],label=view.partitions.find(v=>v.measure===p.measure&&JSON.stringify(v.currencyReference)===JSON.stringify(p.currency))!;
 const controls=node('div');controls.className='risk-result-controls';
 const choose=node('select');choose.setAttribute('aria-label','Parametric native measure');choose.append(...view.partitions.map((v,i)=>new Option(`${v.measureLabel} · ${v.currency} · partition ${i+1}`,String(i))));choose.value=String(state.partition);
 choose.onchange=()=>{state.partition=Number(choose.value);redraw();};const measure=node('label','Native measure and currency ');measure.append(choose);controls.append(measure);
 for(const [metric,text] of [['valueAtRisk','VaR contributions'],['expectedShortfall','ES contributions']] as const){const button=node('button',text);button.type='button';button.className='secondary';button.setAttribute('aria-pressed',String(metric===state.metric));button.onclick=()=>{state.metric=metric;redraw();};controls.append(button);}
 host.append(controls);
 const cards=node('div');cards.className='parametric-cards';
 for(const [key,title] of [['valueAtRisk','Value at risk'],['expectedShortfall','Expected shortfall'],['standardDeviation','Loss standard deviation']] as const){
  const card=node('div');card.className='parametric-stat';const value=node('strong',label.display[key]+' '+label.currency);value.title=p[key];card.append(node('span',title),value);cards.append(card);
 }
 host.append(cards,node('p',`Exact model loss mean: ${p.mean} ${label.currency}. Exact loss variance: ${p.variance}. Book-component reconciliation residuals: VaR ${p.varReconciliation}, ES ${p.esReconciliation}.`));
 if(!p.density.length)host.append(node('p','Zero variance: the fitted loss is a point mass at '+p.mean+' '+label.currency+'. No continuous density is drawn.'));
 const matrixSelect=node('select');matrixSelect.setAttribute('aria-label','Factor matrix');matrixSelect.append(new Option('Correlation','correlation'),new Option('Covariance','covariance'));matrixSelect.value=state.matrix;
 matrixSelect.onchange=()=>{state.matrix=matrixSelect.value as typeof state.matrix;redraw();};const matrixLabel=node('label','Factor matrix ');matrixLabel.append(matrixSelect);controls.append(matrixLabel);
 const size=c.columns.length;
 if(size>32){
  for(const [key,title] of [['rows','Matrix rows'],['columns','Matrix columns']] as const){const wrap=node('label',title+' '),select=node('select');select.setAttribute('aria-label',title);
   for(let start=0;start<size;start+=32)select.append(new Option(`${start+1}–${Math.min(start+32,size)} of ${size}`,String(start)));select.value=String(state[key]);
   select.onchange=()=>{state[key]=Number(select.value);redraw();};wrap.append(select);controls.append(wrap);}
 }
 const graphics=node('div');graphics.className='risk-report-charts';host.append(graphics);
 for(const spec of parametricCharts(view,state.partition,state.metric,state.rows,state.columns,state.matrix)){const plot=node('div');plot.className='chart risk-result-chart';graphics.append(plot);plots.push(plot);renderChart(plot,spec);}
 const book=node('details');book.open=true;book.append(node('summary','Exact component data'),table(['Book','Mean contribution','Covariance with portfolio loss','Component VaR','Component ES','Currency'],p.books.map(v=>[
  view.books.find(b=>b.id===v.id)!.name,v.mean,v.varianceCross,v.valueAtRisk,v.expectedShortfall,label.currency])));host.append(book);
 alignedGrid();diagnostics();
 function alignedGrid(){renderHistoryGrid(host,view.history,view.factors.map(f=>({id:f.id,label:f.factor.label,unit:f.factor.unit})),state.history,redraw);}
 function diagnostics(){
  const factor=(id:string)=>view.factors.find(f=>f.id===id)!.factor;
  if(c.covariance.length){
   const matrix=node('details');matrix.append(node('summary','Exact native covariance · display window only'));
   const columns=c.columns.slice(state.columns,state.columns+32),rows=c.columns.slice(state.rows,state.rows+32);
   matrix.append(table(['Factor / native unit',...columns.map(id=>factor(id).label+' · '+factor(id).unit)],rows.map((id,i)=>[
    factor(id).label+' · '+factor(id).unit,...columns.map((_,j)=>c.covariance[state.rows+i][state.columns+j])])));host.append(matrix);
  }
  if(!view.finiteDifference.estimates.length){host.append(node('p','No finite differences were evaluated for unavailable history or portfolio coverage.'));return;}
  const fd=node('details');fd.append(node('summary','Native finite differences and agreement receipts'),table(['Factor','Book','Measure','Half delta','Nominal delta','Double delta','Nominal gamma','Stable','Native bump'],view.finiteDifference.estimates.map(v=>[
  factor(v.columnId).label,view.books.find(b=>b.id===v.bookId)!.name,v.measure,v.half,v.nominal,v.double,v.gamma,v.stable?'Yes':'No',v.bump+' '+factor(v.columnId).unit])));host.append(fd);
 }
}
