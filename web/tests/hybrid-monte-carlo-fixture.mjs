import {pcaHybridReport} from './pca-hybrid-fixture.mjs';
export function hybridMonteCarloReport(){
 const r=pcaHybridReport(),v=r.monteCarlo,a=v.analysis;delete v.analysis;
 Object.assign(v.request.policy,{convention:'dated_factor_hybrid_monte_carlo_v1',valuationPolicy:'pca_delta_gamma_frozen_nonfactor_inputs_v1',samplingUncertainty:'empirical_prefix_diagnostics_no_interval_v1'});
 for(const d of v.result.diagnostics)delete d.valueAtRiskInterval;
 v.request.analysis.valuationPolicy='sampled_panel_no_fallback_qualification_v1';
 const policy={convention:'pca_delta_gamma_sampled_pv_validation_v1',validationPolicy:'sha256_ranked_indices_independent_of_loss_v1',validationPaths:2,validationSeed:'1',fallbackScope:'whole_portfolio_full_pv_v1'};
 v.request.hybrid=policy;
 v.hybrid={policy,fittingPolicy:v.request.analysis,appliedMethod:'pca_delta_gamma',approximate:true,fallbackApplied:false,fallbackReasons:[],
  classifications:a.comparison.classifications,pca:a.pca,fit:a.fit,finiteDifferenceValues:a.finiteDifferenceValues,models:a.comparison.models,maximumResidualLinearPnl:v.exposures.map(()=> '0'),
  validation:{selectedSamples:v.generation.samples.map(s=>({id:s.id,index:s.index})),available:true,matrix:structuredClone(v.matrix),comparison:{...a.comparison,reference:structuredClone(v.result.partitions)},scope:'observed_panel_only_v1',observedPanelPaths:2,outsideValidationPanel:0,unpricedPaths:0,quoteDomainPaths:2,quoteDomainChecks:2},
  cost:{phases:[{phase:'fitting',work:{...a.cost.additional,scenarioCount:13}},{phase:'validation',work:{...a.cost.reference,scenarioCount:3}}],predictedValues:2*v.exposures.length,attemptUnmetered:false}};
 return r;
}
