// Real recorded source, application controls, native API and retained worker.
import {readFile} from 'node:fs/promises';
import {withOwnedBrowser} from './owned-browser.mjs';

const target=JSON.parse(await readFile(process.argv[2],'utf8')),f=target.lifecycle;
if(!f?.closeId||!['physical','cash'].includes(f.kind)||f.optionTradeIds?.length!==2||!Array.isArray(f.futureTradeIds))throw Error('An owned native valuation fixture is required.');
const q=JSON.stringify;
const canonical=value=>JSON.stringify(value,(_key,v)=>v&&typeof v==='object'&&!Array.isArray(v)?Object.fromEntries(Object.entries(v).sort(([a],[b])=>a.localeCompare(b))):v);
const passed=await withOwnedBrowser(process.argv[2],process.argv[3],async({command,evaluate,wait,click,set,persona,check,shot,idle,url})=>{
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1512,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`(()=>{const send=window.fetch;window.valuationReads=[];window.valuationCanonical=${canonical.toString()};window.fetch=async(...args)=>{const response=await send(...args);if(String(args[0]).includes('/portfolio/risk'))valuationReads.push({path:String(args[0]),status:response.status,body:args[1]?.body?JSON.parse(args[1].body):null,value:await response.clone().json()});return response;};})();`});
 const ready=async expression=>{try{await wait(expression,60000);}catch(error){throw Error(error.message+' · '+JSON.stringify(await evaluate("({error:document.querySelector('#error').textContent,note:document.querySelector('[data-economic=note]')?.textContent,reads:valuationReads.filter(r=>r.status!==200),jobs:valuationReads.filter(r=>r.path.includes('/risk/jobs/')).slice(-3)})")),{cause:error});}};
 await command('Page.navigate',{url});await wait(idle,60000);await persona('trader');
 await set('#close-session',f.sourceSession);await wait(idle,60000);await set('#snapshot',f.closeId);await wait(`document.querySelector('#snapshot').value===${q(f.closeId)}&&${idle}`,60000);
 await click('#nav-risk');await wait(idle,60000);
 await evaluate(`(()=>{const s=document.querySelector('#risk-workspace-books');for(const o of s.options)o.selected=o.value===${q(f.bookId)};s.dispatchEvent(new Event('change',{bubbles:true}));})()`);await wait(idle,60000);
 await click('#nav-risk-var');await click('[data-economic=configure]',60000);
 const setup=await evaluate("valuationReads.findLast(r=>r.path.endsWith('/economic/setup'))?.value");
 if(!setup?.physicalBindings||!Array.isArray(setup.cashOptionBindings))throw Error('The native selected source did not expose its exact held valuation bindings: '+q(setup));
 const optionGroups=f.kind==='cash'?setup.cashOptionBindings:setup.physicalBindings.options;
 await check('actual recorded matrix offers exactly the selected future and live option holdings',
  `${q(canonical(setup.physicalBindings.futures.map(g=>g.tradeId).sort()))}===${q(canonical([...f.futureTradeIds].sort()))}&&${q(canonical(optionGroups.map(g=>g.tradeId).sort()))}===${q(canonical([...f.optionTradeIds].sort()))}`);
 const selections={pricing:f.pricingValue,session:JSON.stringify([f.economicSession,1]),'price-path':'shock_then_hold_native_quotes_v1',
  'cash-disposition':'retain_scheduled_cash_outstanding_v1','cash-accumulation':'nominal_currency_cash_v1',
  'input-expiry':'declared_curve_expiry_policy_v1','physical-delivery':'retain_generated_delivery_until_observed_performance_v1'};
 for(const [key,value] of Object.entries(selections))await set(`[data-economic=${key}]`,value);
 await check('actual per-holding model choices start blank and show the selected event assumption',
  "[...document.querySelectorAll('[data-economic=future-binding],[data-economic=option-binding],[data-economic=cash-option-binding]')].every(s=>s.value==='')&&document.querySelector('[data-economic=physical-bindings]').textContent.includes('Reference stopping values the option')");
 const requested={futures:[],options:[],cashOptions:[]};
 for(const [kind,key,groups] of [['futures','future-binding',setup.physicalBindings.futures],['options','option-binding',setup.physicalBindings.options],['cashOptions','cash-option-binding',setup.cashOptionBindings]])for(const group of groups){
  const choices=kind==='cashOptions'?group.choices:group.choices.filter(c=>c.selection.markInterpretation==='explicit_valuation_proxy_v1');
  if(choices.length!==1)throw Error('The synthetic fixture requires one explicitly supported saved model choice: '+q(group));
  requested[kind].push(choices[0].selection);
  await set(`[data-economic=${key}][data-trade="${group.tradeId}"][data-binding="${group.bindingId}"]`,choices[0].value);
 }
 await shot(`valuation-${f.kind}-bindings`);await click('[data-economic=prepare]',60000);
 await ready("!!valuationReads.find(r=>r.path.endsWith('/economic/sources')&&r.status===200)&&"+idle);
 const source=await evaluate("valuationReads.findLast(r=>r.path.endsWith('/economic/sources')).value");
 await check('real source preparation retains every exact binding and the recorded source',
  `(()=>{const r=valuationReads.findLast(r=>r.path.endsWith('/economic/sources'));return valuationCanonical(r.body.pricingPolicy.nativeFutures.economic.physical)===${q(canonical(requested.futures))}&&valuationCanonical(r.body.pricingPolicy.futuresOptions.physical?.options??[])===${q(canonical(requested.options))}&&valuationCanonical(r.body.pricingPolicy.futuresOptions.cashFunding?.options??[])===${q(canonical(requested.cashOptions))}&&${f.kind==='cash'?"!Object.hasOwn(r.body.pricingPolicy.futuresOptions,'physical')":"!Object.hasOwn(r.body.pricingPolicy.futuresOptions,'cashFunding')"}&&r.body.scope.context.snapshotId===${q(f.closeId)}&&r.value.factors.length===${f.sourceFactorCount};})()`);
 const calendar=source.history.calendars.find(c=>c.reference.code==='source_business_days');if(!calendar)throw Error('The native aligned business calendar is missing.');
 for(const [key,value] of Object.entries({method:'economic_lifecycle_historical_full_revaluation_v1',confidence:'0.99',calendar:JSON.stringify(calendar.reference),
  from:'2026-10-13',to:'2026-10-17','history-close':source.history.guided.closeTime,'price-type':'official_close_v1',minimum:'2'}))await set(`[data-economic=${key}]`,value);
 await click('[data-economic=history]',60000);await ready("!!valuationReads.find(r=>r.path.endsWith('/economic/history')&&r.status===200)&&"+idle);
 const history=await evaluate("valuationReads.findLast(r=>r.path.endsWith('/economic/history')).value");
 if(!history.manifest?.available)throw Error('The owned fixture must retain complete guided history: '+q(history));
 await click('[data-economic=run]',60000);await ready("!document.querySelector('#risk-economic-results').hidden&&document.querySelector('#risk-economic-results canvas')&&"+idle);
 await check('actual full revaluation retains native owners and both exact selected valuation laws',
  `(()=>{const r=valuationReads.findLast(r=>r.path==='/api/portfolio/risk').value;return r.measure==='economic_pnl_v1'&&r.result.samples===2&&r.optionModelHelp===${q(setup.optionModelHelp)}&&document.querySelector('#risk-economic-results').textContent.includes(r.optionModelHelp)&&${q([...f.futureTradeIds,...f.optionTradeIds])}.every(id=>r.holdings.some(h=>h.owner.owner.id===id))&&valuationCanonical(r.pricingPolicy.futuresOptions.physical?.options??[])===${q(canonical(requested.options))}&&valuationCanonical(r.pricingPolicy.futuresOptions.cashFunding?.options??[])===${q(canonical(requested.cashOptions))}&&valuationCanonical(r.pricingPolicy.nativeFutures.economic.physical)===${q(canonical(requested.futures))};})()`);
 await shot(`valuation-${f.kind}-report`);await click('#nav-risk-var');await click('[data-economic=queue]',60000);
 await ready("!!valuationReads.find(r=>r.path==='/api/portfolio/risk/jobs'&&r.status===200)&&"+idle);
 const queued=await evaluate("valuationReads.findLast(r=>r.path==='/api/portfolio/risk/jobs'&&r.status===200)");
 await ready(`valuationReads.some(r=>r.path==='/api/portfolio/risk/jobs/${queued.value.id}'&&['completed','failed','cancelled'].includes(r.value.state))&&${idle}`);
 const terminal=await evaluate(`valuationReads.findLast(r=>r.path==='/api/portfolio/risk/jobs/${queued.value.id}').value`);
 if(terminal.state!=='completed')throw Error('The owned native worker refused the complete joined request: '+q(terminal));
 await check('actual risk worker retains the chosen models and original complete source',
  `(()=>{const r=valuationReads.findLast(r=>r.path==='/api/portfolio/risk/jobs/${queued.value.id}'&&r.value.state==='completed').value;return r.kind==='economic_lifecycle'&&r.result.result.samples===2&&valuationCanonical(r.result.pricingPolicy)===${q(canonical(queued.body.request.pricingPolicy))}&&valuationCanonical(r.result.source)===${q(canonical(queued.body.request.context))};})()`);
 const before=await evaluate('performance.timeOrigin');await command('Page.reload',{ignoreCache:true});
 await ready(`performance.timeOrigin!==${before}&&document.querySelector('#risk-economic-results canvas')&&${idle}`);
 await check('the selected model report reopens with its held-decision assumption',`location.hash==='#risk/job/${queued.value.id}'&&document.querySelector('#risk-economic-results').textContent.includes('Retained job · completed')&&document.querySelector('#risk-economic-results').textContent.includes(${q(setup.optionModelHelp)})`);
});
console.log(`${passed} actual per-holding valuation browser checks passed.`);
