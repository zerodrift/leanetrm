import {renderRegularTable} from './regular-table.js';
import {displayInstant,publicText} from './market-view.js';
import {PendingCommand,type RequestCommand} from './pending-command.js';
import {renderChart,clearChart,type ChartSpec} from './chart-view.js';
import {guardedCsv,downloadCsv} from './csv-export.js';
import {currencyKey,currencyLabels,type Currency,type CurrencyReference} from './currency-labels.js';
import {modelLabel} from './agent-settings.js';
import {zoneChoices} from './preferences.js';

type Api=<T>(path:string,body?:unknown)=>Promise<T>;
type Amount={amount:string;display:string};
type Spent='uncachedInput'|'cacheRead'|'cacheWrite'|'output'|'reasoning';
type Tokens={count:string|null;unknownCalls:number};
// Every cost a report sums is the cost of its priced calls, named so, beside the calls without a cost.
type Turns={completed:number;failed:number;cancelled:number;interrupted:number;unfinished:number;other:number;answersPricedCost:Amount|null;
  answersWithoutCost:number;costPerAnswer:Amount|null;otherPricedCost:Amount|null};
type Figures={calls:{total:number;priced:number;withoutCost:number;unpriced:number;unknown:number;unrecognized:number;open:number};tokens:Record<Spent,Tokens>;
  pricedCost:Amount|null;pricedShare:string|null;superseded:{calls:number;cost:Amount|null};turns:Turns|null};
type Member={kind:'tests'}|{kind:'model';id:string;vendor:string;identifier:string;name:string}|{kind:'user';id:string;name:string;login:string}|{kind:'agent';id:string;name:string};
type Vendor={code:string;name:string};
type Day={date:string;pricedCost:Amount|null;calls:number;withoutCost:number};
type Section={currency:CurrencyReference;label:Currency|null;total:Figures;rows:({member:Member}&Figures)[];daily:Day[]};
type Report={reportFormat:'usage_report_v1';zone:string;from:string;to:string;utcFrom:string;utcTo:string;by:string;generatedAt:string;costMethod:string;displayMethod:string;ratioMethod:string;
  days:{date:string;from:string;to:string}[];currencies:Section[];vendors:Vendor[]};
type Export={operationId:string;at:string;actor:string;zone:string;from:string;to:string;by:string};
type Directory={dimensions:string[];maxDays:number;audit:Export[]};
type UsageEntry={zone:string;from:string;to:string;by:string};

const inconsistent=()=>Error('The AI settings directory is inconsistent. Refresh AI settings.');
const grouped=(count:number)=>count.toLocaleString('en-US');
const counted=(count:number,one:string,many:string)=>`${grouped(count)} ${count===1?one:many}`;
const groupings:Record<string,string>={model:'Model',user:'User',agent:'Agent'};
const dimensions:[Spent,string][]=[['uncachedInput','Uncached input'],['cacheRead','Cache read'],['cacheWrite','Cache write'],['output','Output'],['reasoning','Reasoning']];
const exportPath='settings/ai/usage/export',reportFormat='usage_report_v1';
// Refusals that prove an export recorded nothing, so its retained request can be released.
const rejections=['report_zone_invalid','report_range_invalid','model_admin_input','model_admin_conflict','access_denied','request_format','request_too_large','request_token'];
export const knownUsageRejection=(code:string|undefined)=>!!code&&rejections.includes(code);
// The codes Usage shares with the model commands, in its own terms.
const messages:Record<string,string>={model_admin_input:'Group usage by model, user or agent, as this tab offers.',
  model_admin_conflict:'This export’s request identity was already used for a different request, so nothing was exported. Export again.',
  model_admin_outcome_unknown:'We could not confirm the export. Retry the retained request: it returns the recorded report if the export was recorded, and otherwise exports once.'};
export function usageMessage(code:string|undefined){return code&&Object.hasOwn(messages,code)?messages[code]:undefined;}

export function groupingName(by:string){if(!Object.hasOwn(groupings,by))throw inconsistent();return groupings[by];}
// The page reads one report format, and refuses any other rather than show or export what it might misread.
export function readableReport(answer:unknown):Report{
  if((answer as {reportFormat?:unknown}|null)?.reportFormat!==reportFormat)
    throw Error('This usage report is in a format this page does not read. Refresh the page to load the current Usage tab, then show usage again.');
  return answer as Report;
}
// A report names its zone, its first day, the day after its last and its grouping, each as entered; the server
// decides whether they make a report.
export function usageRequest(entry:UsageEntry):UsageEntry{
  if(!entry.zone)throw Error('Choose a time zone.');
  if(!entry.from)throw Error('Enter the first day.');
  if(!entry.to)throw Error('Enter the day after the last day.');
  if(!entry.by)throw Error('Choose whether to group by model, user or agent.');
  return {zone:entry.zone,from:entry.from,to:entry.to,by:entry.by};
}
// A readable currency is labelled as Models & prices labels it; one the reader cannot read shows only its code, and
// which of the unreadable totals sharing that code it is, in the report's order.
export function sectionLabels(sections:Pick<Section,'currency'|'label'>[]){
  const readable=currencyLabels(sections.flatMap(s=>s.label?[s.label]:[])),unreadable=sections.filter(s=>!s.label);
  return sections.map(s=>{
    if(s.label)return readable.get(currencyKey(s.label))!;
    const same=unreadable.filter(u=>u.currency.code===s.currency.code),plain=`Currency not readable · ${s.currency.code}`;
    return same.length>1?`${plain} (reference ${same.indexOf(s)+1} of ${same.length})`:plain;
  });
}
// A row names its member and nothing it holds.
export function memberLabel(member:Member,vendors:Vendor[]){
  switch(member?.kind){
    case 'tests':return 'Provider tests';
    case 'model':return modelLabel({vendors},{displayName:member.name,identifier:member.identifier,vendor:member.vendor});
    case 'user':return `${member.name} (${member.login})`;
    case 'agent':return member.name;
  }
  throw inconsistent();
}
// Token sums are exact integers of any size; a dimension no call reported is unknown, never zero.
function tokenText({count,unknownCalls}:Tokens){
  if(count===null)return null;
  if(!/^[0-9]+$/.test(count))throw inconsistent();
  const sum=count.replace(/\B(?=([0-9]{3})+$)/g,',');
  return unknownCalls?`${sum} · ${counted(unknownCalls,'call','calls')} unknown`:sum;
}
function superseded({calls,cost}:Figures['superseded']){
  if(!calls)return null;if(!cost)throw inconsistent();
  return `${cost.display} · ${counted(calls,'call','calls')}`;
}
// A cost per answer exists only while every answer has a cost.
function perAnswer(turns:Turns){
  if(!turns.completed)return null;if(turns.costPerAnswer)return turns.costPerAnswer.display;
  if(!turns.answersWithoutCost)throw inconsistent();
  return `${counted(turns.answersWithoutCost,'answer','answers')} without a cost`;
}

// One row per member, provider tests last; the currency's total is its summary. Provider tests answer no question,
// so they have no turns.
export function usageTable(report:Pick<Report,'by'|'vendors'>,section:Pick<Section,'rows'>){
  const headers=[groupingName(report.by),'Calls','Cost of priced calls','Calls without a cost','Share of priced cost','Answers','Cost per answer',
    "Other turns' cost of priced calls",'Failed','Cancelled','Interrupted','Unfinished',...dimensions.map(([,name])=>name),'Unpriced','Usage unknown',
    'Usage not recognized','In progress','Superseded price'];
  const rows=section.rows.map(row=>{
    const {calls,turns}=row;
    const answers=turns?[grouped(turns.completed),perAnswer(turns)??'No answers',turns.otherPricedCost?.display??(turns.other?'No priced calls':'No other turns'),
      ...[turns.failed,turns.cancelled,turns.interrupted,turns.unfinished].map(grouped)]:Array<string>(7).fill('');
    return [memberLabel(row.member,report.vendors),grouped(calls.total),row.pricedCost?.display??'No priced calls',grouped(calls.withoutCost),
      row.pricedShare===null?'':`${row.pricedShare}%`,...answers,...dimensions.map(([d])=>tokenText(row.tokens[d])??'Unknown'),
      ...[calls.unpriced,calls.unknown,calls.unrecognized,calls.open].map(grouped),superseded(row.superseded)??'None'];
  });
  return {headers,rows,numeric:headers.flatMap((_,i)=>i?[i]:[])};
}
// A currency's total as sentences, the other turns and their priced cost on their own line.
export function usageSummary(section:Pick<Section,'total'>){
  const {calls,pricedCost,tokens,turns}=section.total;if(!turns)throw inconsistent();
  const answers=perAnswer(turns);
  return [`${counted(calls.total,'call','calls')}: ${grouped(calls.priced)} priced, ${grouped(calls.unpriced)} unpriced, ${grouped(calls.unknown)} with usage unknown, `
      +`${grouped(calls.unrecognized)} with usage not recognized, ${grouped(calls.open)} in progress.`,
    `Cost of priced calls: ${pricedCost?.display??'none'}; calls without a cost: ${grouped(calls.withoutCost)}.`,
    `Tokens: ${dimensions.map(([d,name])=>`${name.toLowerCase()} ${tokenText(tokens[d])??'unknown'}`).join(', ')}.`,
    answers===null?'Answers: none.':`Answers: ${grouped(turns.completed)}; cost per answer: ${answers}.`,
    turns.other?`Other turns' cost of priced calls: ${turns.otherPricedCost?.display??'none'}.`:'Other turns: none.',
    `Turns: ${grouped(turns.completed)} completed, ${grouped(turns.failed)} failed, ${grouped(turns.cancelled)} cancelled, ${grouped(turns.interrupted)} interrupted, `
      +`${grouped(turns.unfinished)} unfinished.`,
    `Priced by a superseded revision: ${superseded(section.total.superseded)??'none'}.`];
}
// Each day's exact cost of priced calls; a day whose calls have no cost has no bar, and the note names the days with calls
// lacking one.
export function dailyChart(section:Pick<Section,'daily'>,label:string):ChartSpec{
  return {title:'Daily cost of priced calls',subtitle:label,axis:'category',axisName:'Day',kind:'bar',unit:label,
    series:[{name:'Cost of priced calls',points:section.daily.map(d=>({x:d.date,y:d.pricedCost?.amount??null}))}]};
}
export function withoutCostNote(section:Pick<Section,'daily'>){
  const days=section.daily.filter(d=>d.withoutCost>0);
  return days.length?`Calls without a cost: ${days.map(d=>`${d.date} (${grouped(d.withoutCost)})`).join(', ')}. Each day shows the cost of its priced calls only.`:'';
}
// The exported report under its export's identity: exact amounts and counts, the exact currency reference, the zone,
// range, UTC bounds, format and methods, ratios only as their report strings, and each day with its bounds. A cost that
// does not exist is blank.
export function usageCsv(report:Report,exportId:string){
  const labels=sectionLabels(report.currencies),by=groupingName(report.by);
  const reference=(section:Section,index:number)=>[labels[index],section.currency.catalogId,section.currency.revisionId,section.currency.code];
  const figures=(f:Figures)=>[...[f.calls.total,f.calls.priced,f.calls.withoutCost,f.calls.unpriced,f.calls.unknown,f.calls.unrecognized,f.calls.open].map(String),
    f.pricedCost?.amount??'',f.pricedShare??'',
    ...(f.turns?[String(f.turns.completed),f.turns.answersPricedCost?.amount??'',String(f.turns.answersWithoutCost),f.turns.costPerAnswer?.amount??'',
      String(f.turns.other),f.turns.otherPricedCost?.amount??'',...[f.turns.failed,f.turns.cancelled,f.turns.interrupted,f.turns.unfinished].map(String)]:Array<string>(10).fill('')),
    ...dimensions.flatMap(([d])=>[f.tokens[d].count??'',String(f.tokens[d].unknownCalls)]),String(f.superseded.calls),f.superseded.cost?.amount??''];
  const headers=['Currency','Currency catalog','Currency revision','Currency code','Row','Member','Member ID','Calls','Priced calls','Calls without a cost',
    'Unpriced calls','Usage unknown calls','Usage not recognized calls','Calls in progress','Cost of priced calls','Share of priced cost %','Answers',
    "Answers' cost of priced calls",'Answers without a cost','Cost per answer','Other turns',"Other turns' cost of priced calls",'Failed turns','Cancelled turns',
    'Interrupted turns','Unfinished turns',...dimensions.flatMap(([,name])=>[`${name} tokens`,`${name} unknown calls`]),'Superseded-price calls','Superseded-price cost'];
  const rows=report.currencies.flatMap((section,index)=>[...section.rows.map(row=>[...reference(section,index),row.member.kind==='tests'?'Provider tests':by,
    memberLabel(row.member,report.vendors),row.member.kind==='tests'?'':row.member.id,...figures(row)]),[...reference(section,index),'Total','','',...figures(section.total)]]);
  const days=report.currencies.flatMap((section,index)=>section.daily.map(entry=>{
    const day=report.days.find(d=>d.date===entry.date);if(!day)throw inconsistent();
    return [...reference(section,index),entry.date,day.from,day.to,entry.pricedCost?.amount??'',String(entry.calls),String(entry.withoutCost)];
  }));
  const metadata=[['Usage report'],['Export ID',exportId],['Report format',report.reportFormat],['Time zone',report.zone],['From',report.from],
    ['To, not included',report.to],['UTC from',report.utcFrom],['UTC to',report.utcTo],['Grouped by',by],['Generated',report.generatedAt],
    ['Cost method',report.costMethod],['Ratio method',report.ratioMethod],[]];
  return guardedCsv(metadata)+guardedCsv([headers,...rows],headers.flatMap((_,i)=>i>=7?[i]:[]))+guardedCsv([[]])
    +guardedCsv([['Currency','Currency catalog','Currency revision','Currency code','Date','UTC from','UTC to','Cost of priced calls','Calls','Calls without a cost'],
      ...days],[7,8,9]);
}
export function exportRows(audit:Export[],instant:(at:string)=>string){return audit.map(a=>[instant(a.at),a.actor,a.zone,a.from,a.to,groupingName(a.by)]);}
export function reportBounds(report:Report,instant:(at:string)=>string){
  return `${report.zone} · ${report.from} up to ${report.to}, not included · UTC ${report.utcFrom} to ${report.utcTo} · by ${groupingName(report.by).toLowerCase()} `
    +`· generated ${instant(report.generatedAt)}`;
}
export function exportPending(body:Record<string,unknown>){
  return `An export of usage in ${String(body.zone)} from ${String(body.from)} up to ${String(body.to)}, by ${groupingName(String(body.by)).toLowerCase()}, `
    +'has no confirmed outcome. '
    +'Retry sends the same request: it returns the recorded report if the export was recorded, and otherwise exports once. Discarding the request leaves any recorded export in place.';
}

// The Usage tab of Settings › AI (docs/ai-assistant.md §9): a report reads and records nothing; an export records the
// report it carries in the audit this tab lists, so like a change its outcome can be unknown.
export function setupUsageSettings(options:{api:Api;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;zone:()=>string;identity:()=>string}){
  const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
  const select=(id:string)=>get<HTMLSelectElement>(id),form=get<HTMLFormElement>('ai-usage-form');
  const instant=(at:string)=>displayInstant(at,options.zone());
  const pending=new PendingCommand(knownUsageRejection);
  const storage=()=>`openetrm.ai.usage.request.${options.identity()}`;
  let directory:Directory|undefined,shown:Report|undefined,generation=0;

  function store(){
    try{if(pending.command)sessionStorage.setItem(storage(),JSON.stringify(pending.command));else sessionStorage.removeItem(storage());}catch{/* The in-memory request is retained for this visit. */}
  }
  function stored():RequestCommand|undefined{
    try{
      const v=JSON.parse(sessionStorage.getItem(storage())??'null');
      return v&&typeof v.operationId==='string'&&v.path===exportPath&&v.body&&typeof v.body==='object'&&Object.keys(v.body).sort().join()==='by,from,to,zone'
        &&Object.values(v.body).every(value=>typeof value==='string')&&Object.hasOwn(groupings,v.body.by)
        ?{operationId:v.operationId,path:v.path,body:v.body,uncertain:true}:undefined;
    }catch{return undefined;/* Invalid browser state cannot grant authority. */}
  }
  const entry=()=>usageRequest({zone:select('ai-usage-zone').value,from:get<HTMLInputElement>('ai-usage-from').value,to:get<HTMLInputElement>('ai-usage-to').value,
    by:select('ai-usage-by').value});
  // The zone starts as Profile's and then stays as chosen.
  function fillZones(){
    const node=select('ai-usage-zone'),chosen=node.value||options.zone();
    node.replaceChildren(...zoneChoices(chosen).map(z=>new Option(z.name,z.value)));node.value=chosen;
  }
  function renderReport(){
    const host=get('ai-usage-sections');
    for(const panel of host.querySelectorAll<HTMLElement>('.chart'))clearChart(panel);
    host.replaceChildren();get('ai-usage-report').hidden=!shown;get('ai-usage-bounds').textContent='';
    if(!shown)return;
    const report=shown,labels=sectionLabels(report.currencies);
    get('ai-usage-bounds').textContent=publicText(reportBounds(report,instant));get('ai-usage-empty').hidden=report.currencies.length>0;
    for(const [index,section] of report.currencies.entries()){
      const part=document.createElement('section'),heading=document.createElement('h3'),summary=document.createElement('ul'),chart=document.createElement('div');
      const note=document.createElement('p'),table=document.createElement('div');
      part.className='ai-usage-currency';heading.textContent=publicText(labels[index]);summary.className='ai-usage-summary';chart.className='chart';
      note.className='field-help';note.textContent=withoutCostNote(section);note.hidden=!note.textContent;table.className='table-wrap';
      summary.append(...usageSummary(section).map(line=>{const item=document.createElement('li');item.textContent=publicText(line);return item;}));
      part.append(heading,summary,chart,note,table);host.append(part);
      renderChart(chart,dailyChart(section,labels[index]));
      const {headers,rows,numeric}=usageTable(report,section);
      renderRegularTable(table,`ai-usage-${index}`,JSON.stringify([options.identity(),currencyKey(section.currency),report.by]),headers,rows,numeric,options.busy);
    }
  }
  function renderAudit(){
    if(directory)renderRegularTable(get('ai-usage-audit'),'ai-usage-audit',options.identity(),['Exported','By','Time zone','From','To, not included','Grouped by'],
      exportRows(directory.audit,instant),[],options.busy);
  }
  function render(){renderReport();renderAudit();}
  // The form's choices outlast a directory that could not be read; only an end of access removes them.
  function clear(){directory=undefined;get('ai-usage-audit').replaceChildren();}
  // Nothing an identity entered or was shown outlives its access.
  function wipe(){
    clear();shown=undefined;renderReport();form.reset();select('ai-usage-zone').replaceChildren();
    select('ai-usage-by').replaceChildren(new Option('Choose a grouping…',''));get('ai-usage-range').textContent='';
  }
  function sync(){
    const busy=options.busy(),held=!!pending.command;
    for(const node of form.querySelectorAll<HTMLInputElement|HTMLSelectElement|HTMLButtonElement>('input,select,button'))node.disabled=busy;
    // Without a current directory, only a retained export may be resent.
    get<HTMLButtonElement>('ai-usage-export').disabled=busy||held||!directory;
    get('ai-usage-pending').hidden=!held;get('ai-usage-pending-text').textContent=pending.command?exportPending(pending.command.body):'';
  }
  // A refusal the page can name is stated in Usage's terms; an end of access keeps nothing.
  function refused(error:unknown):never{
    const code=(error as {code?:string}).code;
    if(code==='access_denied')wipe();
    const message=usageMessage(code);
    throw message?Object.assign(Error(message),{code}):error;
  }
  async function load(){
    const request=++generation,identity=options.identity();
    clear();
    let next:Directory;
    try{next=await options.api<Directory>('settings/ai/usage');}
    catch(error){if(request===generation)refused(error);throw error;}
    if(request!==generation||identity!==options.identity())return;
    if(!Number.isSafeInteger(next.maxDays))throw inconsistent();
    const groupedBy=next.dimensions.map(by=>new Option(groupingName(by),by)),by=select('ai-usage-by'),chosen=by.value;
    directory=next;fillZones();
    by.replaceChildren(new Option('Choose a grouping…',''),...groupedBy);by.value=next.dimensions.includes(chosen)?chosen:'';
    get('ai-usage-range').textContent=`Up to ${grouped(next.maxDays)} days, from the first day up to the day after the last, which is not included.`;
    renderAudit();sync();
  }
  async function report(){
    const request=entry(),current=generation,identity=options.identity();
    shown=undefined;renderReport();
    let answer:Report;
    try{answer=await options.api<Report>('settings/ai/usage/report',request);}
    catch(error){if(current===generation)refused(error);throw error;}
    if(current!==generation||identity!==options.identity())return;
    shown=readableReport(answer);renderReport();
  }
  // Once sent, an export keeps its identity until it is confirmed, refused before any write, or discarded; a repeat
  // answers with the report the export recorded.
  async function send(){
    const command=pending.command,identity=options.identity();if(!command)return;
    const previouslyUncertain=pending.sending();store();
    let answer:Report;
    try{
      answer=await options.api<Report>(command.path,{...command.body,operationId:command.operationId});
      if(identity!==options.identity()||pending.command!==command)return;
    }catch(error){
      if(identity!==options.identity()||pending.command!==command)return;
      pending.failed((error as {code?:string}).code,previouslyUncertain);store();
      // After an unconfirmed outcome the audit shows whether the export was recorded.
      if((error as {code?:string}).code!=='access_denied'&&pending.command)await load().catch(()=>undefined);
      sync();refused(error);
    }
    // The file is built before the request is released, so a report the page cannot read keeps it for a later Retry.
    const exported=readableReport(answer),csv=usageCsv(exported,command.operationId);
    pending.clear();store();
    downloadCsv('leanetrm-usage.csv',csv);
    shown=exported;renderReport();
    await load();
  }

  get('ai-usage-refresh').onclick=()=>void options.act(load);
  form.onsubmit=event=>{event.preventDefault();void options.act(report);};
  get('ai-usage-export').onclick=()=>void options.act(async()=>{
    if(pending.command)throw Error('Retry or discard the retained export before exporting again.');
    if(!directory)throw Error('Refresh AI settings to confirm your current access before exporting.');
    pending.begin(exportPath,entry());store();await send();
  });
  get('ai-usage-retry').onclick=()=>void options.act(send);
  get('ai-usage-discard').onclick=()=>{if(options.busy())return;pending.clear();store();sync();};
  return {load,sync,unconfirmed:()=>!!pending.command,
    refresh(){if(directory)fillZones();render();sync();},
    reset(){
      generation++;wipe();
      // This identity's unconfirmed export stays offered whether or not its directory can be read.
      pending.command=stored();sync();
    }};
}
