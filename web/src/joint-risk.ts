import type {ChartSpec} from './chart-view.js';
import type {RiskFactor,RiskReport} from './risk-report.js';
import type {ScenarioShift} from './risk-scenarios.js';

export type RiskTolerance={absolute:string;relative:string};
export type JointRequest={convention:string;scenarios:{id:string;version:string;convention:string;scenarios:{id:string;label:string;shifts:ScenarioShift[]}[]}|null;
 derivatives:{convention:string;axes:{id:string;bump:ScenarioShift;deltaTolerance:RiskTolerance;gammaTolerance:RiskTolerance}[];mixed:{id:string;firstAxis:string;secondAxis:string;tolerance:RiskTolerance}[]}|null};
export type JointCapabilities={convention:string;scenarioConvention:string;derivativeConvention:string;bumpConvention:string;shiftConvention:string;maxScenarios:number;maxShifts:number;maxAxes:number;maxMixed:number};
type RowIdentity={bookId:string;book:string;measure:string;measureLabel:string};
type Position=RowIdentity&{tradeId:string;bindingId:string;tradeReference:string;instrumentReference:string;instrumentType:string};
export type JointValues={base:string;value:string;pnl:string;loss:string;currency:string;currencyReference:unknown;display:{base:string;value:string;pnl:string;loss:string}};
export type JointEstimate={half:string;nominal:string;double:string;stable:boolean;currency:string;currencyReference:unknown;unit:string;tolerance:RiskTolerance;display:{half:string;nominal:string;double:string}};
export type JointSample={id:string;label:string;shifts:{factor:RiskFactor;size:string}[];totals:(RowIdentity&{values:JointValues})[];positions:(Position&{values:JointValues})[]};
export type JointDerivative={id:string;formula:string;axes:string[];factorUnits:{unit:string;priceUnit:unknown}[];totals:(RowIdentity&{values:JointEstimate})[];positions:(Position&{values:JointEstimate})[]};
export type JointReport={kind:'scenarios'|'derivatives';request:JointRequest;baseTotals:(RowIdentity&{value:string;display:string;currency:string;currencyReference:unknown})[];
 axes:{id:string;factor:RiskFactor;bump:string}[];samples:JointSample[];derivatives:JointDerivative[];
 partitions:{definitions:{revisionId:string};applicableAxes:string[];work:{scenarioCount:number};samples:{id:string;unchanged:boolean;scenario:unknown}[]}[]};
export type JointMetric='loss'|'pnl'|'delta'|'gamma'|'mixedGamma';
export const jointLabels={loss:'Scenario loss',pnl:'Scenario P&L',delta:'Delta',gamma:'Gamma',mixedGamma:'Cross-gamma'};
const formulas={delta:'central_first_difference_v1',gamma:'central_diagonal_second_difference_v1',mixedGamma:'central_mixed_second_difference_v1'};
const canonical=(value:unknown):string=>JSON.stringify(value,(_key,v)=>v&&typeof v==='object'&&!Array.isArray(v)?Object.fromEntries(Object.entries(v).sort(([a],[b])=>a.localeCompare(b))):v);
const same=(a:string[],b:string[])=>{const x=[...a].sort(),y=[...b].sort();return x.length===y.length&&x.every((v,i)=>v===y[i]);};
const binding=(p:{tradeId:string;bindingId:string;bookId:string;measure:string})=>canonical([p.tradeId,p.bindingId,p.bookId,p.measure]);
const book=(p:{bookId:string;measure:string;currencyReference:unknown})=>canonical([p.bookId,p.measure,p.currencyReference]);
const invalid=()=>Error('The joint report has missing or inconsistent scenario, derivative or binding coverage. Reread its retained result.');

/** Identity/coverage checks only. Valuation, aggregation and step agreement are backend results. */
export function validateJoint(report:RiskReport):JointReport{
 const j=report.joint;
 if(!j||j.request?.convention!=='portfolio_joint_risk_v1'||report.factors!==undefined||!['Holdings','Books'].includes(report.detail??'')||
  !Array.isArray(j.baseTotals)||!j.baseTotals.length||!Array.isArray(j.samples)||!Array.isArray(j.derivatives)||!Array.isArray(j.axes)||
  !Array.isArray(j.partitions)||!j.partitions.length||!Array.isArray(report.inputs?.positions)||!Array.isArray(report.inputs?.markets))throw invalid();
 const bases=j.baseTotals.map(book),bindings=report.detail==='Holdings'?report.inputs.positions.map(binding):[];
 if(new Set(bases).size!==bases.length||new Set(bindings).size!==bindings.length||
  !same(j.partitions.map(p=>p.definitions.revisionId),report.inputs.markets.map(m=>m.definitions.revisionId)))throw invalid();
 for(const p of j.partitions){
  if(!Array.isArray(p.samples)||!Array.isArray(p.applicableAxes)||new Set(p.applicableAxes).size!==p.applicableAxes.length||
   p.applicableAxes.some(id=>!j.axes.some(a=>a.id===id))||!Number.isInteger(p.work?.scenarioCount)||p.work.scenarioCount!==p.samples.length+1||
   new Set(p.samples.map(s=>s.id)).size!==p.samples.length)throw invalid();
 }
 const expected=new Set<string>();
 if(j.kind==='scenarios'){
  if(!j.request.scenarios||j.request.derivatives||j.request.scenarios.convention!=='independent_joint_absolute_curve_scenarios_v1'||j.derivatives.length||j.axes.length)throw invalid();
  const ids=j.request.scenarios.scenarios.map(s=>s.id);
  if(!ids.length||new Set(ids).size!==ids.length||!same(ids,j.samples.map(s=>s.id))||j.partitions.some(p=>!same(ids,p.samples.map(s=>s.id))))throw invalid();
  for(const sample of j.samples){
   if(!same(bases,sample.totals.map(t=>book({...t,currencyReference:t.values.currencyReference})))||!same(bindings,sample.positions.map(binding)))throw invalid();
   for(const row of [...sample.totals,...sample.positions])if(['base','value','pnl','loss'].some(k=>typeof row.values[k as keyof JointValues]!=='string'||typeof row.values.display[k as keyof JointValues['display']]!=='string'))throw invalid();
  }
 }else if(j.kind==='derivatives'){
  const request=j.request.derivatives;
  if(!request||j.request.scenarios||request.convention!=='exact_central_half_nominal_double_v1'||j.samples.length||!request.axes.length||
   !same(request.axes.map(a=>a.id),j.axes.map(a=>a.id)))throw invalid();
  for(const axis of request.axes){expected.add(formulas.delta+'/'+axis.id);expected.add(formulas.gamma+'/'+axis.id);}
  for(const pair of request.mixed)expected.add(formulas.mixedGamma+'/'+pair.id);
  if(!same([...expected],j.derivatives.map(d=>d.formula+'/'+d.id)))throw invalid();
  for(const derivative of j.derivatives){
   if(!same(bases,derivative.totals.map(t=>book({...t,currencyReference:t.values.currencyReference})))||!same(bindings,derivative.positions.map(binding))||
    derivative.axes.some(id=>!j.axes.some(a=>a.id===id)))throw invalid();
   for(const row of [...derivative.totals,...derivative.positions])if(typeof row.values.stable!=='boolean'||!row.values.unit||
    ['half','nominal','double'].some(k=>typeof row.values[k as keyof JointEstimate]!=='string'||typeof row.values.display[k as keyof JointEstimate['display']]!=='string'))throw invalid();
  }
 }else throw invalid();
 return j;
}

/** Graph coordinates use exact server strings; no totals or sensitivities are calculated here. */
export function jointCharts(j:JointReport,metric:JointMetric):{key:string;spec:ChartSpec}[]{
 type Entry={id:string;label:string;rows:(RowIdentity&{values:JointValues|JointEstimate})[]};
 const scenarios=j.kind==='scenarios';
 const entries:Entry[]=scenarios?j.samples.map(s=>({id:s.id,label:s.label,rows:s.totals})):j.derivatives.filter(d=>d.formula===formulas[metric as keyof typeof formulas]).map(d=>({
  id:d.formula+'/'+d.id,label:d.axes.map(id=>j.axes.find(a=>a.id===id)?.factor.label??id).join(' × '),rows:d.totals}));
 const groups=new Map<string,{unit:string;measure:string;books:Map<string,{name:string;points:Map<string,{value:string;text:string}>}>}>();
 for(const entry of entries)for(const row of entry.rows){
  const v=row.values,field=scenarios?(metric==='pnl'?'pnl':'loss'):'nominal';
  const unit=scenarios?v.currency:(v as JointEstimate).unit,key=canonical([row.measure,v.currencyReference,unit]);
  const group=groups.get(key)??{unit,measure:row.measureLabel,books:new Map()};groups.set(key,group);
  const series=group.books.get(row.bookId)??{name:row.book,points:new Map()};group.books.set(row.bookId,series);
  series.points.set(entry.id,{value:(v as unknown as Record<string,string>)[field],text:(v.display as Record<string,string>)[field]});
 }
 return [...groups].map(([key,g])=>({key,spec:{title:`${jointLabels[metric]} · ${g.measure}`,unit:g.unit,axis:'category',categoryTitle:scenarios?'Named scenario':'Selected derivative',kind:metric==='mixedGamma'?'heatmap':'bar',colorBySign:scenarios,positiveTone:scenarios&&metric==='loss'?'loss':'gain',
  subtitle:scenarios?'Deterministic stresses have no assigned probabilities. Native measures and currencies remain separate.':'Half, nominal and double estimates and agreement are retained in the table below.',
  series:[...g.books].map(([id,b])=>({id,name:b.name,points:entries.map(e=>({x:e.id,label:e.label,y:b.points.get(e.id)?.value??null,text:b.points.get(e.id)?.text}))}))}}));
}
