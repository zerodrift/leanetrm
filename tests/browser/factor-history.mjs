export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,fitsWidth,url}){
 await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1150,deviceScaleFactor:1,mobile:false});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`(()=>{const send=fetch;window.__factorHistoryReads=[];window.fetch=async(...args)=>{const r=await send(...args);if(String(args[0]).endsWith('/portfolio/risk/history')&&r.ok)window.__factorHistoryReads.push(await r.clone().json());return r;};})();`});
 await command('Page.navigate',{url});await wait(idle);await persona('risk');
 const frame=await evaluate("(async()=>{const b=await(await fetch('/api/bootstrap')).json();return b.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");
 await set('#snapshot',frame);await wait(idle);await click('#nav-risk');await wait(idle);
 await check('Risk offers aligned factor history beside its shared portfolio/book/trade scope',"!!document.querySelector('#risk-history-open')&&!document.querySelector('#risk-history-open').disabled");
 await set('#risk-workspace-trade-mode','selected');await wait(idle);
 await check('an empty individual selection cannot prepare whole-book history',"document.querySelector('#risk-history-open').disabled");
 const trade=await evaluate("[...document.querySelector('#risk-workspace-trades').options].find(o=>o.textContent.includes(' · Futures')).value");
 await set('#risk-workspace-trades',trade);await wait(idle);await click('#risk-history-open');await wait("document.querySelector('#risk-history-calendar option[value]')&&"+idle);
 await check('guided history uses end of day without user-entered times',"!document.querySelector('#risk-history-close')&&document.querySelector('#risk-history-minimum').value==='60'&&document.querySelector('#risk-parametric-profile').value==='dated_factor_delta_normal_v1'");
 const calendar=await evaluate("[...document.querySelector('#risk-history-calendar').options].find(o=>o.value&&JSON.parse(o.value).code==='weekdays_utc').value");
 await set('#risk-history-calendar',calendar);await set('#risk-history-lookback','custom');await set('#risk-history-from','2020-12-15');await set('#risk-history-to','2020-12-19');await set('#risk-history-minimum','2');
 await click('#risk-history-run');await wait("document.querySelector('#risk-history-results canvas')&&"+idle,60000);
 await set('#risk-history-results select[aria-label="Aligned factor grid values"]','changes');
 const captured=await evaluate('window.__factorHistoryReads.at(-1)');
 await check('the history view shows the exact backend changes, native unit and captured trade subset',
  `(()=>{const r=${JSON.stringify(captured)},root=document.querySelector('#risk-history-results');return r.coverage.available&&r.columns.length===1&&r.manifest.samples.length===2&&r.scope.tradeIds[0]===${JSON.stringify(trade)}&&r.manifest.samples.every(s=>root.textContent.includes(s.changes[0]))&&root.textContent.includes(r.columns[0].factor.unit)&&root.textContent.includes('1 selected trade');})()`);
 await evaluate("(()=>{const create=URL.createObjectURL;URL.createObjectURL=blob=>{window.__factorHistoryDownload=blob;return create(blob);};})()");
 await click('#risk-history-export');await wait(idle);
 await check('history export rereads authorized inputs and preserves the captured manifest identity',
  `(async()=>{const file=JSON.parse(await window.__factorHistoryDownload.text());return window.__factorHistoryReads.length===2&&file.manifest.id===${JSON.stringify(captured.manifest.id)}&&file.request.expectedManifestId===file.manifest.id;})()`);
 await set('#risk-history-minimum','4');
 await check('editing a history input clears its previous result and export immediately',"!document.querySelector('#risk-history-results canvas')&&document.querySelector('#risk-history-export').disabled");
 await click('#risk-history-run');await wait(idle);
 await check('insufficient common history stays unavailable while retaining exact eligible changes',"document.querySelector('#risk-history-results').textContent.includes('Price coverage needs attention')&&window.__factorHistoryReads.at(-1).manifest.samples.length===2&&!window.__factorHistoryReads.at(-1).coverage.available");
 await set('#risk-history-minimum','2');await click('#risk-history-price-types button[aria-label="Remove Official daily close"]');await set('#risk-history-price-type-add','parameter_final_v1');await click('#risk-history-panel .risk-price-policy .actions > button');await click('#risk-history-run');await wait(idle);
 await check('an explicitly excluded official type is not silently reused as a fallback',"!document.querySelector('#risk-history-results canvas')&&document.querySelector('#risk-history-results').textContent.includes('price type unavailable')&&window.__factorHistoryReads.at(-1).manifest.samples.length===0");
 await click('#risk-history-price-types button[aria-label="Remove Final parameter"]');await set('#risk-history-price-type-add','official_close_v1');await click('#risk-history-panel .risk-price-policy .actions > button');await click('#risk-history-run');await wait(idle);
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate("document.querySelector('#risk-history-panel').scrollIntoView({block:'start'});true");await shot('risk-history-phone');
 await check('factor history controls, graphic and exact tables fit a phone viewport',fitsWidth,"({width:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,wide:[...document.querySelectorAll('#risk-history-panel *')].filter(n=>n.getClientRects().length&&n.getBoundingClientRect().right>document.documentElement.clientWidth+1&&!n.closest('.table-wrap')).slice(0,8).map(n=>({tag:n.tagName,id:n.id,class:n.className,width:n.getBoundingClientRect().width}))})");
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1150,deviceScaleFactor:1,mobile:false});await evaluate("document.querySelector('#risk-history-results').scrollIntoView({block:'start'});true");await shot('risk-history-report');
 await set('#risk-workspace-trade-mode','all');await wait(idle);
 await check('a parent scope change removes the previous history and its export',"!document.querySelector('#risk-history-results canvas')&&document.querySelector('#risk-history-export').disabled");
}
