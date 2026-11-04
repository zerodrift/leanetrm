import {validatePcaHybrid,type PcaHybridPolicy,type PcaHybridCapabilities,type PcaHybridView} from './pca-hybrid-risk.js';
import {validateHistoryPrices,type HistoryManifest,type HistoryRequest} from './factor-history.js';
import {validateSampleCoverage,type SampleRiskView} from './historical-risk.js';
import type {ParametricView} from './parametric-risk.js';
import type {RiskFactor,ExcludedPosition} from './risk-report.js';
import type {ChartSpec} from './chart-view.js';
import {sameTradeSelection} from './risk-scope.js';
import {exactNumber} from './exact-number.js';
import {amountText} from './amount-display.js';

export type MonteCarloPolicy={convention:string;numericalPolicy:string;estimator:string;centering:string;drift:string;horizon:string;quantilePolicy:string;tailPolicy:string;valuationPolicy:string;confidence:string;distribution:string;randomGenerator:string;shockPolicy:string;seed:string;paths:number;samplingUncertainty:string};
export type MonteCarloRequest={history:HistoryRequest;policy:MonteCarloPolicy;factors?:unknown[];analysis?:PcaHybridPolicy};
export type MonteCarloCapabilities={profile:Omit<MonteCarloPolicy,'confidence'|'drift'|'paths'|'seed'>;defaults:{confidence:string;paths:number;seed:string;drift:string};drifts:{zero:string;sample:string};limits:{maxFactors:number;maxPaths:number;scopeMaxPaths?:number;maxShockValues:number};analysis?:PcaHybridCapabilities};
type Sample={id:string;index:number;changes:string[]};
type Diagnostic={measure:string;currency:unknown;convergence:{paths:number;valueAtRisk:string;expectedShortfall:string;tailSampleMass:string}[];valueAtRiskInterval:{confidence:string;lowerRank:number;upperRank:number;lower:string|null;upper:string|null}};
export type MonteCarloView=Omit<SampleRiskView,'result'>&{request:MonteCarloRequest;history:HistoryManifest;
 generation:{historyId:string;policy:MonteCarloPolicy;calibration:ParametricView['result']['calibration'];lower:string[][];diagonal:string[];samples:Sample[];available:boolean;unavailableReasons:string[]};
 result:SampleRiskView['result']&{policy:MonteCarloPolicy;diagnostics:Diagnostic[]};analysis?:PcaHybridView|null};
export type MonteCarloContainer={monteCarlo:MonteCarloView;context:{contextId:string};snapshotId:string;tradeIds?:string[]|null;excludedPositions?:ExcludedPosition[];inputs?:{positions:{bookId:string;tradeId:string;bindingId:string;measure:string}[]};historical?:unknown;parametric?:unknown;joint?:unknown;factors?:unknown};
const canonical=(v:unknown)=>JSON.stringify(v,(_k,x)=>x&&typeof x==='object'&&!Array.isArray(x)?Object.fromEntries(Object.entries(x).sort(([a],[b])=>a.localeCompare(b))):x);
const numeric=(v:unknown):v is string=>typeof v==='string'&&exactNumber(v)!==null;
const distinct=(v:unknown[])=>new Set(v).size===v.length;
const key=(measure:string,currency:unknown)=>canonical([measure,currency]);
export function validateMonteCarlo(report:MonteCarloContainer,expected?:MonteCarloRequest):MonteCarloView{
 const v=report?.monteCarlo,p=v?.request?.policy,g=v?.generation,h=v?.history,r=v?.result,q=v?.request;
 const bad=()=>Error('The Monte Carlo report has inconsistent captured inputs or incomplete simulation evidence. Reread the complete report.');
 if(!v||!p||!g||!h||!r||!q||report.historical!==undefined||report.parametric!==undefined||report.joint!==undefined||report.factors!==undefined||
  !/^[a-f0-9]{64}$/.test(v.studyId)||h.id!==q.history?.expectedManifestId||r.historyId!==h.id||g.historyId!==h.id||g.calibration?.historyId!==h.id||
  h.forecast?.contextId!==report.context?.contextId||q.history.scope.context.contextId!==report.context.contextId||q.history.scope.context.snapshotId!==report.snapshotId||
  !sameTradeSelection(report.tradeIds,q.history.scope.tradeIds)||canonical(p)!==canonical(g.policy)||canonical(p)!==canonical(r.policy)||
  expected&&canonical({...q,factors:undefined})!==canonical({...expected,factors:undefined}))throw bad();
 const profile={convention:'dated_factor_full_pv_monte_carlo_v1',numericalPolicy:'exact_ldl_binary64_polar_v1',estimator:'equal_weight_sample_n_minus_one_v1',centering:'sample_mean_centered_v1',horizon:'frozen_portfolio_one_adjacent_session_v1',quantilePolicy:'empirical_inverse_cdf_v1',tailPolicy:'signed_upper_loss_fractional_mass_uniform_ties_v1',valuationPolicy:'current_instrument_pv_frozen_nonfactor_inputs_v1',distribution:'joint_additive_gaussian_v1',randomGenerator:'sha256_counter_polar_log_series_v1',shockPolicy:'binary64_exact_to_native_18_places_to_even_v1',samplingUncertainty:'binomial_order_statistic_binary64_95_v1'};
 if(Object.entries(profile).some(([k,value])=>p[k as keyof MonteCarloPolicy]!==value)||!numeric(p.confidence)||!['zero_drift_v1','sample_mean_drift_v1'].includes(p.drift)||
  typeof p.seed!=='string'||!/^(0|[1-9][0-9]{0,19})$/.test(p.seed)||BigInt(p.seed)>18446744073709551615n||!Number.isInteger(p.paths)||p.paths<2||p.paths>16384)throw bad();
 validateHistoryPrices(h,q.history.policy);
 const ids=h.columns?.map(c=>c.id),c=g.calibration,d=ids?.length;
 if(!Array.isArray(ids)||!d||!distinct(ids)||!Array.isArray(h.samples)||!distinct(h.samples.map(s=>s.id))||h.samples.some(s=>!Array.isArray(s.changes)||s.changes.length!==d||s.changes.some(x=>!numeric(x)))||
  !Array.isArray(v.factors)||v.factors.length!==d||v.factors.some((f,i)=>f.id!==ids[i]||!f.factor?.unit)||
  canonical(c.columns)!==canonical(ids)||c.samples!==h.samples.length||!Number.isInteger(c.rank)||c.rank<0||c.rank>Math.min(d,Math.max(0,c.samples-1))||!Array.isArray(g.samples)||!Array.isArray(r.diagnostics)||!Array.isArray(r.unavailableReasons)||
  !Array.isArray(v.books)||!distinct(v.books.map(b=>b.id))||!Array.isArray(v.exposures)||!distinct(v.exposures.map(e=>e.id))||!v.matrix||!Array.isArray(v.matrix.rows)||!Array.isArray(v.matrix.columns)||!Array.isArray(r.partitions)||!Array.isArray(v.partitions))throw bad();
 if(c.samples>=2&&(c.means.length!==d||c.means.some(x=>!numeric(x))||c.covariance.length!==d||c.correlation.length!==d||
  c.covariance.some(row=>row.length!==d||row.some(x=>!numeric(x)))||c.correlation.some(row=>row.length!==d||row.some(x=>x!==null&&!numeric(x)))))throw bad();
 if(g.available){
  if(!h.available||g.samples.length!==p.paths||g.samples.some((s,i)=>s.index!==i||s.id!=='mc_'+String(i).padStart(5,'0')||s.changes.length!==d||s.changes.some(x=>!numeric(x)))||
   g.lower.length!==d||g.lower.some(row=>row.length!==d||row.some(x=>!numeric(x)))||g.diagonal.length!==d||g.diagonal.some(x=>!numeric(x)))throw bad();
 }else if(g.samples.length||g.lower.length||g.diagonal.length||r.available)throw bad();
 try{validateSampleCoverage(report,v,g.samples.map(s=>s.id));}catch{throw bad();}
 if(!r.available){if(r.samples!==0||r.diagnostics.length)throw bad();validatePcaHybrid(report,v);return v;}
 if(!g.available||r.samples!==p.paths||r.diagnostics.length!==r.partitions.length||!distinct(r.diagnostics.map(p=>key(p.measure,p.currency))))throw bad();
 for(const p of r.partitions){
  const diagnostic=r.diagnostics.find(x=>key(x.measure,x.currency)===key(p.measure,p.currency)),b=diagnostic?.valueAtRiskInterval,points=diagnostic?.convergence;
  if(!b||b.confidence!=='0.95'||!Number.isInteger(b.lowerRank)||!Number.isInteger(b.upperRank)||b.lowerRank<0||b.upperRank>r.samples+1||b.lowerRank>=b.upperRank||
   (b.lowerRank===0?b.lower!==null:!numeric(b.lower))||(b.upperRank===r.samples+1?b.upper!==null:!numeric(b.upper))||
   !points?.length||points.some((x,i)=>!Number.isInteger(x.paths)||x.paths<2||x.paths>r.samples||i>0&&x.paths<=points[i-1].paths||![x.valueAtRisk,x.expectedShortfall,x.tailSampleMass].every(numeric))||
   points.at(-1)!.paths!==r.samples||points.at(-1)!.valueAtRisk!==p.valueAtRisk||points.at(-1)!.expectedShortfall!==p.expectedShortfall)throw bad();
 }
 validatePcaHybrid(report,v);return v;
}
export function monteCarloCharts(v:MonteCarloView,index:number):ChartSpec[]{
 const p=v.result.partitions[index];if(!v.result.available||!p)return [];
 const unit=v.partitions.find(x=>key(x.measure,x.currencyReference)===key(p.measure,p.currency))!.currency;
 const losses=new Map(p.samples.map(s=>[s.id,s])),diagnostic=v.result.diagnostics.find(x=>key(x.measure,x.currency)===key(p.measure,p.currency))!;
 const charts:ChartSpec[]=[
  {title:'Simulated loss distribution',subtitle:'Equal probability per generated full-PV scenario. Repeated outcomes retain their mass.',axis:'category',kind:'bar',unit:'Probability',categoryTitle:`Loss interval · ${unit}`,series:[{name:'Simulation probability',points:p.histogram.map((b,i)=>({x:String(i),label:`${amountText(b.lower,'money')} to ${amountText(b.upper,'money')}`,y:b.probability,text:`${b.count} paths`}))}]},
  {title:'Simulated losses · VaR and ES',subtitle:'Each path is an independent sibling under the same frozen current portfolio.',axis:'category',kind:'line',unit,categoryTitle:'Simulation path',reference:{value:p.valueAtRisk,label:'Monte Carlo VaR'},series:[{name:'Full-PV loss',points:v.generation.samples.map(s=>({x:s.id,label:`Path ${s.index+1}`,y:losses.get(s.id)!.loss}))},{name:'Expected shortfall',points:v.generation.samples.map(s=>({x:s.id,label:`Path ${s.index+1}`,y:p.expectedShortfall}))}]},
  {title:'Convergence by simulation count',subtitle:'Prefixes of the same seeded sample set. Movement is a diagnostic, not an error guarantee.',axis:'category',kind:'line',unit,categoryTitle:'Simulation paths',series:[{name:'VaR',points:diagnostic.convergence.map(x=>({x:String(x.paths),y:x.valueAtRisk}))},{name:'ES',points:diagnostic.convergence.map(x=>({x:String(x.paths),y:x.expectedShortfall}))}]},
  {title:'Book VaR / ES allocations',subtitle:'Portfolio quantile/tail weights applied to book losses; signed allocations may be negative.',axis:'category',kind:'bar',unit,positiveTone:'loss',colorBySign:true,series:([{name:'VaR',metric:'valueAtRisk'},{name:'ES',metric:'expectedShortfall'}] as const).map(m=>({name:m.name,points:p.books.map(b=>({x:b.id,label:v.books.find(x=>x.id===b.id)!.name,y:b[m.metric]}))}))}
 ];
 return charts.map(spec=>({...spec,formatValueAxis:(coordinate:number)=>coordinate.toLocaleString('en-US',{maximumSignificantDigits:6})}));
}
