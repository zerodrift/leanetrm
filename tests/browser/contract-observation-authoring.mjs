import {mkdtemp,copyFile} from 'node:fs/promises';
import {dirname,join} from 'node:path';
import {tmpdir} from 'node:os';
import {newTrade} from './new-trade.mjs';

// The browser enters only explicit terms. Backend receipts supply fixing instants
// and concrete contracts, and booking/reload must retain those exact choices.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,url}){
 await command('Page.navigate',{url});await wait(idle);await persona('trader');
 const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");
 await set('#snapshot',frame);await wait(idle,30000);await click('#nav-futures',30000);
 const catalog=await evaluate(`(async()=>{const token=(await(await fetch('/api/bootstrap')).json()).csrfToken;window.contractPost=async(path,body)=>{const r=await fetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});if(!r.ok)throw Error(await r.text());return r.json();};const send=window.fetch;window.fetch=async(input,init)=>{const r=await send(input,init);if(String(input).endsWith('/api/swaps/book')&&r.ok)window.contractBooking={request:JSON.parse(init.body),result:await r.clone().json()};return r;};return window.contractPost('swaps/catalog',{snapshotId:${JSON.stringify(frame)},convention:'independent_side_financial_swap_v2',knowledgeMode:'current'});})()`);
 const source=catalog.contractObservationSources.find(s=>s.curve==='future');
 if(!source)throw Error('Missing qualified futures observation source');
 await newTrade({click,wait},'swaps');await click('#swap-scheduled-open');await wait(idle);
 await set('#scheduled-kind','fixed_floating');await set('#scheduled-commodity',source.commodity);await set('#scheduled-market',source.definitionRevisionId);
 await set('#scheduled-direction','-1');await set('#scheduled-discounting','none');await set('#scheduled-price-mode','agreed');
 const sides="document.querySelectorAll('#scheduled-swap-editor .scheduled-side-columns>.instrument-side')";
 const period=slot=>`${sides}[${slot}].querySelector('.scheduled-period')`;
 await evaluate(`(()=>{const put=(n,v)=>{n.value=v;n.dispatchEvent(new Event('change',{bubbles:true}));};for(const side of ${sides}){put(side.querySelector('[data-role=delivery-from]'),'2021-01-01');put(side.querySelector('[data-role=delivery-to]'),'2021-02-01');put(side.querySelector('[data-role=quantity-basis]'),'period_total');const unit=side.querySelector('[data-role=quantity-unit]');put(unit,[...unit.options].find(o=>o.textContent==='MMBtu (IT)').value);}for(const id of ['book','legal-entity','counterparty']){const n=document.querySelector('#scheduled-'+id);put(n,[...n.options].find(o=>o.value).value);}})()`);
 for(const slot of [0,1]){await evaluate(`[...${sides}[${slot}].querySelectorAll('button')].find(b=>b.textContent==='Generate monthly period rows').click()`);await wait(idle);}
 await set('[data-role=period-source-mode]','contract');
 const curveValue=await evaluate("[...document.querySelector('[data-role=contract-curve]').options].find(o=>o.textContent.includes('future')&&!o.textContent.includes('carry')&&!o.textContent.includes('prefix')).value");
 await set('[data-role=contract-curve]',curveValue);
 await check('contract selection has no inferred mode or prompt offset',"document.querySelector('[data-role=contract-selection-mode]').value===''&&document.querySelector('[data-role=contract-offset]').value===''");
 await set('[data-role=contract-selection-mode]','prompt_offset');await set('[data-role=contract-offset]','0');await set('[data-role=contract-roll]','contract_expiry_roll_v1');
 await set('[data-role=contract-expiry-policy]','require_live');await set('[data-role=contract-history-policy]','visible_corrections_at_cutoff_v1');await set('[data-role=contract-role]','daily_settlement');
 const clockValue=await evaluate("[...document.querySelector('[data-role=contract-clock]').options].find(o=>o.value.includes('test_utc')).value");
 await set('[data-role=contract-clock]',clockValue);await set('[data-role=contract-fixing-time]','16:00:00');
 await set('[data-role=contract-ambiguous-time]','reject');await set('[data-role=contract-invalid-time]','reject');
 await evaluate(`(()=>{for(const slot of [0,1]){const p=${sides}[slot].querySelector('.scheduled-period');p.querySelector('[data-role=period-amount]').value='31';const fixed=p.querySelector('[data-role=fixed-price]');if(fixed)fixed.value='0';}const spread=[...${period(1)}.querySelectorAll('input')].find(n=>n.getClientRects().length&&n.previousElementSibling?.textContent==='Agreed floating spread');spread.value='0';spread.dispatchEvent(new Event('input',{bubbles:true}));})()`);await wait(idle);
 for(const slot of [0,1]){await evaluate(`[...${period(slot)}.querySelectorAll('button')].find(b=>b.textContent==='Apply displayed starting payment terms').click()`);await wait(idle);}
 await click('[data-role=contract-preview]');await wait(idle);
 await check('backend contract receipt is visible before booking',"document.querySelector('[data-role=contract-receipt]').textContent.includes('2021-01-01')","document.querySelector('#scheduled-swap-editor [role=alert]').textContent");
 const artifacts=await mkdtemp(join(tmpdir(),'openetrm-contract-ticket-'));console.log('CONTRACT_TICKET_ARTIFACTS='+artifacts);
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:false});
 await evaluate("document.querySelector('[data-role=contract-receipt]').scrollIntoView({block:'start'})");
 await check('contract observation receipt fits the narrow ticket',"document.querySelector('#scheduled-swap-editor').scrollWidth<=document.querySelector('#scheduled-swap-editor').clientWidth+1");
 await shot('contract-ticket-narrow');await copyFile(join(dirname(process.argv[2]),'contract-ticket-narrow.png'),join(artifacts,'narrow.png'));
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 // A dependent change must invalidate the prior source preview, even if the
 // person later enters the same value again.
 await set('[data-role=contract-offset]','1');await set('[data-role=contract-offset]','0');
 await submit('#scheduled-swap-form');await wait(idle);
 await check('edited selection cannot book using an earlier source receipt',"document.querySelector('#scheduled-confirm').parentElement.hidden&&document.querySelector('#scheduled-submit').disabled");
 await click('[data-role=contract-preview]');await wait(idle);await submit('#scheduled-swap-form');await wait(idle);
 await check('dated observation ticket reaches authoritative booking review',"!document.querySelector('#scheduled-swap-editor [role=alert]').textContent&&!document.querySelector('#scheduled-confirm').parentElement.hidden","document.querySelector('#scheduled-swap-editor [role=alert]').textContent");
 await click('#scheduled-confirm');await click('#scheduled-submit',35000);await wait(idle);
 const saved=await evaluate(`(async()=>{const b=window.contractBooking;if(!b)throw Error('No successful contract observation booking');return window.contractPost('swaps/editor',{context:{snapshotId:${JSON.stringify(frame)},convention:'independent_side_financial_swap_v2',knowledgeMode:'current'},tradeId:b.result.tradeId,revision:null});})()`);
 const terms=saved.state.terms.scheduled.second.periods[0].floating;
 if(terms.index!==null||terms.contractObservations.observations.length!==31)throw Error('Source terms were lost or replaced by indexed defaults');
 await set('#blotter-filters .table-search',saved.state.tradeReference);await wait(idle);
 await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait(idle);await click('#side-panel-amend');await wait(idle);
 await check('retained source reopens in contract mode without inventing a fixing clock',"document.querySelector('[data-role=period-source-mode]').value==='contract'&&document.querySelector('[data-role=contract-clock]').value===''");
 await check('retained concrete observations remain visible on reopen',"document.querySelector('[data-role=contract-receipt]').textContent.includes('2021-01-01')");
 return 7;
}
