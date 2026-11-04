import {node,table} from './risk-report-elements.js';
import {pcaCharts} from './pca-hybrid-risk.js';
import type {MonteCarloView} from './monte-carlo-risk.js';
import {calibrationMatrix} from './parametric-risk.js';
import {renderChart} from './chart-view.js';
import {amountText} from './amount-display.js';

const shown=(value:string,kind:'money'|'factor'='money')=>{const n=node('span',amountText(value,kind));n.title=value;return n;};
const same=(a:unknown,b:unknown)=>JSON.stringify(a)===JSON.stringify(b);
export function renderPcaHybrid(host:HTMLElement,v:MonteCarloView,partition:number,rows:number,columns:number,plots:HTMLElement[]){
 const a=v.analysis;if(!a)return;
 const panel=node('details');panel.open=true;panel.append(node('summary','PCA and hybrid qualification'),
  node('p','Full-reference qualification: every generated path is repriced through ordinary PV. The candidate uses all native factor deltas plus diagonal and mixed curvature in the selected PCA directions. A failed valuation uses full PV for every path; portfolio validation can fall back the complete portfolio. This mode adds calculation work. Eligibility belongs only to this captured run.'));
 host.append(panel);
 const c=a.pca;
 if(c)panel.append(node('p',`${a.policy.retainedComponents} curvature direction${a.policy.retainedComponents===1?'':'s'} · retained standardized market-variance share ${amountText(c.retainedVarianceShare,'factor')} · ${c.roundoffNegativeEigenvalues} roundoff-negative eigenvalues. Explained market variance does not establish portfolio risk coverage. All residual shocks remain in full PV and every factor remains in the linear term.`));
 if(!a.available)panel.append(node('p','The shared fitting attempt could not qualify the portfolio. Every valuation uses full PV for this run. '+a.unavailableReasons.map(r=>r.message).join(' ')),
  table(['Instrument / shared valuation','Applied method','Reason'],a.fallbackClassifications.map(c=>[
   [...new Set(c.exposureIds.map(id=>v.exposures.find(e=>e.id===id)!.instrumentReference))].join(', '),'Full PV',c.reasons.map(x=>x.replaceAll('_',' ')).join('; ')])));
 const q=a.comparison,p=v.result.partitions[partition],label=v.partitions.find(x=>x.measure===p.measure&&same(x.currencyReference,p.currency));
 if(q&&label){
  const comparison=q.partitions.find(x=>x.measure===p.measure&&same(x.currency,p.currency))!;
  panel.append(table(['Valuation','VaR','Expected shortfall','Currency'],[
   ['Full PV',shown(comparison.fullValueAtRisk),shown(comparison.fullExpectedShortfall),label.currency],
   ['Candidate approximation',shown(comparison.candidateValueAtRisk),shown(comparison.candidateExpectedShortfall),label.currency],
   ['Qualified hybrid',shown(comparison.hybridValueAtRisk),shown(comparison.hybridExpectedShortfall),label.currency]]),
   node('p',`Maximum candidate path error ${amountText(comparison.maximumCandidateError,'money')} ${label.currency}; maximum error in the full-PV quantile / ES tail ${amountText(comparison.maximumCandidateTailError,'money')} ${label.currency}.${comparison.portfolioFallback?' Portfolio error limits failed; the complete portfolio uses full PV.':''}`));
  const holdingRows=q.exposures.filter(e=>{const x=v.exposures.find(x=>x.id===e.exposureId)!;return x.measure===p.measure&&same(x.currency,p.currency);});
  const holdingLabel=(id:string)=>{const e=v.exposures.find(x=>x.id===id)!;return e.tradeReference+' · '+e.instrumentReference;};
  const classification=node('details');classification.open=true;classification.append(node('summary','Instrument qualification and fallback'),
   table(['Instrument / shared valuation','Applied method','Reason'],q.classifications.map(c=>[
    [...new Set(c.exposureIds.map(id=>v.exposures.find(e=>e.id===id)!.instrumentReference))].join(', '),
    c.method==='full_pv'?'Full PV':'PCA delta-gamma',c.reasons.length?c.reasons.map(x=>x.replaceAll('_',' ')).join('; '):'Derivative, all-path loss and standalone VaR / ES checks passed'])),
   node('p','Standalone holding VaR / ES are calculated independently for each native binding; they are not portfolio allocations.'),
   table(['Trade / instrument','Full-PV VaR','Candidate VaR','Full-PV ES','Candidate ES','Currency'],holdingRows.map(e=>[
    holdingLabel(e.exposureId),shown(e.fullValueAtRisk),shown(e.candidateValueAtRisk),shown(e.fullExpectedShortfall),shown(e.candidateExpectedShortfall),label.currency])),
   table(['Trade / instrument','Maximum path error','Error on portfolio-tail paths','Maximum actual-projection residual linear P&L','Currency'],holdingRows.map(e=>[
    holdingLabel(e.exposureId),shown(e.maximumAbsoluteError),shown(e.maximumTailError),shown(e.maximumResidualLinearPnl),label.currency])));
  panel.append(classification);
 }
 const graphics=node('div');graphics.className='risk-report-charts';panel.append(graphics);
 for(const spec of pcaCharts(v,partition,rows)){const plot=node('div');plot.className='chart risk-result-chart';graphics.append(plot);plots.push(plot);renderChart(plot,spec);}
 if(c){
  const matrix=node('details');matrix.append(node('summary','Residual covariance and numerical reconstruction'),
   node('p',`Dimensionless covariance reconstruction error ${amountText(c.reconstructionError,'factor')}; orthogonality error ${amountText(c.orthogonalityError,'factor')}; eigen-equation residual ${amountText(c.eigenResidual,'factor')}. Raw signed eigenvalues are retained. Small negative eigenvalues affect only the explained-variance diagnostic; the original covariance and native shock generator are unchanged.`),
   table(['Component','Raw eigenvalue','Variance share'],c.eigenvalues.map((x,i)=>[`PC ${i+1}`,shown(x,'factor'),shown(c.varianceShares[i],'factor')])));
  for(const [covariance,title] of [[c.residualCovariance,'Ideal PCA residual covariance'],[c.retainedResidualCovariance,'Ideal PCA retained / residual cross covariance']] as const){
   const plot=node('div');plot.className='chart risk-result-chart';matrix.append(plot);plots.push(plot);
   const spec=calibrationMatrix({...v.generation.calibration,covariance},v.factors,rows,columns,'covariance');spec.title=title;
   spec.subtitle='Diagnostic native units after ideal loading projection. The hybrid model uses the separately retained actual quantized-direction projection. Finite-precision retained and residual moves may have nonzero cross covariance; the full model retains both.';renderChart(plot,spec);
  }
  panel.append(matrix);
 }
 const cost=node('details');cost.open=true;cost.append(node('summary','Calculation cost'),
  node('p','Full repricing remains required for qualification. The additional column is measured fitting work, not estimated savings.'),
  table(['Work','Full-PV reference','Additional qualification'],([
   ['Unique unit valuations','uniqueValuations'],['Unique market contracts','uniqueMarketContracts'],['Scenarios including base','scenarioCount'],
   ['Unit-pricer calls','unitPricerCalls'],['Quantity applications','quantityApplications'],['Prepared volume computations','volumeComputations'],['Evaluated curve nodes','curveGraphEvaluatedNodes'],['Interval rows','rowEvaluations']
  ] as const).map(([label,key])=>[label,String(a.cost.reference[key]),a.cost.additional?String(a.cost.additional[key]):a.cost.attemptUnmetered?'Unavailable after failed attempt':'0'])));
 if(a.cost.attemptUnmetered)cost.append(node('p','The fitting attempt failed before complete work counters were returned. Reference counters exclude that attempt; total work is unavailable.'));
 panel.append(cost);
}
