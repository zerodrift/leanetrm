import test from 'node:test';
import assert from 'node:assert/strict';
import {validateParametric,parametricCharts} from '../dist/parametric-risk.js';
import {chartOptions} from '../dist/chart-view.js';

import {currency,report,pricedReport} from './parametric-fixture.mjs';

test('parametric graphics transport backend distribution/contribution/correlation values in native currency',()=>{
 const r=report(),view=validateParametric(r,r.parametric.request),charts=parametricCharts(view,0);
 assert.ok(charts.some(c=>c.kind==='heatmap'));assert.ok(charts.some(c=>c.title.includes('contribution')));
 for(const spec of charts)assert.doesNotThrow(()=>chartOptions(spec));
 assert.equal(charts[0].series[0].points[0].y,r.parametric.result.partitions[0].density[0].density);
});
test('missing covariance/FD coverage, unstable successful results and wrong captured policy refuse display',()=>{
 for(const edit of [r=>r.parametric.result.calibration.covariance=[],r=>r.parametric.finiteDifference.estimates=[],r=>r.parametric.finiteDifference.estimates[0].stable=false,
  r=>r.parametric.history.id='c'.repeat(64),r=>r.parametric.result.partitions[0].valueAtRisk='4E6',r=>r.parametric.result.partitions=[]]){
  const r=report();edit(r);assert.throws(()=>validateParametric(r),/parametric|coverage|captured|consistent/i);
 }
 const expected=report().parametric.request,r=report();r.parametric.request.policy={...expected.policy,confidence:'0.95'};r.parametric.result.policy=r.parametric.request.policy;
 assert.throws(()=>validateParametric(r,expected),/parametric|captured|consistent/i);
});
test('unavailable calibration never displays successful risk cards; zero variance has no density',()=>{
 const r=report();r.parametric.result.available=false;r.parametric.result.partitions=[];r.parametric.result.unavailableReasons=['incomplete_portfolio'];r.parametric.partitions=[];
 validateParametric(r);assert.deepEqual(parametricCharts(r.parametric,0),[]);
 const constant=report();constant.parametric.result.partitions[0].density=[];constant.parametric.result.partitions[0].variance='0';constant.parametric.result.partitions[0].standardDeviation='0';
 validateParametric(constant);assert.ok(!parametricCharts(constant.parametric,0).some(c=>c.title.includes('density')));
});

test('one complete statistical report enters the shared report presentation and refuses sensitivity multipart composition',async()=>{
 const {reportPresentation}=await import('../dist/risk-results.js');const r=report();r.scenario={id:'base',depth:0,isBase:true};
 const run={key:1,queued:false,running:false,retained:false,setupText:'Parametric risk',context:r.context,scope:r.parametric.request.history.scope,parts:[{report:r,unknown:false,unlisted:false}]};
 const p=reportPresentation(run,'delta');assert.equal(p.state,'complete');assert.equal(p.parametric,r.parametric);assert.equal(p.factors.length,0);
 assert.throws(()=>reportPresentation({...run,parts:[...run.parts,...run.parts]},'delta'),/one complete|parts/);
});
test('a successful report cannot omit a native measure or book present in its finite differences',()=>{
 const r=report();r.parametric.finiteDifference.estimates.push({...r.parametric.finiteDifference.estimates[0],measure:'other_pv'});
 assert.throws(()=>validateParametric(r),/parametric|coverage|captured|consistent/i);
 const book=report();book.parametric.books.push({id:'hedge',name:'Hedge book'});book.parametric.finiteDifference.estimates.push({...book.parametric.finiteDifference.estimates[0],bookId:'hedge'});
 assert.throws(()=>validateParametric(book),/parametric|coverage|captured|consistent/i);
});

test('deleting a whole native partition or book cannot turn missing FD exposure into complete coverage',()=>{
 const partition=report(),v=partition.parametric;
 v.exposures.push({bookId:'book',measure:'other_pv',currency});v.finiteDifference.estimates.push({...v.finiteDifference.estimates[0],measure:'other_pv'});
 v.result.partitions.push({...structuredClone(v.result.partitions[0]),measure:'other_pv'});v.partitions.push({...structuredClone(v.partitions[0]),measure:'other_pv'});validateParametric(partition);
 v.finiteDifference.estimates.pop();v.result.partitions.pop();v.partitions.pop();assert.throws(()=>validateParametric(partition),/parametric|coverage|consistent/i);
 const book=report(),w=book.parametric;w.books.push({id:'hedge',name:'Hedge book'});w.exposures.push({bookId:'hedge',measure:'present_value',currency});
 w.finiteDifference.estimates.push({...w.finiteDifference.estimates[0],bookId:'hedge'});w.result.partitions[0].books.push({...w.result.partitions[0].books[0],id:'hedge'});validateParametric(book);
 w.finiteDifference.estimates.pop();w.result.partitions[0].books.pop();assert.throws(()=>validateParametric(book),/parametric|coverage|consistent/i);
});

test('covariance heatmap carries exact backend cells and both native units',()=>{
 const sample=report();const view=validateParametric(sample);const chart=parametricCharts(view,0,'valueAtRisk',0,0,'covariance').at(-1);
 assert.equal(chart.title,'Factor covariance');assert.equal(chart.series[0].points[0].y,view.result.calibration.covariance[0][0]);
 assert.ok(chart.series[0].points[0].text.includes(view.factors[0].factor.unit+' × '+view.factors[0].factor.unit));
});

test('retained parametric reports refuse incomplete EOD price provenance',()=>{
 const make=pricedReport;
 validateParametric(make());
 for(const edit of [r=>{delete r.parametric.history.dayPrices;r.parametric.history.policy={...r.parametric.history.policy,alignment:'exact_session_close_v1',regimes:'exact_source_role_state_unit_contract_v1'};delete r.parametric.history.policy.priceTypes;},r=>delete r.parametric.history.evidence[0].observation.observedAt,r=>delete r.parametric.history.evidence[0].observation.source,r=>delete r.parametric.history.dayPrices,r=>r.parametric.history.dayPrices[0].choices=[],r=>r.parametric.history.dayPrices[0].choices[0].availableTypes=[],r=>r.parametric.history.policy.priceTypes=['trader_mark_final_v1']]){
  const r=make();edit(r);assert.throws(()=>validateParametric(r),/price|provenance/i);
 }
});
