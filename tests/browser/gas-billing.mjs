import {selectDeliveryFrame,openPhysicalTrade,clickPhysicalTrade} from './physical-operations-grid.mjs';
// A completed physical delivery can be billed even while contractual PV is unavailable.
export async function run(actions){
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,url,fixture}=actions;
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelector('#snapshot')&&"+idle);await persona('trader');
 await selectDeliveryFrame(actions,'2021-01-15T08:');await click('#nav-futures');
 const key=fixture.tradeId+':'+fixture.bindingId;
 const row=`[...document.querySelectorAll('#desk-positions .tabulator-row')].find(r=>r.dataset.rowKey===${JSON.stringify(key)})`;
 await wait(`!!(${row})&&`+idle);await evaluate(`${row}.click()`);await wait(idle);
 await click('#nav-physical-operations');await openPhysicalTrade(actions,fixture.tradeId);await wait("!document.querySelector('#physical-operations-tabs-cash').disabled&&"+idle);await click('#physical-operations-tabs-cash');
 const root='.gas-billing',field=name=>root+' [data-billing="'+name+'"]';
 await wait(`document.querySelector(${JSON.stringify(field('claim'))})?.options.length>1`);
 await check('gas commercial billing remains available after delivery starts',`!document.querySelector(${JSON.stringify(field('claim'))}).disabled&&document.querySelector('${root}').textContent.includes('Final daily')`);
 const fill=async()=>{for(const [name,value] of [['claim','0'],['policy','final_allocated_billing_v1'],['profile','gas_final_daily_fixed_cash_v1'],['rounding','currency_minor_units_to_even_v1'],['evidence','Synthetic browser zero-allocation correction']])await set(field(name),value);};
 await fill();
 await evaluate(`document.querySelector(${JSON.stringify(field('evidence'))}).focus();document.querySelector(${JSON.stringify(field('evidence'))}).select();`);await command('Input.insertText',{text:'Synthetic kept billing edit'});
 const prompt="#physical-operations-page-prompt",shown=`!document.querySelector('${prompt}').hidden`;
 const answer=async text=>{await evaluate(`[...document.querySelectorAll('${prompt} button')].find(b=>b.textContent===${JSON.stringify(text)}).click()`);await wait(`document.querySelector('${prompt}').hidden&&`+idle);};
 const keep=async(action,label)=>{await action();await wait(shown);await answer('Keep editing');await check(label,`document.querySelector(${JSON.stringify(field('evidence'))})?.value==='Synthetic kept billing edit'&&document.querySelector('#physical-operations-tabs-cash').getAttribute('aria-selected')==='true'`);};
 await keep(()=>click('#physical-operations-tabs-overview'),'Keep editing preserves the billing form across tab changes');
 const originalSession=await evaluate("document.querySelector('#close-session').value"),originalFrame=await evaluate("document.querySelector('#snapshot').value");
 const otherSession=await evaluate("[...document.querySelector('#close-session').options].find(o=>o.value&&o.value!==document.querySelector('#close-session').value)?.value");
 if(!otherSession)throw Error('The synthetic session-switch scenario requires two published close sessions.');
 await keep(()=>set('#close-session',otherSession),'Keep editing refuses a close-session switch');
 await check('a refused session switch preserves the exact header context',`document.querySelector('#close-session').value===${JSON.stringify(originalSession)}&&document.querySelector('#snapshot').value===${JSON.stringify(originalFrame)}`);
 const otherTrade=await evaluate(`[...document.querySelectorAll('#physical-operations-trades .tabulator-row[data-row-key]')].find(r=>r.dataset.rowKey!==${JSON.stringify(key)}).dataset.rowKey.split(':')[0]`);
 await keep(()=>clickPhysicalTrade(actions,otherTrade),'Keep editing refuses a different instrument');
 await keep(()=>click('#nav-market'),'Keep editing refuses page navigation');
 const otherFrame=await evaluate("[...document.querySelector('#snapshot').options].find(o=>o.value&&o.value!==document.querySelector('#snapshot').value).value");
 await keep(()=>set('#snapshot',otherFrame),'Keep editing preserves the selected header context');
 await keep(async()=>{await click('#profile-open');await set('#profile','viewer');await click('#open');},'Keep editing refuses an account switch');
 await keep(async()=>{await click('#profile-open');await set('#zone','America/Chicago');await click('#profile-save');},'Keep editing refuses preference changes that would remount the workflow');
 await click('#physical-operations-tabs-overview');await wait(shown);await answer('Discard changes');await wait("!document.querySelector('#physical-operations-tabs-cash').disabled&&"+idle);await click('#physical-operations-tabs-cash');await wait(`document.querySelector(${JSON.stringify(field('claim'))})?.options.length>1`);await fill();

 await submit(root+' form');
 await wait(`document.querySelector(${JSON.stringify(field('message'))}).textContent.includes('correction reason')`);
 await check('a correction requires explicit cause and due time',`document.querySelector(${JSON.stringify(field('confirm'))}).hidden`);
 await set(field('reason'),'quantity_correction');await set(field('due'),'2021-03-02T00:00:00');await submit(root+' form');
 await wait(`!document.querySelector(${JSON.stringify(field('confirm'))}).hidden`);
 await check('the reviewed zero-quantity correction shows the managed signed difference',`document.querySelector(${JSON.stringify(field('review'))}).textContent.includes('Restated amount: 0')&&document.querySelector(${JSON.stringify(field('review'))}).textContent.includes('New contribution: 294')`);
 await set(field('evidence'),'Synthetic revised browser review');
 await check('editing evidence invalidates the prior review',`document.querySelector(${JSON.stringify(field('confirm'))}).hidden`);
 await submit(root+' form');await wait(`!document.querySelector(${JSON.stringify(field('confirm'))}).hidden`);
 await evaluate(`(()=>{const send=window.fetch;window.__billingFetch=send;window.__billingLost=false;window.__billingRequests=[];window.fetch=async(...args)=>{const determine=String(args[0]).endsWith('/gas/billing/determine');if(determine)window.__billingRequests.push(JSON.parse(args[1].body));const r=await send(...args);if(determine&&r.ok&&!window.__billingLost){window.__billingLost=true;throw new TypeError('Synthetic lost committed billing reply');}return r;};const put=Storage.prototype.setItem;window.__billingPut=put;Storage.prototype.setItem=function(key,value){if(String(value).includes('gas/billing/determine'))throw Error('Synthetic unavailable command storage');return put.call(this,key,value);};})()`);
 await click(field('confirm'));await wait("!document.querySelector('#physical-operations-pending').hidden&&"+idle);
 await check('an unknown committed billing outcome uses the shared retained request card',"document.querySelector('#physical-operations-pending').textContent.includes('Gas cash determination')&&!document.querySelector('#physical-operations-status').disabled");
 await click('#physical-operations-tabs-overview');if(await evaluate(shown))await answer('Discard changes');
 await check('leaving the billing widget preserves its original recovery command',"!document.querySelector('#physical-operations-pending').hidden");
 const retainedRequest=await evaluate('JSON.stringify(window.__billingRequests)');
 await set('#close-session',otherSession);await wait(idle);
 await check('a close-session change retains the original uncertain billing command',`!document.querySelector('#physical-operations-pending').hidden&&JSON.stringify(window.__billingRequests)===${JSON.stringify(retainedRequest)}&&window.__billingRequests.length===1`);
 await wait("!document.querySelector('#physical-operations-status').disabled&&"+idle);await click('#physical-operations-status');await wait("document.querySelector('#physical-operations-pending').hidden&&"+idle);await evaluate('window.fetch=window.__billingFetch;Storage.prototype.setItem=window.__billingPut');
 await selectDeliveryFrame(actions,'2021-01-15T08:');await openPhysicalTrade(actions,fixture.tradeId);await click('#physical-operations-tabs-cash');

 await wait(`!!document.querySelector('${root} tbody')&&document.querySelector('${root}').textContent.includes('Synthetic revised browser review')&&`+idle);
 await check('accepted corrections retain the original and revised cash history',`[...document.querySelectorAll('${root} tbody tr')].some(r=>r.textContent.includes('-300'))&&[...document.querySelectorAll('${root} tbody tr')].some(r=>r.textContent.includes('-294'))&&[...document.querySelectorAll('${root} tbody tr')].some(r=>r.textContent.includes('Synthetic revised browser review'))`);
 await fill();await submit(root+' form');await wait(`document.querySelector(${JSON.stringify(field('message'))}).textContent.includes('already have a cash determination')`);
 await check('the same accepted source cannot create a zero correction',`document.querySelector(${JSON.stringify(field('confirm'))}).hidden`);
 await check('billing fields fit the viewport','document.documentElement.scrollWidth<=innerWidth');await shot('gas-commercial-correction');
 await evaluate(`document.querySelector(${JSON.stringify(field('evidence'))}).focus();document.querySelector(${JSON.stringify(field('evidence'))}).select()`);await command('Input.insertText',{text:'Synthetic discarded session edit'});
 const discardSession=await evaluate("[...document.querySelector('#close-session').options].find(o=>o.value&&o.value!==document.querySelector('#close-session').value).value");
 await set('#close-session',discardSession);await wait(shown);await answer('Discard changes');
 await check('explicit discard applies the chosen close session',`(async()=>{const choice=await(await fetch('/api/close-sessions/choice')).json();return document.querySelector('#close-session').value===${JSON.stringify(discardSession)}&&choice.session===${JSON.stringify(discardSession)};})()`);

}
