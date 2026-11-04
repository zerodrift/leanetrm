import {node,table} from './risk-report-elements.js';
import {monteCarloCharts,type MonteCarloView} from './monte-carlo-risk.js';
import {calibrationMatrix} from './parametric-risk.js';
import {renderChart} from './chart-view.js';
import {historyGridState,renderHistoryGrid} from './risk-history-grid.js';
import {amountText} from './amount-display.js';

export const newMonteCarloView=()=>({partition:0,sample:0,book:'',rows:0,columns:0,matrix:'correlation' as 'correlation'|'covariance',history:historyGridState()});
const money=(value:string)=>{const cell=node('span',amountText(value,'money'));cell.title=value;return cell;};
const key=(measure:string,currency:unknown)=>JSON.stringify([measure,currency]);
/** All shocks, loss amounts, tail weights, diagnostics and matrix cells are backend results. */
export function renderMonteCarlo(host:HTMLElement,v:MonteCarloView,state:ReturnType<typeof newMonteCarloView>,redraw:()=>void,plots:HTMLElement[]){
 const policy=v.result.policy,c=v.generation.calibration;
 host.append(node('p',`Full-PV Monte Carlo · additive joint Gaussian model · ${policy.confidence} confidence · ${policy.paths} paths · seed ${policy.seed} · ${policy.drift==='zero_drift_v1'?'zero drift':'sample-mean drift'} · one adjacent business-session move.`));
 const assumptions=node('details');assumptions.append(node('summary','Model and frozen inputs'),
  node('p','Every joint factor draw reprices the same current portfolio. Valuation time, holdings, fixings, rates and current option calibration are frozen. Gaussian additive changes can produce negative prices; a pricing-domain failure refuses the whole run without clipping or replacing paths.'),
  node('p','Equal-weight sample covariance uses complete aligned changes. Singular covariance is allowed without adding variance. Numerical generation and native quote rounding are approximate; actual submitted shocks are retained. Book and trade allocations use portfolio quantile/tail weights and are not marginal risk or Euler contributions.'));host.append(assumptions);
 host.append(node('p',`${c.samples} calibration changes · ${c.columns.length} factor${c.columns.length===1?'':'s'} · covariance rank ${c.rank}${c.conditioning?' · '+c.conditioning.replaceAll('_',' '):''}.`));
 if(!v.result.available){host.append(node('p','Monte Carlo risk unavailable: '+v.result.unavailableReasons.map(x=>x.replaceAll('_',' ')).join('; ')));grid();return;}
 if(state.partition>=v.result.partitions.length)state.partition=0;
 const p=v.result.partitions[state.partition],label=v.partitions.find(x=>key(x.measure,x.currencyReference)===key(p.measure,p.currency))!;
 const controls=node('div');controls.className='risk-result-controls';host.append(controls);
 const choose=node('select');choose.setAttribute('aria-label','Monte Carlo native measure');
 choose.append(...v.result.partitions.map((p,i)=>new Option(v.partitions.find(x=>key(x.measure,x.currencyReference)===key(p.measure,p.currency))!.measureLabel+' · '+v.partitions.find(x=>key(x.measure,x.currencyReference)===key(p.measure,p.currency))!.currency,String(i))));choose.value=String(state.partition);choose.onchange=()=>{state.partition=Number(choose.value);state.book='';redraw();};controls.append(choose);
 const cards=node('div');cards.className='parametric-cards';
 for(const [field,title] of [['valueAtRisk','Value at risk'],['expectedShortfall','Expected shortfall'],['mean','Mean simulated loss']] as const){const card=node('div');card.className='parametric-stat';const amount=node('strong',amountText(p[field],'money')+' '+label.currency);amount.title=p[field];card.append(node('span',title),amount);cards.append(card);}host.append(cards);
 const interval=v.result.diagnostics.find(x=>key(x.measure,x.currency)===key(p.measure,p.currency))!.valueAtRiskInterval;
 host.append(node('p',`95% Monte Carlo sampling interval for VaR: ${interval.lower===null?'unbounded below':amountText(interval.lower,'money')} to ${interval.upper===null?'unbounded above':amountText(interval.upper,'money')} ${label.currency}. Conditional on the fixed fitted model; excludes calibration and model uncertainty and does not bound ES.`),node('p',`ES uses ${p.tailSampleMass} path-equivalents. A small tail count limits simulation evidence. Prefix convergence is a diagnostic, not an accuracy guarantee.`));
 if(p.histogram.length===1&&p.histogram[0].lower===p.histogram[0].upper)host.append(node('p','Point-mass distribution: every path has the same portfolio loss.'));
 const graphics=node('div');graphics.className='risk-report-charts';host.append(graphics);
 for(const spec of monteCarloCharts(v,state.partition)){const plot=node('div');plot.className='chart risk-result-chart';graphics.append(plot);plots.push(plot);if(spec.title.startsWith('Simulated losses'))spec.onSelect=id=>{const sample=v.generation.samples.find(s=>s.id===id);if(sample){state.sample=sample.index;redraw();}};renderChart(plot,spec);}
 const allocations=node('details');allocations.open=true;allocations.append(node('summary','Book and trade allocations'));
 const book=node('select');book.setAttribute('aria-label','Monte Carlo allocation book');book.append(new Option('All books',''),...p.books.map(b=>new Option(v.books.find(x=>x.id===b.id)!.name,b.id)));book.value=state.book;book.onchange=()=>{state.book=book.value;redraw();};allocations.append(book,
  table(['Book','Allocated VaR','Allocated ES','Currency'],p.books.map(b=>[v.books.find(x=>x.id===b.id)!.name,money(b.valueAtRisk),money(b.expectedShortfall),label.currency])),
  table(['Trade','Allocated VaR','Allocated ES','Currency'],p.trades.filter(t=>!state.book||v.exposures.some(e=>e.tradeId===t.id&&e.bookId===state.book)).map(t=>[v.exposures.find(e=>e.tradeId===t.id)!.tradeReference,money(t.valueAtRisk),money(t.expectedShortfall),label.currency])));host.append(allocations);
 const detail=node('details');detail.open=true;detail.append(node('summary','Simulation path and full-PV drilldown'));
 const sampleInput=node('input');sampleInput.type='number';sampleInput.min='1';sampleInput.max=String(v.result.samples);sampleInput.step='1';sampleInput.setAttribute('aria-label','Simulation path');sampleInput.value=String(state.sample+1);sampleInput.onchange=()=>{const n=Number(sampleInput.value);if(Number.isInteger(n)&&n>=1&&n<=v.result.samples){state.sample=n-1;redraw();}};detail.append(node('p','Choose a path number or select a point in the simulated-loss chart.'),sampleInput);
 const sample=v.generation.samples[state.sample],row=v.matrix.rows.find(r=>r.id===sample.id)!,loss=p.samples.find(s=>s.id===sample.id)!;
 detail.append(node('p',`Path ${sample.index+1} · loss ${amountText(loss.loss,'money')} ${label.currency} · VaR weight ${loss.varWeight} · ES weight ${loss.esWeight}.`),
  table(['Factor','Absolute simulated move','Native unit'],v.factors.map((f,i)=>[f.factor.label,sample.changes[i],f.factor.unit])),
  table(['Book','Trade','Instrument','Base PV','Scenario PV','Loss','Currency'],v.exposures.filter(e=>key(e.measure,e.currency)===key(p.measure,p.currency)&&(!state.book||e.bookId===state.book)).map(e=>{const i=v.matrix.columns.indexOf(e.id);return [v.books.find(b=>b.id===e.bookId)!.name,e.tradeReference,e.instrumentReference,money(e.baseValue),money(row.scenarioValues[i]),money(row.losses[i]),label.currency];})));host.append(detail);
 const matrix=node('details');matrix.append(node('summary','Calibration correlations and covariances'));
 const mode=node('select');mode.setAttribute('aria-label','Monte Carlo factor matrix');mode.append(new Option('Correlation','correlation'),new Option('Covariance','covariance'));mode.value=state.matrix;mode.onchange=()=>{state.matrix=mode.value as typeof state.matrix;redraw();};matrix.append(mode);
 for(const [field,title] of [['rows','Matrix rows'],['columns','Matrix columns']] as const){if(c.columns.length<=32)continue;const choose=node('select');choose.setAttribute('aria-label',title);for(let start=0;start<c.columns.length;start+=32)choose.append(new Option(`${start+1}–${Math.min(start+32,c.columns.length)}`,String(start)));choose.value=String(state[field]);choose.onchange=()=>{state[field]=Number(choose.value);redraw();};matrix.append(choose);}
 const plot=node('div');plot.className='chart risk-result-chart';matrix.append(plot);host.append(matrix);plots.push(plot);renderChart(plot,calibrationMatrix(c,v.factors,state.rows,state.columns,state.matrix));grid();
 const receipt=node('details');receipt.append(node('summary','Reproduction receipt'),node('p',`Seed ${policy.seed} · ${policy.randomGenerator} · ${policy.numericalPolicy} · ${policy.shockPolicy} · study ${v.studyId}. Complete shocks, source observations and exact PV results are included in the authorized JSON export.`));host.append(receipt);
 function grid(){renderHistoryGrid(host,v.history,v.factors.map(f=>({id:f.id,label:f.factor.label,unit:f.factor.unit})),state.history,redraw);}
}
