// Read the synthetic January service statements in their explicit business context.
import {blotterAt,decemberClose} from './gas-ticket.mjs';
import {newTrade,closeNewTrade} from './new-trade.mjs';
import {fitsWidth} from './owned-browser.mjs';

export async function checkGasEntryOutlook(actions){
 const {command,click,evaluate,set,wait,check,idle}=actions;
 await click('#nav-facilities');
 if(await evaluate("document.querySelector('#date-context-on').value!=='2021-01-31'")){
  await set('#date-context-on','2021-01-31');await wait("!document.querySelector('#use-business-date-current').disabled&&"+idle,60000);
  await click('#use-business-date-current',60000);
 }
 await click('[data-facilities-view=outlook]');
 await set('#outlook-from','2021-01-01');await set('#outlook-to','2021-01-03');await click('#outlook-read');
 await wait("document.querySelector('#facilities-workspace-content').innerText.includes('Gross pipeline volumes')&&"+idle);
 await check('the first outlook view shows pipeline stages with values above their coverage',
  "document.querySelector('#outlook-views-volumes')?.getAttribute('aria-selected')==='true'&&!!document.querySelector('.outlook-amount small')&&['Nominated','Submitted','Confirmed','Scheduled','Allocated'].every(t=>document.querySelector('#facilities-workspace-content').innerText.includes(t))");
 await click('#outlook-views-sources');await wait("document.querySelector('#facilities-workspace-content').innerText.includes('Native start')");
 await check('booked Sides retain native gas-day intervals and service balances have a dedicated view',
  "document.querySelector('#facilities-workspace-content').innerText.includes('Native end')&&!!document.querySelector('#outlook-views-balances')");
 await click('#outlook-views-balances');await wait("document.querySelector('#facilities-workspace-content').innerText.includes('synthetic-service')");
 await click('#outlook-views-volumes');await set('#outlook-period','month');await click('#outlook-read');
 const month="[...document.querySelectorAll('.outlook-report button')].find(b=>b.textContent==='2021-01')";
 await wait('!!('+month+')&&'+idle);await evaluate(month+'.click()');
 await wait("document.querySelector('#outlook-period').value==='day'&&document.querySelector('.outlook-report').innerText.includes('2021-01-01')&&"+idle);
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await wait("document.querySelector('#sidebar-toggle').getAttribute('aria-expanded')==='false'");
 await check('phone opens with the report visible and full navigation is available on demand',
  fitsWidth+"&&document.querySelector('#sidebar-menus').getBoundingClientRect().height===0&&document.querySelector('#facilities-section-picker').checkVisibility()&&document.querySelector('#facilities-section-picker').value==='outlook'");
 await click('#sidebar-toggle');await check('phone menu opens by keyboard-accessible control',"document.querySelector('#sidebar-toggle').getAttribute('aria-expanded')==='true'&&document.querySelector('#nav-facilities').checkVisibility()");await click('#sidebar-toggle');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:960,deviceScaleFactor:1,mobile:false});
 await blotterAt(actions,await decemberClose(evaluate));await newTrade(actions,'gas-locations');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await check('a chosen instrument leaves room for trade entry on a phone',
  "document.querySelector('#new-trade-switch')?.checkVisibility()&&!document.querySelector('#new-trade-product').checkVisibility()&&document.querySelector('#gas-location-assemble').getBoundingClientRect().top<innerHeight");
 await click('#new-trade-switch');await check('the same grouped chooser reopens on demand',"document.querySelector('#new-trade-product').checkVisibility()&&document.querySelector('#new-trade-gas-locations').getAttribute('aria-pressed')==='true'");
 await click('#new-trade-switch');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:960,deviceScaleFactor:1,mobile:false});
 const root='#gas-locations-page';
 await check('both endpoint directions and all three unselected choices are visible before building a ticket',
  "document.querySelectorAll('#gas-locations-page input[type=radio]').length===6&&document.querySelectorAll('#gas-locations-page input:checked').length===0&&document.querySelector('#gas-location-assemble').disabled&&['Receive gas','Deliver gas','Pay','Receive payment'].every(t=>document.querySelector('#gas-locations-page').innerText.includes(t))");
 const pick=async(name,value)=>click(root+' input[name="'+name+'"][value="'+value+'"]');
 await pick('gas-location-agreements','separate_confirmations');await pick('gas-location-purchase-price','fixed');await pick('gas-location-sale-price','indexed');await click('#gas-location-assemble');
 await wait("document.querySelectorAll('#gas-locations-page .ticket-card').length===4&&"+idle);
 const card=code=>root+' .ticket-card[data-side="'+code+'"]';
 await check('purchase shows its two editable Sides',`document.querySelector('${card('purchase')}').checkVisibility()&&document.querySelector('${card('purchase_payment')}').checkVisibility()&&!document.querySelector('${card('sale')}').checkVisibility()`);
 await set(card('purchase')+' [data-field=quantity] input','137');
 await click('#gas-location-legs-sale');
 await check('sale shows its own physical and floating payment Sides',`document.querySelector('${card('sale')}').checkVisibility()&&document.querySelector('${card('sale_payment')}').dataset.kind==='floating'&&!document.querySelector('${card('purchase')}').checkVisibility()`);
 await click('#gas-location-legs-purchase');
 await check('switching legs preserves native drafts and both independent confirmations',
  `document.querySelector('${card('purchase')} [data-field=quantity] input').value==='137'&&document.querySelectorAll('#gas-locations-page .deal-ticket').length===2&&[...document.querySelectorAll('#gas-locations-page .ticket-book')].every(b=>b.hidden)`);
 await closeNewTrade(actions);await newTrade(actions,'gas-locations');
 await pick('gas-location-agreements','one_agreement');await pick('gas-location-purchase-price','indexed');await pick('gas-location-sale-price','fixed');await click('#gas-location-assemble');
 await wait("document.querySelectorAll('#gas-locations-page .ticket-card').length===4&&"+idle);
 await evaluate("document.querySelector('#gas-location-legs-purchase').focus()");
 await command('Input.dispatchKeyEvent',{type:'keyDown',key:'ArrowRight'});await command('Input.dispatchKeyEvent',{type:'keyUp',key:'ArrowRight'});
 await check('keyboard leg navigation preserves a single bilateral ticket and selects the matching payment',
  `document.activeElement.id==='gas-location-legs-sale'&&document.querySelectorAll('#gas-locations-page .deal-ticket').length===1&&document.querySelector('${card('sale_payment')}').checkVisibility()&&document.querySelector('${card('sale_payment')}').dataset.kind==='cash'&&!document.querySelector('${card('purchase_payment')}').checkVisibility()`);
}
