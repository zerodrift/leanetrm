import {node,table} from './risk-report-elements.js';
import {amountText,prepareAmountDisplay} from './amount-display.js';
import {actorTransport,PendingCommand,type RequestCommand} from './pending-command.js';
import {knownRiskRejection} from './risk-scenarios.js';
import {clearChart,renderChart} from './chart-view.js';
import type {HistoryRequest} from './factor-history.js';
import {sameUtcInstant} from './user-time.js';
import {canonicalEconomic,restoreEconomicCommand,validateEconomicReport,requireEconomicCalculationReceipt,type EconomicChoice,type EconomicSetup,type EconomicSourceRequest,
 type EconomicSources,type EconomicHistory,type EconomicRequest,type EconomicReport,type EconomicJob,type EconomicSpec} from './economic-risk.js';

type Options={api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;canRun:()=>boolean;
 identity:()=>string;scope:()=>EconomicSourceRequest['scope'];instant:(at:string)=>string;zone:()=>string;unconfirmed:()=>boolean;
 onShow:(jobId?:string)=>void;changed:()=>void};

/** Financial choices and the complete branch universe come from the managed source adapter. */
export function setupEconomicRisk(host:HTMLElement,resultHost:HTMLElement,o:Options){
 const form=node('form'),fields=node('fieldset'),sourceFields=node('div'),riskFields=node('div'),note=node('p'),sourceSummary=node('p'),coverage=node('div'),pendingHost=node('div');
 form.dataset.economic='form';fields.dataset.economic='fields';sourceFields.dataset.economic='source-fields';sourceSummary.dataset.economic='source-summary';pendingHost.dataset.economic='pending';note.dataset.economic='note';note.setAttribute('role','status');
 sourceFields.className=riskFields.className='factor-history-fields';pendingHost.className='actions';
 const select=(parent:HTMLElement,key:string,label:string,required=true)=>{const wrap=node('label',label),control=node('select');control.dataset.economic=key;control.required=required;control.append(new Option('Choose…',''));wrap.append(control);parent.append(wrap);return control;};
 const input=(parent:HTMLElement,key:string,label:string,type='text')=>{const wrap=node('label',label),control=node('input');control.dataset.economic=key;control.type=type;control.required=true;control.autocomplete='off';wrap.append(control);parent.append(wrap);return control;};
 const button=(key:string,label:string,submit=false)=>{const control=node('button',label);control.dataset.economic=key;control.type=submit?'submit':'button';return control;};
 const configureButton=button('configure','Read event-aware setup'),sourceButton=button('prepare','Prepare complete source');
 const pricing=select(sourceFields,'pricing','Holdings and valuation profile'),session=select(sourceFields,'session','Next-close schedule');
 const path=select(sourceFields,'price-path','Market path to the next close'),disposition=select(sourceFields,'cash-disposition','Scheduled cash'),accumulation=select(sourceFields,'cash-accumulation','Cash value at the horizon');
 const expiry=select(sourceFields,'input-expiry','Expiring market inputs'),physical=select(sourceFields,'physical-delivery','New physical delivery obligations');
 const gas=node('details'),gasBody=node('div');gas.append(node('summary','Saved gas account projection profiles'),node('p','Select profiles for required owned-gas accounts and any explicitly linked supply or inventory destinations. The backend checks the complete scope.'),gasBody);gasBody.className='factor-history-fields';
 const method=select(riskFields,'method','Event-aware method'),confidence=input(riskFields,'confidence','Confidence · decimal, for example 0.99');confidence.inputMode='decimal';
 const historyFields=node('div');historyFields.className='factor-history-fields';historyFields.dataset.economic='history-fields';
 const calendar=select(historyFields,'calendar','History business calendar'),from=input(historyFields,'from','First history date · inclusive','date'),to=input(historyFields,'to','End history date · exclusive','date');
 const close=select(historyFields,'history-close','History close · selected calendar clock'),priceType=select(historyFields,'price-type','Allowed history price type'),minimum=input(historyFields,'minimum','Minimum complete changes','number');minimum.min='2';minimum.step='1';
 const simulationFields=node('div');simulationFields.className='factor-history-fields';simulationFields.dataset.economic='simulation-fields';
 const drift=select(simulationFields,'drift','Market-move mean'),paths=input(simulationFields,'paths','Simulation paths','number'),seed=input(simulationFields,'seed','Reproducible random seed');paths.min='2';paths.step='1';seed.inputMode='numeric';
 const preview=button('history','Prepare complete price history'),run=button('run','Run event-aware VaR / ES',true),queue=button('queue','Queue event-aware job',true);queue.className=preview.className=sourceButton.className='secondary';
 const actions=node('div');actions.className='actions';actions.append(preview,run,queue);
 fields.append(sourceFields,gas,sourceButton,sourceSummary,riskFields,historyFields,simulationFields,actions);form.append(fields,note,pendingHost);
 host.replaceChildren(node('h3','Event-aware economic VaR / ES'),node('p','Revalue the full portfolio through the next configured close, including contractual events, surviving holdings and cash. Every path uses the same horizon. Market paths use the selected contract curves; captured rates and option volatility stay fixed.'),configureButton,form,coverage);
 let epoch=0,owner='',scopeStamp='',configuration:EconomicSetup|undefined,source:EconomicSources|undefined,history:EconomicHistory|undefined;
 let report:EconomicReport|undefined,job:EconomicJob|undefined,timer:ReturnType<typeof setTimeout>|undefined,chart:HTMLElement|undefined,dirty=false,allowed=false;
 const gasInputs=new Map<string,HTMLSelectElement>(),commands=new Map<string,PendingCommand>(),transports=new Map<string,ReturnType<typeof actorTransport<RequestCommand>>>();
 function transport(actor:string){let saved=transports.get(actor);if(!saved){saved=actorTransport(()=>`openetrm.economic-risk.submission:${actor}`,()=>sessionStorage);transports.set(actor,saved);}return saved;}
 function pending(actor=o.identity()){let command=commands.get(actor);if(!command){command=new PendingCommand(knownRiskRejection);command.command=restoreEconomicCommand(transport(actor).read());commands.set(actor,command);}return command;}
 function persist(actor:string){transport(actor).write(pending(actor).command);}
 const current=(version:number,actor=owner)=>version===epoch&&actor===o.identity()&&owner===actor;
 const sameScope=()=>{try{return !!configuration&&canonicalEconomic(o.scope())===scopeStamp;}catch{return false;}};
 const fill=(control:HTMLSelectElement,choices:EconomicChoice[])=>control.replaceChildren(new Option('Choose…',''),...choices.map(c=>new Option(c.label,c.value)));
 function chosen(control:HTMLSelectElement,choices:EconomicChoice[],label:string){const item=choices.find(c=>c.value===control.value);if(!item)throw Error('Choose '+label+'.');return item.value;}
 function stop(){if(timer!==undefined)clearTimeout(timer);timer=undefined;}
 function clearReport(){stop();if(chart)clearChart(chart);chart=undefined;report=undefined;job=undefined;resultHost.replaceChildren();resultHost.hidden=true;}
 function invalidateSource(){epoch++;source=undefined;history=undefined;sourceSummary.textContent='';coverage.replaceChildren();method.replaceChildren(new Option('Prepare the complete source first…',''));dirty=true;describe();sync();o.changed();}
 function invalidateHistory(){epoch++;history=undefined;coverage.replaceChildren();dirty=true;describe();sync();o.changed();}
 function describe(){
  const selected=configuration?.risk.methods.find(m=>m.value===method.value);
  historyFields.hidden=!source||!selected?.history;simulationFields.hidden=!source||!selected?.simulation;
  preview.hidden=!source||!selected?.history;
  for(const control of historyFields.querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))control.disabled=historyFields.hidden;
  for(const control of simulationFields.querySelectorAll<HTMLInputElement|HTMLSelectElement>('input,select'))control.disabled=simulationFields.hidden;
  gas.hidden=!configuration?.gasAccounts.length||!configuration.pricing.find(p=>p.value===pricing.value)?.policy.gasOwned;
 }
 function drawPending(){
  const command=pending().command;pendingHost.replaceChildren();pendingHost.hidden=!command;
  if(!command)return;
  const retry=button('retry','Retry exact submission'),check=button('reconcile','Check recent jobs');check.className='secondary';
  retry.disabled=check.disabled=o.busy()||!o.canRun();retry.onclick=()=>void o.act(()=>send(command,o.identity()));check.onclick=()=>void o.act(reconcile);
  pendingHost.append(node('span','This account has an unconfirmed event-aware submission. Its original source and choices are retained.'),retry,check);
 }
 function sync(available?:boolean){
  if(available!==undefined)allowed=available;
  const locked=o.busy()||!allowed||!o.canRun()||!!pending().command||o.unconfirmed();
  configureButton.disabled=o.busy()||!allowed||!o.canRun()||!!pending().command||o.unconfirmed();fields.disabled=locked||!configuration;
  sourceButton.disabled=locked||!configuration;run.disabled=queue.disabled=locked||!source||!sameScope()||!method.value;
  preview.disabled=locked||!source||!method.value;drawPending();
 }
 function reset(){
  epoch++;clearReport();configuration=undefined;source=undefined;history=undefined;owner=o.identity();scopeStamp='';dirty=false;gasInputs.clear();gasBody.replaceChildren();coverage.replaceChildren();form.reset();
  for(const control of sourceFields.querySelectorAll<HTMLSelectElement>('select'))control.replaceChildren(new Option('Choose…',''));
  method.replaceChildren(new Option('Prepare the complete source first…',''));calendar.replaceChildren(new Option('Choose…',''));sourceSummary.textContent='';note.textContent='Read setup for the selected Risk scope.';describe();sync();
 }
 async function configure(){
  if(pending().command||o.unconfirmed())throw Error('Resolve the unconfirmed risk submission before preparing a new run.');
  const selected=structuredClone(o.scope());reset();owner=o.identity();scopeStamp=canonicalEconomic(selected);const version=epoch;
  note.textContent='Reading source-known schedules and supported policies…';
  try{
   const c=await o.api<EconomicSetup>('portfolio/risk/economic/setup',selected);if(!current(version)||canonicalEconomic(o.scope())!==scopeStamp)return;
   if(c.context?.contextId!==selected.context.contextId||!Array.isArray(c.sessions)||!Array.isArray(c.pricing)||!c.projection?.physicalDelivery?.length||!c.convention||!c.numericalPolicy)throw Error('The event-aware setup does not match the selected source. Refresh Risk.');
   configuration=c;fill(pricing,c.pricing);fill(session,c.sessions.map(s=>({value:JSON.stringify([s.code,s.version]),label:`${s.name} · version ${s.version}`})));
   fill(path,c.projection.pricePaths);fill(disposition,c.projection.cashDisposition);fill(accumulation,c.projection.cashAccumulation);fill(expiry,c.projection.inputExpiry);fill(physical,c.projection.physicalDelivery);
   fill(drift,c.risk.drifts);paths.max=String(c.risk.limits.maxPaths);
   for(const account of c.gasAccounts){const control=select(gasBody,'gas-profile',account.book+' · '+account.code,false);control.dataset.account=account.accountId;
    control.replaceChildren(new Option('No profile selected',''),...account.profiles.map(p=>new Option(`${p.label} · version ${p.revision} · ${o.instant(p.effectiveAt)}`,JSON.stringify([p.profileId,p.revision]))));
    control.onchange=invalidateSource;gasInputs.set(account.accountId,control);}
   note.textContent=`Opening ${o.instant(c.asOf)} · known as of ${o.instant(c.context.knownAt)} · ${o.zone()}. Select each financial treatment, then prepare the complete source.`;
   describe();
  }catch(error){if(current(version)){configuration=undefined;note.textContent=error instanceof Error?error.message:'Event-aware setup is unavailable.';}throw error;}finally{if(current(version))sync();}
 }
 function sourceRequest():EconomicSourceRequest{
  const c=configuration;if(!c||!sameScope()||owner!==o.identity())throw Error('The opening scope changed. Read event-aware setup again.');
  const price=c.pricing.find(p=>p.value===chosen(pricing,c.pricing,'a holdings and valuation profile'))!;
  const selected=c.sessions.find(s=>JSON.stringify([s.code,s.version])===session.value);if(!selected)throw Error('Choose the next-close schedule.');
  const gasOwned=price.policy.gasOwned?c.gasAccounts.flatMap(a=>{const control=gasInputs.get(a.accountId)!;if(!control.value)return[];const profile=a.profiles.find(p=>JSON.stringify([p.profileId,p.revision])===control.value);if(!profile)throw Error('Reread the selected gas account profile.');return[{accountId:a.accountId,profileId:profile.profileId,revision:profile.revision}];}):[];
  const scope=structuredClone(c.scope);scope.bookIds.sort();scope.portfolioIds.sort();scope.tradeIds?.sort();
  return {scope,pricingPolicy:structuredClone(price.policy),projection:{implementation:c.projection.implementation,session:selected.code,sessionVersion:selected.version,
   horizonPolicy:c.projection.horizonPolicy,rateTimingPolicy:c.projection.rateTimingPolicy,eventMarketPolicy:c.projection.eventMarketPolicy,gasOwned,
   policy:{...c.projection.policy,pricePath:chosen(path,c.projection.pricePaths,'a market path'),cashDisposition:chosen(disposition,c.projection.cashDisposition,'a scheduled cash treatment'),
    cashAccumulation:chosen(accumulation,c.projection.cashAccumulation,'a cash accumulation treatment'),inputExpiry:chosen(expiry,c.projection.inputExpiry,'an input-expiry treatment'),
    physicalDelivery:chosen(physical,c.projection.physicalDelivery,'a physical delivery treatment')}}};
 }
 async function prepare(){
  const request=sourceRequest(),version=epoch;history=undefined;source=undefined;coverage.replaceChildren();sourceSummary.textContent='Preparing every transition and surviving-holding dependency…';sync();
  try{
   const read=await o.api<EconomicSources>('portfolio/risk/economic/sources',request);if(!current(version)||!sameScope())return;
   if(read.engineVersion!==configuration!.engineVersion||canonicalEconomic(read.source)!==canonicalEconomic(request)||read.horizon?.contextId!==configuration!.context.contextId||!sameUtcInstant(read.horizon.openingAt,configuration!.asOf)||read.horizon.closes?.length!==1||!Array.isArray(read.factors)||!Array.isArray(read.methods))throw Error('The complete source receipt does not match these choices or calculation version. Read setup again.');
   source=read;sourceSummary.textContent=`Common horizon: ${o.instant(read.horizon.closes[0].instant)} · ${o.zone()} · ${read.factors.length} required market factors. All transition and residual branches are included.`;
   fill(method,configuration!.risk.methods.filter(m=>read.methods.includes(m.value)));
   fill(calendar,(read.history.calendars??[]).map(c=>({value:JSON.stringify(c.reference),label:c.label+' · '+(c.clockRule.ianaZone??c.clock)})));
   fill(priceType,(read.history.priceTypes??[]).map(t=>({value:t.code,label:t.label})));fill(close,read.history.guided?[{value:read.history.guided.closeTime,label:read.history.guided.closeTime+' · selected calendar clock'}]:[]);
   coverage.append(table(['Required factor','Native units'],read.factors.map(f=>[f.label,f.unit])));note.textContent='The complete source is ready. Choose an offered method and confidence. Historical and Monte Carlo methods require complete aligned history.';
   describe();
  }catch(error){if(current(version)){source=undefined;sourceSummary.textContent='';note.textContent=error instanceof Error?error.message:'Complete source preparation failed.';}throw error;}finally{if(current(version))sync();}
 }
 function historyRequest():HistoryRequest{
  const s=source;if(!s||!sameScope())throw Error('Prepare the matching complete source first.');
  const c=s.history.calendars.find(c=>JSON.stringify(c.reference)===calendar.value);if(!c)throw Error('Choose the history business calendar.');
  const count=Number(minimum.value);if(!minimum.value||!Number.isSafeInteger(count))throw Error('Enter the minimum complete daily changes.');
  if(!from.value||!to.value)throw Error('Choose both history dates.');
  if(close.value!==s.history.guided.closeTime)throw Error('Choose the configured history close time.');
  const selectedType=chosen(priceType,s.history.priceTypes.map(t=>({value:t.code,label:t.label})),'an allowed history price type');
  return {scope:structuredClone(s.source.scope),pricingPolicy:structuredClone(s.source.pricingPolicy),policy:{...s.history.guided.profile,minimumSamples:count,priceTypes:[selectedType]},
   calendar:c.reference,from:from.value,to:to.value,closeTime:close.value,ambiguousTime:s.history.clockPolicy.ambiguousTime,invalidTime:s.history.clockPolicy.invalidTime};
 }
 async function captureHistory():Promise<EconomicHistory|undefined>{
  const s=source;if(!s)throw Error('Prepare the complete source first.');const request=historyRequest(),version=epoch;
  note.textContent='Capturing aligned history for every required factor…';
  const value=await o.api<EconomicHistory>('portfolio/risk/economic/history',{source:s.source,history:request});if(!current(version)||!sameScope())return;
  const offered=new Map(s.factors.map(f=>[f.id,f]));
  if(value.engineVersion!==s.engineVersion||offered.size!==s.factors.length||canonicalEconomic(value.source)!==canonicalEconomic(s.source)||canonicalEconomic(value.horizon)!==canonicalEconomic(s.horizon)||
   value.manifest?.id!==value.request?.expectedManifestId||!/^[0-9a-f]{64}$/.test(value.manifest.id)||
   canonicalEconomic({...value.request,expectedManifestId:undefined})!==canonicalEconomic(request)||canonicalEconomic(value.manifest.policy)!==canonicalEconomic(value.request.policy)||
   value.manifest.forecast.contextId!==s.source.scope.context.contextId||!sameUtcInstant(value.manifest.forecast.valuationAt,configuration!.asOf)||!sameUtcInstant(value.manifest.forecast.knownAt,configuration!.context.knownAt)||
   value.coverage?.factors!==s.factors.length||value.factors?.length!==s.factors.length||value.columns?.length!==s.factors.length||
   value.manifest.columns?.length!==value.columns.length||new Set(value.columns.map(c=>c.factor?.id)).size!==offered.size||
   value.columns.some((column,i)=>column.id!==value.manifest.columns[i].id||!offered.has(column.factor?.id)||
    canonicalEconomic(column.factor.factor)!==canonicalEconomic(offered.get(column.factor.id)!.factor)||canonicalEconomic(value.factors[i])!==canonicalEconomic(column.factor.factor)))
   throw Error('The captured history does not cover this exact complete source. Prepare it again.');
  history=value;note.textContent=value.coverage.available?`Aligned history ready: ${value.coverage.factors} factors · ${value.coverage.samples} complete changes · ${value.manifest.rejected.length} rejected history intervals retained in the evidence.`:
   'Complete aligned history is unavailable: '+(value.coverage.unavailableReason??'Review the selected dates, calendar and price type.');
  return value;
 }
 async function request():Promise<EconomicRequest|undefined>{
  const s=source,c=configuration;if(!s||!c||!sameScope())throw Error('Prepare the matching complete source.');
  const selected=c.risk.methods.find(m=>m.value===method.value&&s.methods.includes(m.value));if(!selected)throw Error('Choose an offered event-aware method.');
  if(!confidence.value.trim())throw Error('Enter a confidence level.');
  const version=epoch;let captured:EconomicHistory|undefined;
  if(selected.history){captured=history??await captureHistory();if(!captured||!current(version)||!sameScope())return;if(!captured.coverage.available)throw Error('Every branch factor needs complete aligned history before running economic risk.');}
  const simulation=selected.simulation?{...c.risk.simulation,confidence:confidence.value.trim(),drift:chosen(drift,c.risk.drifts,'a market-move mean'),paths:Number(paths.value),seed:seed.value.trim()}:null;
  if(simulation&&(!paths.value||!Number.isSafeInteger(simulation.paths)||!/^\d+$/.test(simulation.seed)))throw Error('Enter a whole path count and explicit reproducible seed.');
  const economic:EconomicSpec={projection:structuredClone(s.source.projection),policy:{implementation:selected.value,confidence:confidence.value.trim(),quantilePolicy:c.risk.quantilePolicy,tailPolicy:c.risk.tailPolicy},
   history:captured?.request??null,simulation,factors:captured?.factors??[]};
  return {...structuredClone(s.source.scope),pricingPolicy:structuredClone(s.source.pricingPolicy),convention:c.convention,numericalPolicy:c.numericalPolicy,detail:'holdings',bumps:[],stress:[],economic};
 }
 async function show(value:EconomicReport,receipt?:EconomicJob){
  validateEconomicReport(value);const actor=o.identity(),version=epoch;
  await prepareAmountDisplay({holdings:value.holdings,economic:value.result.economic,diagnostic:value.result.marketOnlyDiagnostic},body=>o.api<string[]>('amounts/display',body)).catch(()=>undefined);
  if(version!==epoch||actor!==o.identity())return;
  report=value;job=receipt;if(!receipt)dirty=false;draw();o.onShow(receipt?.id);o.changed();
 }
 function draw(){
  if(chart)clearChart(chart);chart=undefined;resultHost.replaceChildren();resultHost.hidden=!report&&!job;if(!report&&!job)return;
  resultHost.append(node('h3','Event-aware economic VaR / ES'));
  if(job){resultHost.append(node('p','Retained job · '+job.state));if(job.error)resultHost.append(node('p',job.error.message));}
  if(!report)return;
  const r=report.result;resultHost.append(node('p',`Opening ${o.instant(report.asOf)} · horizon ${o.instant(r.horizon)} · known as of ${o.instant(report.context.knownAt)} · ${o.zone()}`),
   node('p',r.samples===undefined?'Deterministic time and carry · complete preparation found no variable factors.':`${r.samples} complete full-revaluation paths · ${report.factors.length} factors. A failed path stops the complete run.`),
   node('p','Economic loss compares horizon residual value plus horizon-valued period cash with opening value. Signed gains can produce negative VaR and expected shortfall.'));
  const currencyName=(ref:unknown)=>report!.holdings.find(h=>canonicalEconomic(h.currencyReference)===canonicalEconomic(ref))?.currency??'Currency';
  resultHost.append(table(['Currency','Economic VaR','Economic expected shortfall','Mean economic loss'],r.economic.map(p=>[currencyName(p.currency),amountText(p.valueAtRisk,'money'),amountText(p.expectedShortfall,'money'),amountText(p.mean,'money')])));
  resultHost.append(node('h4','No-shock time and carry'),table(['Holding','Book','Currency','Opening value','Horizon residual','Period cash at horizon','Economic P&L'],report.holdings.map(h=>[
   h.reference+(h.instrument?' · '+h.instrument:''),h.book,h.currency,amountText(h.openingValue,'money'),amountText(h.residualValue,'money'),amountText(h.periodCash,'money'),amountText(h.economicPnl,'money')])));
  const diagnostic=node('details');diagnostic.append(node('summary','Market-only diagnostic · compared with no-shock horizon wealth'),node('p','This separate diagnostic removes the displayed time and carry from its comparison value. The economic VaR and expected shortfall above retain them.'),
   table(['Currency','Market-only VaR','Market-only expected shortfall'],r.marketOnlyDiagnostic.map(p=>[currencyName(p.currency),amountText(p.valueAtRisk,'money'),amountText(p.expectedShortfall,'money')])));resultHost.append(diagnostic);
  const contributions=node('details');contributions.append(node('summary','Economic tail contributions by book and owner'));
  for(const partition of r.economic){
   contributions.append(node('h4',currencyName(partition.currency)),table(['Book','VaR contribution','Expected shortfall contribution'],partition.books.map(b=>[
    report!.books.find(book=>book.id===b.id)?.name??b.id,amountText(b.valueAtRisk,'money'),amountText(b.expectedShortfall,'money')])),
    table(['Economic owner','VaR contribution','Expected shortfall contribution'],partition.trades.map(t=>[
     report!.holdings.find(h=>canonicalEconomic(h.owner.owner)===canonicalEconomic(t.owner))?.reference??t.id,amountText(t.valueAtRisk,'money'),amountText(t.expectedShortfall,'money')])));
  }resultHost.append(contributions);
  const selected=node('select');selected.setAttribute('aria-label','Economic loss distribution currency');selected.append(...r.economic.map((p,i)=>new Option(currencyName(p.currency),String(i))));
  const plot=node('div');plot.className='chart risk-result-chart';chart=plot;
  const graph=()=>{const partition=r.economic[Number(selected.value)];renderChart(plot,{title:'Economic loss distribution · '+currencyName(partition.currency),subtitle:'Complete backend full-revaluation loss observations',axis:'category',categoryTitle:'Economic loss interval',unit:'Paths',kind:'bar',series:[{id:'economic-losses',name:'Paths',points:partition.histogram.map((b,i)=>({x:String(i),label:`${amountText(b.lower,'money')} to ${amountText(b.upper,'money')}`,y:String(b.count),text:String(b.count)}))}]});};
  selected.onchange=graph;resultHost.append(selected,plot);graph();
  const evidence=node('details'),data=node('pre');evidence.append(node('summary','Captured policies and event-source evidence'),data);
  const captured=report;evidence.ontoggle=()=>{if(evidence.open&&!data.textContent)data.textContent=JSON.stringify({request:captured.request,horizon:captured.horizon,products:captured.products,optionCalibrations:captured.optionCalibrations},null,2);};resultHost.append(evidence);
 }
 function schedule(){stop();if(!job||!['queued','running','cancellation_requested'].includes(job.state))return;const actor=o.identity(),id=job.id;timer=setTimeout(()=>{
  if(actor!==o.identity()||job?.id!==id)return;if(o.busy()){schedule();return;}void o.act(()=>openJob(id)).catch(()=>undefined);
 },2000);}
 async function openJob(id:string,anchor?:EconomicJob){
  const actor=o.identity(),version=epoch;const read=anchor??await o.api<EconomicJob>('portfolio/risk/jobs/'+encodeURIComponent(id));if(version!==epoch||actor!==o.identity())return;
  if(read.id!==id||read.kind!=='economic_lifecycle')throw Error('Open the complete event-aware job under its captured context.');
  clearReport();job=read;if(read.result)await show(read.result,read);else{draw();o.onShow(read.id);o.changed();}schedule();
 }
 async function send(command:RequestCommand,actor:string){
  if(actor!==o.identity()||!o.canRun())throw Error('Restore this submission’s account and risk permission before retrying.');
  const state=pending(actor),wasUncertain=state.sending(),version=epoch;let accepted=false;persist(actor);sync();
  try{
   const receipt=await o.api<EconomicJob>('portfolio/risk/jobs',{requestId:command.operationId,request:command.body.request});
   if(receipt.requestId!==command.operationId||receipt.kind!=='economic_lifecycle'||!receipt.id)throw Error('The job receipt does not match the original submission.');
   accepted=true;state.clear();persist(actor);if(actor!==o.identity()||version!==epoch)return;dirty=false;
   await openJob(receipt.id,receipt);
  }catch(error){if(!accepted){state.failed((error as {code?:string}).code,wasUncertain);persist(actor);}if(actor===o.identity())note.textContent=accepted?'The job was accepted, but its report could not be read. Open it from Run history.':state.command?'Submission is unconfirmed. Retry its original request or check recent jobs.':'The submission was refused before acceptance.';throw error;}finally{if(actor===o.identity()){sync();o.changed();}}
 }
 async function reconcile(){
  const actor=o.identity(),command=pending(actor).command;if(!command)return;const version=epoch;
  const recent=await o.api<EconomicJob[]>('portfolio/risk/jobs');if(version!==epoch||actor!==o.identity())return;
  const match=recent.find(j=>j.requestId===command.operationId);if(!match){note.textContent='No matching job is in the current bounded directory. Absence does not prove failure; retry the exact original submission to resolve it.';return;}
  const receipt=await o.api<EconomicJob>('portfolio/risk/jobs/'+encodeURIComponent(match.id));if(version!==epoch||actor!==o.identity())return;
  if(receipt.requestId!==command.operationId||receipt.kind!=='economic_lifecycle')throw Error('The retained job does not match the pending economic submission.');
  pending(actor).clear();persist(actor);await openJob(receipt.id,receipt);sync();
 }
 async function calculate(queued:boolean){
  if(pending().command||o.unconfirmed())throw Error('Resolve the unconfirmed risk submission first.');
  const actor=o.identity(),version=epoch,body=await request();if(!body||!current(version,actor)||!sameScope())return;
  if(queued){const command=pending(actor).begin('portfolio/risk/jobs',{request:body});persist(actor);o.changed();await send(command,actor);}
  else{
   const prepared=source!,engine=configuration!.engineVersion;note.textContent='Revaluing every path through the common event horizon…';
   try{const value=await o.api<EconomicReport>('portfolio/risk',body);if(!current(version,actor)||!sameScope())return;
    requireEconomicCalculationReceipt(value,body,prepared,engine);await show(value);
   }catch(error){if(current(version,actor))note.textContent=error instanceof Error?error.message:'Economic revaluation failed.';throw error;}
  }
 }
 configureButton.onclick=()=>void o.act(configure);sourceButton.onclick=()=>void o.act(prepare);preview.onclick=()=>void o.act(async()=>{await captureHistory();});
 form.onsubmit=event=>{event.preventDefault();const queued=(event as SubmitEvent).submitter===queue;void o.act(()=>calculate(queued));};
 for(const control of [pricing,session,path,disposition,accumulation,expiry,physical])control.onchange=invalidateSource;
 for(const control of [method,confidence,calendar,from,to,close,priceType,minimum,drift,paths,seed])control.onchange=invalidateHistory;
 reset();
 function financialChanged(){
  epoch++;configuration=undefined;source=undefined;history=undefined;scopeStamp='';
  sourceSummary.textContent='';coverage.replaceChildren();method.replaceChildren(new Option('Read setup for the updated scope…',''));dirty=false;
  note.textContent='Lifecycle facts changed. Refresh Risk scope and prepare a new source. Captured reports and original submissions keep their inputs.';
  describe();sync(false);schedule();
 }
 return {configure,reset,sync,openJob,clearReport,financialChanged,shown:()=>!!report||!!job,canDownload:()=>!!report,jobId:()=>job?.id,edited:()=>dirty,dropEdits:()=>{dirty=false;},unconfirmed:()=>!!pending().command,
  refresh(){if(report)draw();sync();},download(){if(!report)throw Error('Open a complete event-aware report before exporting.');const url=URL.createObjectURL(new Blob([JSON.stringify(report,null,2)],{type:'application/json'}));const a=node('a');a.href=url;a.download='economic-risk-report.json';a.click();URL.revokeObjectURL(url);}};
}
