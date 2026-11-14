import {swapLifecycleFixture} from './lifecycle-swaps.mjs';
const q=JSON.stringify;

// Product-native settlement and retained P&L remain visible in Terms. New
// observed facts require explicit saved settings and the shared Lifecycle UI.
export async function run(driver){
 const {evaluate,wait,click,set,submit,check,idle,command,shot}=driver;
 const f=await swapLifecycleFixture(driver,'settlement_risk_browser');
 await click('#blotter-risk');await wait("[...document.querySelector('#risk-factor').options].some(o=>o.value)&&"+idle,60000);
 await click('input[name=risk-choice][value=custom]');await set('#risk-factor',await evaluate("[...document.querySelector('#risk-factor').options].find(o=>o.value).value"));await set('#risk-bump','0.1');await submit('#risk-form');await wait("!document.querySelector('#desk-run').hidden&&"+idle,60000);
 await f.legacy();
 await check('Terms retains native settlement measures, gross occurrences and exact evidence without retired write forms',"document.querySelector('#swap-settlement').textContent.includes('Remaining contractual value')&&!!document.querySelector('#swap-settlement details pre')&&!!document.querySelector('#swap-open-lifecycle')&&!document.querySelector('#swap-settlement-determine')&&!document.querySelector('#swap-settlement-payment-form')");
 await click('#swap-settlement-opening');await click('#swap-open-lifecycle');await wait("!document.querySelector('#lifecycle-page').hidden&&"+idle,60000);
 await f.action('swap.determine',0);await f.review();
 await check('determination review uses the selected saved period and backend amount',`document.querySelector(${q(f.panel+' [data-lifecycle-review]')}).textContent.includes('-300')&&document.querySelector(${q(f.panel)}).textContent.includes(${q(f.reference)})`);
 await f.record();
 const first=await f.inspect();await check('recorded shared determination retains the signed original once',q(first.settlement.version===1&&first.settlement.determinations.length===1&&first.balances.length===1&&first.balances[0].amount==='-300'));
 await click('#nav-futures');await wait(idle,60000);
 await check('a confirmed lifecycle fact invalidates the current risk result before returning to the Blotter',"document.querySelector('#desk-run').hidden&&[...document.querySelectorAll('#blotter-strip .strip-item')].some(i=>i.querySelector('.strip-label')?.textContent.startsWith('Risk')&&i.querySelector('.strip-value')?.textContent==='Not run')","({resultHidden:document.querySelector('#desk-run').hidden,notice:document.querySelector('#notice').textContent,strip:document.querySelector('#blotter-strip').textContent})");
 await f.action('swap.payment',0);await f.field('Determination',first.balances[0].determinationId);await f.field('Observed signed payment','-100');await f.observedTime();await f.field('Payment reference','Synthetic browser observed payment');await f.review();await f.record();
 const paid=await f.inspect();await check('observed payment reduces the exact gross balance without recognizing profit again',q(paid.settlement.version===2&&paid.cashMovements.length===1&&paid.balances[0].outstanding==='-200'&&paid.valuation.netCash==='-100'));
 await f.action('swap.payment',0);
 const reversal=await evaluate(`[...document.querySelector(${q(f.panel+' [data-payment-reversal]')}).options].find(o=>o.textContent.includes('Synthetic browser observed payment')).value`);
 await set(f.panel+' [data-payment-reversal]',reversal);await f.observedTime();await f.field('Payment reference','Synthetic browser observed reversal');
 await check('a single-payment reversal uses backend exact original amounts and locks replacement financial fields',`(()=>{const labels=[...document.querySelectorAll(${q(f.panel+' label')})],amount=labels.find(l=>l.firstChild.textContent.startsWith('Observed signed payment')).querySelector('input');return amount.readOnly&&amount.value==='100';})()`);
 await f.review();await f.record();
 const reversed=await f.inspect();await check('reversal retains both cash facts and restores the original gross balance',q(reversed.settlement.version===3&&reversed.cashMovements.length===2&&reversed.balances[0].outstanding==='-300'&&reversed.valuation.netCash==='0'));
 await f.action('swap.determine',1);await f.review();await f.lostReply();
 const completed=await f.inspect();await check('a lost determination reply and exact retry leave two original contributions',q(completed.settlement.version===4&&completed.settlement.determinations.length===2&&completed.cashMovements.length===2&&completed.balances.map(b=>b.amount).sort().join()==='-300,350'));
 await f.legacy();await set('#swap-pnl-opening-snapshot',f.openingFrame);await click('#swap-pnl-capture-opening',60000);await wait(idle,60000);await set('#swap-pnl-contract-policy','reject_contract_changes_v1');await click('#swap-settlement-reconcile',60000);await wait(idle,60000);
 await check('retained opening and closing native evidence reconcile cash and remaining value',"!document.querySelector('#swap-settlement [role=alert]').textContent&&document.querySelector('#swap-settlement-bridge').textContent.includes('Economic P&L')&&document.querySelector('#swap-settlement-cash').textContent.includes('Synthetic browser observed reversal')","document.querySelector('#swap-settlement').textContent.slice(-2500)");
 await shot('settlement-lifecycle-desktop');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await wait(idle);
 await check('the retained settlement and P&L view fits a phone while its figures remain in scrollable tables',"document.documentElement.scrollWidth<=document.documentElement.clientWidth+1&&document.querySelector('#swap-settlement').scrollWidth<=document.querySelector('#swap-settlement').clientWidth+1");
 await shot('settlement-lifecycle-narrow');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 const shared=await swapLifecycleFixture(driver,'shared_side_lifecycle_browser',{shared:true});
 await shared.lifecycle();
 await check('reused scheduled payment templates retain both native memberships and distinct action rows',`(()=>{const f=window.__swapFixture,rows=[...document.querySelectorAll('#lifecycle-page .tabulator-row[data-row-key]')].filter(r=>{const k=JSON.parse(r.dataset.rowKey);return k[1]===f.tradeId&&k[3]==='swap.determine';});return f.occurrences.length===2&&new Set(f.occurrences.map(o=>o.occurrence_id)).size===1&&rows.length===2&&new Set(rows.map(r=>r.dataset.rowKey)).size===2;})()`);
 await shared.determine(0);await shared.determine(1);
 const evidence=await shared.inspect(),writes=await evaluate('window.__swapWrites');
 await check('each shared-template review records only its selected independent membership',q(writes.length===2&&writes[0].body.selection.profileId!==writes[1].body.selection.profileId&&writes[0].body.selection.determination.preview.membershipOrdinal===0&&writes[1].body.selection.determination.preview.membershipOrdinal===1));
 await check('shared-template gross obligations stay separate while their values offset',q(evidence.settlement.version===2&&evidence.settlement.occurrences.length===2&&evidence.settlement.determinations.length===2&&evidence.balances.map(b=>b.amount).sort().join()==='-350,350'&&evidence.valuation.determinedOutstanding==='0'));
}
