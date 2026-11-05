import test from 'node:test';
import assert from 'node:assert/strict';
import {validateMonteCarlo} from '../dist/monte-carlo-risk.js';
import {hybridMonteCarloReport} from './hybrid-monte-carlo-fixture.mjs';
test('hybrid reader accepts only its explicit approximate method without an inherited sampling interval',()=>{
 const r=hybridMonteCarloReport();assert.doesNotThrow(()=>validateMonteCarlo(r,r.monteCarlo.request));
 assert.equal(r.monteCarlo.hybrid.approximate,true);
});

test('hybrid reader refuses silent substitutions and missing policy, panel, model, work or domain evidence',()=>{
 for(const edit of [
  (v)=>delete v.request.hybrid,(v)=>delete v.request.analysis,(v)=>v.request.policy.valuationPolicy='current_instrument_pv_frozen_nonfactor_inputs_v1',
  (v)=>v.result.diagnostics[0].valueAtRiskInterval={confidence:'0.95',lowerRank:0,upperRank:3,lower:null,upper:null},
  (v)=>v.hybrid.appliedMethod='full_pv',(v)=>v.hybrid.fallbackApplied=true,(v)=>v.hybrid.classifications[0].method='full_pv',
  (v)=>v.hybrid.validation.selectedSamples[1]=v.hybrid.validation.selectedSamples[0],(v)=>v.hybrid.validation.selectedSamples[0].index=1,
  (v)=>v.hybrid.validation.selectedSamples.reverse(),(v)=>v.hybrid.validation.quoteDomainPaths=1,(v)=>v.hybrid.validation.observedPanelPaths=0,
  (v)=>v.hybrid.validation.unpricedPaths=1,(v)=>v.hybrid.validation.scope='entire_distribution',
  (v)=>v.hybrid.validation.comparison.reference=null,(v)=>v.hybrid.validation.matrix.rows.pop(),
  (v)=>v.hybrid.validation.comparison.candidate[0].samples[0].loss='999',
  (v)=>v.hybrid.models=[],(v)=>v.hybrid.maximumResidualLinearPnl=[],(v)=>v.hybrid.cost.phases.pop(),
  (v)=>v.hybrid.cost.phases[0].work.scenarioCount=12,(v)=>v.hybrid.cost.attemptUnmetered=true,(v)=>v.hybrid.cost.predictedValues=0,
 ]){const r=hybridMonteCarloReport();edit(r.monteCarlo);assert.throws(()=>validateMonteCarlo(r),/hybrid|Monte Carlo|PCA|coverage/i);}
});
function fallback(){
 const r=hybridMonteCarloReport(),v=r.monteCarlo,x=v.hybrid;
 v.request.analysis.valuationPolicy='sampled_panel_opt_in_whole_portfolio_full_pv_v1';
 x.appliedMethod='full_pv';x.approximate=false;x.fallbackApplied=true;x.fallbackReasons=[{code:'risk_hybrid_validation',message:'Observed panel error failed.'}];
 x.classifications=x.classifications.map(c=>({...c,method:'full_pv',reasons:['risk_hybrid_validation']}));
 x.models[0].stable=false;
 x.validation.comparison.classifications[0]={...x.validation.comparison.classifications[0],method:'full_pv',reasons:['unstable_finite_differences']};
 x.cost.phases.push({phase:'consented_full_pv_fallback',work:{...x.cost.phases[1].work,scenarioCount:3}});
 return r;
}
test('consented full-portfolio fallback retains all actual PV rows and never inherits an interval',()=>{
 const r=fallback();assert.doesNotThrow(()=>validateMonteCarlo(r));
 assert.equal(r.monteCarlo.hybrid.validation.unpricedPaths,0);
 r.monteCarlo.result.diagnostics[0].valueAtRiskInterval={confidence:'0.95',lowerRank:0,upperRank:3,lower:null,upper:null};
 assert.throws(()=>validateMonteCarlo(r),/Monte Carlo/i);
});
test('a failed fitting attempt can return only an explicitly consented complete fallback with honest work',()=>{
 const r=fallback(),x=r.monteCarlo.hybrid;x.pca=null;x.fit=null;x.models=[];x.maximumResidualLinearPnl=[];x.finiteDifferenceValues=[];
 x.validation.available=false;x.validation.matrix=null;x.validation.comparison=null;x.validation.observedPanelPaths=0;
 x.cost.phases=x.cost.phases.filter(p=>p.phase==='consented_full_pv_fallback');x.cost.predictedValues=0;x.cost.attemptUnmetered=true;
 assert.doesNotThrow(()=>validateMonteCarlo(r));
 x.fittingPolicy.valuationPolicy='sampled_panel_no_fallback_qualification_v1';assert.throws(()=>validateMonteCarlo(r),/hybrid/i);
});
test('fallback with a completed comparison requires an observed failed qualification',()=>{
 const r=fallback(),x=r.monteCarlo.hybrid;
 x.validation.comparison.classifications=x.validation.comparison.classifications.map(c=>({...c,method:'pca_delta_gamma',reasons:[]}));
 x.models.forEach(m=>m.stable=true);
 x.validation.comparison.partitions.forEach(p=>p.portfolioFallback=false);
 assert.throws(()=>validateMonteCarlo(r),/hybrid/i);
});
