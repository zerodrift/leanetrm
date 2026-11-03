import {validateHistoryPrices,type HistoryRequest,type HistoryManifest} from './factor-history.js';
import type {RiskFactor,ExcludedPosition} from './risk-report.js';
import type {ChartSpec} from './chart-view.js';
import {exactNumber} from './exact-number.js';
import {sameTradeSelection} from './risk-scope.js';

export type ParametricPolicy={convention:string;numericalPolicy:string;estimator:string;centering:string;drift:string;horizon:string;tail:string;confidence:string};
export type ParametricRequest={history:HistoryRequest;policy:ParametricPolicy;finiteDifference:{convention:string;tradingTicks:number;deltaTolerance:{absolute:string;relative:string}}};
export type ParametricCapabilities={defaults:{confidence:string;drift:string;tradingTicks:number;deltaTolerance:{absolute:string;relative:string}};profile:Omit<ParametricPolicy,'confidence'|'drift'>;drifts:{zero:string;sample:string};finiteDifference:string;limits:{maxFactors:number;maxProducts:number;maxExposures:number}};
type Contribution={id:string;mean:string;varianceCross:string;valueAtRisk:string;expectedShortfall:string};
export type ParametricPartition={measure:string;currency:unknown;mean:string;variance:string;standardDeviation:string;valueAtRisk:string;expectedShortfall:string;
 books:Contribution[];factors:Contribution[];density:{score:string;loss:string;density:string}[];linearizedLosses:{sample:string;loss:string}[];varReconciliation:string;esReconciliation:string};
export type ParametricView={request:ParametricRequest;history:HistoryManifest;result:{policy:ParametricPolicy;available:boolean;unavailableReasons:string[];
 calibration:{historyId:string;samples:number;columns:string[];means:string[];covariance:string[][];correlation:(string|null)[][];rank:number;constantColumns:string[];conditioning:string;correlationCondition:string|null};partitions:ParametricPartition[]};
 exposures:{bookId:string;measure:string;currency:unknown}[];factors:{id:string;factor:RiskFactor}[];books:{id:string;name:string}[];partitions:{measure:string;measureLabel:string;currencyReference:unknown;currency:string}[];
 finiteDifference:{convention:string;tradingTicks:number;deltaTolerance:{absolute:string;relative:string};evaluated:boolean;estimates:{columnId:string;bookId:string;measure:string;currency:unknown;half:string;nominal:string;double:string;stable:boolean;gamma:string;bump:string;unit:unknown}[]};passes:unknown[]};
export type ParametricContainer={parametric:ParametricView;context:{contextId:string};snapshotId:string;tradeIds?:string[]|null;excludedPositions?:ExcludedPosition[];joint?:unknown;factors?:unknown};
const canonical=(value:unknown)=>JSON.stringify(value,(_key,v)=>v&&typeof v==='object'&&!Array.isArray(v)?Object.fromEntries(Object.entries(v).sort(([a],[b])=>a.localeCompare(b))):v);
const number=(value:unknown):value is string=>typeof value==='string'&&exactNumber(value)!==null;
const pair=(measure:string,currency:unknown)=>canonical([measure,currency]);
/** Receipt identity and complete shape only. Covariance, delta eligibility, normal risk and contributions are backend calculations. */
export function validateParametric(report:ParametricContainer,expected?:ParametricRequest):ParametricView{
 const bad=()=>Error('The parametric report has inconsistent captured inputs or incomplete numerical coverage. Reread the complete report.');
 const view=report?.parametric,r=view?.result,c=r?.calibration,h=view?.history,request=view?.request;
 if(!view||!r||!c||!h||!request||report.joint!==undefined||report.factors!==undefined||!Array.isArray(report.excludedPositions)||
  r.policy?.convention!=='dated_factor_delta_normal_v1'||r.policy.numericalPolicy!=='exact_moments_binary64_normal_v1'||canonical(r.policy)!==canonical(request.policy)||
  !/^[0-9a-f]{64}$/.test(h.id)||h.id!==request.history?.expectedManifestId||c.historyId!==h.id||
  h.forecast?.contextId!==report.context?.contextId||request.history.scope.context.contextId!==report.context.contextId||request.history.scope.context.snapshotId!==report.snapshotId||
  !sameTradeSelection(report.tradeIds,request.history.scope.tradeIds)||expected&&canonical(request)!==canonical(expected))throw bad();
 if(!Array.isArray(c.columns)||!c.columns.length||new Set(c.columns).size!==c.columns.length||!Array.isArray(h.columns)||
  c.columns.some((id,i)=>id!==h.columns[i]?.id)||h.columns.length!==c.columns.length||!Array.isArray(h.samples)||c.samples!==h.samples.length||
  h.samples.some(s=>s.changes.length!==c.columns.length||s.changes.some(v=>!number(v)))||!Array.isArray(view.factors)||view.factors.length!==c.columns.length||
  view.factors.some((f,i)=>f.id!==c.columns[i]||!f.factor?.unit)||!Array.isArray(view.books)||new Set(view.books.map(b=>b.id)).size!==view.books.length||
  !Array.isArray(r.partitions)||!Array.isArray(view.partitions)||!Array.isArray(r.unavailableReasons)||
  c.rank<0||c.rank>c.columns.length||!Number.isInteger(c.rank))throw bad();
 validateHistoryPrices(h,request.history.policy);
 const d=c.columns.length;
 if(c.samples>=2&&(c.means.length!==d||c.means.some(v=>!number(v))||c.covariance.length!==d||c.covariance.some(row=>row.length!==d||row.some(v=>!number(v)))||
  c.correlation.length!==d||c.correlation.some(row=>row.length!==d||row.some(v=>v!==null&&!number(v)))))throw bad();
 const fd=view.finiteDifference;
 if(!fd||fd.convention!==request.finiteDifference.convention||fd.tradingTicks!==request.finiteDifference.tradingTicks||canonical(fd.deltaTolerance)!==canonical(request.finiteDifference.deltaTolerance)||!Array.isArray(fd.estimates))throw bad();
 if(!Array.isArray(view.exposures)||!view.exposures.length)throw bad();
 const exposureKeys=view.exposures.map(e=>canonical([e.bookId,e.measure,e.currency]));
 if(new Set(exposureKeys).size!==exposureKeys.length||view.exposures.some(e=>!view.books.some(b=>b.id===e.bookId)||typeof e.measure!=='string'||!e.measure||!e.currency))throw bad();
 const groups=new Map<string,Set<string>>();
 for(const estimate of fd.estimates){
  const i=c.columns.indexOf(estimate.columnId),key=canonical([estimate.bookId,estimate.measure,estimate.currency]),columns=groups.get(key)??new Set<string>();
  if(i<0||columns.has(estimate.columnId)||!view.books.some(b=>b.id===estimate.bookId)||![estimate.half,estimate.nominal,estimate.double,estimate.gamma,estimate.bump].every(number)||
   canonical(estimate.unit)!==canonical((h.columns[i].mapping as {factor:{unit:unknown}})?.factor?.unit))throw bad();
  columns.add(estimate.columnId);groups.set(key,columns);
 }
 if(r.available&&(groups.size!==exposureKeys.length||exposureKeys.some(key=>!groups.has(key)))||[...groups.values()].some(columns=>columns.size!==d)||r.available&&(!h.available||report.excludedPositions.length||r.unavailableReasons.length||
  !fd.evaluated||!fd.estimates.length||fd.estimates.some(e=>!e.stable)||!r.partitions.length)||!r.available&&(r.partitions.length||!r.unavailableReasons.length))throw bad();
 const nativeBooks=new Map<string,Set<string>>();for(const e of fd.estimates){const key=pair(e.measure,e.currency),books=nativeBooks.get(key)??new Set<string>();books.add(e.bookId);nativeBooks.set(key,books);}
 const labels=new Set(view.partitions.map(p=>pair(p.measure,p.currencyReference))),seen=new Set<string>();
 for(const p of r.partitions){
  const key=pair(p.measure,p.currency),books=nativeBooks.get(key);if(!books||p.books.length!==books.size||p.books.some(b=>!books.has(b.id))||seen.has(key)||!labels.has(key)||![p.mean,p.variance,p.standardDeviation,p.valueAtRisk,p.expectedShortfall,p.varReconciliation,p.esReconciliation].every(number)||
   !Array.isArray(p.books)||!p.books.length||new Set(p.books.map(b=>b.id)).size!==p.books.length||p.books.some(b=>!view.books.some(v=>v.id===b.id))||
   p.factors.length!==d||p.factors.some((f,i)=>f.id!==c.columns[i])||[...p.books,...p.factors].some(v=>![v.mean,v.varianceCross,v.valueAtRisk,v.expectedShortfall].every(number))||
   p.density.some(v=>![v.score,v.loss,v.density].every(number))||p.linearizedLosses.length!==c.samples||p.linearizedLosses.some((s,i)=>s.sample!==h.samples[i].id||!number(s.loss)))throw bad();
  seen.add(key);
 }
 if(r.available&&(nativeBooks.size!==seen.size||[...nativeBooks.keys()].some(key=>!seen.has(key)))||labels.size!==view.partitions.length||seen.size!==labels.size)throw bad();
 return view;
}
export function parametricCharts(view:ParametricView,partition:number,metric:'valueAtRisk'|'expectedShortfall'='valueAtRisk',rows=0,columns=0,matrix:'correlation'|'covariance'='correlation'):ChartSpec[]{
 const p=view.result.partitions[partition];if(!view.result.available||!p)return [];
 const label=view.partitions.find(v=>pair(v.measure,v.currencyReference)===pair(p.measure,p.currency))!;
 const currency=label.currency,charts:ChartSpec[]=[],risk=metric==='valueAtRisk'?'VaR':'ES';
 if(p.density.length)charts.push({title:'Gaussian loss density',subtitle:'Fitted delta-normal model. Native loss labels and density are backend estimates.',axis:'category',categoryTitle:`Modeled loss · ${currency}`,unit:`1 / ${currency}`,kind:'line',series:[{name:'Normal density',points:p.density.map(v=>({x:v.score,label:v.loss,y:v.density,text:v.density}))}]});
 charts.push({title:`Book contribution to ${risk}`,subtitle:'Euler components of the selected native portfolio partition. Hedging contributions can be negative.',axis:'category',unit:currency,kind:'bar',positiveTone:'loss',colorBySign:true,
  series:[{name:`Component ${risk}`,points:p.books.map(b=>({x:b.id,label:view.books.find(v=>v.id===b.id)!.name,y:b[metric],text:b[metric]}))}]});
 charts.push({title:`Factor contribution to ${risk}`,axis:'category',unit:currency,kind:'bar',positiveTone:'loss',colorBySign:true,
  series:[{name:`Component ${risk}`,points:p.factors.map(f=>({x:f.id,label:view.factors.find(v=>v.id===f.id)!.factor.label,y:f[metric],text:f[metric]}))}]});
 charts.push({title:'In-sample linearized losses',subtitle:'Current local deltas applied to the calibration changes. These are not full-repricing historical simulation or out-of-sample outcomes.',axis:'category',unit:currency,kind:'line',
  reference:{value:p.valueAtRisk,label:'Gaussian VaR'},series:[{name:'Linearized loss',points:p.linearizedLosses.map(s=>({x:s.sample,label:s.sample,y:s.loss,text:s.loss}))}]});
 const ids=view.result.calibration.columns,r=ids.slice(rows,rows+32),c=ids.slice(columns,columns+32);
 charts.push({title:matrix==='correlation'?'Factor correlation':'Factor covariance',subtitle:`Rows ${rows+1}–${rows+r.length}, columns ${columns+1}–${columns+c.length} of ${ids.length}. Display window only; calibration retains every factor. ${matrix==='correlation'?'A constant column has undefined correlation.':'Covariance uses row × column native units; it is not standardized.'}`,axis:'category',unit:matrix==='correlation'?'Correlation':'Row × column native units',kind:'heatmap',
  series:r.map((id,i)=>({id,name:view.factors.find(f=>f.id===id)!.factor.label,points:c.map((column,j)=>({x:column,label:view.factors.find(f=>f.id===column)!.factor.label,y:view.result.calibration[matrix][rows+i][columns+j],text:matrix==='covariance'?view.result.calibration.covariance[rows+i][columns+j]+' · '+view.factors.find(f=>f.id===id)!.factor.unit+' × '+view.factors.find(f=>f.id===column)!.factor.unit:undefined}))}))});
 return charts;
}
