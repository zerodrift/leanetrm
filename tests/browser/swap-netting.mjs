import {swapLifecycleFixture} from './lifecycle-swaps.mjs';
const q=JSON.stringify;

export async function run(driver){
 const {evaluate,wait,click,set,check,idle,command,shot}=driver;
 const f=await swapLifecycleFixture(driver,'netting_risk_browser',{net:true});await f.determine(0);await f.determine(1);
 async function net(amount,lines,reference){await f.action('swap.net');await set(f.panel+' [data-net-mode]','net');await set(f.panel+' [data-net-amount]',amount);
  const balances=(await f.inspect()).balances.sort((a,b)=>a.membershipOrdinal-b.membershipOrdinal);
  for(let i=0;i<lines.length;i++){await click(f.panel+' [data-net-add]');await set(f.panel+` [data-net-line]:nth-of-type(${i+1}) select`,balances[i].determinationId);await set(f.panel+` [data-net-line]:nth-of-type(${i+1}) input`,lines[i]);}
  await f.observedTime();await f.field('Payment reference',reference);await f.review();
 }
 async function reverse(reference,label){await f.action('swap.net');await set(f.panel+' [data-net-mode]','reversal');await f.field('Original net settlement',await evaluate(`(()=>{const label=[...document.querySelectorAll(${q(f.panel+' label')})].find(l=>l.firstChild.textContent==='Original net settlement');return [...label.querySelector('select').options].find(o=>o.textContent.includes(${q(reference)})).value;})()`));await f.observedTime();await f.field('Payment reference',label);await f.review();await f.record();}
 await net('20',['-100','120'],'Synthetic browser partial net');
 await check('net review shows backend gross allocations and signed net cash before recording',`(()=>{const text=document.querySelector(${q(f.panel+' [data-lifecycle-review]')}).textContent;return text.includes('-100')&&text.includes('120')&&text.includes('20');})()`);
 await shot('net-lifecycle-desktop');await f.lostReply();
 const paid=await f.inspect();await check('partial net plus lost-reply retry records one cash movement with its two gross applications',q(paid.settlement.version===3&&paid.cashMovements.length===1&&paid.cashMovements[0].amount==='20'&&paid.cashMovements[0].allocations.length===2&&paid.balances.map(b=>b.outstanding).sort().join()==='-200,230'));
 await reverse('Synthetic browser partial net','Synthetic browser exact net reversal');
 const reversed=await f.inspect();await check('net reversal retains the original gross applications and restores both balances',q(reversed.settlement.version===4&&reversed.cashMovements.length===2&&reversed.balances.map(b=>b.outstanding).sort().join()==='-300,350'&&reversed.valuation.netCash==='0'));
 await net('0',['-200','200'],'Synthetic browser zero discharge');await f.record();
 const zero=await f.inspect();await check('zero-net discharge records no cash value and exactly the selected offsetting gross obligations',q(zero.settlement.version===5&&zero.cashMovements.length===3&&zero.cashMovements.some(c=>c.reference==='Synthetic browser zero discharge'&&c.recordKind==='zero_net_discharge_v1'&&c.amount==='0')&&zero.balances.map(b=>b.outstanding).sort().join()==='-100,150'));
 await reverse('Synthetic browser zero discharge','Synthetic browser zero discharge reversal');
 const final=await f.inspect();await check('zero discharge reversal conserves original P&L and restores the two gross obligations once',q(final.settlement.version===6&&final.cashMovements.length===4&&final.cashMovements.reduce((n,c)=>n+c.allocations.length,0)===8&&final.balances.map(b=>b.outstanding).sort().join()==='-300,350'&&final.valuation.netCash==='0'));
 await f.legacy();await check('native cash history keeps explicit gross allocation evidence for partial net, reversal and noncash discharge',"['Synthetic browser partial net','Synthetic browser exact net reversal','Synthetic browser zero discharge','Synthetic browser zero discharge reversal'].every(label=>document.querySelector('#swap-settlement-cash').textContent.includes(label))&&document.querySelectorAll('#swap-settlement-cash details').length===4");
 await click('#swap-settlement-opening');await set('#swap-pnl-contract-policy','reject_contract_changes_v1');await click('#swap-settlement-reconcile',60000);await wait(idle,60000);
 await check('same-source netting and reversals do not manufacture new trading P&L',"!document.querySelector('#swap-settlement [role=alert]').textContent&&document.querySelector('#swap-settlement-bridge').textContent.includes('Economic P&L')");
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await wait(idle);
 await check('net cash history remains readable without widening the mobile application',"document.documentElement.scrollWidth<=document.documentElement.clientWidth+1&&document.querySelector('#swap-settlement').scrollWidth<=document.querySelector('#swap-settlement').clientWidth+1");await shot('net-lifecycle-narrow');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
}
