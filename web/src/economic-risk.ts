import type {HistoryScope,HistoryRequest,HistoryCapabilities,HistoryManifest} from './factor-history.js';
import type {MonteCarloPolicy} from './monte-carlo-risk.js';
import type {RequestCommand} from './pending-command.js';
import {sameUtcInstant} from './user-time.js';

export type EconomicChoice={value:string;label:string};
export type EconomicMethod=EconomicChoice&{history:boolean;simulation:boolean};
export type EconomicCurrency={revision:{catalogId:string;revisionId:string};code:string};
export type EconomicCashFundingSelection={currency:EconomicCurrency;snapshotId:string;curve:{revision:{catalogId:string;revisionId:string};code:string};mappingId:string;definitionId:string;definitionVersion:number};
export type EconomicCashFunding={context:EconomicSetup['context'];snapshotId:string;currencies:EconomicCurrency[];
 choices:{selection:EconomicCashFundingSelection;label:string;currencyLabel:string;sourceLabel:string;effectiveAt:string}[]};
export type EconomicProjectionPolicy={implementation:string;pricePath:string;cashDisposition:string;cashAccumulation:string;numericalPolicy:string;nonPriceInputs:string;inputExpiry:string;physicalDelivery:string};
export type EconomicProjection={implementation:string;session:string;sessionVersion:number;horizonPolicy:string;rateTimingPolicy:string;eventMarketPolicy:string;policy:EconomicProjectionPolicy;gasOwned:{accountId:string;profileId:string;revision:number}[];cashAccumulationSelections?:EconomicCashFundingSelection[]};
export type EconomicSetup={context:{contextId:string;knownAt:string;effectiveOn:string};asOf:string;scope:HistoryScope;convention:string;numericalPolicy:string;engineVersion:string;
 sessions:{code:string;version:number;name:string}[];pricing:(EconomicChoice&{policy:Record<string,unknown>})[];
 gasAccounts:{accountId:string;code:string;book:string;profiles:{profileId:string;revision:number;label:string;quantityDisposition:string;effectiveAt:string}[]}[];
 projection:{implementation:string;horizonPolicy:string;rateTimingPolicy:string;eventMarketPolicy:string;policy:Pick<EconomicProjectionPolicy,'implementation'|'numericalPolicy'|'nonPriceInputs'>;
 cashFunding?:boolean;pricePaths:EconomicChoice[];cashDisposition:EconomicChoice[];cashAccumulation:(EconomicChoice&{requiresFunding?:boolean})[];inputExpiry:EconomicChoice[];physicalDelivery:EconomicChoice[]};
 risk:{quantilePolicy:string;tailPolicy:string;methods:EconomicMethod[];simulation:Omit<MonteCarloPolicy,'confidence'|'drift'|'paths'|'seed'>;drifts:EconomicChoice[];limits:{maxPaths:number;maxFactors:number;maxShockValues:number}}};
export type EconomicSourceRequest={scope:HistoryScope;pricingPolicy:unknown;projection:EconomicProjection};
export type EconomicHorizon={openingAt:string;contextId:string;knownAt:string;closes:{date:string;instant:string}[];session:{code:string;version:number}};
export type EconomicSources={engineVersion:string;source:EconomicSourceRequest;horizon:EconomicHorizon;products:unknown[];books:{id:string;name:string}[];
 factors:{id:string;label:string;unit:string;factor:unknown}[];history:HistoryCapabilities;methods:string[];parametricAvailable:boolean};
export type EconomicHistory={source:EconomicSourceRequest;request:HistoryRequest;manifest:HistoryManifest;factors:unknown[];
 columns:{id:string;factor:{id:string;label:string;unit:string;factor:unknown}}[];horizon:EconomicHorizon;coverage:{available:boolean;unavailableReason:string|null;factors:number;samples:number};engineVersion:string};
export type EconomicSpec={projection:EconomicProjection;policy:{implementation:string;confidence:string;quantilePolicy:string;tailPolicy:string};history:HistoryRequest|null;simulation:MonteCarloPolicy|null;factors:unknown[]};
export type EconomicRequest=HistoryScope&{pricingPolicy:unknown;convention:string;numericalPolicy:string;detail:'holdings';bumps:[];stress:[];economic:EconomicSpec};
export type EconomicOwner={owner:{kind:number;id:string};bindingId:string;bookId:string};
export type EconomicHolding={owner:EconomicOwner;reference:string;instrument:string;book:string;currency:string;currencyReference:unknown;openingValue:string;residualValue:string;periodCash:string;economicPnl:string};
export type EconomicPartition={measure:string;currency:unknown;baseValue:string;mean:string;valueAtRisk:string;expectedShortfall:string;
 books:{id:string;valueAtRisk:string;expectedShortfall:string}[];trades:{id:string;owner:{kind:number;id:string};valueAtRisk:string;expectedShortfall:string}[];
 samples:{id:string;value:string;loss:string;varWeight:string;esWeight:string}[];histogram:{lower:string;upper:string;count:number;probability:string}[]};
export type EconomicReport={measure:string;convention:string;engineVersion:string;context:EconomicSetup['context'];source:HistoryScope['context'];snapshotId:string;asOf:string;bookIds:string[];tradeIds:string[]|null;pricingPolicy:unknown;
 request:EconomicSpec;horizon:EconomicHorizon;holdings:EconomicHolding[];books:{id:string;name:string}[];products:unknown[];optionCalibrations:unknown[];history:HistoryManifest|null;factors:unknown[];
 result:{policy:EconomicSpec['policy'];horizon:string;historyId?:string;samples?:number;noShockTimeCarry:{owner:EconomicOwner;currency:unknown}[];economic:EconomicPartition[];marketOnlyDiagnostic:EconomicPartition[];monteCarloDiagnostics?:unknown[]}};
export type EconomicJob={id:string;requestId?:string;state:string;kind:'economic_lifecycle';result?:EconomicReport;error?:{code:string;message:string}};
export const canonicalEconomic=(value:unknown):string=>JSON.stringify(value,(_key,v)=>v&&typeof v==='object'&&!Array.isArray(v)?Object.fromEntries(Object.entries(v).sort(([a],[b])=>a.localeCompare(b))):v);
const exact=(value:unknown):value is string=>typeof value==='string'&&/^-?\d+(?:\.\d+)?(?:\/[1-9]\d*)?$/.test(value);
const unique=(values:unknown[])=>new Set(values.map(canonicalEconomic)).size===values.length;
const positionKey=(position:EconomicOwner)=>canonicalEconomic([position?.owner?.kind,position?.owner?.id,position?.bindingId,position?.bookId]);
const methods=new Set(['economic_lifecycle_no_variable_factors_v1','economic_lifecycle_historical_full_revaluation_v1','economic_lifecycle_monte_carlo_full_revaluation_v1']);

/** Identity and complete report coverage only; wealth, carry and tails are backend calculations. */
export function validateEconomicReport(report:EconomicReport):EconomicReport{
 const bad=()=>Error('The event-aware report has inconsistent captured inputs or incomplete owner, currency or path coverage. Reread the complete report.');
 const r=report?.result,q=report?.request,h=report?.horizon;
 if(report?.measure!=='economic_pnl_v1'||!r||!q||!methods.has(q.policy?.implementation)||canonicalEconomic(r.policy)!==canonicalEconomic(q.policy)||
  !q.projection?.policy?.physicalDelivery||!q.projection.policy.inputExpiry||!Array.isArray(q.projection.gasOwned)||
  !report.context?.contextId||report.context.contextId!==report.source?.contextId||report.snapshotId!==report.source?.snapshotId||report.source.knowledgeMode!=='retained'||
  !h||h.contextId!==report.context.contextId||!sameUtcInstant(h.openingAt,report.asOf)||!sameUtcInstant(h.knownAt,report.context.knownAt)||!Array.isArray(h.closes)||h.closes.length!==1||!sameUtcInstant(r.horizon,h.closes[0].instant)||
  !Array.isArray(report.bookIds)||!unique(report.bookIds)||!Array.isArray(report.books)||!unique(report.books.map(b=>b.id))||
  !Array.isArray(report.holdings)||!report.holdings.length||!unique(report.holdings.map(p=>positionKey(p.owner)))||!Array.isArray(r.noShockTimeCarry)||
  r.noShockTimeCarry.length!==report.holdings.length||!unique(r.noShockTimeCarry.map(v=>positionKey(v.owner)))||
  report.holdings.some(p=>![1,2].includes(p.owner?.owner?.kind)||!p.owner.owner.id||!p.owner.bindingId||!report.bookIds.includes(p.owner.bookId)||
   !report.books.some(b=>b.id===p.owner.bookId)||![p.openingValue,p.residualValue,p.periodCash,p.economicPnl].every(exact)||
   !r.noShockTimeCarry.some(v=>positionKey(v.owner)===positionKey(p.owner)&&canonicalEconomic(v.currency)===canonicalEconomic(p.currencyReference))))throw bad();
 const deterministic=q.policy.implementation==='economic_lifecycle_no_variable_factors_v1';
 let sampleIds:string[];
 if(deterministic){
  if(q.history!==null||q.simulation!==null||report.history!==null||!Array.isArray(q.factors)||q.factors.length||!Array.isArray(report.factors)||report.factors.length||r.samples!==undefined||r.historyId!==undefined)throw bad();
  sampleIds=['no_variable_factors'];
 }else{
  const history=report.history;
  if(!history||!history.available||history.id!==q.history?.expectedManifestId||r.historyId!==history.id||history.forecast.contextId!==report.context.contextId||
   !sameUtcInstant(history.forecast.knownAt,report.context.knownAt)||!sameUtcInstant(history.forecast.valuationAt,report.asOf)||q.history?.scope.context.contextId!==report.context.contextId||
   q.history.scope.context.snapshotId!==report.snapshotId||canonicalEconomic(q.history.pricingPolicy)!==canonicalEconomic(report.pricingPolicy)||
   !Array.isArray(history.columns)||!history.columns.length||!Array.isArray(q.factors)||q.factors.length!==history.columns.length||!Array.isArray(report.factors)||report.factors.length!==history.columns.length)throw bad();
  if(q.policy.implementation==='economic_lifecycle_historical_full_revaluation_v1'){
   if(q.simulation!==null||r.samples!==history.samples.length)throw bad();sampleIds=history.samples.map(s=>s.id);
  }else{
   if(!q.simulation||q.simulation.confidence!==q.policy.confidence||r.samples!==q.simulation.paths)throw bad();
   sampleIds=r.economic?.[0]?.samples?.map(s=>s.id)??[];
   if(sampleIds.length!==r.samples||!unique(sampleIds))throw bad();
  }
 }
 const currencies=[...new Set(report.holdings.map(p=>canonicalEconomic(p.currencyReference)))];
 for(const [partitions,measure] of [[r.economic,'economic_pnl_v1'],[r.marketOnlyDiagnostic,'market_only_horizon_diagnostic_v1']] as const){
  if(!Array.isArray(partitions)||partitions.length!==currencies.length||!unique(partitions.map(p=>p.currency))||partitions.some(p=>p.measure!==measure||!currencies.includes(canonicalEconomic(p.currency))||
    ![p.baseValue,p.mean,p.valueAtRisk,p.expectedShortfall].every(exact)||!Array.isArray(p.samples)||p.samples.length!==(deterministic?1:r.samples)||!unique(p.samples.map(s=>s.id))||
    p.samples.some(s=>![s.value,s.loss,s.varWeight,s.esWeight].every(exact))||sampleIds.length>0&&canonicalEconomic([...p.samples.map(s=>s.id)].sort())!==canonicalEconomic([...sampleIds].sort())||
    !Array.isArray(p.histogram)||p.histogram.some(b=>![b.lower,b.upper,b.probability].every(exact)||!Number.isSafeInteger(b.count)||b.count<0)))throw bad();
 }
 return report;
}
/** A new calculation answers its submitted source; retained jobs keep their own captured source instead. */
export function requireEconomicCalculationReceipt(report:EconomicReport,request:EconomicRequest,source:EconomicSources,engineVersion:string):void{
 validateEconomicReport(report);
 const sameSet=(a:string[],b:string[])=>unique(a)&&unique(b)&&canonicalEconomic([...a].sort())===canonicalEconomic([...b].sort());
 const sameTrades=(a:string[]|null|undefined,b:string[]|null|undefined)=>a==null&&b==null||Array.isArray(a)&&Array.isArray(b)&&sameSet(a,b);
 if(canonicalEconomic(report.source)!==canonicalEconomic(request.context)||canonicalEconomic(report.request)!==canonicalEconomic(request.economic)||
  canonicalEconomic(report.pricingPolicy)!==canonicalEconomic(request.pricingPolicy)||!sameUtcInstant(report.asOf,source.horizon.openingAt)||
  canonicalEconomic(report.horizon)!==canonicalEconomic(source.horizon)||!sameSet(report.bookIds,source.books.map(b=>b.id))||!sameSet(report.bookIds,request.bookIds)||
  !sameTrades(report.tradeIds,request.tradeIds)||report.convention!==request.convention||report.engineVersion!==engineVersion)
  throw Error('The economic calculation response does not match the exact submitted source, scope, pricing or lifecycle policies. Prepare this source again.');
}
/** Untrusted transport may restore only this exact operation, never a different write. */
export function restoreEconomicCommand(value:unknown):RequestCommand|undefined{
 const saved=value as RequestCommand,request=saved?.body?.request as EconomicRequest;
 if(saved?.path!=='portfolio/risk/jobs'||typeof saved.operationId!=='string'||!saved.operationId||!request?.context?.contextId||
  !request.context.snapshotId||!request.economic?.projection?.policy?.physicalDelivery||!methods.has(request.economic.policy?.implementation)||
  !Array.isArray(request.economic.factors)||!Array.isArray(request.bookIds)||!Array.isArray(request.bumps)||request.bumps.length||!Array.isArray(request.stress)||request.stress.length||
  !Object.hasOwn(request.economic,'history')||!Object.hasOwn(request.economic,'simulation'))return;
 return structuredClone(saved);
}
