import {historyGridState,renderHistoryGrid} from './risk-history-grid.js';
import {node,table} from './risk-report-elements.js';
import {renderChart} from './chart-view.js';
import {historicalCharts,historicalSampleLabel,type HistoricalView} from './historical-risk.js';
import {newParametricView,renderParametric} from './parametric-risk-view.js';
import {amountText} from './amount-display.js';

// Shown amounts use the money display precision; each keeps its exact value as its title.
const money=(value:string)=>{const cell=node('span',amountText(value,'money'));cell.title=value;return cell;};
export const newHistoricalView=()=>({partition:0,metric:'valueAtRisk' as 'valueAtRisk'|'expectedShortfall',book:'',sample:'',history:historyGridState(),comparison:newParametricView()});
/** Shared Risk/Blotter report, using backend amounts and portfolio allocation weights. */
export function renderHistorical(host:HTMLElement,v:HistoricalView,state:ReturnType<typeof newHistoricalView>,redraw:()=>void,plots:HTMLElement[]){
 host.append(node('p',`Full-PV historical simulation · ${v.result.policy.confidence} confidence · one adjacent business-session market move · ${v.result.samples} equal-weight common changes.`));
 const assumptions=node('details');assumptions.append(node('summary','Frozen inputs and allocation rules'),
  node('p','Each historical factor vector shocks the same current portfolio. Valuation time, holdings, fixings, rates and current option calibration are frozen. This measures market-move loss without time passage or recalibrating volatility.'),
  node('p','Signed loss tails retain gains, zero changes and repeated outcomes. Quantile/tail allocations reconcile to the portfolio and are not marginal risk or Euler contributions.'));host.append(assumptions);
 if(!v.result.available){host.append(node('p','Historical risk unavailable: '+v.result.unavailableReasons.map(s=>s.replaceAll('_',' ')).join('; ')));grid();return;}
 if(state.partition>=v.result.partitions.length)state.partition=0;
 const p=v.result.partitions[state.partition],label=v.partitions.find(l=>l.measure===p.measure&&JSON.stringify(l.currencyReference)===JSON.stringify(p.currency))!;
 const controls=node('div');controls.className='risk-result-controls';host.append(controls);
 const select=node('select');select.setAttribute('aria-label','Historical native measure');select.append(...v.result.partitions.map((p,i)=>new Option(`${v.partitions.find(l=>l.measure===p.measure&&JSON.stringify(l.currencyReference)===JSON.stringify(p.currency))!.measureLabel} · ${v.partitions.find(l=>l.measure===p.measure&&JSON.stringify(l.currencyReference)===JSON.stringify(p.currency))!.currency}`,String(i))));select.value=String(state.partition);select.onchange=()=>{state.partition=Number(select.value);state.sample='';state.book='';redraw();};const wrap=node('label','Native measure and currency ');wrap.append(select);controls.append(wrap);
 for(const [metric,text] of [['valueAtRisk','VaR allocation'],['expectedShortfall','ES allocation']] as const){const button=node('button',text);button.type='button';button.className='secondary';button.setAttribute('aria-pressed',String(state.metric===metric));button.onclick=()=>{state.metric=metric;redraw();};controls.append(button);}
 const cards=node('div');cards.className='parametric-cards';
 for(const [key,text] of [['valueAtRisk','Value at risk'],['expectedShortfall','Expected shortfall'],['mean','Mean historical loss']] as const){const card=node('div');card.className='parametric-stat';const amount=node('strong',amountText(p[key],'money')+' '+label.currency);amount.title=p[key];card.append(node('span',text),amount);cards.append(card);}host.append(cards,
  node('p',`Tail contains ${p.tailSampleMass} sample-equivalents; ${p.quantileTies} sample${p.quantileTies===1?'':'s'} tie at VaR. Fractional boundary mass is shared uniformly across ties. Small tail counts provide limited historical evidence.`));
 if(p.histogram.length===1&&p.histogram[0].lower===p.histogram[0].upper)host.append(node('p','Point-mass distribution: every sample has the same portfolio loss.'));
 const graphics=node('div');graphics.className='risk-report-charts';host.append(graphics);
 for(const spec of historicalCharts(v,state.partition,state.metric)){const plot=node('div');plot.className='chart risk-result-chart';graphics.append(plot);plots.push(plot);if(spec.title.startsWith('Historical losses'))spec.onSelect=id=>{state.sample=id;redraw();};renderChart(plot,spec);}
 const allocations=node('details');allocations.open=true;allocations.append(node('summary','Book and trade allocations'));
 const books=node('select');books.setAttribute('aria-label','Allocation book');books.append(new Option('All books',''),...p.books.map(b=>new Option(v.books.find(l=>l.id===b.id)!.name,b.id)));books.value=state.book;books.onchange=()=>{state.book=books.value;redraw();};allocations.append(books,
  table(['Book','Allocated VaR','Allocated ES','Currency'],p.books.map(b=>[v.books.find(l=>l.id===b.id)!.name,money(b.valueAtRisk),money(b.expectedShortfall),label.currency])));
 const trades=p.trades.filter(t=>!state.book||v.exposures.some(e=>e.tradeId===t.id&&e.bookId===state.book));
 allocations.append(node('p','Trade allocations use portfolio-wide tail weights. A book selection filters the trade list; it does not recalculate standalone book VaR.'),table(['Trade','Allocated VaR','Allocated ES','Currency'],trades.map(t=>[v.exposures.find(e=>e.tradeId===t.id)!.tradeReference,money(t.valueAtRisk),money(t.expectedShortfall),label.currency])));host.append(allocations);
 const samples=node('details');samples.open=true;samples.append(node('summary','Historical samples and full-PV drilldown'));
 const sample=node('select');sample.setAttribute('aria-label','Historical sample');sample.append(...v.history.samples.map(s=>new Option(`${historicalSampleLabel(v.history,s.id)} · ${amountText(p.samples.find(l=>l.id===s.id)!.loss,'money')} ${label.currency}${p.samples.find(l=>l.id===s.id)!.esWeight!=='0'?' · ES tail':''}`,s.id)));
 if(!v.history.samples.some(s=>s.id===state.sample))state.sample=p.samples.find(s=>s.esWeight!=='0')?.id??v.history.samples[0].id;sample.value=state.sample;sample.onchange=()=>{state.sample=sample.value;redraw();};samples.append(sample);
 const source=v.history.samples.find(s=>s.id===state.sample)!,loss=p.samples.find(s=>s.id===state.sample)!,row=v.matrix.rows.find(r=>r.id===state.sample)!;
 samples.append(node('p',`Portfolio loss ${amountText(loss.loss,'money')} ${label.currency} · quantile allocation weight ${loss.varWeight} · ES allocation weight ${loss.esWeight}.`),
  table(['Factor','Absolute move','Native unit'],v.factors.map((f,i)=>[f.factor.label,source.changes[i],f.factor.unit])),
  table(['Book','Trade','Instrument','Base PV','Scenario PV','Loss','Currency'],v.exposures.filter(e=>e.measure===p.measure&&JSON.stringify(e.currency)===JSON.stringify(p.currency)&&(!state.book||e.bookId===state.book)).map(e=>{const i=v.matrix.columns.indexOf(e.id),instrument=node('span',e.instrumentReference);instrument.title='Binding '+e.bindingId;return [v.books.find(b=>b.id===e.bookId)!.name,e.tradeReference,instrument,money(e.baseValue),money(row.scenarioValues[i]),money(row.losses[i]),label.currency];})));
 samples.append(table(['Interval','Portfolio loss','VaR weight','ES weight'],v.history.samples.map(s=>{const l=p.samples.find(l=>l.id===s.id)!;return [`${historicalSampleLabel(v.history,s.id)}`,money(l.loss),l.varWeight,l.esWeight];})));host.append(samples);
 grid();
 if(v.comparison){const comparison=node('details');comparison.append(node('summary','Matching parametric comparison'),node('p','Same captured history, quantities, pricing inputs, confidence and frozen horizon. The normal approximation uses sample-mean drift.'));
  if(v.comparison.report)renderParametric(comparison,v.comparison.report,state.comparison,redraw,plots);else comparison.append(node('p','Parametric comparison unavailable: '+v.comparison.unavailableReason!.message+' ('+v.comparison.unavailableReason!.code+'). Historical risk remains available. Completed work counters exclude this failed comparison attempt.'));
  host.append(comparison);
 }
 function grid(){renderHistoryGrid(host,v.history,v.factors.map(f=>({id:f.id,label:f.factor.label,unit:f.factor.unit})),state.history,redraw);}
}
