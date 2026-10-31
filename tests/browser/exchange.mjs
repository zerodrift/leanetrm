import {chooseFutures} from './futures-selection.mjs';
import {newTrade} from './new-trade.mjs';
import {newTarget,popOut,press} from './blotter-window.mjs';
export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,url,tab}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/?example=exchange'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await evaluate(`(()=>{const send=window.fetch;window.__exchangeCatalog=null;window.fetch=async(input,init)=>{const response=await send(input,init);if(String(input).endsWith('/api/trading/workspace')&&response.ok){const data=await response.clone().json();if(data.catalog.kind==='priced_contracts')window.__exchangeCatalog=data.catalog;}return response;};})()`);
 await persona('trader');await click('#example');await click('#nav-futures');
 await check('exchange dataset is visibly selected and prices are identified as synthetic',"document.querySelector('#example-dataset').value==='exchange'&&document.querySelector('#example-dataset').selectedOptions[0].textContent.includes('synthetic prices')");
 await wait("document.querySelectorAll('#desk-positions .sparkline-loading').length===0&&!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')");
 await check('the exchange dataset opens on the desk: its futures, their execution P&L tile and their curves',"document.querySelector('#error').hidden&&!document.querySelector('#desk').hidden&&!![...document.querySelectorAll('#blotter-strip .strip-item')].find(i=>i.querySelector('.strip-label').textContent==='Futures execution P&L')?.dataset.currency&&document.querySelectorAll('#desk-positions svg.sparkline').length===document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)').length&&[...document.querySelectorAll('#desk-positions .desk-pill')].every(p=>p.textContent==='FUT')");
 await click('#profile-open');await check('Profile does not expose deal amount precision',"!document.querySelector('#precision-money')&&!document.querySelector('#precision-price')");await click('#profile-close');
 await newTrade({click,wait},'futures');
 const select=async(curve,day)=>{const c=await evaluate(`window.__exchangeCatalog.contracts.find(c=>c.curve===${JSON.stringify(curve)}&&new Intl.DateTimeFormat('en',{month:'short',timeZone:'UTC'}).format(new Date(c.deliveryFrom+'T00:00:00Z'))===${JSON.stringify(day)})`);if(!c)throw Error('Missing '+curve+' '+day);await chooseFutures({evaluate,set,click},c);};
 await select('nymex_ng','Nov');
 await check('physical Henry Hub futures show typed lot, execution tick and lifecycle scope',"(()=>{const t=document.querySelector('#futures-terms').textContent;return t.includes('10000 MMBtu')&&t.includes('physical delivery')&&t.includes('Execution tick 0.001')&&t.includes('daily/final settlement is not processed');})()");
 await select('ice_spm','Nov');await check('monthly power distinguishes nominal MW from dated MWh per lot',"(()=>{const t=document.querySelector('#futures-terms').textContent;return t.includes('384 MWh per lot')&&t.includes('nominal 1 MW');})()");
 await select('ice_spm','Jan');await check('different delivery periods retain their own published power lot',"document.querySelector('#futures-terms').textContent.includes('400 MWh per lot')");
 await select('ice_wah','Nov');await check('basis future shows its exact benchmark and independent quote meaning',"(()=>{const t=document.querySelector('#futures-terms').textContent;return t.includes('waha_bidweek minus nymex_ng_20261101')&&t.includes('own market factor');})()");
 await check('unconfirmed daily maturities are excluded from trading',"!window.__exchangeCatalog.contracts.some(c=>['ice_css','ice_pda'].includes(c.curve))");
 await shot('exchange-contract-entry');
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');await click('#example');await click('#nav-futures');
 await newTrade({click,wait},'futures');
 await check('returning to the original example restores its independent dated catalog',"document.querySelector('#example-dataset').value==='baseline'&&document.querySelector('#futures-contract').options.length===1");
 // The Blotter popped out of the exchange example reads that example, and so does a position's own window opened from it. A fresh page, as
 // pressing Pop out is a person's gesture, after which the page would ask before it is left holding the edits above.
 await command('Page.navigate',{url:url+'/?example=exchange'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await click('#example');await click('#nav-futures');await wait("!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&"+idle,30000);
 const popup=await popOut({command,evaluate,url,tab});
 await check('the pop-out Blotter reads the exchange example it was popped out of',JSON.stringify(await popup.evaluate("performance.getEntriesByType('resource').some(r=>new URL(r.name).pathname==='/examples/exchange/api/trading/workspace')&&!performance.getEntriesByType('resource').some(r=>new URL(r.name).pathname==='/api/trading/workspace')")));
 const row="#desk-positions .tabulator-row[data-row-key] .desk-reference";
 const position=await newTarget(command,async()=>{await press(popup,row,1);await press(popup,row,2);},url+'/position.html#');
 if(!position)throw Error('A double click in the pop-out opened no trade window.');
 await check('a position\'s own window opened from the pop-out names the exchange example',JSON.stringify(new URLSearchParams(new URL(position.url).hash.slice(1)).get('example')==='exchange'));
 await command('Target.closeTarget',{targetId:position.targetId});await popup.close();
 await wait("document.querySelector('#blotter-away').hidden&&"+idle,30000);
}
