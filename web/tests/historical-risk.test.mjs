import test from 'node:test';
import assert from 'node:assert/strict';
import {validateHistorical,historicalCharts} from '../dist/historical-risk.js';
import {chartOptions} from '../dist/chart-view.js';
import {historicalReport} from './historical-fixture.mjs';
const currency={revision:{catalogId:'reference',revisionId:'version'},code:'usd'};
test('historical graphics preserve backend histogram, loss, VaR/ES and allocation values',()=>{
 const r=historicalReport(),view=validateHistorical(r,r.historical.request),charts=historicalCharts(view,0,'valueAtRisk');
 assert.ok(charts.some(c=>c.title.includes('distribution')));assert.ok(charts.some(c=>c.title.includes('allocation')));
 for(const c of charts)assert.doesNotThrow(()=>chartOptions(c));
 const losses=charts.find(c=>c.title.includes('Historical losses'));
 assert.equal(losses.series[0].points[1].y,'10');assert.equal(losses.reference.value,'10');
});
test('historical reader refuses missing native holdings, sample PV cells, mixed methods and altered capture',()=>{
 for(const edit of [r=>r.historical.matrix.rows.pop(),r=>r.historical.matrix.columns=[],r=>r.historical.matrix.rows[0].values=[],r=>r.historical.result.partitions[0].samples.pop(),r=>r.historical.exposures=[],r=>r.historical.result.partitions=[],r=>r.historical.result.partitions[0].books=[],r=>r.historical.history.id='b'.repeat(64),r=>r.historical.request.policy={...r.historical.request.policy,confidence:'0.99'},r=>r.parametric={},r=>r.factors=[],r=>r.inputs.positions.push({...r.inputs.positions[0],bindingId:'missing'})]){
  const r=historicalReport();edit(r);assert.throws(()=>validateHistorical(r),/historical|coverage|captured|complete/i);
 }
});
test('unavailable history has no fabricated distribution and is shown as unavailable',async()=>{
 const r=historicalReport(),v=r.historical;v.result.available=false;v.result.unavailableReasons=['insufficient_common_history'];v.result.partitions=[];v.partitions=[];v.exposures=[];v.matrix={columns:[],rows:[]};
 validateHistorical(r);assert.deepEqual(historicalCharts(v,0),[]);
 const {reportPresentation}=await import('../dist/risk-results.js');r.scenario={id:'base',depth:0,isBase:true};
 const run={key:1,queued:false,running:false,retained:false,context:r.context,scope:v.request.history.scope,parts:[{report:r,unknown:false,unlisted:false}]};
 assert.equal(reportPresentation(run,'delta').state,'unavailable');
 assert.throws(()=>reportPresentation({...run,parts:[...run.parts,...run.parts]},'delta'),/complete|parts/);
});
function withComparison(r){
 const v=r.historical,policy={convention:'dated_factor_delta_normal_v1',numericalPolicy:'exact_moments_binary64_normal_v1',estimator:'equal_weight_sample_n_minus_one_v1',centering:'sample_mean_centered_v1',drift:'sample_mean_drift_v1',horizon:v.request.policy.horizon,tail:'signed_upper_loss_tail_v1',confidence:v.request.policy.confidence};
 const finiteDifference={convention:'native_tick_central_half_nominal_double_v1',tradingTicks:1,deltaTolerance:{absolute:'0',relative:'0'}};
 v.request.comparison={policy,finiteDifference};
 v.comparison={studyId:v.studyId,report:{request:{history:v.request.history,policy,finiteDifference},history:v.history,
  result:{policy,available:false,unavailableReasons:['unstable_delta'],calibration:{historyId:v.history.id,samples:2,columns:['factor'],means:['0'],covariance:[['1']],correlation:[['1']],rank:1},partitions:[]},
  exposures:[{bookId:'book',measure:'present_value',currency}],factors:v.factors,books:v.books,partitions:[],finiteDifference:{...finiteDifference,evaluated:false,estimates:[]},passes:[]}};
 return r;
}
test('a matching comparison must cover the exact historical native exposure groups, including unavailable normal risk',()=>{
 const valid=withComparison(historicalReport());validateHistorical(valid);
 const substituted=withComparison(historicalReport());substituted.historical.comparison.report.exposures[0].bookId='different-book';substituted.historical.comparison.report.books=[{id:'different-book',name:'Other book'}];
 assert.throws(()=>validateHistorical(substituted),/historical|coverage|captured/i);
 const deleted=withComparison(historicalReport()),v=deleted.historical,e={...v.exposures[0],id:'second',bookId:'hedge',tradeId:'hedge-trade',bindingId:'hedge-binding'};
 v.exposures.push(e);deleted.inputs.positions.push(e);v.books=[...v.books,{id:'hedge',name:'Hedge'}];v.matrix.columns.push('second');for(const row of v.matrix.rows){row.values.push('100');row.losses.push('0');row.displayLosses.push('0');}
 const contribution={id:'hedge',baseValue:'100',valueAtRisk:'0',expectedShortfall:'0'};v.result.partitions[0].books.push(contribution);v.result.partitions[0].trades.push({...contribution,id:'hedge-trade'});v.partitions[0].books.push(contribution);v.partitions[0].trades.push({...contribution,id:'hedge-trade'});
 assert.throws(()=>validateHistorical(deleted),/historical|coverage|captured/i);
});
test('historical sample labels use captured business dates across NY end-of-day DST, not UTC endpoint dates',()=>{
 const r=historicalReport(),v=r.historical;v.history.sessions=[{date:'2026-10-31',close:'2026-11-01T04:00:00Z'},{date:'2026-11-01',close:'2026-11-02T05:00:00Z'},{date:'2026-11-02',close:'2026-11-03T05:00:00Z'}];
 for(const [i,s] of v.history.samples.entries()){s.from={at:v.history.sessions[i].close};s.to={at:v.history.sessions[i+1].close};}
 const loss=historicalCharts(v,0).find(c=>c.title.includes('Historical losses'));
 assert.equal(loss.series[0].points[0].label,'2026-10-31 → 2026-11-01');
 assert.equal(loss.series[0].points[1].label,'2026-11-01 → 2026-11-02');
});
test('Blotter statistical status directs complete reports to shared graphics without inventing sensitivity cells',async()=>{
 const {runBar}=await import('../dist/risk-lens.js'),r=historicalReport();r.projectionPolicies=[];r.scenario={depth:0,isBase:true};
 const run={key:1,queued:false,running:false,retained:false,setupText:'Historical VaR',context:r.context,parts:[{report:r,unknown:false,unlisted:false}]};
 const bar=runBar(run,{zone:'UTC',knowledge:null,results:{state:'statistical',excluded:[]}});
 assert.ok(bar.notes.some(n=>n.text.includes('VaR')&&n.text.includes('Full risk report')));
 assert.ok(!bar.notes.some(n=>n.kind==='failed'));
});
