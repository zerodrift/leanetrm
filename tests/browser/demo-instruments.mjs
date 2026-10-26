import {mkdtemp,copyFile} from 'node:fs/promises';
import {dirname,join} from 'node:path';
import {tmpdir} from 'node:os';
import {newTrade} from './new-trade.mjs';

export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,fitsWidth,url}){
 const artifacts=await mkdtemp(join(tmpdir(),'openetrm-demo-visual-'));console.log('VISUAL_ARTIFACTS='+artifacts);
 const screenshot=async name=>{await shot(name);await copyFile(join(dirname(process.argv[2]),name+'.png'),join(artifacts,name+'.png'));};
 await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('trader');
 await evaluate(`(()=>{const send=window.fetch;window.demoReads=[];window.fetch=async(...args)=>{const response=await send(...args);if(args[0]==='/api/trading/workspace'&&response.ok)window.demoReads.push(await response.clone().json());return response;};})()`);
 const frame=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18T16:00:00')).id;})()`);
 await set('#snapshot',frame);await wait(idle,30000);await click('#nav-futures',30000);await wait(idle,30000);
 await check('shared blotter renders PPA and heat-rate totals alongside futures and swaps',`(()=>{const r=window.demoReads.at(-1)?.blotter;return r?.holdings.length===37&&document.querySelector('#desk-tile-ppaValue')?.textContent.includes('PPA value excluding overdue cash')&&document.querySelector('#desk-tile-heatRatePv')?.textContent.includes('Heat-rate PV')&&document.querySelector('#error').hidden;})()`,`({reads:window.demoReads.map(r=>({keys:Object.keys(r),holdings:r.blotter?.holdings.length})),error:document.querySelector('#error').textContent,ppa:document.querySelector('#desk-tile-ppaValue')?.textContent,heat:document.querySelector('#desk-tile-heatRatePv')?.textContent})`);
 await screenshot('mixed-instrument-blotter');
 const ppa=await evaluate(`window.demoReads.at(-1).blotter.holdings.find(h=>h.instrumentType==='ppa').tradeReference`);
 await set('#blotter-filters .table-search',ppa);await screenshot('ppa-filter');await wait(`(()=>{const rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.getClientRects().length);return rows.length===1&&rows[0].textContent.includes(${JSON.stringify(ppa)});})()`).catch(async error=>{throw Error(error.message+' · '+JSON.stringify(await evaluate(`({count:document.querySelector('#blotter-filters').textContent,query:document.querySelector('#blotter-filters .table-search').value,rows:[...document.querySelectorAll('#desk-positions .tabulator-row')].map(r=>r.textContent),error:document.querySelector('#error').textContent})`)));});await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("document.querySelector('#side-panel').dataset.mode==='position'&&"+idle,30000);
 await check('PPA position uses its own measure and retained terms in the common position panel',`document.querySelector('#side-panel-head').textContent.includes('Value excluding overdue cash')&&document.querySelector('#side-panel-body').textContent.includes('Overdue known')&&document.querySelector('#error').hidden`,`({head:document.querySelector('#side-panel-head').textContent,body:document.querySelector('#side-panel-body').textContent,error:document.querySelector('#error').textContent})`);
 await click('#side-panel-tabs-terms');await wait("!document.querySelector('#side-panel-body').textContent.includes('Loading…')",30000);
 await check('PPA terms name its quantity rule and delivery instead of calling the futures editor',`document.querySelector('#side-panel').textContent.includes('PPA trade')&&document.querySelector('#side-panel').textContent.includes('Quantity rule')`,`document.querySelector('#side-panel').textContent`);
 await screenshot('ppa-in-shared-position-panel');await click('#side-panel-close');await wait(idle);
 await set('#blotter-filters .table-search','');await newTrade({click,wait},'swaps');await click('#swap-scheduled-open');await wait(idle);
 await set('#scheduled-kind','factor_floating');await set('#scheduled-commodity','test_power');await set('#scheduled-second-commodity','test_gas');
 const market=await evaluate(`[...document.querySelector('#scheduled-market').options].find(o=>o.value)?.value`);await set('#scheduled-market',market);
 await check('heat-rate entry exposes independent commodity and explicit dimensional rate controls',`!document.querySelector('#scheduled-second-commodity').parentElement.hidden&&document.querySelector('#scheduled-factor-value')&&document.querySelector('#scheduled-factor-numerator')&&document.querySelector('#scheduled-factor-denominator')&&document.querySelectorAll('.scheduled-side-columns>.instrument-side').length===2`);
 await set('#scheduled-commodity','test_gas');await set('#scheduled-second-commodity','test_power');await set('#scheduled-market',market);
 await check('second floating power side derives quantities from the explicit factor',`[...document.querySelectorAll('.scheduled-side-columns>.instrument-side')][1].querySelector('[data-role=quantity-basis]').value==='factor_of_first_side_v1'`);
 await set('#scheduled-commodity','test_power');await set('#scheduled-second-commodity','test_gas');await set('#scheduled-market',market);
 await screenshot('heat-rate-entry');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await check('heat-rate ticket fits a narrow viewport',fitsWidth);await screenshot('heat-rate-entry-mobile');
}
