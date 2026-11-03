import type {StatisticalRequest,StatisticalCapabilities} from './historical-risk.js';
import {node,table} from './risk-report-elements.js';
import {renderChart,clearChart} from './chart-view.js';
import {valuationReason} from './valuation-status.js';
import {renderHistoryGrid,historyGridState,historyPriceLabels} from './risk-history-grid.js';
import {validateFactorHistory,factorHistoryChart,type HistoryCapabilities,type HistoryScope,type HistoryRequest,type HistoryReport} from './factor-history.js';

type Options={api:<T>(path:string,body?:unknown)=>Promise<T>;act:(work:()=>Promise<void>)=>Promise<void>;busy:()=>boolean;identity:()=>string;scope:()=>HistoryScope;instant:(at:string)=>string;unconfirmed:()=>boolean;calculate:(request:StatisticalRequest,metadata:{pricingPolicy:unknown;convention:string;numericalPolicy:string;context:HistoryReport['context'];asOf:string},queued:boolean)=>Promise<void>};
type Configuration=StatisticalCapabilities&{scope:HistoryScope;history:HistoryCapabilities;pricingPolicy:unknown;convention:string;numericalPolicy:string};

/** Guided inputs materialize the visible server preset; calculation APIs still require every policy explicitly. */
export function setupFactorHistory(o:Options){
 const get=<T extends HTMLElement=HTMLElement>(id:string)=>document.getElementById(id) as T;
 const panel=get<HTMLDetailsElement>('risk-history-panel'),host=get('risk-history-body'),open=get<HTMLButtonElement>('risk-history-open');
 const form=node('form'),fields=node('fieldset'),grid=node('div'),message=node('p'),results=node('div');form.id='risk-parametric-form';fields.id='risk-parametric-fields';grid.className='factor-history-fields';fields.append(grid);form.append(fields,message);host.append(form,results);
 message.setAttribute('role','status');results.id='risk-history-results';results.setAttribute('aria-live','polite');
 const select=(parent:HTMLElement,id:string,label:string)=>{const wrap=node('label',label),control=node('select');control.id=id;control.required=true;wrap.append(control);parent.append(wrap);return control;};
 const input=(parent:HTMLElement,id:string,label:string,type='text')=>{const wrap=node('label',label),control=node('input');control.id=id;control.type=type;control.required=true;control.autocomplete='off';wrap.append(control);parent.append(wrap);return control;};
 const method=select(grid,'risk-parametric-profile','Method');
 const comparisonLabel=node('label','Include matching parametric comparison '),comparison=node('input');comparison.type='checkbox';comparison.id='risk-historical-comparison';comparisonLabel.append(comparison);grid.append(comparisonLabel);
 const lookback=select(grid,'risk-history-lookback','History window');lookback.append(new Option('3 months','three_months'),new Option('6 months','six_months'),new Option('1 year','one_year'),new Option('Custom dates','custom'));
 const confidence=select(grid,'risk-parametric-confidence','Confidence level');confidence.append(new Option('95%','0.95'),new Option('97.5%','0.975'),new Option('99%','0.99'),new Option('99.9%','0.999'),new Option('Custom','custom'));
 const customConfidence=input(grid,'risk-parametric-confidence-custom','Custom confidence · decimal greater than 0.5, at most 0.9999');customConfidence.parentElement!.hidden=true;customConfidence.required=false;
 const calendar=select(grid,'risk-history-calendar','Business-day calendar');
 const dates=node('div');dates.className='factor-history-fields';const from=input(dates,'risk-history-from','First date · inclusive','date'),to=input(dates,'risk-history-to','End date · exclusive','date');fields.append(dates);
 const summary=node('p');summary.id='risk-parametric-preset';summary.className='risk-setup-summary';fields.append(summary);
 const pricePanel=node('div'),priceList=node('ol'),priceActions=node('div');pricePanel.className='risk-price-policy';priceList.id='risk-history-price-types';priceActions.className='actions';
 const addType=select(priceActions,'risk-history-price-type-add','Additional price type'),add=node('button','Allow as next fallback');add.type='button';add.className='secondary';priceActions.append(add);
 pricePanel.append(node('h3','Allowed price types · priority order'),node('p','Official daily closes are selected initially. Alternatives are used only if listed here, in this order, and every use is flagged. Feed/source, dated contract and native units stay pinned. A price-type change between days is visible in the grid.'),priceList,priceActions);fields.append(pricePanel);
 const advanced=node('details'),advancedGrid=node('div');advanced.id='risk-parametric-advanced';advancedGrid.className='factor-history-fields';advanced.append(node('summary','Advanced settings'),advancedGrid);fields.append(advanced);
 const minimum=input(advancedGrid,'risk-history-minimum','Minimum complete daily changes','number');minimum.min='2';minimum.max='2048';minimum.step='1';
 const drift=select(advancedGrid,'risk-parametric-drift','Forecast drift'),ticks=input(advancedGrid,'risk-parametric-ticks','Finite-difference step · trading ticks','number');ticks.min='1';ticks.max='1000';ticks.step='1';
 const absolute=input(advancedGrid,'risk-parametric-absolute','Delta agreement · absolute native tolerance'),relative=input(advancedGrid,'risk-parametric-relative','Delta agreement · relative tolerance');absolute.inputMode=relative.inputMode='decimal';
 const methodHelp=node('p');advanced.append(methodHelp);
 const actions=node('div');actions.className='actions';
 for(const [id,label] of [['risk-parametric-run','Run VaR'],['risk-parametric-queue','Queue VaR job'],['risk-history-run','Preview price coverage'],['risk-history-export','Export aligned history']]){const button=node('button',label);button.id=id;button.type=id==='risk-history-export'?'button':'submit';if(id!=='risk-parametric-run')button.className='secondary';actions.append(button);}fields.append(actions);
 let generation=0,allowed=false,owner='',configuration:Configuration|undefined,result:HistoryReport|undefined,cacheKey='',selectedColumn:string|undefined,priceTypes:string[]=[],plots:HTMLElement[]=[];
 const gridState=historyGridState();
 const current=(version:number)=>version===generation&&owner===o.identity();
 const sameScope=()=>!!configuration&&JSON.stringify(o.scope())===JSON.stringify(configuration.scope);
 function clear(text:string){for(const plot of plots)clearChart(plot);plots=[];result=undefined;cacheKey='';results.replaceChildren();message.textContent=text;sync();}
 function invalidate(){generation++;clear('Settings changed. Run VaR to prepare the matching price history automatically.');describe();}
 function sync(available?:boolean){if(available!==undefined)allowed=available;open.disabled=o.busy()||!allowed;fields.disabled=o.busy()||!allowed||!configuration;get<HTMLButtonElement>('risk-history-export').disabled=o.busy()||!result;}
 function reset(){generation++;configuration=undefined;owner='';selectedColumn=undefined;priceTypes=[];panel.open=false;form.reset();calendar.replaceChildren(new Option('Choose a calendar…',''));method.replaceChildren();drift.replaceChildren();priceList.replaceChildren();summary.textContent='';clear('Select the books or trades, then open VaR setup.');}
 function describe(){
  const c=configuration?.history.calendars.find(c=>JSON.stringify(c.reference)===calendar.value);
  dates.hidden=lookback.value!=='custom';from.disabled=to.disabled=dates.hidden;
  customConfidence.parentElement!.hidden=confidence.value!=='custom';customConfidence.required=confidence.value==='custom';
  const historical=method.value===configuration?.historical.profile.convention,fd=!historical||comparison.checked;
  comparisonLabel.hidden=!historical;drift.parentElement!.hidden=historical;drift.disabled=historical;
  for(const c of [ticks,absolute,relative]){c.parentElement!.hidden=!fd;c.disabled=!fd;}
  methodHelp.textContent=historical?'Equal-weight full-PV repricing of joint absolute changes. Time, holdings, fixings, rates and current option calibration stay frozen. Signed empirical quantile and fractional tail mass; no carry, interpolation, proxy or contract roll.':'Equal-weight sample covariance (n−1), centered changes and signed normal loss tails. Finite differences use half, nominal and double steps. Each currency and valuation measure stays separate.';
  summary.textContent=configuration?`Starting settings: ${confidence.selectedOptions[0]?.textContent} confidence · one business day · ${lookback.selectedOptions[0]?.textContent} (${from.value||'choose dates'} to ${to.value||'choose dates'}, exclusive) · end of day${c?' in '+(c.clockRule.ianaZone??(c.clockRule.fixedOffsetSeconds===0?'UTC':c.clock)):''} · minimum ${minimum.value} common changes. All settings are sent explicitly and retained. Incomplete days after the selected market cutoff are omitted.`:'';
 }
 function windowDates(){
  if(lookback.value==='custom'){describe();return;}
  const c=configuration?.history.calendars.find(c=>JSON.stringify(c.reference)===calendar.value),window=c?.windows.find(w=>w.code===lookback.value);
  from.value=window?.from??'';to.value=window?.to??'';describe();
  if(c?.setupError)message.textContent=c.setupError;
  else if(window&&!window.covered)message.textContent='This calendar does not cover the selected history window. Choose a shorter window or custom dates; the requested dates have not been shortened.';
 }
 function drawPriceTypes(){
  priceList.replaceChildren();
  for(const [i,code] of priceTypes.entries()){
   const row=node('li'),label=node('span',`${i+1}. ${historyPriceLabels[code]??code}`);row.append(label);
   for(const [text,enabled,action] of [ ['Move up',i>0,()=>{[priceTypes[i-1],priceTypes[i]]=[priceTypes[i],priceTypes[i-1]];}],['Move down',i<priceTypes.length-1,()=>{[priceTypes[i+1],priceTypes[i]]=[priceTypes[i],priceTypes[i+1]];}],['Remove',true,()=>{priceTypes.splice(i,1);}]] as [string,boolean,()=>void][]){
    if(priceTypes.length===1&&text!=='Remove')continue;
    const button=node('button',text);button.type='button';button.className='secondary';button.disabled=!enabled;button.setAttribute('aria-label',text+' '+(historyPriceLabels[code]??code));button.onclick=()=>{action();drawPriceTypes();invalidate();};row.append(button);
   }priceList.append(row);
  }
  addType.replaceChildren(new Option('Choose an alternative…',''),...(configuration?.history.priceTypes??[]).filter(t=>!priceTypes.includes(t.code)).map(t=>new Option(t.label,t.code)));addType.required=false;if(addType.options.length===2)addType.selectedIndex=1;add.disabled=!addType.options[1];
 }
 async function configure(){
  panel.open=true;if(configuration)return;
  owner=o.identity();const scope=structuredClone(o.scope()),version=++generation;clear('Reading the held factors and available calendars…');
  try{
   const catalog=await o.api<Configuration&{context:{contextId:string}}>('portfolio/risk/catalog',scope);
   if(!current(version))return;
   if(!catalog.history?.guided||!catalog.parametric?.defaults||!catalog.historical?.defaults||catalog.context.contextId!==scope.context.contextId)throw Error('Refresh Risk to load the supported VaR setup for this scope.');
   configuration={...catalog,scope};const d=catalog.parametric.defaults,h=catalog.history;
   method.replaceChildren(new Option('Parametric VaR / expected shortfall',catalog.parametric.profile.convention),new Option('Historical simulation VaR / expected shortfall',catalog.historical.profile.convention));method.value=catalog.parametric.profile.convention;comparison.checked=catalog.historical.defaults.compareParametric;
   drift.replaceChildren(new Option('Zero drift',catalog.parametric.drifts.zero),new Option('Sample mean drift',catalog.parametric.drifts.sample));drift.value=d.drift;
   confidence.value=d.confidence;ticks.value=String(d.tradingTicks);absolute.value=d.deltaTolerance.absolute;relative.value=d.deltaTolerance.relative;minimum.value=String(h.guided.minimumSamples);lookback.value=h.guided.lookback;priceTypes=[...h.guided.priceTypes];
   calendar.replaceChildren(new Option('Choose a calendar…',''),...h.calendars.map(c=>new Option(c.label+' · '+(c.clockRule.ianaZone??(c.clockRule.fixedOffsetSeconds===0?'UTC':c.clock)),JSON.stringify(c.reference))));
   if(h.calendars.length===1)calendar.selectedIndex=1;
   drawPriceTypes();windowDates();message.textContent='Choose your scope, history window and confidence, then Run VaR. Price history is aligned automatically; Preview price coverage is optional.';
  }catch(error){if(current(version))clear(error instanceof Error?error.message:'VaR setup is unavailable.');throw error;}finally{sync();}
 }
 function request():HistoryRequest{
  if(!configuration||owner!==o.identity()||!sameScope())throw Error('The scope or market context changed. Refresh Risk and open VaR setup again.');
  const selected=configuration.history.calendars.find(c=>JSON.stringify(c.reference)===calendar.value),count=Number(minimum.value);
  if(!selected)throw Error('Choose a business-day calendar.');
  if(!Number.isSafeInteger(count)||count<2||count>2048)throw Error('Minimum complete daily changes must be a whole number from 2 to 2048.');
  if(!from.value||!to.value||from.value>=to.value)throw Error('Choose a history window with a first date before its end date.');
  if(!priceTypes.length)throw Error('Allow at least one price type. Official daily close is the starting selection.');
  return {scope:structuredClone(configuration.scope),pricingPolicy:configuration.pricingPolicy,policy:{...configuration.history.guided.profile,minimumSamples:count,priceTypes:[...priceTypes]},calendar:selected.reference,from:from.value,to:to.value,closeTime:configuration.history.guided.closeTime,ambiguousTime:configuration.history.clockPolicy.ambiguousTime,invalidTime:configuration.history.clockPolicy.invalidTime};
 }
 function draw(){
  for(const p of plots)clearChart(p);plots=[];results.replaceChildren();if(!result)return;
  const r=result,c=r.coverage,status=node('div');status.className='risk-coverage';status.dataset.state=c.available?'complete':'incomplete';
  status.append(node('strong',c.available?'Aligned history ready':'Price coverage needs attention'),node('p',`${c.factors} dated factor${c.factors===1?'':'s'} · ${c.samples} common changes · ${c.rejected} rejected intervals · minimum ${c.minimumSamples}.`));
  results.append(status,node('p',`${o.instant(r.asOf)} · known at ${o.instant(r.manifest.forecast.knownAt)} · ${r.scope.bookIds.length} books · ${r.scope.tradeIds==null?'all trades':r.scope.tradeIds.length+' selected trade'+(r.scope.tradeIds.length===1?'':'s')}`));
  const omitted=(r.manifest.calendar?.businessDates.length??r.manifest.sessions.length)-r.manifest.sessions.length;if(omitted)results.append(node('p',`${omitted} unfinished business sessions omitted at the captured forecast cutoff.`));
  if(r.excludedPositions.length)results.append(node('p','Excluded positions: '+r.excludedPositions.map(p=>p.tradeReference+' · '+valuationReason(p.reason,p.instrumentType)).join('; ')));
  renderHistoryGrid(results,r.manifest,r.columns.map(c=>({id:c.id,label:c.factor.label,unit:c.factor.unit})),gridState,draw);
  const details=node('details');details.append(node('summary','Individual factor history and rejected intervals'));
  if(!selectedColumn||!r.columns.some(c=>c.id===selectedColumn))selectedColumn=r.columns[0].id;
  const choose=node('select');choose.setAttribute('aria-label','History factor');choose.append(...r.columns.map(c=>new Option(c.factor.label+' · '+c.factor.unit,c.id)));choose.value=selectedColumn;choose.onchange=()=>{selectedColumn=choose.value;draw();};details.append(choose);
  if(r.manifest.samples.length){const plot=node('div');plot.className='chart risk-result-chart';details.append(plot);plots.push(plot);renderChart(plot,factorHistoryChart(r,selectedColumn));}
  if(r.manifest.rejected.length)details.append(table(['Interval','Factor','Endpoint','Reason'],r.manifest.rejected.flatMap(s=>s.issues.map(i=>[s.id,r.columns.find(c=>c.id===i.factorId)!.factor.label,i.endpoint,i.reason.replaceAll('_',' ')]))));
  results.append(details);
 }
 async function prepare():Promise<HistoryReport|undefined>{
  const body=request(),key=JSON.stringify(body);if(result&&key===cacheKey)return result;
  const version=++generation;clear('Step 1 of 2 · aligning end-of-day factor prices and checking coverage…');
  try{const value=await o.api<HistoryReport>('portfolio/risk/history',body);if(!current(version)||!sameScope())return;
   validateFactorHistory(value,body);result=value;cacheKey=key;message.textContent='History aligned. The grid shows official closes, approved alternatives and missing prices. Run VaR computes the selected risk method automatically.';draw();return value;
  }catch(error){if(current(version))clear(error instanceof Error?error.message:'History preparation failed.');throw error;}finally{sync();}
 }
 async function exportManifest(){
  if(!result)throw Error('Run VaR or preview coverage first.');const captured=result,version=generation;
  const value=await o.api<HistoryReport>('portfolio/risk/history',captured.request);if(!current(version)||!sameScope())return;
  validateFactorHistory(value,captured.request);if(value.manifest.id!==captured.manifest.id)throw Error('The authorized inputs changed. Run a new calculation before export.');
  const blob=new Blob([JSON.stringify(value,null,2)+'\n'],{type:'application/json'}),url=URL.createObjectURL(blob),link=node('a');link.href=url;link.download='leanetrm-factor-history.json';link.click();URL.revokeObjectURL(url);
 }
 form.onsubmit=event=>{event.preventDefault();if(!form.reportValidity())return;const action=(event as SubmitEvent).submitter?.id;void o.act(async()=>{
  if(action!=='risk-history-run'&&o.unconfirmed())throw Error('Reconcile the unconfirmed risk submission before running another VaR.');
  const config=configuration;if(!config)throw Error('Open VaR setup first.');
  const historical=method.value===config.historical.profile.convention;
  const count=Number(ticks.value);if((!historical||comparison.checked)&&(!Number.isSafeInteger(count)||count<1||count>1000))throw Error('The trading-tick step must be a whole number from 1 to 1000.');
  const policy={...config.parametric.profile,drift:historical?config.historical.defaults.comparisonDrift:drift.value,confidence:confidence.value==='custom'?customConfidence.value.trim():confidence.value},finiteDifference={convention:config.parametric.finiteDifference,tradingTicks:count,deltaTolerance:{absolute:absolute.value.trim(),relative:relative.value.trim()}};
  const requestedKey=JSON.stringify(request());
  const captured=await prepare(),version=generation;if(!captured||!current(version)||!sameScope()||configuration!==config||captured!==result||cacheKey!==requestedKey||JSON.stringify(request())!==requestedKey)return;
  if(action==='risk-history-run')return;
  message.textContent=historical?'Step 2 of 2 · repricing every joint historical change and calculating VaR and expected shortfall…':'Step 2 of 2 · calculating covariance, sensitivities, VaR and expected shortfall…';
  const body:StatisticalRequest=historical?{historical:{history:structuredClone(captured.request),policy:{...config.historical.profile,confidence:policy.confidence},...(comparison.checked?{comparison:{policy,finiteDifference}}:{})}}:{parametric:{history:structuredClone(captured.request),policy,finiteDifference}};
  await o.calculate(body,{pricingPolicy:config.pricingPolicy,convention:config.convention,numericalPolicy:config.numericalPolicy,context:captured.context,asOf:captured.asOf},action==='risk-parametric-queue');
  if(current(version))message.textContent='Calculation submitted. Its report retains the history, price-type choices and numerical settings.';
 });};
 panel.ontoggle=()=>{if(panel.open&&allowed&&!configuration&&!o.busy())void o.act(configure);};
 open.onclick=()=>void o.act(configure);get('risk-history-export').onclick=()=>void o.act(exportManifest);
 lookback.onchange=()=>{windowDates();invalidate();};calendar.onchange=()=>{windowDates();invalidate();};confidence.onchange=describe;method.onchange=describe;comparison.onchange=describe;
 for(const control of [from,to,minimum])control.oninput=invalidate;
 for(const control of [drift,ticks,absolute,relative,customConfidence])control.oninput=describe;
 add.onclick=()=>{if(addType.value){priceTypes.push(addType.value);drawPriceTypes();invalidate();}};
 reset();return {reset,sync};
}
