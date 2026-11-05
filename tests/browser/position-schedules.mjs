export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,url}){
 await command('Page.enable');await command('Runtime.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('trader');
 const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')&&w.initialFrameIds.includes(f.id)).id;})()");
 await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
 await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
 await evaluate("document.querySelector('#desk-positions .desk-pill-swap').closest('.tabulator-row').click()");
 await wait("!!document.querySelector('#position-details-view .side-terms-list')");
 await check('position details show retained terms without a Curve tab or curve plot',"[...document.querySelectorAll('#position-details-tabs [role=tab]')].map(n=>n.textContent).join()==='Terms,Side schedules,Risk,History'&&!document.querySelector('#position-details-view .chart')");
 await click('#position-details-tabs-schedules');
 await wait("document.querySelectorAll('#position-details-view .swap-schedule').length===2&&"+idle);
 await check('each swap side has its own payment schedule with quantity, signed cash and PV',"(()=>{const sides=[...document.querySelectorAll('#position-details-view .swap-schedule')];return sides.length===2&&sides.every(s=>s.querySelector('h3')&&/quantity/i.test(s.textContent)&&s.textContent.includes('PV')&&s.querySelector('.swap-payment-table tbody tr')&&s.querySelector('.swap-pricing-rows th')&&s.textContent.includes('Weight')&&s.textContent.includes('Unit cash contribution'))&&!document.querySelector('#position-details-tabs-curve')&&document.querySelector('#error').hidden;})()","document.querySelector('#position-details-view').innerText");
 await evaluate("document.querySelector('#blotter-pane-details').closest('.dock-group').querySelector('.dock-maximize').click()");
 await shot('position-side-schedules');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:false});
 await wait("document.documentElement.scrollWidth<=document.documentElement.clientWidth+1");
 await check('side schedules fit the narrow detail pane while their tables scroll inside it',"document.documentElement.scrollWidth<=document.documentElement.clientWidth+1&&document.querySelector('#position-details-tabs-schedules').getAttribute('aria-selected')==='true'");
}
