import {amountText} from './amount-display.js';
import {exactNumber} from './exact-number.js';
import {validateSampleCoverage} from './historical-risk.js';
import type {MonteCarloContainer,MonteCarloView} from './monte-carlo-risk.js';
import type {ChartSpec} from './chart-view.js';

export type PcaHybridPolicy={convention:string;numericalPolicy:string;scalingPolicy:string;residualPolicy:string;valuationPolicy:string;directionPolicy:string;retainedComponents:number;tradingTicks:number;stabilityRelative:string;lossAbsolute:string;lossRelative:string};
export type PcaHybridCapabilities={profile:Omit<PcaHybridPolicy,'retainedComponents'|'tradingTicks'|'stabilityRelative'|'lossAbsolute'|'lossRelative'>;defaults:Pick<PcaHybridPolicy,'retainedComponents'|'tradingTicks'|'stabilityRelative'|'lossAbsolute'|'lossRelative'>;capacities:{components:number;scopeMaxPaths:number}[]};
type Work={unitPricerCalls:number;scenarioCount:number;uniqueValuations:number;uniqueMarketContracts:number;quantityApplications:number;curveGraphEvaluatedNodes:number;rowEvaluations:number;volumeComputations:number};
export type PcaHybridView={policy:PcaHybridPolicy;available:boolean;fallbackClassifications:{valuationKey:string;exposureIds:string[];method:string;reasons:string[]}[];unavailableReasons:{code:string;message:string}[];
 pca:null|{policy:PcaHybridPolicy;columns:string[];scales:string[];eigenvalues:string[];varianceShares:string[];loadings:string[][];residualCovariance:string[][];retainedResidualCovariance:string[][];matrixNorm:string;sweeps:number;roundoffNegativeEigenvalues:number;reconstructionError:string;orthogonalityError:string;eigenResidual:string;retainedVarianceShare:string;samples:{id:string;scores:string[];residualNorm:string;reconstructionError:string}[]};
 fit:null|{nativeSteps:string[];pcDirections:string[][];projection:string[][];directionError:string;projectionError:string;scenarioCount:number};
 finiteDifferenceValues:{id:string;values:{exposureId:string;value:string}[]}[];
 comparison:null|{models:{exposureId:string;delta:string[];gamma:string[][];stable:boolean}[];exposures:{exposureId:string;fullValueAtRisk:string;fullExpectedShortfall:string;candidateValueAtRisk:string;candidateExpectedShortfall:string;maximumAbsoluteError:string;maximumTailError:string;maximumResidualLinearPnl:string;lossesPass:boolean}[];classifications:{valuationKey:string;exposureIds:string[];method:string;reasons:string[]}[];
  partitions:{measure:string;currency:unknown;fullValueAtRisk:string;fullExpectedShortfall:string;candidateValueAtRisk:string;candidateExpectedShortfall:string;hybridValueAtRisk:string;hybridExpectedShortfall:string;maximumCandidateError:string;maximumCandidateTailError:string;portfolioFallback:boolean}[];
  candidate:MonteCarloView['result']['partitions'];hybrid:MonteCarloView['result']['partitions']};
 cost:{mode:string;reference:Work;additional:Work|null;attemptUnmetered:boolean}};
export const pcaHybridProfile={convention:'correlation_pca_hybrid_qualification_v1',numericalPolicy:'cyclic_jacobi_binary64_residual_checks_v1',scalingPolicy:'sample_sd_constant_unit_v1',residualPolicy:'complete_native_shocks_all_factor_delta_v1',valuationPolicy:'full_reference_qualification_v1',directionPolicy:'pc_0_01_native_17_places_collinear_v1'};
const numeric=(v:unknown):v is string=>typeof v==='string'&&exactNumber(v)!==null;
const nonnegative=(v:unknown)=>numeric(v)&&!v.startsWith('-');
// Receipt validation only: compare declared numerical residuals with their
// versioned bounds without calculating any risk or plotting coordinates.
function fraction(value:unknown):{n:bigint;d:bigint}|null{
 if(typeof value!=='string'||value.length>2500)return null;
 const x=exactNumber(value);if(!x||x.sign==='-')return null;
 const digits=x.integer+(x.fraction??'');if(digits.length>1234||(x.denominator?.length??0)>1234)return null;
 return {n:BigInt(digits),d:x.denominator?BigInt(x.denominator):10n**BigInt(x.fraction?.length??0)};
}
const positive=(value:unknown)=>{const x=fraction(value);return !!x&&x.n>0;};
const bounded=(value:unknown,n:bigint,d=1n)=>{const x=fraction(value);return !!x&&x.n*d<=n*x.d;};
const scaledNormBound=(value:unknown,norm:unknown)=>{const m=fraction(norm);return !!m&&bounded(value,m.n>m.d?m.n:m.d,m.d*1000000000000n);};
const policyNumber=(v:unknown)=>typeof v==='string'&&v.length<=45&&/^-?(0|[1-9][0-9]{0,23})(\.[0-9]{1,18})?$/.test(v)&&(!v.startsWith('-')||!/[1-9]/.test(v));
const canonical=(v:unknown)=>JSON.stringify(v,(_k,x)=>x&&typeof x==='object'&&!Array.isArray(x)?Object.fromEntries(Object.entries(x).sort(([a],[b])=>a.localeCompare(b))):x);
const same=(a:unknown,b:unknown)=>canonical(a)===canonical(b);
const covers=(actual:string[],expected:string[])=>actual.length===expected.length&&new Set(actual).size===actual.length&&(()=>{const accepted=new Set(expected);return actual.every(id=>accepted.has(id));})();
const vector=(v:unknown,n:number):v is string[]=>Array.isArray(v)&&v.length===n&&v.every(numeric);
const matrix=(v:unknown,rows:number,columns:number):v is string[][]=>Array.isArray(v)&&v.length===rows&&v.every(row=>vector(row,columns));
export function validatePcaHybrid(report:MonteCarloContainer,v:MonteCarloView){
 const a=v.analysis,p=v.request.analysis,d=v.factors.length,ids=v.generation.samples.map(s=>s.id),exposures=v.exposures.map(e=>e.id);
 const bad=()=>Error('The PCA / hybrid report has incomplete directions, qualification or reference evidence. Reread the complete report.');
 if(!p){if(a!=null)throw bad();return;}
 if(Object.entries(pcaHybridProfile).some(([k,value])=>p[k as keyof PcaHybridPolicy]!==value)||!Number.isInteger(p.retainedComponents)||p.retainedComponents<1||p.retainedComponents>Math.min(8,d)||
  !Number.isInteger(p.tradingTicks)||p.tradingTicks<1||p.tradingTicks>1000||![p.stabilityRelative,p.lossAbsolute,p.lossRelative].every(policyNumber))throw bad();
 if(!v.result.available){if(a!=null)throw bad();return;}
 if(!a||!same(a.policy,p)||typeof a.available!=='boolean'||!Array.isArray(a.unavailableReasons)||!a.cost||a.cost.mode!==p.valuationPolicy||typeof a.cost.attemptUnmetered!=='boolean')throw bad();
 for(const work of [a.cost.reference,a.cost.additional].filter(x=>x!==null))if(!work||Object.values(work).some(n=>typeof n==='number'&&(!Number.isSafeInteger(n)||n<0))||!Number.isSafeInteger(work.unitPricerCalls)||!Number.isSafeInteger(work.scenarioCount))throw bad();
 const c=a.pca,k=p.retainedComponents;
 if(c&&(!same(c.policy,p)||!same(c.columns,v.generation.calibration.columns)||!vector(c.scales,d)||!c.scales.every(positive)||!bounded(c.matrixNorm,2n*BigInt(d))||!vector(c.eigenvalues,d)||!vector(c.varianceShares,d)||!c.varianceShares.every(x=>bounded(x,1000000000000n+BigInt(d),1000000000000n))||!matrix(c.loadings,d,d)||!matrix(c.residualCovariance,d,d)||!matrix(c.retainedResidualCovariance,d,d)||
  !Number.isInteger(c.sweeps)||c.sweeps<0||c.sweeps>=100||!Number.isInteger(c.roundoffNegativeEigenvalues)||c.roundoffNegativeEigenvalues<0||c.roundoffNegativeEigenvalues>d||
  !scaledNormBound(c.reconstructionError,c.matrixNorm)||!scaledNormBound(c.eigenResidual,c.matrixNorm)||!bounded(c.orthogonalityError,BigInt(d),1000000000000n)||!bounded(c.retainedVarianceShare,1000000000000n+BigInt(d),1000000000000n)||!Array.isArray(c.samples)||!covers(c.samples.map(s=>s.id),ids)||c.samples.some(s=>!vector(s.scores,k)||!nonnegative(s.residualNorm)||!bounded(s.reconstructionError,BigInt(d),1000000000000n))))throw bad();
 if(!Array.isArray(a.fallbackClassifications)||a.available&&a.fallbackClassifications.length)throw bad();
 if(!a.available){if(!covers(a.fallbackClassifications.flatMap(c=>c.exposureIds),exposures)||a.fallbackClassifications.some(c=>c.method!=='full_pv'||!c.valuationKey||!c.reasons.length)||a.comparison!==null||a.fit!==null||!a.unavailableReasons.length||a.finiteDifferenceValues.length||a.unavailableReasons.some(r=>typeof r.code!=='string'||!r.message))throw bad();return;}
 const fit=a.fit,q=a.comparison;
 if(!c||!fit||!q||a.unavailableReasons.length||a.cost.attemptUnmetered||!a.cost.additional||!vector(fit.nativeSteps,d)||!matrix(fit.pcDirections,k,d)||!matrix(fit.projection,k,d)||![fit.directionError,fit.projectionError].every(x=>bounded(x,1n,10000000000n))||fit.scenarioCount!==6*d+6*k*k)throw bad();
 const expected:string[]=[];
 for(const kind of ['native','pc'])for(let axis=0;axis<(kind==='native'?d:k);axis++)for(let step=0;step<3;step++)for(let sign=0;sign<2;sign++)expected.push(`${kind}/${axis}/${step}/${sign}`);
 for(let first=0;first<k;first++)for(let second=first+1;second<k;second++)for(let step=0;step<3;step++)for(let corner=0;corner<4;corner++)expected.push(`cross/${first}/${second}/${step}/${corner}`);
 if(!Array.isArray(a.finiteDifferenceValues)||!covers(a.finiteDifferenceValues.map(s=>s.id),expected)||a.finiteDifferenceValues.some(s=>!Array.isArray(s.values)||!covers(s.values.map(x=>x.exposureId),exposures)||s.values.some(x=>!numeric(x.value)))||
  !Array.isArray(q.models)||!covers(q.models.map(x=>x.exposureId),exposures)||q.models.some(m=>!vector(m.delta,d)||!matrix(m.gamma,k,k)||typeof m.stable!=='boolean')||
  !Array.isArray(q.exposures)||!covers(q.exposures.map(x=>x.exposureId),exposures)||q.exposures.some(e=>![e.fullValueAtRisk,e.fullExpectedShortfall,e.candidateValueAtRisk,e.candidateExpectedShortfall].every(numeric)||![e.maximumAbsoluteError,e.maximumTailError,e.maximumResidualLinearPnl].every(nonnegative)||typeof e.lossesPass!=='boolean')||
  !Array.isArray(q.classifications)||!covers(q.classifications.flatMap(c=>c.exposureIds),exposures)||new Set(q.classifications.map(c=>c.valuationKey)).size!==q.classifications.length||q.classifications.some(c=>!c.valuationKey||!['pca_delta_gamma','full_pv'].includes(c.method)||!Array.isArray(c.reasons)||!c.exposureIds.length||
   c.method==='pca_delta_gamma'&&(c.reasons.length||c.exposureIds.some(id=>!q.models.find(m=>m.exposureId===id)!.stable||!q.exposures.find(e=>e.exposureId===id)!.lossesPass))))throw bad();
 const partKey=(p:{measure:string;currency:unknown})=>canonical([p.measure,p.currency]);
 if(!Array.isArray(q.partitions)||!covers(q.partitions.map(partKey),v.result.partitions.map(partKey)))throw bad();
 for(const partitions of [q.candidate,q.hybrid])validateSampleCoverage(report,{...v,result:{...v.result,partitions}},ids);
 for(const part of q.partitions){
  const full=v.result.partitions.find(x=>partKey(x)===partKey(part))!,candidate=q.candidate.find(x=>partKey(x)===partKey(part))!,hybrid=q.hybrid.find(x=>partKey(x)===partKey(part))!;
  if(part.fullValueAtRisk!==full.valueAtRisk||part.fullExpectedShortfall!==full.expectedShortfall||part.candidateValueAtRisk!==candidate.valueAtRisk||part.candidateExpectedShortfall!==candidate.expectedShortfall||part.hybridValueAtRisk!==hybrid.valueAtRisk||part.hybridExpectedShortfall!==hybrid.expectedShortfall||
   ![part.maximumCandidateError,part.maximumCandidateTailError].every(nonnegative)||typeof part.portfolioFallback!=='boolean'||part.portfolioFallback&&(q.classifications.some(c=>c.method!=='full_pv')||!same(full,hybrid)))throw bad();
 }
}
export function pcaCharts(v:MonteCarloView,partition:number,start=0):ChartSpec[]{
 const a=v.analysis,c=a?.pca;if(!c)return [];const labels=v.factors.slice(start,start+32),k=a.policy.retainedComponents;
 const charts:ChartSpec[]=[
  {title:'PCA explained market variance',categoryTitle:'Principal component',subtitle:'Standardized factors. Explained market variance does not measure portfolio risk coverage; all residual shocks remain.',axis:'category',kind:'bar',unit:'Share',series:[{name:'Variance share',points:c.eigenvalues.map((value,i)=>({x:String(i+1),label:`PC ${i+1}`,y:c.varianceShares[i],text:`Eigenvalue ${amountText(value,'factor')}${i<k?' · retained curvature':''}`}))}]},
  {title:'PCA factor loadings',categoryTitle:'Market factor',subtitle:'Sample-standard-deviation coordinates; repeated eigenvalues identify a subspace, not unique economic moves.',axis:'category',kind:'heatmap',unit:'Standardized loading',series:Array.from({length:k},(_,i)=>({name:`PC ${i+1}`,points:labels.map((factor,n)=>({x:factor.id,label:factor.factor.label,y:c.loadings[start+n][i]}))}))},
  {title:'Ideal PCA residual move by simulation path',subtitle:'Diagnostic residual norm after the ideal loading projection. Fitting uses its separately retained quantized directions. No residual move is removed from full repricing or native delta.',axis:'category',kind:'line',unit:'Standardized norm',categoryTitle:'Simulation path',series:[{name:'Residual norm',points:c.samples.map((s,i)=>({x:s.id,label:`Path ${i+1}`,y:s.residualNorm}))}]}
 ];
 const p=v.result.partitions[partition],q=a.comparison;
 if(q&&p){const samePart=(x:{measure:string;currency:unknown})=>same([x.measure,x.currency],[p.measure,p.currency]);
  const candidate=q.candidate.find(samePart)!,hybrid=q.hybrid.find(samePart)!,unit=v.partitions.find(x=>same([x.measure,x.currencyReference],[p.measure,p.currency]))!.currency;
  charts.push({title:'Same-path full PV / candidate / qualified hybrid',subtitle:'Every path has a full-PV reference. Failed valuations use full PV for the entire sample set.',axis:'category',kind:'line',unit,categoryTitle:'Simulation path',series:[{name:'Full PV',points:p.samples.map((s,i)=>({x:s.id,label:`Path ${i+1}`,y:s.loss}))},{name:'Candidate approximation',points:candidate.samples.map((s,i)=>({x:s.id,label:`Path ${i+1}`,y:s.loss}))},{name:'Qualified hybrid',points:hybrid.samples.map((s,i)=>({x:s.id,label:`Path ${i+1}`,y:s.loss}))}]});
 }
 return charts.map(c=>({...c,formatValueAxis:(x:number)=>x.toLocaleString('en-US',{maximumSignificantDigits:6})}));
}
