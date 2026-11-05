import test from 'node:test';
import assert from 'node:assert/strict';
import {validateMonteCarlo} from '../dist/monte-carlo-risk.js';
import {pcaHybridProfile,pcaHybridFallbackPolicy,pcaHybridLegacyPolicy,pcaCharts} from '../dist/pca-hybrid-risk.js';
import {chartOptions} from '../dist/chart-view.js';
import {pcaHybridReport as fixture} from './pca-hybrid-fixture.mjs';
test('PCA / hybrid reader and charts retain complete qualification, residuals and measured work',()=>{
 const r=fixture(),v=validateMonteCarlo(r,r.monteCarlo.request),charts=pcaCharts(v,0);
 assert.equal(v.analysis.fit.scenarioCount,12);assert.equal(charts.length,4);
 for(const spec of charts)assert.doesNotThrow(()=>chartOptions(spec));
 assert.equal(charts[0].series[0].points[0].y,'1');assert.equal(chartOptions(charts[0]).xAxis.name,'Principal component');
 assert.equal(charts[3].series[2].points[1].y,v.result.partitions[0].samples[1].loss);
});
test('PCA / hybrid refuses missing native, PC, fitting, model, partition and classification evidence',()=>{
 for(const edit of [a=>a.pca=null,a=>a.pca.scales=['0'],a=>a.pca.varianceShares=['-0.1'],a=>a.pca.retainedVarianceShare='2',a=>a.fit.directionError='0.00000000011',a=>a.fit.projectionError='1',a=>a.pca.orthogonalityError='0.1',a=>a.pca.samples[0].reconstructionError='1',a=>a.pca.scales=[],a=>a.pca.columns=['other'],a=>a.pca.samples.pop(),a=>a.pca.samples[0].scores=[],a=>a.fit.pcDirections=[[]],a=>a.fit.projection=[],a=>a.fit.scenarioCount=11,a=>a.finiteDifferenceValues.pop(),a=>a.finiteDifferenceValues[0].values=[],a=>a.comparison.models=[],a=>a.comparison.models[0].delta=[],a=>a.comparison.models[0].gamma=[],a=>a.comparison.models[0].stable=false,a=>a.comparison.exposures[0].lossesPass=false,a=>a.comparison.classifications=[],a=>a.comparison.partitions[0].fullValueAtRisk='999',a=>a.comparison.hybrid[0].samples.pop(),a=>a.cost.attemptUnmetered=true]){
  const r=fixture();edit(r.monteCarlo.analysis);assert.throws(()=>validateMonteCarlo(r),/PCA|coverage/i);
 }
 const missing=fixture();delete missing.monteCarlo.analysis;assert.throws(()=>validateMonteCarlo(missing),/PCA/i);
 const unselected=fixture();delete unselected.monteCarlo.request.analysis;assert.throws(()=>validateMonteCarlo(unselected),/PCA/i);
});
test('PCA fitting failure preserves the complete full-PV reference with honest unmetered-attempt accounting',()=>{
 const r=fixture(),a=r.monteCarlo.analysis;a.policy.valuationPolicy=pcaHybridFallbackPolicy;a.cost.mode=pcaHybridFallbackPolicy;a.available=false;a.fallbackClassifications=a.comparison.classifications.map(c=>({...c,method:'full_pv',reasons:['price_domain']}));a.fit=null;a.comparison=null;a.finiteDifferenceValues=[];a.unavailableReasons=[{code:'price_domain',message:'Fitting direction is outside the pricing domain.'}];a.cost.additional=null;a.cost.attemptUnmetered=true;
 assert.equal(validateMonteCarlo(r).result.available,true);assert.equal(pcaCharts(r.monteCarlo,0).length,3);
});

test('explained-variance shares admit binary64 sum roundoff without claiming extra risk coverage',()=>{
 const r=fixture();r.monteCarlo.analysis.pca.retainedVarianceShare='1.0000000000000002';assert.doesNotThrow(()=>validateMonteCarlo(r));
});

test('a report cannot apply full-PV fallback when the selected policy forbids it',()=>{
 const r=fixture();r.monteCarlo.analysis.comparison.classifications[0].method='full_pv';
 r.monteCarlo.analysis.comparison.classifications[0].reasons=['unstable_finite_differences'];
 assert.throws(()=>validateMonteCarlo(r),/PCA/i);
});


test('new opt-in and immutable legacy fallback reports remain readable under their retained policy',()=>{
 for(const policy of [pcaHybridFallbackPolicy,pcaHybridLegacyPolicy]){
  const r=fixture(),a=r.monteCarlo.analysis;a.policy.valuationPolicy=policy;a.cost.mode=policy;
  a.comparison.classifications[0].method='full_pv';a.comparison.classifications[0].reasons=['unstable_finite_differences'];
  assert.doesNotThrow(()=>validateMonteCarlo(r));
 }
});
test('no-fallback reports reject unavailable analysis, portfolio fallback and unclassified hybrid substitution',()=>{
 for(const edit of [a=>{a.available=false;},a=>{a.comparison.partitions[0].portfolioFallback=true;},a=>{a.comparison.hybrid[0].valueAtRisk='99';}]){
  const r=fixture();edit(r.monteCarlo.analysis);assert.throws(()=>validateMonteCarlo(r),/PCA/i);
 }
});
