// Real application and native backend; the observer records responses unchanged.
import {readFile} from 'node:fs/promises';
import {withOwnedBrowser} from './owned-browser.mjs';

const target=JSON.parse(await readFile(process.argv[2],'utf8')),f=target.economic;
if(!f?.bookId||!f?.profileId)throw Error('An owned economic fixture is required.');
const passed=await withOwnedBrowser(process.argv[2],process.argv[3],async({command,evaluate,wait,click,set,persona,check,shot,idle,url})=>{
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1512,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`(()=>{const send=window.fetch;window.__economicReads=[];window.fetch=async(...args)=>{const response=await send(...args);if(String(args[0]).includes('/portfolio/risk')){window.__economicReads.push({path:String(args[0]),status:response.status,body:args[1]?.body?JSON.parse(args[1].body):null,value:await response.clone().json()});}return response;};})();`});
 const ready=async expression=>{try{await wait(expression,60000);}catch(error){throw Error(error.message+' · observed '+JSON.stringify(await evaluate("({error:document.querySelector('#error').textContent,note:document.querySelector('[data-economic=note]').textContent,result:document.querySelector('#risk-economic-results').textContent,reads:window.__economicReads.filter(r=>r.status!==200||r.path==='/api/portfolio/risk')})")),{cause:error});}};
 await command('Page.navigate',{url});await wait(idle,60000);await persona('trader');
 const frame=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith(${JSON.stringify(f.at.slice(0,10))})&&w.initialFrameIds.includes(f.id))?.id;})()`);
 if(!frame)throw Error('The application did not expose its explicitly seeded economic opening frame.');
 await set('#snapshot',frame);await wait(idle,60000);await click('#nav-risk');await wait(idle,60000);
 await evaluate(`(()=>{const s=document.querySelector('#risk-workspace-books');for(const o of s.options)o.selected=o.value===${JSON.stringify(f.bookId)};s.dispatchEvent(new Event('change',{bubbles:true}));})()`);await wait(idle,60000);
 await click('#nav-risk-var');await click('[data-economic=configure]',60000);
 const setup=await evaluate("window.__economicReads.findLast(r=>r.path.endsWith('/economic/setup'))?.value");
 if(!setup)throw Error('The real event-aware setup was not returned: '+await evaluate("document.querySelector('#error').textContent"));
 await check('actual setup exposes only source-known schedules and requires every financial choice',
  `document.querySelector('[data-economic=session]').options.length>1&&['pricing','session','price-path','cash-disposition','cash-accumulation','input-expiry','physical-delivery'].every(k=>document.querySelector('[data-economic='+k+']').value==='')`);
 const choices={pricing:'native_whole_books',session:JSON.stringify([f.economicSession,1]),'price-path':'shock_then_hold_native_quotes_v1',
  'cash-disposition':'retain_scheduled_cash_outstanding_v1','cash-accumulation':'nominal_currency_cash_v1',
  'input-expiry':'declared_curve_expiry_policy_v1','physical-delivery':'retain_generated_delivery_until_observed_performance_v1'};
 for(const [key,value] of Object.entries(choices))await set(`[data-economic=${key}]`,value);
 await set(`[data-economic=gas-profile][data-account="${f.accountId}"]`,JSON.stringify([f.profileId,1]));
 await click('[data-economic=prepare]',60000);
 await wait("!!window.__economicReads.find(r=>r.path.endsWith('/economic/sources'))&&"+idle,60000);
 const sources=await evaluate("window.__economicReads.findLast(r=>r.path.endsWith('/economic/sources')).value");
 await check('actual preparation preserves the common next close and the complete native factor universe',
  `document.querySelector('[data-economic=source-summary]').textContent.includes('1 required market factors')&&${JSON.stringify(sources.factors.length)}===1&&${JSON.stringify(sources.books.map(b=>b.id))}.includes(${JSON.stringify(f.bookId)})`);
 await set('[data-economic=method]','economic_lifecycle_historical_full_revaluation_v1');await set('[data-economic=confidence]','0.99');
 const calendar=sources.history.calendars.find(c=>c.reference.code==='test_business');if(!calendar)throw Error('The native business calendar is missing.');
 await set('[data-economic=calendar]',JSON.stringify(calendar.reference));await set('[data-economic=from]','2020-12-15');await set('[data-economic=to]','2020-12-18');
 await set('[data-economic=history-close]',sources.history.guided.closeTime);await set('[data-economic=price-type]','official_close_v1');await set('[data-economic=minimum]','2');
 await click('[data-economic=history]',60000);
 await check('actual history capture retains all native columns and both complete changes',
  "window.__economicReads.findLast(r=>r.path.endsWith('/economic/history'))?.value.manifest.samples.length===2&&document.querySelector('[data-economic=note]').textContent.includes('complete')",
  "({history:window.__economicReads.findLast(r=>r.path.endsWith('/economic/history'))?.value,note:document.querySelector('[data-economic=note]').textContent,error:document.querySelector('#error').textContent})");
 await click('[data-economic=run]',60000);await ready("!document.querySelector('#risk-economic-results').hidden&&document.querySelector('#risk-economic-results canvas')&&"+idle);
 await check('real browser submission renders independent signed economic tails, native owners and separate time carry',
  `(()=>{const r=window.__economicReads.findLast(r=>r.path==='/api/portfolio/risk').value;return r.measure==='economic_pnl_v1'&&r.result.samples===2&&r.result.economic[0].valueAtRisk===${JSON.stringify(f.expectedHistoricalVar)}&&r.holdings.length>1&&r.holdings.every(h=>h.owner.owner.kind===2&&h.reference.startsWith('synthetic_')&&h.instrument.startsWith('synthetic_'))&&document.querySelector('#risk-economic-results').textContent.includes('No-shock time and carry')&&document.querySelector('#risk-economic-results').textContent.includes('Market-only diagnostic')&&!document.querySelector('#risk-economic-results').textContent.includes('internal record');})()`);
 await shot('economic-real-application');
 await click('#nav-risk-var');await click('[data-economic=queue]',60000);
 await wait("window.__economicReads.some(r=>r.path==='/api/portfolio/risk/jobs'&&r.body?.requestId)&&"+idle,60000);
 const id=await evaluate("window.__economicReads.findLast(r=>r.path==='/api/portfolio/risk/jobs'&&r.body?.requestId).value.id");
 await wait(`window.__economicReads.some(r=>r.path==='/api/portfolio/risk/jobs/${id}'&&r.value.state==='completed')&&document.querySelector('#risk-economic-results canvas')&&${idle}`,60000);
 await check('actual queued worker completes under the retained engine and full source proof',
  `(()=>{const done=window.__economicReads.findLast(r=>r.path==='/api/portfolio/risk/jobs/${id}'&&r.value.state==='completed').value;return done.kind==='economic_lifecycle'&&done.result.result.samples===2&&done.result.engineVersion===${JSON.stringify(sources.engineVersion)}&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.economic-risk.submission:'));})()`);
 await check('static risk synchronization preserves the selected economic report address',`location.hash==='#risk/job/${id}'`);
 const before=await evaluate('performance.timeOrigin');
 await command('Page.reload',{ignoreCache:true});await ready(`performance.timeOrigin!==${before}&&document.querySelector('#risk-economic-results canvas')&&${idle}`);
 await check('a real retained economic report reopens after a full application reload',
  `location.hash==='#risk/job/${id}'&&document.querySelector('#risk-economic-results').textContent.includes('Retained job · completed')&&!document.querySelector('#risk-workspace-export').disabled`,
  "({hash:location.hash,report:document.querySelector('#risk-economic-results').textContent,disabled:document.querySelector('#risk-workspace-export').disabled,error:document.querySelector('#error').textContent})");
 const calculations=await evaluate("window.__economicReads.filter(r=>r.path==='/api/portfolio/risk').length");
 await click('#profile-open');await set('#zone','America/Chicago');await evaluate("document.querySelector('#preferences-form').requestSubmit();true");await wait(idle,60000);
 await check('retained economic timestamps follow the selected user zone without another financial calculation',
  `document.querySelector('#risk-economic-results').textContent.includes('America/Chicago')&&window.__economicReads.filter(r=>r.path==='/api/portfolio/risk').length===${calculations}`);
 await shot('economic-real-retained-report');
});
console.log(`${passed} real economic application browser checks passed.`);
