import {newTrade,closeNewTrade} from './new-trade.mjs';
import {bookedTicket,gasTicket,q} from './gas-ticket.mjs';
import {copyFile,mkdtemp} from 'node:fs/promises';
import {dirname,join} from 'node:path';
import {tmpdir} from 'node:os';

export async function run(a){
 const {command,evaluate,wait,click,set,persona,check,idle,url,shot}=a;
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
 // Physical gas is entered in its deal ticket: a receipt at point A whose delivery at point B loses 2% fuel, and a fee on the delivery.
 await newTrade(a,'gas');
 const g=gasTicket(a);
 await wait(`document.querySelectorAll(${q(g.ticket+' .ticket-card')}).length===2`);
 await click(`${g.card('cash-1')} .ticket-remove`);await g.addSide('Physical','physical-2');await g.addSide('Fee','fee-1');
 await g.tradeTerms();await g.physicalTerms('physical-1','receive','1000');await g.physicalTerms('physical-2','deliver',null,{zone:'synthetic_gas_zone_b',point:'synthetic_gas_point_b'});
 await g.choose('physical-2','link_source','physical-1');await g.choose('physical-2','link_formula','gross_less_fraction');await set(g.control('physical-2','link_fraction'),'0.02');
 await g.cashTerms('fee-1','fee','pay','physical-2','0.1');
 const fee=`(document.querySelector(${q(g.card('fee-1')+' .ticket-summary')})?.textContent??'')`;
 await g.until(g.current()+`&&${fee}.includes('-3,038')`);
 await check('the integrated ticket prices the linked sides natively',`${fee}.includes('Billed quantity-30,380')`,fee);
 await capture('unified-gas-ticket');
 // Fail supplementary storage and lose an accepted response. Reopening the
 // ticket must keep the exact request in memory, without another booking.
 await evaluate(`{const original=window.fetch;window.bookRequests=[];Storage.prototype.setItem=function(){throw Error('Storage unavailable');};Storage.prototype.removeItem=function(){throw Error('Storage unavailable');};window.fetch=async(...args)=>{const r=await original(...args);if(String(args[0]).endsWith('/tickets/book')){window.bookRequests.push(JSON.parse(args[1].body));if(r.ok){window.bookReceipt=await r.clone().json();throw new TypeError('Lost accepted response');}}return r;};}`);
 const reconcile="[...document.querySelectorAll('#gas-ticket .ticket-booking button')].find(b=>b.textContent==='Check booking status'&&!b.disabled)";
 await click(`${g.ticket} .ticket-book`);await wait(`!!${reconcile}`);
 await check('uncertain booking blocks replacement terms',`document.querySelector('#gas-ticket > div').inert`);
 await closeNewTrade(a);
 await check('common workflow offers recovery before another new trade',`document.querySelector('#futures-new').disabled&&[...document.querySelectorAll('#futures-retained button')].some(b=>b.textContent==='Reconcile physical gas request')`,`({disabled:document.querySelector('#futures-new').disabled,retained:[...document.querySelectorAll('#futures-retained button')].map(b=>b.textContent),details:document.querySelector('#position-details').hidden})`);
 await evaluate(`[...document.querySelectorAll('#futures-retained button')].find(b=>b.textContent==='Reconcile physical gas request').click()`);await wait(`!!${reconcile}&&${idle}`);
 await check('reopening restores the same pending request with storage unavailable',`document.querySelector('#position-details-form').contains(document.querySelector('#gas-page'))&&document.querySelector('#gas-ticket > div').inert`);
 await evaluate(`${reconcile}.click()`);
 const booked=bookedTicket,opened=gasTicket(a,booked);
 await opened.until(`!!document.querySelector(${q(booked)})&&`+opened.current(booked));
 await check('confirmed booking opens common retained trade terms exactly once',`window.bookRequests.length===1&&window.tradeReads.at(-1).blotter.holdings.filter(h=>h.tradeId===window.bookReceipt.tradeId).length===1&&document.querySelector('#position-details').textContent.includes('Physical gas trade')`, `({error:document.querySelector('#error').textContent,details:document.querySelector('#position-details').textContent.slice(0,300)})`);
 // A day's quantity is recorded from the booked physical side's tab, without changing its contract.
 await set(`${booked} .ticket-level`,'day');await click(`${booked} .ticket-tab[data-tab="physical-1"]`);
 const cell=`${booked} .tabulator-row[data-row-key="2021-01-05"] .ticket-cell-input`,record=`${booked} .ticket-record`;
 await wait(`!!document.querySelector(${q(cell)})`);await set(cell,'990');await wait(`!!document.querySelector(${q(record)})`);
 for(const [id,value] of [['kind','nominated'],['status','provisional'],['evidence','Daily nomination']])await set(`${record} [data-field="${id}"] .ticket-control`,value);
 await click(`${record} .ticket-record-send`);
 await opened.until(`${opened.row('2021-01-05',booked)}.includes('Nominated')&&!document.querySelector(${q(record)})`);
 await check('an operational day record keeps the contractual amount',`[...document.querySelectorAll(${q(booked+' .tabulator-row[data-row-key="2021-01-05"] .grid-source')})].map(t=>t.textContent).includes('Contract')`);
 await check('all integrated forms fit the desktop viewport',`document.documentElement.scrollWidth<=innerWidth`);
 await capture('unified-trading');
}
