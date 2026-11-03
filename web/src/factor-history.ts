import type {ChartSpec} from './chart-view.js';
import type {RiskFactor,ExcludedPosition} from './risk-report.js';

export type HistoryScope={context:{snapshotId:string;convention:string;knowledgeMode:string;contextId:string};bookIds:string[];portfolioIds:string[];tradeIds?:string[]|null};
export type HistoryPolicy={convention:string;numericalPolicy:string;basis:string;revisions:string;alignment:string;transform:string;missing:string;regimes:string;minimumSamples:number;priceTypes?:string[]};
export type HistoryCapabilities={guided:{profile:Omit<HistoryPolicy,'minimumSamples'|'priceTypes'>;lookback:string;minimumSamples:number;closeTime:string;priceTypes:string[]};priceTypes:{code:string;label:string}[];profile:Omit<HistoryPolicy,'minimumSamples'>;clockPolicy:{ambiguousTime:string;invalidTime:string;resolution:string};
 calendars:{reference:unknown;label:string;from:string;to:string;clock:string;clockRule:{kind:string;ianaZone:string|null;fixedOffsetSeconds:number|null;tzdbVersion:string|null};windows:{code:string;label:string;from:string;to:string;covered:boolean}[];setupError:string|null}[];
 limits:{maxFactors:number;maxSessions:number;maxCells:number;maxCalendarDays:number;maxResponseBytes:number}};
export type HistoryRequest={scope:HistoryScope;pricingPolicy:unknown;policy:HistoryPolicy;calendar:unknown;from:string;to:string;closeTime:string;ambiguousTime:string;invalidTime:string;expectedManifestId?:string};
type Mark={observation:unknown;value:string;knowledgeOrder:number;knownAt:string;lineage:unknown[]};
type Endpoint={at:string;marks:(Mark|null)[]};
export type HistoryEvidence={observation:{key:{id:string};source:{code:string};role:number;state:number;value:string;observedAt:string}};
export type HistoryManifest={id:string;forecast:{contextId:string;valuationAt:string;marketCutoff:string;knownAt:string;marketKnownThrough:number};
 policy:HistoryPolicy;columns:{id:string;mapping:unknown}[];sessions:{date:string;close:string}[];evidence:HistoryEvidence[];calendar?:{businessDates:string[]};dayPrices?:{at:string;choices:{selectedType:string|null;officialCloseStatus:string;availableTypes:string[]}[]}[];
 samples:{id:string;from:Endpoint;to:Endpoint;changes:string[]}[];rejected:{id:string;from:Endpoint;to:Endpoint;issues:{factorId:string;endpoint:string;reason:string}[]}[];
 available:boolean;unavailableReason:string|null};
export type HistoryReport={manifest:HistoryManifest;request:HistoryRequest;context:{contextId:string;knownAt:string;effectiveOn:string;knownThrough:number};asOf:string;snapshotId:string;engineVersion:string;
 selection:HistoryScope;scope:{bookIds:string[];portfolioIds:string[];tradeIds?:string[]|null};excludedPositions:ExcludedPosition[];columns:{id:string;factor:RiskFactor;state:string}[];
 coverage:{available:boolean;historyAvailable:boolean;portfolioComplete:boolean;factors:number;sessions:number;samples:number;rejected:number;minimumSamples:number;unavailableReason:string|null}};

const canonical=(value:unknown)=>JSON.stringify(value,(_key,v)=>v&&typeof v==='object'&&!Array.isArray(v)?Object.fromEntries(Object.entries(v).sort(([a],[b])=>a.localeCompare(b))):v);
/** Receipt identity and coverage only; alignment, changes and eligibility are backend decisions. */
export function validateFactorHistory(report:HistoryReport,expected?:HistoryRequest):void{

 const bad=()=>Error('The factor history has inconsistent context, interval or column coverage. Reread its captured inputs.');
 const m=report?.manifest,c=report?.coverage,scope=report?.request?.scope,selection=report?.selection;
 const same=(a:string[],b:string[])=>{const x=[...a].sort(),y=[...b].sort();return x.length===y.length&&x.every((v,i)=>v===y[i]);};
 const trades=(a:string[]|null|undefined,b:string[]|null|undefined)=>a==null&&b==null||Array.isArray(a)&&Array.isArray(b)&&a.length>0&&same(a,b);
 if(!m||!c||!scope||!selection||!/^[0-9a-f]{64}$/.test(m.id)||m.id!==report.request.expectedManifestId||m.policy?.convention!=='dated_factor_common_session_changes_v1'||
  m.forecast.contextId!==report.context?.contextId||m.forecast.contextId!==scope.context?.contextId||report.snapshotId!==scope.context.snapshotId||
  !Array.isArray(m.columns)||!m.columns.length||!Array.isArray(report.columns)||!Array.isArray(m.sessions)||m.sessions.length<2||
  !Array.isArray(m.samples)||!Array.isArray(m.rejected)||!Array.isArray(report.excludedPositions))throw bad();
 if(!trades(selection.tradeIds,scope.tradeIds)||selection.context.contextId!==scope.context.contextId||selection.context.snapshotId!==scope.context.snapshotId)throw bad();
 if(expected&&(!same(selection.bookIds,expected.scope.bookIds)||!same(selection.portfolioIds,expected.scope.portfolioIds)||!trades(scope.tradeIds,expected.scope.tradeIds)||
  scope.context.contextId!==expected.scope.context.contextId||scope.context.snapshotId!==expected.scope.context.snapshotId||
  ['from','to','closeTime','ambiguousTime','invalidTime','policy','calendar','pricingPolicy'].some(key=>canonical(report.request[key as keyof HistoryRequest])!==canonical(expected[key as keyof HistoryRequest]))))throw bad();
 validateHistoryPrices(m,report.request.policy);
 const ids=m.columns.map(c=>c.id),seen=new Set<string>(),window=(from:string,to:string)=>JSON.stringify([from,to]);
 const windows=new Set(m.sessions.slice(1).map((s,i)=>window(m.sessions[i].close,s.close)));
 if(new Set(ids).size!==ids.length||report.columns.length!==ids.length||report.columns.some((c,i)=>c.id!==ids[i]||!c.factor?.unit)||
  windows.size!==m.sessions.length-1||new Set(m.sessions.map(s=>s.close)).size!==m.sessions.length||
  c.factors!==ids.length||c.sessions!==m.sessions.length||c.samples!==m.samples.length||c.rejected!==m.rejected.length||
  c.minimumSamples!==m.policy.minimumSamples||c.historyAvailable!==m.available||c.available&&(!m.available||!c.portfolioComplete||report.excludedPositions.length>0)||
  !same(report.scope.bookIds,scope.bookIds)||!same(report.scope.portfolioIds,scope.portfolioIds)||!trades(report.scope.tradeIds,scope.tradeIds))throw bad();
 for(const row of [...m.samples,...m.rejected]){
  const key=window(row.from?.at,row.to?.at);
  if(!windows.delete(key)||seen.has(row.id)||!Array.isArray(row.from?.marks)||!Array.isArray(row.to?.marks)||row.from.marks.length!==ids.length||row.to.marks.length!==ids.length)throw bad();
  seen.add(row.id);
  if('changes' in row){
   if(row.changes.length!==ids.length||row.changes.some(v=>typeof v!=='string')||[...row.from.marks,...row.to.marks].some(v=>!v||typeof v.value!=='string'||!Array.isArray(v.lineage)||!v.lineage.length))throw bad();
  }else if(!Array.isArray(row.issues)||!row.issues.length||row.issues.some(i=>!ids.includes(i.factorId)||!['from','to'].includes(i.endpoint)))throw bad();
 }
 if(windows.size)throw bad();
}
export function factorHistoryChart(report:HistoryReport,column:string):ChartSpec{
 const index=report.columns.findIndex(c=>c.id===column);if(index<0)throw Error('Choose a column in this captured history.');
 const chosen=report.columns[index];
 return {title:'Absolute changes · '+chosen.factor.label,subtitle:'One dated factor is shown. Eligibility uses common complete rows across the entire captured universe.',
  axis:'observation',categoryTitle:'Eligible session close',unit:chosen.factor.unit,kind:'line',series:[{id:chosen.id,name:chosen.factor.label,
   points:report.manifest.samples.map(s=>({x:s.to.at,label:s.id,y:s.changes[index],text:s.changes[index]}))}]};
}

export const historyPriceLabels:Record<string,string>={official_close_v1:'Official daily close',trader_mark_final_v1:'Final trader mark',index_fixing_final_v1:'Final index fixing',parameter_final_v1:'Final parameter',contract_settlement_final_v1:'Final contract settlement',trader_mark_preliminary_v1:'Preliminary trader mark',settlement_preliminary_v1:'Preliminary daily settlement'};
const priceClassification:Record<string,[number,number]>={official_close_v1:[2,2],trader_mark_final_v1:[1,2],index_fixing_final_v1:[5,2],parameter_final_v1:[4,2],contract_settlement_final_v1:[3,2],trader_mark_preliminary_v1:[1,1],settlement_preliminary_v1:[2,1]};
/** Require complete price provenance before presenting any EOD cell as official or an allowed alternative. */
export function validateHistoryPrices(m:HistoryManifest,policy?:HistoryPolicy):void{
 const bad=()=>Error('The aligned price coverage has missing or inconsistent price-type provenance. Reread the captured history.');
 if(policy!==undefined&&canonical(m.policy)!==canonical(policy))throw bad();
 if(m.policy.alignment!=='daily_latest_visible_session_end_v1'){if(m.dayPrices!==undefined)throw bad();return;}
 const allowed=m.policy.priceTypes;
 if(canonical(m.policy)!==canonical(policy)||m.policy.regimes!=='ordered_price_types_fixed_series_source_unit_contract_v1'||!Array.isArray(allowed)||!allowed.length||new Set(allowed).size!==allowed.length||allowed.some(t=>!priceClassification[t])||
  !Array.isArray(m.dayPrices)||!Array.isArray(m.sessions)||m.dayPrices.length!==m.sessions.length||!Array.isArray(m.evidence)||!Array.isArray(m.samples)||!Array.isArray(m.rejected))throw bad();
 const evidence=new Map(m.evidence.map(e=>[e.observation?.key?.id,e]));if(evidence.size!==m.evidence.length)throw bad();
 const endpoints=new Map<string,Endpoint>();
 for(const s of [...m.samples,...m.rejected])for(const endpoint of [s.from,s.to]){
  if(!endpoint||!Array.isArray(endpoint.marks)||endpoint.marks.length!==m.columns.length)throw bad();
  const previous=endpoints.get(endpoint.at);if(previous&&JSON.stringify(previous)!==JSON.stringify(endpoint))throw bad();endpoints.set(endpoint.at,endpoint);
 }
 for(const [i,day] of m.dayPrices.entries()){
  if(day.at!==m.sessions[i].close||!Array.isArray(day.choices)||day.choices.length!==m.columns.length)throw bad();
  const endpoint=endpoints.get(day.at);if(!endpoint)throw bad();
  for(const [j,choice] of day.choices.entries()){
   const mark=endpoint.marks[j];
   if(!choice||!Array.isArray(choice.availableTypes)||new Set(choice.availableTypes).size!==choice.availableTypes.length||choice.availableTypes.some(t=>!priceClassification[t])||
    !['available','missing_mark','publication_state','ambiguous_revision','mapping_regime'].includes(choice.officialCloseStatus)||
    (choice.officialCloseStatus==='available')!==choice.availableTypes.includes('official_close_v1')||
    (choice.selectedType!==null)!==!!mark)throw bad();
   if(mark){
    const selected=choice.selectedType!;if(!allowed.includes(selected)||!choice.availableTypes.includes(selected))throw bad();
    const actual=evidence.get((mark.observation as {id:string})?.id)?.observation,classification=priceClassification[selected];
    if(!actual||!classification||actual.role!==classification[0]||actual.state!==classification[1]||actual.value!==mark.value||typeof actual.observedAt!=='string'||!Number.isFinite(Date.parse(actual.observedAt))||!actual.source?.code)throw bad();
   }
  }
 }
}
