import {node,table} from './risk-report-elements.js';
import {pcaMarketCharts} from './pca-hybrid-risk.js';
import {hybridFallback} from './hybrid-monte-carlo-risk.js';
import {amountText} from './amount-display.js';
import {renderChart} from './chart-view.js';
import {calibrationMatrix} from './parametric-risk.js';
import type {MonteCarloView} from './monte-carlo-risk.js';
const same=(a:unknown,b:unknown)=>JSON.stringify(a)===JSON.stringify(b);
const money=(value:string)=>{const cell=node('span',amountText(value,'money'));cell.title=value;return cell;};
export function renderHybridMonteCarlo(host:HTMLElement,v:MonteCarloView,state:{partition:number;rows:number;columns:number;validationSample:number},redraw:()=>void,plots:HTMLElement[]){
 const x=v.hybrid;if(!x)return;
 const h=x.validation,p=v.result.partitions[state.partition],label=v.partitions.find(l=>l.measure===p.measure&&same(l.currencyReference,p.currency))!;
 const section=node('details');section.open=true;section.append(node('summary','Hybrid Monte Carlo validation'),
  node('p',x.fallbackApplied?'Full-PV fallback was explicitly enabled and applied to every path and every holding. '+x.fallbackReasons.map(r=>r.message).join(' '):
   'The reported distribution uses model PV estimates on every path, including the validation sample. Full-PV validation values are shown separately.'),
  node('p',`${x.fittingPolicy.valuationPolicy===hybridFallback?'Full-PV fallback was enabled for this run.':'Full-PV fallback was not enabled.'} ${h.observedPanelPaths} of ${v.result.samples} paths have completed validation PVs; ${h.unpricedPaths} paths have no full PV. All ${h.quoteDomainPaths} generated paths passed declared quote-domain checks.`),
  node('p','Observed validation errors describe this sample only. They do not bound unpriced losses or certify the portfolio tail. Validation never changes the selected paths, factors, components or tolerances.'));
 host.append(section);
 const holding=(id:string)=>{const e=v.exposures.find(e=>e.id===id)!;return e.tradeReference+' · '+e.instrumentReference;};
 section.append(table(['Instrument / shared valuation','Applied valuation','Reason'],x.classifications.map(c=>[
  [...new Set(c.exposureIds.map(id=>v.exposures.find(e=>e.id===id)!.instrumentReference))].join(', '),
  c.method==='full_pv'?'Full PV · consented fallback':'PCA delta-gamma model',c.reasons.length?c.reasons.map(r=>r.replaceAll('_',' ')).join('; '):'Observed validation checks passed'])));
 const comparison=h.comparison,ref=comparison?.reference?.find(n=>n.measure===p.measure&&same(n.currency,p.currency)),candidate=comparison?.candidate.find(n=>n.measure===p.measure&&same(n.currency,p.currency));
 const graphics=node('div');graphics.className='risk-report-charts';
 if(ref&&candidate&&comparison){
  section.append(node('h3',`Observed panel only · ${h.observedPanelPaths} paths · ${label.currency}`),
   node('p',`These VaR / ES values use the validation panel, not the complete simulation. Panel ES uses ${ref.tailSampleMass} path-equivalents.`),
   table(['Panel valuation','VaR','Expected shortfall','Currency'],[['Full PV',money(ref.valueAtRisk),money(ref.expectedShortfall),label.currency],['Model estimate',money(candidate.valueAtRisk),money(candidate.expectedShortfall),label.currency]]),
   table(['Holding','Observed maximum loss error','Observed panel-tail error','Checks'],comparison.exposures.filter(e=>{const value=v.exposures.find(x=>x.id===e.exposureId)!;return value.measure===p.measure&&same(value.currency,p.currency);}).map(e=>[
    holding(e.exposureId),money(e.maximumAbsoluteError),money(e.maximumTailError),e.lossesPass&&comparison.models.find(m=>m.exposureId===e.exposureId)!.stable?'Passed':'Failed'])));
  const plot=node('div');plot.className='chart risk-result-chart';graphics.append(plot);plots.push(plot);
  renderChart(plot,{title:'Observed validation losses · full PV and model',subtitle:'Recorded validation indices only. These curves do not show full-PV losses for unpriced simulation paths.',axis:'category',kind:'line',unit:label.currency,categoryTitle:'Original simulation path',series:[
   {name:'Validation full-PV loss',points:ref.samples.map(s=>({x:s.id,label:`Path ${v.generation.samples.find(g=>g.id===s.id)!.index+1}`,y:s.loss}))},
   {name:'Validation model loss',points:candidate.samples.map(s=>({x:s.id,label:`Path ${v.generation.samples.find(g=>g.id===s.id)!.index+1}`,y:s.loss}))}]});
 }else section.append(node('p','The validation comparison did not complete. The retained fallback reasons and completed-work counters describe this run.'));
 section.append(graphics);
 if(h.matrix){
  const detail=node('details');detail.append(node('summary','Validation full-PV drilldown'));
  const select=node('select');select.setAttribute('aria-label','Hybrid validation path');
  select.append(...h.selectedSamples.map((s,i)=>new Option(`Path ${s.index+1}`,String(i))));
  if(state.validationSample>=h.selectedSamples.length)state.validationSample=0;select.value=String(state.validationSample);select.onchange=()=>{state.validationSample=Number(select.value);redraw();};detail.append(select);
  const sample=h.selectedSamples[state.validationSample],row=h.matrix.rows.find(r=>r.id===sample.id)!;
  detail.append(table(['Holding','Validation full PV','Validation loss','Currency'],v.exposures.filter(e=>e.measure===p.measure&&same(e.currency,p.currency)).map(e=>{
   const i=h.matrix!.columns.indexOf(e.id);return [holding(e.id),money(row.scenarioValues[i]),money(row.losses[i]),label.currency];})));section.append(detail);
 }
 if(x.pca){
  for(const spec of pcaMarketCharts(v,x.pca,x.fittingPolicy,state.rows)){const plot=node('div');plot.className='chart risk-result-chart';graphics.append(plot);plots.push(plot);renderChart(plot,spec);}
  const residual=node('details');residual.append(node('summary','Ideal PCA residual and cross covariance'));
  for(const [title,covariance] of [['Ideal PCA residual covariance',x.pca.residualCovariance],['Ideal retained / residual cross covariance',x.pca.retainedResidualCovariance]] as const){
   const plot=node('div');plot.className='chart risk-result-chart';residual.append(plot);plots.push(plot);renderChart(plot,{...calibrationMatrix({...v.generation.calibration,covariance},v.factors,state.rows,state.columns,'covariance'),title,subtitle:'Ideal loading-projection diagnostic in native units. Actual fitting directions and observed PV errors are retained separately.'});}
  section.append(residual);
 }
 const costs=node('details');costs.open=true;costs.append(node('summary','Actual hybrid calculation work'),
  table(['Phase','Full-PV scenarios including base','Unit PV calls','Quantity applications'],x.cost.phases.map(p=>[p.phase.replaceAll('_',' '),String(p.work.scenarioCount),String(p.work.unitPricerCalls),String(p.work.quantityApplications)])),
  node('p',`${x.cost.predictedValues} native holding values were generated by the model. Full-PV calls above include fitting, validation and any consented complete fallback.`));
 if(x.cost.attemptUnmetered)costs.append(node('p','An interrupted attempt has incomplete counters. The table shows completed work; total work is unavailable.'));
 section.append(costs);
}
