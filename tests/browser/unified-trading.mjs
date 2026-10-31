import {newTrade,closeNewTrade} from './new-trade.mjs';
import {copyFile,mkdtemp} from 'node:fs/promises';
import {dirname,join} from 'node:path';
import {tmpdir} from 'node:os';

export async function run(a){
 const {command,evaluate,wait,click,set,submit,persona,check,idle,url,shot}=a;
 const artifacts=await mkdtemp(join(tmpdir(),'openetrm-unified-trading-'));console.log('VISUAL_ARTIFACTS='+artifacts);
 const capture=async name=>{await shot(name);await copyFile(join(dirname(process.argv[2]),name+'.png'),join(artifacts,name+'.png'));};
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait(idle);await persona('trader');
 await evaluate(`{const original=window.fetch;window.tradeReads=[];window.fetch=async(...args)=>{const r=await original(...args);if(String(args[0]).endsWith('/trading/workspace')&&r.ok)window.tradeReads.push(await r.clone().json());return r;};}`);
 const frame=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>w.initialFrameIds.includes(f.id)&&f.at.startsWith('2020-12-18')).id;})()`);
 await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
 await check('one blotter includes futures, financial swaps, heat rates, PPAs and physical gas',`['future','financial_swap','heat_rate','ppa','physical_gas'].every(t=>window.tradeReads.at(-1)?.blotter.holdings.some(h=>h.instrumentType===t))`,`({types:window.tradeReads.at(-1)?.blotter.holdings.map(h=>h.instrumentType),error:document.querySelector('#error').textContent})`);
 await check('gas has an explicit contractual PV measure in the common workspace',`[...document.querySelectorAll('#blotter-strip .strip-item .strip-label')].some(l=>l.textContent==='Gas contractual PV')`);
 await capture('unified-blotter');
 for(const [product,selector] of [['futures','#futures-new-trade'],['swaps','#swap-editing'],['ppa','#ppa-page'],['gas','#gas-page']]){
  await newTrade(a,product);
  await check(product+' enters through the same trade panel',`document.querySelector('#page-title').textContent==='Blotter'&&document.querySelector('#position-details-form').contains(document.querySelector('${selector}'))&&!document.querySelector('${selector}').hidden`);
  await closeNewTrade(a);
  await check(product+' closes without retaining a browser page-leave warning',`(()=>{const event=new Event('beforeunload',{cancelable:true});window.dispatchEvent(event);return !event.defaultPrevented;})()`);
 }
 await newTrade(a,'gas');
 const field=(n,key)=>`#gc-physical [data-physical="${n}"] [data-field="${key}"]`;
 const cash=(n,key)=>`#gc-cash [data-cash="${n}"] [data-field="${key}"]`;
 for(const id of ['gc-book','gc-entity','gc-party'])await set('#'+id,await evaluate(`document.querySelector('#${id}').options[1].value`));
 await set('#gc-policy','no_discounting');
 async function physical(n,zone,point,direction,mode){
  for(const [key,value] of Object.entries({direction,product:'test_gas',pipeline:'test_pipeline_a',zone,deliveryPoint:point,deliveryFrom:'2021-01-01',deliveryTo:'2021-02-01',quantityBasis:'period_total_equal_days',mode}))await set(field(n,key),value);
 }
 await physical(0,'synthetic_gas_zone_a','synthetic_gas_point_a','receive','independent');await set(field(0,'quantity'),'1000');await click('#gc-add-physical');
 await physical(1,'synthetic_gas_zone_b','synthetic_gas_point_b','deliver','linked');await set(field(1,'source'),'gas_1');await set(field(1,'formula'),'gross_less_fraction');await set(field(1,'fraction'),'0.02');
 await click('#gc-add-cash');
 for(const [key,value] of Object.entries({role:'fee',direction:'pay',source:'gas_2',stage:'delivery',tariffCode:'manual',price:'0.1',paymentAt:'2021-02-02T00:00'}))await set(cash(0,key),value);
 await submit('#gc-form');
 await check('the integrated ticket uses native linked-side pricing',`document.querySelector('#gc-total').textContent==='Present value: 64 USD'`, `document.querySelector('#error').textContent`);
 await capture('unified-gas-ticket');
 // Fail supplementary storage and lose an accepted response. Remounting the
 // editor must keep the exact operation in memory, without another booking.
 await evaluate(`{const original=window.fetch;window.bookRequests=[];Storage.prototype.setItem=function(){throw Error('Storage unavailable');};Storage.prototype.removeItem=function(){throw Error('Storage unavailable');};window.fetch=async(...args)=>{const r=await original(...args);if(String(args[0]).endsWith('/gas/composition/book')){window.bookRequests.push(JSON.parse(args[1].body));if(r.ok){window.bookReceipt=await r.clone().json();throw new TypeError('Lost accepted response');}}return r;};}`);
 await click('#gc-book-submit');
 await check('uncertain booking blocks replacement terms',`!document.querySelector('#gc-pending').hidden&&document.querySelector('#gc-policy').disabled`);
 await closeNewTrade(a);
 await check('common workflow offers recovery before another new trade',`document.querySelector('#futures-new').disabled&&[...document.querySelectorAll('#futures-retained button')].some(b=>b.textContent==='Reconcile physical gas request')`);
 await evaluate(`[...document.querySelectorAll('#futures-retained button')].find(b=>b.textContent==='Reconcile physical gas request').click()`);await wait(idle);
 await check('remount restores the same pending request with storage unavailable',`!document.querySelector('#gc-pending').hidden&&document.querySelector('#position-details-form').contains(document.querySelector('#gc-page')??document.querySelector('#gas-page'))`);
 await click('#gc-check');
 await wait(`!!document.querySelector('#gas-operations')&&${idle}`);
 await check('confirmed booking opens common retained trade terms exactly once',`window.bookRequests.length===1&&window.tradeReads.at(-1).blotter.holdings.filter(h=>h.tradeId===window.bookReceipt.tradeId).length===1&&document.querySelector('#position-details').textContent.includes('Physical gas trade')`, `({error:document.querySelector('#error').textContent,details:document.querySelector('#position-details').textContent})`);
 await click('#gas-operations');
 await check('daily operations open from that position without changing its contract',`document.querySelector('#position-details-form').contains(document.querySelector('#gq-grid'))&&document.querySelectorAll('#gq-grid tbody tr').length===62`, `({error:document.querySelector('#error').textContent,rows:document.querySelectorAll('#gq-grid tbody tr').length,mode:document.querySelector('#position-details').dataset.mode,form:document.querySelector('#position-details-form').hidden,parent:document.querySelector('#gas-page').parentElement.id})`);
 await set('#gq-row','0');await set('#gq-kind','nominated');await set('#gq-status','provisional');await set('#gq-quantity','31');await set('#gq-evidence','Daily nomination');await submit('#gq-form');
 await check('operational revision preserves contractual delivery quantities',`document.querySelector('#gq-grid tbody tr').textContent.includes('1000/31')&&document.querySelector('#gq-grid tbody tr').textContent.includes('31 · provisional')`);
 await check('all integrated forms fit the desktop viewport',`document.documentElement.scrollWidth<=innerWidth`);
 await capture('unified-trading');
}
