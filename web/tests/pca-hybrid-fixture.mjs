import {pcaHybridProfile} from '../dist/pca-hybrid-risk.js';
import {monteCarloReport} from './monte-carlo-fixture.mjs';
export function pcaHybridReport(){
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
