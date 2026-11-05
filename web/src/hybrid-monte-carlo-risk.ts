import {exactNumber} from './exact-number.js';
import {validateSampleCoverage} from './historical-risk.js';
import {validatePcaPolicy,validatePcaCalibration,validatePcaPlan,validatePcaFit,validatePcaComparison,type PcaHybridView,type PcaWork} from './pca-hybrid-risk.js';
import type {MonteCarloPolicy,MonteCarloView,MonteCarloContainer} from './monte-carlo-risk.js';

export const hybridMode='dated_factor_hybrid_monte_carlo_v1',hybridValuation='pca_delta_gamma_frozen_nonfactor_inputs_v1',hybridUncertainty='empirical_prefix_diagnostics_no_interval_v1';
export const hybridStrict='sampled_panel_no_fallback_qualification_v1',hybridFallback='sampled_panel_opt_in_whole_portfolio_full_pv_v1';
export const hybridExecutionProfile={convention:'pca_delta_gamma_sampled_pv_validation_v1',validationPolicy:'sha256_ranked_indices_independent_of_loss_v1',fallbackScope:'whole_portfolio_full_pv_v1'};
export type HybridExecutionPolicy=typeof hybridExecutionProfile&{validationPaths:number;validationSeed:string};
export type HybridCapabilities={profile:Omit<MonteCarloPolicy,'confidence'|'drift'|'paths'|'seed'>;executionProfile:typeof hybridExecutionProfile;fittingPolicies:{strict:string;fullPvFallback:string};
 defaults:{retainedComponents:number;validationPaths:number;validationSeed:string;paths:number};capacities:{components:number;validationPaths:number;fallback:boolean;scopeMaxPaths:number}[]};
type Comparison=NonNullable<PcaHybridView['comparison']>;
export type HybridMonteCarloView={policy:HybridExecutionPolicy;fittingPolicy:NonNullable<MonteCarloView['request']['analysis']>;appliedMethod:'pca_delta_gamma'|'full_pv';approximate:boolean;fallbackApplied:boolean;
 fallbackReasons:{code:string;message:string}[];classifications:Comparison['classifications'];pca:PcaHybridView['pca'];fit:PcaHybridView['fit'];finiteDifferenceValues:PcaHybridView['finiteDifferenceValues'];models:Comparison['models'];maximumResidualLinearPnl:string[];
 validation:{selectedSamples:{id:string;index:number}[];available:boolean;matrix:MonteCarloView['matrix']|null;comparison:Comparison|null;scope:string;observedPanelPaths:number;outsideValidationPanel:number;unpricedPaths:number;quoteDomainPaths:number;quoteDomainChecks:number};
 cost:{phases:{phase:string;work:PcaWork}[];predictedValues:number;attemptUnmetered:boolean}};
const canonical=(v:unknown)=>JSON.stringify(v,(_k,x)=>x&&typeof x==='object'&&!Array.isArray(x)?Object.fromEntries(Object.entries(x).sort(([a],[b])=>a.localeCompare(b))):x);
const same=(a:unknown,b:unknown)=>canonical(a)===canonical(b);
const covers=(a:string[],b:string[])=>{const expected=new Set(b);return a.length===b.length&&new Set(a).size===a.length&&a.every(x=>expected.has(x));};
const numeric=(v:unknown):v is string=>typeof v==='string'&&exactNumber(v)!==null;
const count=(v:unknown):v is number=>typeof v==='number'&&Number.isSafeInteger(v)&&v>=0;
const bad=()=>Error('The hybrid Monte Carlo report has incomplete validation, consent or valuation evidence. Reread the complete report.');
const partKey=(p:{measure:string;currency:unknown})=>canonical([p.measure,p.currency]);
export function validateHybridMonteCarlo(report:MonteCarloContainer,v:MonteCarloView){
 const e=v.request.hybrid,p=v.request.analysis,x=v.hybrid,n=v.request.policy.paths;
 if(!e||!p||v.analysis!=null||Object.entries(hybridExecutionProfile).some(([k,value])=>e[k as keyof HybridExecutionPolicy]!==value)||
  !Number.isInteger(e.validationPaths)||e.validationPaths<2||e.validationPaths>n||typeof e.validationSeed!=='string'||!/^(0|[1-9][0-9]{0,19})$/.test(e.validationSeed)||BigInt(e.validationSeed)>18446744073709551615n)throw bad();
 validatePcaPolicy(p,v.factors.length,[hybridStrict,hybridFallback]);
 if(!v.result.available){if(x!=null)throw bad();return;}
 if(!x||!same(x.policy,e)||!same(x.fittingPolicy,p)||!['pca_delta_gamma','full_pv'].includes(x.appliedMethod)||typeof x.fallbackApplied!=='boolean'||
  x.approximate!==(x.appliedMethod==='pca_delta_gamma')||x.fallbackApplied!==(x.appliedMethod==='full_pv')||!Array.isArray(x.fallbackReasons)||
  (x.fallbackApplied?(p.valuationPolicy!==hybridFallback||!x.fallbackReasons.length):x.fallbackReasons.length>0)||
  x.fallbackReasons.some(r=>!r||typeof r.code!=='string'||!r.code||typeof r.message!=='string'||!r.message))throw bad();
 const h=x.validation,ids=h?.selectedSamples?.map(s=>s.id),exposures=v.exposures.map(e=>e.id);
 if(!h||h.scope!=='observed_panel_only_v1'||typeof h.available!=='boolean'||!Array.isArray(h.selectedSamples)||h.selectedSamples.length!==e.validationPaths||
  new Set(ids).size!==ids.length||h.selectedSamples.some((s,i)=>!Number.isInteger(s.index)||s.index<0||s.index>=n||s.id!==v.generation.samples[s.index]?.id||i>0&&s.index<=h.selectedSamples[i-1].index)||
  ![h.observedPanelPaths,h.outsideValidationPanel,h.unpricedPaths,h.quoteDomainPaths,h.quoteDomainChecks].every(count)||
  h.outsideValidationPanel!==n-e.validationPaths||h.quoteDomainPaths!==n||h.observedPanelPaths!==(h.matrix?e.validationPaths:0)||
  h.unpricedPaths!==(x.fallbackApplied?0:n-h.observedPanelPaths)||h.available!==(h.comparison!==null))throw bad();
 if(!Array.isArray(x.classifications)||!covers(x.classifications.flatMap(c=>c.exposureIds),exposures)||new Set(x.classifications.map(c=>c.valuationKey)).size!==x.classifications.length||
  x.classifications.some(c=>!c.valuationKey||c.method!==x.appliedMethod||!Array.isArray(c.reasons)||(x.fallbackApplied?!c.reasons.length:c.reasons.length>0)))throw bad();
 const cost=x.cost;if(!cost||!Array.isArray(cost.phases)||!cost.phases.length||!count(cost.predictedValues)||typeof cost.attemptUnmetered!=='boolean'||!Array.isArray(x.finiteDifferenceValues)||!Array.isArray(x.models)||!Array.isArray(x.maximumResidualLinearPnl))throw bad();
 const names=cost.phases.map(s=>s.phase),order=['fitting','validation','consented_full_pv_fallback'];
 if(new Set(names).size!==names.length||names.some((s,i)=>!order.includes(s)||i>0&&order.indexOf(s)<=order.indexOf(names[i-1]))||
  names.includes('consented_full_pv_fallback')!==x.fallbackApplied||!x.fallbackApplied&&(cost.attemptUnmetered||!same(names,['fitting','validation'])||!h.available))throw bad();
 for(const phase of cost.phases){
  const expected=phase.phase==='fitting'?1+6*v.factors.length+6*p.retainedComponents*p.retainedComponents:phase.phase==='validation'?1+e.validationPaths:1+n;
  if(!phase.work||phase.work.scenarioCount!==expected||!count(phase.work.unitPricerCalls)||Object.values(phase.work).some(value=>typeof value==='number'&&!count(value)))throw bad();
 }
 validatePcaCalibration(v,p,x.pca);
 if(x.fit){if(!x.pca)throw bad();validatePcaPlan(v,p,x.fit);}
 const fitted=names.includes('fitting');
 if(fitted){if(!x.pca)throw bad();validatePcaFit(v,p,x.fit,x.finiteDifferenceValues);}else if(x.finiteDifferenceValues.length||x.models.length)throw bad();
 if(x.models.length){
  if(!covers(x.models.map(m=>m.exposureId),exposures)||x.models.some(m=>!Array.isArray(m.delta)||m.delta.length!==v.factors.length||!m.delta.every(numeric)||!Array.isArray(m.gamma)||m.gamma.length!==p.retainedComponents||m.gamma.some(row=>row.length!==p.retainedComponents||!row.every(numeric))||typeof m.stable!=='boolean')||
   x.maximumResidualLinearPnl.length!==exposures.length||x.maximumResidualLinearPnl.some(s=>!numeric(s)||s.startsWith('-'))||cost.predictedValues!==n*exposures.length)throw bad();
 }else if(cost.predictedValues!==0||x.maximumResidualLinearPnl.length||!x.fallbackApplied)throw bad();
 if(h.matrix){
  if(!names.includes('validation')||!Array.isArray(h.matrix.rows)||!covers(h.matrix.rows.map(s=>s.id),ids)||!same(h.matrix.columns,v.matrix.columns))throw bad();
 }else if(names.includes('validation')||h.available)throw bad();
 const q=h.comparison;
 if(q){
  if(!x.pca||!fitted||!h.matrix||!Array.isArray(q.reference)||!same(q.models,x.models))throw bad();
  const panelView={...v,matrix:h.matrix,result:{...v.result,samples:e.validationPaths,partitions:q.reference}};
  validateSampleCoverage(report,panelView,ids);validatePcaComparison(report,panelView,p,q,p.valuationPolicy===hybridFallback);
  const failed=q.classifications.some(c=>c.method==='full_pv');
  if(x.fallbackApplied!==failed||!x.fallbackApplied&&q.partitions.some(p=>p.portfolioFallback))throw bad();
  for(const native of q.candidate){const main=v.result.partitions.find(p=>partKey(p)===partKey(native));if(!main)throw bad();
   const mainLosses=new Map(main.samples.map(s=>[s.id,s.loss]));
   if(!x.fallbackApplied&&native.samples.some(s=>mainLosses.get(s.id)!==s.loss))throw bad();}
 }
 if(x.fallbackApplied&&h.matrix){const rows=new Map(v.matrix.rows.map(s=>[s.id,s]));for(const row of h.matrix.rows){const main=rows.get(row.id);if(!main||!same(main,row))throw bad();}}
}
