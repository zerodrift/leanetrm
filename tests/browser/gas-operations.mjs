import {selectDeliveryFrame,openPhysicalTrade,openGasDelivery} from './physical-operations-grid.mjs';
// Real browser: manual scheduling review, trusted unsaved edits and shared recovery.
export async function run(actions){
 const {command,evaluate,wait,click,set,submit,persona,check,shot,idle,url,fixture}=actions;
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelector('#snapshot')&&"+idle);await persona('trader');
 await selectDeliveryFrame(actions,'2021-01-15T08:');await click('#nav-futures');
 const key=fixture.tradeId+':'+fixture.bindingId,row=`[...document.querySelectorAll('#desk-positions .tabulator-row')].find(r=>r.dataset.rowKey===${JSON.stringify(key)})`;
 await wait(`!!(${row})&&`+idle);await evaluate(`${row}.click()`);await wait("document.querySelector('#position-details-tabs-schedules')&&"+idle);await click('#position-details-tabs-schedules');
 await check('Side schedules contains only schedule inspection, with no physical work forms',"!document.querySelector('.gas-operations')&&!document.querySelector('.gas-billing')&&!document.querySelector('.ticket-cell-input')");
 await click('#nav-physical-operations');await openPhysicalTrade(actions,fixture.tradeId);
 await wait("document.querySelector('#physical-operations-tabs-overview')?.getAttribute('aria-selected')==='true'&&document.querySelector('#physical-operations-view').textContent.includes('Pipeline')&&"+idle);
 await check('physical operations starts with an instrument overview without requiring PV',"document.querySelector('#physical-operations-view').textContent.includes('Pipeline')&&[...document.querySelectorAll('#physical-operations-view .side-term')].some(r=>r.querySelector('dt').textContent==='Quantity'&&r.querySelector('dd').textContent.startsWith('100 '))&&document.querySelector('#physical-operations-message').textContent===''&&!document.querySelector('.gas-operations')");
 await click('#physical-operations-tabs-delivery');
 const root='.gas-operations',field=name=>root+' [name="'+name+'"]';
 await openGasDelivery(actions,'2021-01-01');
 await wait(`!document.querySelector('${root} [data-operation=nomination]').disabled&&`+idle);
 await check('gas delivery offers four stages without a version selector',`[...document.querySelectorAll('${root} [data-stage-title]')].map(n=>n.textContent).join('|')==='Before flow|Scheduling|After flow|Reconciliation'&&!document.querySelector('${root} [name=serviceVersionId]')`);
 await click(root+' [data-operation=nomination]');await wait(`!!document.querySelector(${JSON.stringify(field('quantity'))})`);
 await check('the sole service account is selected and the external connector stays TBD',`document.querySelector(${JSON.stringify(field('accountId'))}).value===${JSON.stringify(fixture.accountId)}&&document.querySelector('${root} .operations-mode').textContent.includes('Live pipeline connection · TBD')`);
 const fill=async()=>{for(const [name,value] of [['externalReference','synthetic-browser-retro'],['quantity','85'],['submittedAt','2021-01-13T00:00:00'],['channel','portal'],['reason','Synthetic agreed retro request'],['evidence','Synthetic browser submission evidence']])await set(field(name),value);};
 await fill();await evaluate(`document.querySelector(${JSON.stringify(field('evidence'))}).focus();document.querySelector(${JSON.stringify(field('evidence'))}).select()`);await command('Input.insertText',{text:'Synthetic trusted operator edit'});
 await click('#physical-operations-tabs-cash');await wait("!document.querySelector('#physical-operations-page-prompt').hidden");
 await evaluate("[...document.querySelectorAll('#physical-operations-page-prompt button')].find(b=>b.textContent==='Keep editing').click()");await wait("document.querySelector('#physical-operations-page-prompt').hidden&&"+idle);
 await check('Keep editing preserves the operational form and selected tab',`document.querySelector(${JSON.stringify(field('evidence'))})?.value==='Synthetic trusted operator edit'&&document.querySelector('#physical-operations-tabs-delivery').getAttribute('aria-selected')==='true'`);
 await submit(root+' [data-ops=editor] form');await wait(`!document.querySelector('${root} [data-ops=confirm]').hidden||document.querySelector('${root} [data-ops=message]').textContent.includes('Error')`);if(await evaluate(`document.querySelector('${root} [data-ops=confirm]').hidden`))throw Error(await evaluate(`document.querySelector('${root} [data-ops=message]').textContent+' · '+JSON.stringify([...document.querySelectorAll('${root} form input,${root} form select')].map(n=>[n.name,n.value,n.disabled,n.validationMessage]))`));
 await check('the native review identifies a retro request and its nominated-only effect',`document.querySelector('${root} [data-ops=review]').textContent.includes('retro')&&document.querySelector('${root} [data-ops=review]').textContent.includes('nominated quantity only')`);
 await set(field('evidence'),'Synthetic reviewed browser request');await check('changing external evidence invalidates the old review',`document.querySelector('${root} [data-ops=confirm]').hidden`);
 await submit(root+' [data-ops=editor] form');await wait(`!document.querySelector('${root} [data-ops=confirm]').hidden`);
 await evaluate("(()=>{const send=window.fetch;window.__opsFetch=send;window.__opsLost=false;window.fetch=async(...args)=>{const r=await send(...args);if(r.ok&&String(args[0]).endsWith('/gas/operations/record')&&!window.__opsLost){window.__opsLost=true;throw new TypeError('Synthetic lost committed operator reply');}return r;};})()");
 await click(root+' [data-ops=confirm]');await wait("!document.querySelector('#physical-operations-pending').hidden&&"+idle);
 await check('an unknown operational outcome uses the shared retained command card',"document.querySelector('#physical-operations-pending').textContent.includes('Submitted nomination')&&!document.querySelector('#physical-operations-status').disabled");
 await click('#physical-operations-tabs-overview');if(await evaluate("!document.querySelector('#physical-operations-page-prompt').hidden"))await evaluate("[...document.querySelectorAll('#physical-operations-page-prompt button')].find(b=>b.textContent==='Discard changes').click()");
 await wait("!document.querySelector('#physical-operations-status').disabled&&"+idle);await click('#physical-operations-status');await wait("document.querySelector('#physical-operations-pending').hidden||!document.querySelector('#error').hidden||document.querySelector('#physical-operations-message').textContent.includes('No committed')");if(await evaluate("!document.querySelector('#physical-operations-pending').hidden"))throw Error(await evaluate("document.querySelector('#error').textContent+' · '+document.querySelector('#physical-operations-message').textContent"));await evaluate('window.fetch=window.__opsFetch');await click('#physical-operations-tabs-delivery');
 await openGasDelivery(actions,'2021-01-01');await wait(`document.querySelector('${root} [data-ops=history]').textContent.includes('synthetic-browser-retro')&&`+idle);
 await check('status recovery retains one external request without repeating submission',`[...document.querySelectorAll('${root} [data-ops=history] tbody tr')].filter(r=>r.textContent.includes('Submitted request')&&r.textContent.includes('synthetic-browser-retro')).length===1`);
 await evaluate("document.querySelector('.gas-operations').scrollIntoView({block:'start'});");
 await check('the four-stage desk fits the viewport','document.documentElement.scrollWidth<=innerWidth');await shot('gas-manual-delivery-desk');
}
