import {validateHistoryPrices,type HistoryRequest,type HistoryManifest} from './factor-history.js';
import type {RiskFactor,ExcludedPosition} from './risk-report.js';
import {validateParametric,type ParametricRequest,type ParametricCapabilities,type ParametricView} from './parametric-risk.js';
import type {ChartSpec} from './chart-view.js';
import {exactNumber} from './exact-number.js';
import {amountText} from './amount-display.js';
import {sameTradeSelection} from './risk-scope.js';

export type HistoricalPolicy={convention:string;numericalPolicy:string;horizon:string;samplePolicy:string;quantilePolicy:string;tailPolicy:string;valuationPolicy:string;confidence:string};
export type HistoricalRequest={history:HistoryRequest;policy:HistoricalPolicy;comparison?:Omit<ParametricRequest,'history'>;factors?:unknown[]};
export type HistoricalCapabilities={profile:Omit<HistoricalPolicy,'confidence'>;defaults:{confidence:string;compareParametric:boolean;comparisonDrift:string};limits:{maxFactors:number;maxSamples:number;maxExposures:number;maxResultValues:number}};
export type StatisticalRequest={parametric:ParametricRequest;historical?:never}|{historical:HistoricalRequest;parametric?:never};
export type StatisticalCapabilities={parametric:ParametricCapabilities;historical:HistoricalCapabilities};
type Contribution={id:string;baseValue:string;valueAtRisk:string;expectedShortfall:string};
type Exposure={id:string;bookId:string;tradeId:string;bindingId:string;measure:string;currency:unknown;baseValue:string;tradeReference:string;instrumentReference:string;instrumentType:string};
type Partition={measure:string;currency:unknown;baseValue:string;mean:string;valueAtRisk:string;expectedShortfall:string;tailSampleMass:string;quantileTies:number;
 books:Contribution[];trades:Contribution[];samples:{id:string;value:string;loss:string;varWeight:string;esWeight:string}[];histogram:{lower:string;upper:string;count:number;probability:string}[]};
export type HistoricalView={studyId:string;request:HistoricalRequest;history:HistoryManifest;factors:{id:string;factor:RiskFactor}[];books:{id:string;name:string}[];exposures:Exposure[];
 result:{policy:HistoricalPolicy;historyId:string;samples:number;available:boolean;unavailableReasons:string[];partitions:Partition[]};
 partitions:{measure:string;measureLabel:string;currencyReference:unknown;currency:string}[];
 matrix:{columns:string[];rows:{id:string;scenarioValues:string[];losses:string[]}[]};
 comparison:{studyId:string;report:ParametricView|null;unavailableReason?:{code:string;message:string};request?:Omit<ParametricRequest,'history'>}|null};
export type HistoricalContainer={historical:HistoricalView;context:{contextId:string};snapshotId:string;tradeIds?:string[]|null;excludedPositions?:ExcludedPosition[];inputs?:{positions:{bookId:string;tradeId:string;bindingId:string;measure:string}[]};parametric?:unknown;joint?:unknown;factors?:unknown};
const canonical=(v:unknown)=>JSON.stringify(v,(_k,x)=>x&&typeof x==='object'&&!Array.isArray(x)?Object.fromEntries(Object.entries(x).sort(([a],[b])=>a.localeCompare(b))):x);
const number=(v:unknown):v is string=>typeof v==='string'&&exactNumber(v)!==null;
const distinct=(v:unknown[])=>new Set(v).size===v.length;
const same=(a:unknown[],b:unknown[])=>a.length===b.length&&a.every(v=>b.includes(v));
const pair=(measure:string,currency:unknown)=>canonical([measure,currency]);
/** Receipt identity and complete native coverage only; all loss/tail/weight calculations belong to the backend. */
export function validateHistorical(report:HistoricalContainer,expected?:HistoricalRequest):HistoricalView{
 const bad=()=>Error('The historical report has inconsistent captured inputs or incomplete native coverage. Reread the complete report.');
 const v=report?.historical,r=v?.result,h=v?.history,q=v?.request;
 if(!v||!r||!h||!q||report.parametric!==undefined||report.joint!==undefined||report.factors!==undefined||!Array.isArray(report.excludedPositions)||
  !/^[0-9a-f]{64}$/.test(v.studyId)||!/^[0-9a-f]{64}$/.test(h.id)||r.historyId!==h.id||h.id!==q.history?.expectedManifestId||
  q.policy?.convention!=='dated_factor_full_pv_historical_v1'||q.policy.numericalPolicy!=='exact_empirical_probability_mass_v1'||q.policy.horizon!=='frozen_portfolio_one_adjacent_session_v1'||
  q.policy.samplePolicy!=='equal_weight_raw_joint_changes_v1'||q.policy.quantilePolicy!=='empirical_inverse_cdf_v1'||q.policy.tailPolicy!=='signed_upper_loss_fractional_mass_uniform_ties_v1'||q.policy.valuationPolicy!=='current_instrument_pv_frozen_nonfactor_inputs_v1'||
  canonical(r.policy)!==canonical(q.policy)||!number(q.policy.confidence)||h.forecast?.contextId!==report.context?.contextId||q.history.scope.context.contextId!==report.context.contextId||q.history.scope.context.snapshotId!==report.snapshotId||
  !sameTradeSelection(report.tradeIds,q.history.scope.tradeIds)||expected&&canonical({...q,factors:undefined})!==canonical({...expected,factors:undefined}))throw bad();
 validateHistoryPrices(h,q.history.policy);
 if(!Array.isArray(h.columns)||!h.columns.length||!distinct(h.columns.map(c=>c.id))||!Array.isArray(h.samples)||!distinct(h.samples.map(s=>s.id))||r.samples!==h.samples.length||
  h.samples.some(s=>s.changes.length!==h.columns.length||s.changes.some(x=>!number(x)))||!Array.isArray(v.factors)||v.factors.length!==h.columns.length||v.factors.some((f,i)=>f.id!==h.columns[i].id||!f.factor?.unit)||
  !Array.isArray(v.books)||!distinct(v.books.map(b=>b.id))||!Array.isArray(v.exposures)||!distinct(v.exposures.map(e=>e.id))||!v.matrix||!Array.isArray(v.matrix.columns)||!Array.isArray(v.matrix.rows)||
  !Array.isArray(r.partitions)||!Array.isArray(v.partitions)||!Array.isArray(r.unavailableReasons))throw bad();
 if(!r.available){if(r.partitions.length||v.partitions.length||v.exposures.length||v.matrix.rows.length||v.matrix.columns.length||!r.unavailableReasons.length||v.comparison!==null)throw bad();return v;}
 if(!h.available||report.excludedPositions.length||r.unavailableReasons.length||!v.exposures.length||!r.partitions.length||!report.inputs||!Array.isArray(report.inputs.positions))throw bad();
 const native=(e:{bookId:string;tradeId:string;bindingId:string;measure:string})=>canonical([e.bookId,e.tradeId,e.bindingId,e.measure]);
 if(!distinct(v.exposures.map(native))||!same(v.exposures.map(native),report.inputs.positions.map(native))||v.exposures.some(e=>!number(e.baseValue)||!v.books.some(b=>b.id===e.bookId))||
  !same(v.matrix.columns,v.exposures.map(e=>e.id))||!distinct(v.matrix.columns)||!same(v.matrix.rows.map(row=>row.id),h.samples.map(s=>s.id))||!distinct(v.matrix.rows.map(row=>row.id))||
  v.matrix.rows.some(row=>row.scenarioValues.length!==v.exposures.length||row.losses.length!==v.exposures.length||[...row.scenarioValues,...row.losses].some(x=>!number(x))))throw bad();
 const nativeGroups=[...new Set(v.exposures.map(e=>pair(e.measure,e.currency)))];
 if(!same(r.partitions.map(p=>pair(p.measure,p.currency)),nativeGroups)||!distinct(r.partitions.map(p=>pair(p.measure,p.currency)))||!same(v.partitions.map(p=>pair(p.measure,p.currencyReference)),nativeGroups)||!distinct(v.partitions.map(p=>pair(p.measure,p.currencyReference))))throw bad();
 for(const p of r.partitions){
  const es=v.exposures.filter(e=>pair(e.measure,e.currency)===pair(p.measure,p.currency));
  if(![p.baseValue,p.mean,p.valueAtRisk,p.expectedShortfall,p.tailSampleMass].every(number)||!Number.isInteger(p.quantileTies)||p.quantileTies<1||p.quantileTies>r.samples||
   !Array.isArray(p.samples)||!same(p.samples.map(s=>s.id),h.samples.map(s=>s.id))||!distinct(p.samples.map(s=>s.id))||p.samples.some(s=>![s.value,s.loss,s.varWeight,s.esWeight].every(number))||
   !Array.isArray(p.histogram)||!p.histogram.length||p.histogram.some(b=>![b.lower,b.upper,b.probability].every(number)||!Number.isInteger(b.count)||b.count<0)||
   !Array.isArray(p.books)||!Array.isArray(p.trades)||!same(p.books.map(b=>b.id),[...new Set(es.map(e=>e.bookId))])||!same(p.trades.map(t=>t.id),[...new Set(es.map(e=>e.tradeId))])||
   !distinct(p.books.map(b=>b.id))||!distinct(p.trades.map(t=>t.id))||[...p.books,...p.trades].some(c=>![c.baseValue,c.valueAtRisk,c.expectedShortfall].every(number)))throw bad();
 }
 if(q.comparison){
  const c=v.comparison;if(!c||c.studyId!==v.studyId)throw bad();
  if(c.report){const normal=validateParametric({...report,historical:undefined,parametric:c.report} as Parameters<typeof validateParametric>[0],{history:q.history,...q.comparison});
   const groups=[...new Set(v.exposures.map(e=>canonical([e.bookId,e.measure,e.currency])))];
   if(!same(groups,normal.exposures.map(e=>canonical([e.bookId,e.measure,e.currency])))||normal.history.id!==h.id||canonical(normal.history)!==canonical(h)||normal.request.policy.confidence!==q.policy.confidence||normal.request.policy.drift!=='sample_mean_drift_v1')throw bad();
  }else if(!c.unavailableReason?.code||canonical(c.request)!==canonical(q.comparison))throw bad();
 }else if(v.comparison!==null)throw bad();
 return v;
}
export function historicalSampleLabel(history:HistoryManifest,id:string):string{
 const row=history.samples.find(s=>s.id===id);if(!row)return id;
 const from=history.sessions?.find(s=>s.close===row.from?.at)?.date,to=history.sessions?.find(s=>s.close===row.to?.at)?.date;
 return from&&to?`${from} → ${to}`:id;
}
export function historicalCharts(v:HistoricalView,index:number,metric:'valueAtRisk'|'expectedShortfall'='valueAtRisk'):ChartSpec[]{
 const p=v.result.partitions[index];if(!v.result.available||!p)return [];
 const label=v.partitions.find(l=>pair(l.measure,l.currencyReference)===pair(p.measure,p.currency))!,unit=label.currency;
 const date=(id:string)=>historicalSampleLabel(v.history,id);
 return [
  {title:'Empirical loss distribution',subtitle:'Equal probability per complete historical row. Repeated outcomes retain their probability mass.',axis:'category',kind:'bar',unit:'Probability',categoryTitle:`Loss interval · ${unit}`,series:[{name:'Empirical probability',points:p.histogram.map((b,i)=>({x:String(i),label:`${amountText(b.lower,'money')} to ${amountText(b.upper,'money')}`,y:b.probability,text:`${b.count} samples · probability ${b.probability}`}))}]},
  {title:'Historical losses · VaR and ES',subtitle:'Every joint change reprices the same current frozen portfolio. Select a sample to inspect its factor moves and trade losses.',axis:'category',kind:'line',unit,reference:{value:p.valueAtRisk,label:'Empirical VaR'},series:[
   {name:'Full-PV loss',points:v.history.samples.map(s=>{const loss=p.samples.find(l=>l.id===s.id)!;return {x:s.id,label:date(s.id),y:loss.loss,text:amountText(loss.loss,'money')};})},
   {name:'Expected shortfall',points:v.history.samples.map(s=>({x:s.id,label:date(s.id),y:p.expectedShortfall}))}]},
  {title:`Book ${metric==='valueAtRisk'?'VaR':'ES'} allocation`,subtitle:'Portfolio quantile/tail weights applied to book losses. Allocations may be negative.',axis:'category',kind:'bar',unit,positiveTone:'loss',colorBySign:true,series:[{name:'Allocated loss',points:p.books.map(b=>({x:b.id,label:v.books.find(l=>l.id===b.id)!.name,y:b[metric],text:amountText(b[metric],'money')}))}]}
 ];
}
