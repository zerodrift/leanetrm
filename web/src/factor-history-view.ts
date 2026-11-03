import type {ParametricRequest,ParametricCapabilities} from './parametric-risk.js';
import {node,table} from './risk-report-elements.js';
import {renderChart,clearChart} from './chart-view.js';
import {valuationReason} from './valuation-status.js';
import {validateFactorHistory,factorHistoryChart,type HistoryCapabilities,type HistoryScope,type HistoryRequest,type HistoryReport} from './factor-history.js';

type Options={api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string;scope:()=>HistoryScope;instant:(at:string)=>string;calculate:(request:ParametricRequest,metadata:{pricingPolicy:unknown;convention:string;numericalPolicy:string;context:HistoryReport['context'];asOf:string},queued:boolean)=>Promise<void>};
const reasons:Record<string,string>={missing_mark:'Missing mark',stale_mark:'Stale mark · no carry',asynchronous_close:'Off-close mark',source_role:'Different source or role',
 publication_state:'Ineligible latest publication state',ambiguous_revision:'Conflicting revision heads',mapping_regime:'Changed source mapping or units'};

/** Read-only history preparation; every financial selection comes from the backend profile and explicit form inputs. */
export function setupFactorHistory(o:Options){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const panel=get<HTMLDetailsElement>('risk-history-panel'),host=get('risk-history-body'),open=get<HTMLButtonElement>('risk-history-open');
 const form=node('form'),fields=node('fieldset'),grid=node('div'),message=node('p'),results=node('div'),actions=node('div');
 form.id='risk-history-form';fields.id='risk-history-fields';grid.className='factor-history-fields';results.id='risk-history-results';results.setAttribute('aria-live','polite');
 message.setAttribute('role','status');actions.className='actions';fields.append(grid);form.append(fields,message);host.append(form,results);
 const choose=(id:string,label:string)=>{const wrap=node('label',label),select=node('select');select.id='risk-history-'+id;select.required=true;select.append(new Option('Choose…',''));wrap.append(select);grid.append(wrap);return select;};
 const input=(id:string,label:string,type='text')=>{const wrap=node('label',label),control=node('input');control.id='risk-history-'+id;control.type=type;control.required=true;control.autocomplete='off';wrap.append(control);grid.append(wrap);return control;};
 const profile=choose('profile','History contract'),calendar=choose('calendar','Business-session calendar'),from=input('from','First session date · inclusive','date'),to=input('to','Window end date · exclusive','date'),
  close=input('close','Local close time · HH:mm:ss'),minimum=input('minimum','Minimum complete session changes');
 close.pattern='[0-9]{2}:[0-9]{2}:[0-9]{2}';close.placeholder='HH:mm:ss';minimum.inputMode='numeric';minimum.pattern='[0-9]+';
 const clockNote=node('p');clockNote.className='field-help';fields.append(clockNote,node('p','The selected contract uses raw dated factors, visible correction heads and exact calendar closes. It retains common complete adjacent-session changes. No carry, interpolation, proxy or rolling-contract substitution is applied. Ambiguous and nonexistent closes refuse.'));
 const run=node('button','Prepare aligned history'),download=node('button','Export history manifest');run.id='risk-history-run';run.type='submit';download.id='risk-history-export';download.type='button';download.className='secondary';actions.append(run,download);fields.append(actions);
 let generation=0,allowed=false,owner='',plot:HTMLElement|undefined,result:HistoryReport|undefined,selectedColumn:string|undefined;
 let configuration:{scope:HistoryScope;pricingPolicy:unknown;history:HistoryCapabilities;parametric:ParametricCapabilities;convention:string;numericalPolicy:string}|undefined;
 const statistics=node('form'),statsFields=node('fieldset'),statsGrid=node('div');statistics.id='risk-parametric-form';statsFields.id='risk-parametric-fields';statsGrid.className='factor-history-fields';statsFields.append(node('legend','Parametric VaR / ES'),statsGrid);statistics.append(statsFields);host.append(statistics);
 const statSelect=(id:string,label:string)=>{const wrap=node('label',label),select=node('select');select.id='risk-parametric-'+id;select.required=true;select.append(new Option('Choose…',''));wrap.append(select);statsGrid.append(wrap);return select;};
 const statInput=(id:string,label:string)=>{const wrap=node('label',label),input=node('input');input.id='risk-parametric-'+id;input.required=true;input.autocomplete='off';input.inputMode='decimal';wrap.append(input);statsGrid.append(wrap);return input;};
 const estimator=statSelect('profile','Statistical contract'),drift=statSelect('drift','Forecast drift'),confidence=statInput('confidence','Confidence · greater than 0.5, up to 0.9999'),ticks=statInput('ticks','Native trading ticks · integer 1–1000'),absolute=statInput('absolute','Delta agreement · absolute native tolerance'),relative=statInput('relative','Delta agreement · relative tolerance');
 const statActions=node('div');statActions.className='actions';for(const [id,text] of [['run','Run VaR / ES now'],['queue','Queue VaR / ES job']]){const button=node('button',text);button.id='risk-parametric-'+id;button.type='submit';statActions.append(button);}
 statsFields.append(node('p','Equal-weight sample covariance, centered about the sample mean. A frozen portfolio over one adjacent business session; signed upper loss VaR and ES. Native half/nominal/double finite differences must agree within your explicit tolerances. Each currency and valuation measure stays separate.'),statActions);statistics.hidden=true;
 function clearResult(text:string){if(plot)clearChart(plot);plot=undefined;result=undefined;results.replaceChildren();message.textContent=text;sync();}
 function sync(available?:boolean){if(available!==undefined)allowed=available;open.disabled=o.busy()||!allowed;fields.disabled=o.busy()||!allowed||!configuration;download.disabled=o.busy()||!result;statistics.hidden=!result;statsFields.disabled=o.busy()||!allowed||!result;}
 function reset(){generation++;configuration=undefined;owner='';selectedColumn=undefined;panel.open=false;form.reset();statistics.reset();estimator.replaceChildren(new Option('Choose…',''));drift.replaceChildren(new Option('Choose…',''));profile.replaceChildren(new Option('Choose…',''));calendar.replaceChildren(new Option('Choose…',''));clockNote.textContent='';clearResult('Choose a scope, then configure its aligned history.');}
 const current=(version:number)=>version===generation&&owner===o.identity();
 async function configure(){
  const scope=structuredClone(o.scope());panel.open=true;if(configuration)return;
  owner=o.identity();const version=++generation;clearResult('Reading held factors and available calendars…');
  try{
   const catalog=await o.api<{history:HistoryCapabilities;parametric:ParametricCapabilities;pricingPolicy:unknown;convention:string;numericalPolicy:string;context:{contextId:string}}>('portfolio/risk/catalog',scope);
   if(!current(version))return;
   if(!catalog.history?.profile||catalog.context.contextId!==scope.context.contextId)throw Error('This catalog does not offer aligned history for the captured scope. Refresh Risk.');
   configuration={scope,history:catalog.history,pricingPolicy:catalog.pricingPolicy,parametric:catalog.parametric,convention:catalog.convention,numericalPolicy:catalog.numericalPolicy};
   estimator.append(new Option('Delta-normal · sample n−1 covariance · one session',catalog.parametric.profile.convention));drift.append(new Option('Explicit zero drift',catalog.parametric.drifts.zero),new Option('Sample mean drift',catalog.parametric.drifts.sample));
   profile.append(new Option('Raw dated factors · exact closes · common complete rows',catalog.history.profile.convention));
   calendar.append(...catalog.history.calendars.map(c=>new Option(c.label+' · '+(c.clockRule.ianaZone??(c.clockRule.fixedOffsetSeconds===0?'UTC':c.clock)),JSON.stringify(c.reference))));
   message.textContent='Choose every history input explicitly. No VaR or expected-shortfall estimator runs in this step.';
  }catch(error){if(current(version))clearResult(error instanceof Error?error.message:'History configuration is unavailable.');throw error;}
  finally{sync();}
 }
 function request():HistoryRequest{
  if(!configuration||JSON.stringify(o.scope())!==JSON.stringify(configuration.scope)||owner!==o.identity())throw Error('The history scope or context changed. Refresh Risk and configure history again.');
  if(profile.value!==configuration.history.profile.convention)throw Error('Choose the explicit history contract.');
  const selected=configuration.history.calendars.find(c=>JSON.stringify(c.reference)===calendar.value),count=Number(minimum.value);
  if(!selected||!minimum.value||!Number.isSafeInteger(count))throw Error('Choose an available calendar and enter an explicit integer minimum.');
  return {scope:structuredClone(configuration.scope),pricingPolicy:configuration.pricingPolicy,policy:{...configuration.history.profile,minimumSamples:count},calendar:selected.reference,
   from:from.value,to:to.value,closeTime:close.value.trim(),ambiguousTime:configuration.history.clockPolicy.ambiguousTime,invalidTime:configuration.history.clockPolicy.invalidTime};
 }
 function draw(){
  if(plot)clearChart(plot);plot=undefined;results.replaceChildren();if(!result)return;
  const r=result,m=r.manifest,c=r.coverage;
  const status=node('div');status.className='risk-coverage';status.dataset.state=c.available?'complete':'incomplete';status.setAttribute('role','status');
  status.append(node('strong',c.available?'History ready for calibration':'History unavailable for calibration'),node('p',`${c.factors} dated factors · ${c.samples} complete session changes · ${c.rejected} rejected intervals · minimum ${c.minimumSamples}`));
  if(!c.portfolioComplete)status.append(node('p','The captured portfolio has excluded positions. Its complete risk scope is unavailable.'));
  results.append(node('h3','Aligned factor history'),node('p',`${o.instant(r.asOf)} · known at ${o.instant(m.forecast.knownAt)}`),
   node('p',`Captured scope · ${r.scope.bookIds.length} books · ${r.scope.tradeIds==null?'all trades in those books':`${r.scope.tradeIds.length} selected trade${r.scope.tradeIds.length===1?'':'s'}`}. One adjacent business-session change uses the selected calendar and pinned local close.`),status);
  if(r.excludedPositions.length){const list=node('ul');for(const p of r.excludedPositions)list.append(node('li',p.tradeReference+' · '+valuationReason(p.reason,p.instrumentType)));results.append(list);}
  if(!selectedColumn||!r.columns.some(c=>c.id===selectedColumn))selectedColumn=r.columns[0].id;
  const label=node('label','Dated factor shown '),column=node('select');column.setAttribute('aria-label','History factor');column.append(...r.columns.map(c=>new Option(c.factor.label+' · '+c.factor.unit,c.id)));column.value=selectedColumn;
  column.onchange=()=>{selectedColumn=column.value;draw();results.querySelector<HTMLSelectElement>('[aria-label="History factor"]')?.focus();};label.append(column);results.append(label);
  const index=r.columns.findIndex(c=>c.id===selectedColumn),unit=r.columns[index].factor.unit;
  if(m.samples.length){plot=node('div');plot.className='chart risk-result-chart';results.append(plot);renderChart(plot,factorHistoryChart(r,selectedColumn));}
  else results.append(node('p','No complete interval is available. Missing or off-close inputs are not zero changes.'));
  results.append(table(['From session','To session','From mark','To mark','Exact absolute change','Native unit'],m.samples.map(s=>[o.instant(s.from.at),o.instant(s.to.at),s.from.marks[index]!.value,s.to.marks[index]!.value,s.changes[index],unit])));
  if(m.rejected.length)results.append(node('h4','Rejected interval diagnostics'),table(['Interval','Factor','Endpoint','Reason'],m.rejected.flatMap(s=>s.issues.map(i=>[s.id,r.columns.find(c=>c.id===i.factorId)!.factor.label,i.endpoint,reasons[i.reason]??i.reason]))));
  results.append(node('p','The graphic and table show one factor. Coverage uses the entire captured factor universe. Exact observation lineage, source/unit mappings, calendar dates and resolved close evidence are retained in the exported manifest.'));
 }
 async function prepare(){
  const body=request(),version=++generation;clearResult('Preparing the exact aligned sample manifest…');
  try{const value=await o.api<HistoryReport>('portfolio/risk/history',body);if(!current(version))return;validateFactorHistory(value,body);if(value.manifest.forecast.contextId!==body.scope.context.contextId)throw Error('The history result does not match the requested context.');result=value;message.textContent='Captured history is shown below. Export rereads its inputs under current authority.';draw();}
  catch(error){if(current(version))clearResult(error instanceof Error?error.message:'History preparation failed.');throw error;}
  finally{sync();}
 }
 async function exportManifest(){
  if(!result)throw Error('Prepare history before exporting.');const captured=result,version=generation;
  try{const value=await o.api<HistoryReport>('portfolio/risk/history',captured.request);if(!current(version))return;validateFactorHistory(value,captured.request);
   if(value.manifest.id!==captured.manifest.id)throw Error('The reread history no longer matches the captured manifest. Prepare a new result.');
   const blob=new Blob([JSON.stringify(value,null,2)+'\n'],{type:'application/json'}),url=URL.createObjectURL(blob),link=node('a');link.href=url;link.download='leanetrm-factor-history.json';link.click();URL.revokeObjectURL(url);
  }catch(error){if(current(version))clearResult(error instanceof Error?error.message:'The captured history could not be exported.');throw error;}
 }
 statistics.onsubmit=event=>{event.preventDefault();if(!statistics.reportValidity())return;void o.act(async()=>{
  if(!result||!configuration||owner!==o.identity()||JSON.stringify(o.scope())!==JSON.stringify(configuration.scope))throw Error('Prepare aligned history for the current scope first.');
  const config=configuration.parametric,count=Number(ticks.value);
  if(estimator.value!==config.profile.convention||!Object.values(config.drifts).includes(drift.value)||!ticks.value||!Number.isSafeInteger(count))throw Error('Choose the statistical contract, drift and an explicit integer tick count.');
  await o.calculate({history:structuredClone(result.request),policy:{...config.profile,drift:drift.value,confidence:confidence.value.trim()},finiteDifference:{convention:config.finiteDifference,tradingTicks:count,deltaTolerance:{absolute:absolute.value.trim(),relative:relative.value.trim()}}},
   {pricingPolicy:configuration.pricingPolicy,convention:configuration.convention,numericalPolicy:configuration.numericalPolicy,context:result.context,asOf:result.asOf},(event as SubmitEvent).submitter?.id==='risk-parametric-queue');
 });};
 open.onclick=()=>void o.act(configure);form.onsubmit=event=>{event.preventDefault();if(form.reportValidity())void o.act(prepare);};
 download.onclick=()=>void o.act(exportManifest);
 form.oninput=()=>{generation++;clearResult('History inputs changed. Prepare a new result.');};
 calendar.onchange=()=>{const c=configuration?.history.calendars.find(c=>JSON.stringify(c.reference)===calendar.value);clockNote.textContent=c?`Calendar clock: ${c.clockRule.ianaZone??(c.clockRule.fixedOffsetSeconds===0?'UTC':c.clock)} · coverage ${c.from} to ${c.to}, exclusive.`:'';};
 reset();return {reset,sync};
}
