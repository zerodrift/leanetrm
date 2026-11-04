import {historicalReport} from './historical-fixture.mjs';
export function monteCarloReport(){
 const r=historicalReport(),h=r.historical;delete r.historical;
 const policy={convention:'dated_factor_full_pv_monte_carlo_v1',numericalPolicy:'exact_ldl_binary64_polar_v1',estimator:'equal_weight_sample_n_minus_one_v1',centering:'sample_mean_centered_v1',drift:'zero_drift_v1',horizon:h.result.policy.horizon,quantilePolicy:h.result.policy.quantilePolicy,tailPolicy:h.result.policy.tailPolicy,valuationPolicy:h.result.policy.valuationPolicy,confidence:h.result.policy.confidence,distribution:'joint_additive_gaussian_v1',randomGenerator:'sha256_counter_polar_log_series_v1',shockPolicy:'binary64_exact_to_native_18_places_to_even_v1',seed:'42',paths:2,samplingUncertainty:'binomial_order_statistic_binary64_95_v1'};
 const samples=h.history.samples.map((s,index)=>({id:'mc_'+String(index).padStart(5,'0'),index,changes:s.changes}));
 for(const [i,row] of h.matrix.rows.entries())row.id=samples[i].id;
 for(const p of h.result.partitions)for(const [i,s] of p.samples.entries())s.id=samples[i].id;
 r.monteCarlo={...h,request:{history:h.request.history,policy},result:{...h.result,policy,diagnostics:h.result.partitions.map(p=>({measure:p.measure,currency:p.currency,convergence:[{paths:2,valueAtRisk:p.valueAtRisk,expectedShortfall:p.expectedShortfall,tailSampleMass:p.tailSampleMass}],valueAtRiskInterval:{confidence:'0.95',lowerRank:0,upperRank:3,lower:null,upper:null}}))},generation:{historyId:h.history.id,policy,calibration:{historyId:h.history.id,samples:h.history.samples.length,columns:h.history.columns.map(c=>c.id),means:['0'],covariance:[['1']],correlation:[['1']],rank:1,constantColumns:[],conditioning:'full_rank',correlationCondition:'1'},lower:[['1']],diagonal:['1'],samples,available:true,unavailableReasons:[]}};
 delete r.monteCarlo.comparison;return r;
}
