import test from 'node:test';
import assert from 'node:assert/strict';
import {validateMonteCarlo,monteCarloCharts} from '../dist/monte-carlo-risk.js';
import {chartOptions} from '../dist/chart-view.js';
import {monteCarloReport} from './monte-carlo-fixture.mjs';
test('Monte Carlo report preserves backend losses, convergence and unbounded quantile endpoints',()=>{
 const r=monteCarloReport(),v=validateMonteCarlo(r,r.monteCarlo.request),charts=monteCarloCharts(v,0);
 assert.equal(v.result.diagnostics[0].valueAtRiskInterval.upper,null);
 for(const c of charts)assert.doesNotThrow(()=>chartOptions(c));
 assert.equal(charts.find(c=>c.title.includes('Simulated losses')).series[0].points[1].y,'10');
 assert.ok(charts.some(c=>c.title.includes('Convergence')));
 assert.equal(chartOptions(charts[0]).xAxis.name,'Loss interval · USD');
 const lossChart=chartOptions(charts.find(c=>c.title.includes('Simulated losses')));
 assert.equal(lossChart.yAxis.axisLabel.formatter(10.71318656333629),'10.7132');
 assert.equal(charts.find(c=>c.title.includes('Simulated losses')).series[0].points[1].y,'10');
});
test('Monte Carlo reader refuses incomplete or substituted simulation/PV/calibration evidence and mixed methods',()=>{
 for(const edit of [v=>v.history.samples[0].changes=[],v=>v.history.samples[0].changes=['missing'],v=>v.generation.calibration.rank=999,v=>v.generation.calibration.rank=.5,v=>v.generation.samples.pop(),v=>v.generation.samples[1].index=0,v=>v.generation.samples[0].changes=[],v=>v.matrix.rows.pop(),v=>v.matrix.rows[0].scenarioValues=[],v=>v.generation.policy={...v.generation.policy,seed:'43'},v=>v.generation.calibration.historyId='b'.repeat(64),v=>v.result.diagnostics=[],v=>v.exposures=[]]){
  const r=monteCarloReport();edit(r.monteCarlo);assert.throws(()=>validateMonteCarlo(r),/Monte Carlo|captured|complete/i);
 }
 const mixed=monteCarloReport();mixed.historical={};assert.throws(()=>validateMonteCarlo(mixed),/Monte Carlo/i);
 const numericSeed=monteCarloReport();numericSeed.monteCarlo.request.policy.seed=42;assert.throws(()=>validateMonteCarlo(numericSeed),/Monte Carlo/i);
});
test('Monte Carlo is one complete statistical report and cannot become sensitivity cells',async()=>{
 const {reportPresentation}=await import('../dist/risk-results.js'),{sensitivityFactors}=await import('../dist/risk-report.js');
 const r=monteCarloReport();r.scenario={depth:0,isBase:true};
 const run={key:1,running:false,queued:false,retained:false,context:r.context,scope:r.monteCarlo.request.history.scope,parts:[{report:r,unknown:false,unlisted:false}]};
 assert.equal(reportPresentation(run,'delta').monteCarlo,r.monteCarlo);
 assert.throws(()=>sensitivityFactors(r),/statistical/i);
 assert.throws(()=>reportPresentation({...run,parts:[...run.parts,...run.parts]},'delta'),/complete|parts/i);
});
