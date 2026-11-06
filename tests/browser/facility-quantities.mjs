import {openPhysical} from './physical-workspace.mjs';
import {openBookedSchedules} from './gas-ticket.mjs';
import {inspectPositionSide} from './position-schedules.mjs';
import {openPhysicalTrade,openGasDelivery} from './physical-operations-grid.mjs';
const q=JSON.stringify;

export async function run(actions){
 const {command,evaluate,wait,set,click,submit,persona,check,shot,idle,url,fixture,fitsWidth}=actions;
 const open=()=>openPhysical({click,evaluate,wait},'facilities');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 const {frame,session}=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();const f=w.frames.find(f=>f.at.startsWith('2021-01-15T08:')&&w.initialFrameIds.includes(f.id));return {frame:f.id,session:f.close?.session??null};})()");
 // Where this frame is a recorded close, the As of list offers it under its own session (D334).
 if(session){await set('#close-session',session);await wait(idle,30000);}
 await set('#snapshot',frame);await wait(idle,30000);await open();
 await set('#facility-ledger',fixture.ledgerId);await wait("!document.querySelector('#facility-editor').hidden&&"+idle).catch(async error=>{throw Error(error.message+' · '+JSON.stringify(await evaluate("({page:document.querySelector('#page-title').textContent,error:document.querySelector('#error').textContent,context:document.querySelector('#facility-context').textContent,selected:document.querySelector('#facility-ledger').value,options:document.querySelector('#facility-ledger').innerHTML})")));});
 const shown="document.querySelector('#facility-balance').textContent",saved="document.querySelector('#facility-saved').textContent";
 await check('Facilities uses the compact shared business date and native quantities',
  `${shown}.includes('100 MMBtu')&&${shown}.includes('95 MMBtu')&&${shown}.includes('5 MMBtu')&&document.querySelector('#date-context-on').type==='date'&&document.querySelector('#market-date-calendar').closest('.date-control').hidden`,shown);
 await check('a measured breach remains visible and the loaded allocation controls work',
  "document.querySelector('#facility-exception').dataset.state==='breached'&&document.querySelector('#facility-exception').textContent.includes('10 MMBtu')&&[...document.querySelectorAll('[data-facility-allocation]')].length===3&&[...document.querySelectorAll('[data-facility-allocation]')].every(n=>!n.disabled)&&!document.querySelector('#facility-allocation-table .table-filters input').disabled");
 await shot('facility-quantity-ledger');
 // The shared picker emits a synthetic select change. Its actual button click
 // must still hold the replacement plan as an unsaved edit.
 await evaluate("document.querySelector('#facility-targets-picker').open=true;window.confirm=()=>{window.facilityAsked=true;return false;}");
 await click('#facility-targets-picker .scope-chip button');await click('#facility-refresh');
 await check('removing an allocation with the shared picker retains unsaved edits on Keep',
  "window.facilityAsked===true&&document.querySelectorAll('[data-facility-allocation]').length===2");
 await evaluate('window.confirm=()=>true');await click('#facility-refresh');
 await set('#facility-effective','2021-01-15T17:00');await set('#facility-plan-evidence','Synthetic browser allocation check');await click('#facility-preview');
 await check('allocation validation and operating assessment come from the backend',
  "document.querySelector('#facility-preview-result').textContent.includes('Allocated 95 · unallocated 5')&&document.querySelector('#facility-preview-result').textContent.includes('breached')");
 await evaluate("document.querySelector('#facility-source-evidence').focus()");await command('Input.insertText',{text:'Synthetic trusted edit'});
 await check('trusted facility edits register the browser unload guard',"!window.dispatchEvent(new Event('beforeunload',{cancelable:true}))");
 await set('#facility-source-quantity','94');await set('#facility-source-evidence','Synthetic browser correction');await submit('#facility-source-form');
 await check('a source correction preserves the existing plan and requires reconciliation',
  `${saved}.includes('Saved revision')&&${shown}.includes('94 MMBtu')&&${shown}.includes('-1 MMBtu')&&!document.querySelector('#facility-reconcile').hidden`,shown);
 await set('#facility-effective','2021-01-15T17:01');await set('#facility-source-quantity','100');await set('#facility-source-evidence','Synthetic browser restored source');
 await evaluate(`window.facilityRequests=[];window.originalFacilityFetch=window.fetch;window.fetch=async(...args)=>{const response=await originalFacilityFetch(...args);
  if(String(args[0]).endsWith('/facilities/quantities/record')){facilityRequests.push(JSON.parse(args[1].body));if(response.ok)throw new TypeError('Synthetic lost committed response');}return response;};`);
 await submit('#facility-source-form');
 await check('an unconfirmed facility command retains its exact request and locks new changes',
  "!document.querySelector('#facility-pending').hidden&&document.querySelector('#facility-source-fields').disabled&&facilityRequests.length===1");
 // Browser storage can be unavailable. Navigation still retains the original
 // command in actor-scoped memory and status recovery does not resubmit it.
 await evaluate(`window.fetch=window.originalFacilityFetch;window.facilityStorage={};for(const name of ['getItem','setItem','removeItem']){facilityStorage[name]=Storage.prototype[name];Storage.prototype[name]=function(key,...rest){if(String(key).startsWith('openetrm.facilities.'))throw Error('Synthetic unavailable storage');return facilityStorage[name].call(this,key,...rest);};}`);
 await click('#nav-reference');await open();
 await check('an uncertain command survives navigation when browser storage is unavailable',
  "!document.querySelector('#facility-pending').hidden&&document.querySelector('#facility-pending-description').textContent.includes('2021-01-15T17:01')");
 await click('#facility-status');
 await check('status reconciles the original command without a second fact',
  "document.querySelector('#facility-pending').hidden&&document.querySelector('#facility-source-quantity').value==='100'&&document.querySelector('#error').hidden");
 await evaluate("for(const name of ['getItem','setItem','removeItem'])Storage.prototype[name]=facilityStorage[name]");
 // Saving a different form cannot silently discard a staged allocation.
 await evaluate("document.querySelector('[data-facility-allocation]').focus()");await command('Input.insertText',{text:'1'});
 const draft=await evaluate("[...document.querySelectorAll('[data-facility-allocation]')].map(n=>n.value)");
 await set('#facility-effective','2021-01-15T17:02');await set('#facility-limit-quantity','110');
 await set('#facility-limit-evidence','Synthetic browser maximum');await evaluate('window.confirm=()=>false');await submit('#facility-limit-form');
 await check('saving another form asks before discarding a staged allocation',
  `[...document.querySelectorAll('[data-facility-allocation]')].map(n=>n.value).join()===${q(draft.join())}&&document.querySelector('#facility-limit-quantity').value==='110'&&document.querySelector('#facility-exception').dataset.state==='breached'`);
 await evaluate('window.confirm=()=>true');await set('#facility-limit-evidence','Synthetic browser maximum');await submit('#facility-limit-form');
 await check('an explicitly configured limit updates the operating assessment',"document.querySelector('#facility-exception').dataset.state==='within_limit'");
 await set('#facility-effective','2021-01-15T17:03'); await evaluate("document.querySelector('#facility-limit-remove').checked=true;document.querySelector('#facility-limit-remove').dispatchEvent(new Event('change',{bubbles:true}));");
 await set('#facility-limit-evidence','Synthetic browser withdrawal');await submit('#facility-limit-form');
 await check('removing the latest limit leaves assessment unassessed without an older fallback',"document.querySelector('#facility-exception').dataset.state==='unassessed'&&document.querySelector('#facility-limit-quantity').value===''");
 await set('#facility-effective','2021-01-15T17:04');await set('#facility-limit-quantity','105');await set('#facility-limit-evidence','Synthetic request not delivered');
 await evaluate("window.notDeliveredFetch=window.fetch;window.fetch=(...args)=>String(args[0]).endsWith('/facilities/quantities/record')?Promise.reject(new TypeError('Synthetic request not delivered')):notDeliveredFetch(...args)");
 await submit('#facility-limit-form');await evaluate('window.fetch=window.notDeliveredFetch');await click('#facility-status');
 await check('a request without a receipt visibly offers retry with its original identity',"!document.querySelector('#facility-pending').hidden&&document.querySelector('#facility-pending-description').textContent.includes('No completed receipt')");
 await click('#facility-retry');
 await check('retrying an undelivered request records its original limit once',"document.querySelector('#facility-pending').hidden&&document.querySelector('#facility-limit-quantity').value==='105'&&document.querySelector('#error').hidden");
 await command('Page.reload');await wait("!document.querySelector('#facilities-page').hidden&&!document.querySelector('#facility-editor').hidden&&"+idle);
 await check('reload restores the Facilities page and selected ledger',`document.querySelector('#facility-ledger').value===${q(fixture.ledgerId)}`);
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await shot('facility-quantity-ledger-mobile');
 await check('Facilities fits a narrow viewport',fitsWidth,"[...document.querySelectorAll('#facilities-page *')].filter(n=>n.getBoundingClientRect().right>document.documentElement.clientWidth+1).slice(0,15).map(n=>({tag:n.tagName,id:n.id,cl:n.className,width:n.getBoundingClientRect().width,right:n.getBoundingClientRect().right}))");
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await click('#knowledge-context-summary');await click('#date-context-latest');await click('#apply-view-context');
 await check('Apply replaces working facts with the exact historical view and disables writes',
  "!document.querySelector('#facility-editor').hidden&&document.querySelector('#facility-source-fields').disabled&&document.querySelector('#facility-plan-save').disabled&&document.querySelector('#workspace-mode-label').textContent.includes('Historical')&&document.querySelector('#snapshot').value!==''&&[...document.querySelectorAll('[data-facility-trade]')].every(n=>!n.disabled&&!n.closest('fieldset').disabled)");
 // Historical writes remain disabled. Trade inspection retains its contextual
 // close; choose the later valuation date for the following read.
 await click('#nav-futures');
 const inspectionFrame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2021-01-31T08:')&&w.initialFrameIds.includes(f.id)).id;})()");
 await set('#snapshot',inspectionFrame);await wait(idle);await open();
 await evaluate(`window.facilityTicketPayload=null;window.facilityTradeFetch=window.fetch;window.fetch=async(...args)=>{const response=await facilityTradeFetch(...args);if(String(args[0]).endsWith('/trading/gas')&&response.ok)window.facilityTicketPayload=await response.clone().json();if(String(args[0]).endsWith('/trading/side-schedules')&&response.ok)window.facilityScheduleRead=await response.clone().json();return response;};
  const row=[...document.querySelectorAll('#facility-allocation-table tbody tr')].find(r=>r.querySelector('input')?.value==='60');row.querySelector('[data-facility-trade]').click();`);
 await openBookedSchedules({wait,click});
 await inspectPositionSide(actions,0);
 await check('an unavailable historical Side retains dated rows and explicit valuation refusal',"facilityScheduleRead.page.rows.some(r=>r.operatingDate==='2021-01-01')&&facilityScheduleRead.page.rows.every(r=>r.presentValue===null&&r.lifecycle.unavailableReason)&&[...document.querySelectorAll('#position-details .inspection-rows tbody tr')].some(r=>r.textContent.includes('2021-01-01'))");
 await check('a historical allocation opens read-only Side schedules and retains only its own allocation evidence',
  "!document.querySelector('#position-details .ticket-facility-track,#position-details .ticket-cell-input')&&facilityTicketPayload.facilityAllocations.length===1&&facilityTicketPayload.facilityAllocations[0].quantity==='60'&&Object.keys(facilityTicketPayload.facilityAllocations[0]).sort().join()==='day,finalQuantity,finalState,obligationId,quantity,sideCode,state,unit'&&facilityTicketPayload.facilityAllocations[0].finalQuantity===null&&facilityTicketPayload.facilityAllocations[0].finalState==='missing'");
 const inspection=await evaluate('({tradeId:facilityTicketPayload.head.trade_id,contextId:facilityTicketPayload.context.contextId})');
 await click('#nav-physical-operations');await openPhysicalTrade(actions,inspection.tradeId);await click('#physical-operations-tabs-delivery');await openGasDelivery(actions,'2021-01-01','receipt');await click('.gas-operations [data-stage="2"]');
 await check('Operations shows the historical planned share without accepting it as final or allowing writes',
  `(()=>{const rows=[...document.querySelectorAll('#physical-operations-view .ticket-facility-track tbody tr')];const contract=[...document.querySelectorAll('.gas-operations [data-ops=rows] .tabulator-row')].find(r=>r.dataset.rowKey.endsWith(':2021-01-01')&&r.querySelector('[tabulator-field=c1]').textContent==='receipt');return contract?.querySelector('[tabulator-field=c3]').textContent==='1000'&&rows.length===1&&rows[0].textContent.includes('receipt')&&rows[0].textContent.includes('2021-01-01')&&rows[0].textContent.includes('60 MMBtu')&&rows[0].textContent.includes('Planned')&&rows[0].textContent.includes('Not accepted')&&[...document.querySelectorAll('.gas-operations [data-operation]')].every(b=>b.disabled)&&document.querySelector('#snapshot').value===${q(inspectionFrame)}&&facilityTicketPayload.context.contextId===${q(inspection.contextId)};})()`);
 await evaluate('window.fetch=window.facilityTradeFetch');await open();await click('#current-view-context');
 await check('Working data reloads the facility independently of a market close choice',"!document.querySelector('#facility-source-fields').disabled&&!document.querySelector('#facility-editor').hidden");
 await persona('viewer');
 await check('an account without facility access clears values and navigation',"document.querySelector('#nav-facilities').hidden&&document.querySelector('#facility-balance').children.length===0&&document.querySelector('#facility-source-quantity').value===''");
}
