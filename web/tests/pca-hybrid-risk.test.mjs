import test from 'node:test';
import assert from 'node:assert/strict';
import {validateMonteCarlo} from '../dist/monte-carlo-risk.js';
import {pcaHybridProfile,pcaCharts} from '../dist/pca-hybrid-risk.js';
import {chartOptions} from '../dist/chart-view.js';
import {monteCarloReport} from './monte-carlo-fixture.mjs';
function fixture(){
 const r=monteCarloReport(),v=r.monteCarlo,policy={...pcaHybridProfile,retainedComponents:1,tradingTicks:1,stabilityRelative:'0.0001',lossAbsolute:'0',lossRelative:'0.001'};
 v.request.analysis=policy;
 const work={unitPricerCalls:3,scenarioCount:3,uniqueValuations:1,uniqueMarketContracts:1,quantityApplications:3,curveGraphEvaluatedNodes:3,rowEvaluations:0,volumeComputations:0};
 const fit=[];for(const kind of ['native','pc'])for(let step=0;step<3;step++)for(let sign=0;sign<2;sign++)fit.push({id:`${kind}/0/${step}/${sign}`,values:v.exposures.map(e=>({exposureId:e.id,value:'0'}))});
 v.analysis={policy,available:true,fallbackClassifications:[],unavailableReasons:[],pca:{policy,columns:v.generation.calibration.columns,scales:['1'],eigenvalues:['1'],varianceShares:['1'],loadings:[['1']],residualCovariance:[['0']],retainedResidualCovariance:[['0']],matrixNorm:'1',sweeps:0,roundoffNegativeEigenvalues:0,reconstructionError:'0',orthogonalityError:'0',eigenResidual:'0',retainedVarianceShare:'1',samples:v.generation.samples.map(s=>({id:s.id,scores:s.changes,residualNorm:'0',reconstructionError:'0'}))},
  fit:{nativeSteps:['0.01'],pcDirections:[['0.01']],projection:[['100']],directionError:'0',projectionError:'0',scenarioCount:12},finiteDifferenceValues:fit,
  comparison:{models:v.exposures.map(e=>({exposureId:e.id,delta:['1'],gamma:[['0']],stable:true})),exposures:v.exposures.map(e=>({exposureId:e.id,fullValueAtRisk:'0',fullExpectedShortfall:'0',candidateValueAtRisk:'0',candidateExpectedShortfall:'0',maximumAbsoluteError:'0',maximumTailError:'0',maximumResidualLinearPnl:'0',lossesPass:true})),classifications:v.exposures.map(e=>({valuationKey:e.id,exposureIds:[e.id],method:'pca_delta_gamma',reasons:[]})),partitions:v.result.partitions.map(p=>({measure:p.measure,currency:p.currency,fullValueAtRisk:p.valueAtRisk,fullExpectedShortfall:p.expectedShortfall,candidateValueAtRisk:p.valueAtRisk,candidateExpectedShortfall:p.expectedShortfall,hybridValueAtRisk:p.valueAtRisk,hybridExpectedShortfall:p.expectedShortfall,maximumCandidateError:'0',maximumCandidateTailError:'0',portfolioFallback:false})),candidate:structuredClone(v.result.partitions),hybrid:structuredClone(v.result.partitions)},
  cost:{mode:policy.valuationPolicy,reference:work,additional:{...work,unitPricerCalls:13,scenarioCount:13},attemptUnmetered:false}};
 return r;
}
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
 const r=fixture(),a=r.monteCarlo.analysis;a.available=false;a.fallbackClassifications=a.comparison.classifications.map(c=>({...c,method:'full_pv',reasons:['price_domain']}));a.fit=null;a.comparison=null;a.finiteDifferenceValues=[];a.unavailableReasons=[{code:'price_domain',message:'Fitting direction is outside the pricing domain.'}];a.cost.additional=null;a.cost.attemptUnmetered=true;
 assert.equal(validateMonteCarlo(r).result.available,true);assert.equal(pcaCharts(r.monteCarlo,0).length,3);
});

test('explained-variance shares admit binary64 sum roundoff without claiming extra risk coverage',()=>{
 const r=fixture();r.monteCarlo.analysis.pca.retainedVarianceShare='1.0000000000000002';assert.doesNotThrow(()=>validateMonteCarlo(r));
});
