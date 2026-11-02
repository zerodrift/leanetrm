import type {ChartSpec} from './chart-view.js';
import type {RiskReport,ExcludedPosition} from './risk-report.js';
import type {RunView,RiskJob} from './risk-view.js';
import {runExclusions,runGaps,type RunGap} from './risk-lens.js';

export type RiskMetric='delta'|'gamma'|'upPnl'|'downPnl';
export const riskMetricLabels:Record<RiskMetric,string>={delta:'Delta',gamma:'Gamma',upPnl:'Up P&L',downPnl:'Down P&L'};
export type ReportPresentation={state:'complete'|'incomplete'|'running'|'unavailable';excluded:ExcludedPosition[];gaps:RunGap[];
 factors:RiskReport['factors'];charts:{key:string;spec:ChartSpec}[]};
const canonical=(value:unknown):string=>JSON.stringify(value,(_key,v)=>v&&typeof v==='object'&&!Array.isArray(v)?Object.fromEntries(Object.entries(v).sort(([a],[b])=>a.localeCompare(b))):v);

/** Presentation of server values only. Factors overlap; there is deliberately no sum across rows or report parts. */
export function reportPresentation(run:RunView,metric:RiskMetric,window?:{offset:number;limit:number}):ReportPresentation{
 const gaps=runGaps(run),empty={excluded:[],gaps,factors:[],charts:[]};
 if(run.running)return {state:'running',...empty};
 const reports=run.parts.flatMap(p=>p.report?[p.report]:[]);
 if(!reports.length)return {state:'unavailable',...empty};
 const first=reports[0];
 if(reports.some(r=>r.context.contextId!==run.context?.contextId||r.snapshotId!==first.snapshotId||canonical(r.scenario)!==canonical(first.scenario)))
  throw Error('The report parts do not share one retained context, close and scenario. Their results cannot be displayed together.');
 const excluded=runExclusions(reports),factors=reports.flatMap(r=>r.factors);
 if(new Set(factors.map(f=>f.factor.id)).size!==factors.length)throw Error('This run states a factor more than once. Its results cannot be displayed together.');
 const groups=new Map<string,{unit:string;measure:string;coordinate:string;books:Map<string,{name:string;values:Map<string,{exact:string;text:string}>}>}>();
 const drawn=window?factors.slice(window.offset,window.offset+window.limit):factors;
 for(const f of drawn){
  const seen=new Set<string>();
  for(const total of f.totals){
   const v=total.values,unit=metric==='delta'?v.deltaUnit:metric==='gamma'?v.gammaUnit:v.currency;
   const key=canonical([total.measure,v.currencyReference,unit,f.factor.coordinate]);
   const group=groups.get(key)??{unit,measure:total.measureLabel,coordinate:f.factor.coordinate,books:new Map()};groups.set(key,group);
   if(seen.has(key+'\0'+total.bookId))throw Error('A factor states the same book total twice. Its results cannot be displayed.');
   seen.add(key+'\0'+total.bookId);
   const book=group.books.get(total.bookId)??{name:total.book,values:new Map()};group.books.set(total.bookId,book);
   book.values.set(f.factor.id,{exact:v[metric],text:v.display[metric]});
  }
 }
 const charts=[...groups].map(([key,g])=>({key,spec:{title:`${riskMetricLabels[metric]} · ${g.measure} · ${g.coordinate==='market_quote'?'Market factors':'Computed outputs'}`,
  subtitle:'Each row is an independent shock. Overlapping exposures are not added.',axis:'category',categoryTitle:'Shocked factor',unit:g.unit,kind:'bar',
  series:[...g.books].map(([id,b])=>({id,name:b.name,points:drawn.filter(f=>f.factor.coordinate===g.coordinate&&[...g.books.values()].some(book=>book.values.has(f.factor.id))).map(f=>({
   x:f.factor.id,label:`${f.factor.label} · ±${f.bump} ${f.factor.unit}`,y:b.values.get(f.factor.id)?.exact??null,text:b.values.get(f.factor.id)?.text,
  }))}))} satisfies ChartSpec}));
 return {state:gaps.length||excluded.length||!run.scope?'incomplete':'complete',excluded,gaps,factors,charts};
}

type Scope={bookIds:readonly string[];portfolioIds:readonly string[]};
const sameIds=(a:readonly string[],b:readonly string[])=>canonical([...a].sort())===canonical([...b].sort());
/** Only complete, consistently identified recent runs are candidates; a bounded list is never proof that an absent part completed. */
export function recentCompletedRuns(jobs:readonly RiskJob[],scope:Scope,asOf:string):RiskJob[]{
 const matching=(j:RiskJob)=>j.state==='completed'&&j.asOf===asOf&&!!j.scope&&sameIds(j.scope.bookIds,scope.bookIds)&&sameIds(j.scope.portfolioIds,scope.portfolioIds);
 const grouped=new Map<string,RiskJob[]>();
 for(const job of jobs){const key=job.run?'run:'+job.run.id:job.id;const group=grouped.get(key)??[];group.push(job);grouped.set(key,group);}
 return [...grouped.values()].flatMap(parts=>{
  if(parts.some(j=>!matching(j)))return [];
  const first=parts[0],count=first.run?.parts??1;
  if(parts.length!==count||new Set(parts.map(j=>j.run?.part??1)).size!==count||parts.some(j=>(j.run?.parts??1)!==count||j.context?.contextId!==first.context?.contextId||!j.context?.contextId))return [];
  if(parts.some(j=>j.run&&(j.run.part<1||j.run.part>count)))return [];
  return [{...first,finishedAt:parts.flatMap(j=>j.finishedAt?[j.finishedAt]:[]).sort().at(-1)}];
 }).sort((a,b)=>(b.finishedAt??'').localeCompare(a.finishedAt??'')||a.id.localeCompare(b.id));
}
export function riskAddress(hash:string):string|undefined{
 return /^#risk\/job\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i.exec(hash)?.[1];
}
export function riskReportAddress(id:string,where:{hosted:boolean;example:string}):string{
 if(!riskAddress('#risk/job/'+id)||!['baseline','exchange'].includes(where.example))throw Error('The retained report address is invalid.');
 return (where.hosted?'/app':'/')+(where.example==='exchange'?'?example=exchange':'')+'#risk/job/'+id;
}
