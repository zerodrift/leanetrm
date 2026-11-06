// Ordinary ticket entry for independent physical and indexed payment Sides.
import {newTrade} from './new-trade.mjs';
import {blotterAt,decemberClose,gasTicket,q} from './gas-ticket.mjs';

export async function run(actions){
 const {command,evaluate,wait,set,click,check,persona,idle,url}=actions;
 const {ticket,card,control,choose,until,current,tradeTerms,physicalTerms,addSide}=gasTicket(actions);
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 await blotterAt(actions,await decemberClose(evaluate));await newTrade(actions,'gas');await tradeTerms();
 await physicalTerms('physical-1','receive','100',{point:'synthetic_gas_point_indexed'});
 await until(`document.querySelector(${q(control('cash-1','cash_source'))})?.value==='physical-1'&&document.querySelector(${q(control('cash-1','cash_direction'))})?.value==='pay'`);
 await check('a sole physical source and its purchase payment direction are selected visibly',
  `document.querySelector(${q(control('physical-1','link_source'))}).value===''&&document.querySelector(${q(control('cash-1','cash_stage'))}).value==='delivery'`);
 await click(card('cash-1')+' .ticket-remove');await addSide('Index payment','floating-1');
 await until(`document.querySelector(${q(control('floating-1','floating_index'))})?.value==='test_price'&&document.querySelector(${q(control('floating-1','floating_spread'))})?.value==='0'`);
 await check('the index payment uses the assigned index, explicit zero spread and monthly day 10',
  `document.querySelector(${q(control('floating-1','floating_payment_day'))}).value==='10'&&document.querySelector(${q(control('floating-1','floating_forecast_curve'))}).value==='assigned'`);
 await set(control('physical-1','delivery_to'),'2021-03-01');await until(current());
 await check('extending the term preserves the linked location and eligible indexed forecast',
  `document.querySelector(${q(control('physical-1','delivery_point'))}).value==='synthetic_gas_point_indexed'&&document.querySelector(${q(control('floating-1','floating_index'))}).value==='test_price'&&document.querySelector(${q(control('floating-1','floating_forecast_curve'))}).value==='assigned'`);
 await evaluate(`{const send=window.fetch;window.__indexBooked=null;window.__scheduleReads=[];window.fetch=async(input,init)=>{const response=await send(input,init);if(String(input)==='/api/tickets/trade')window.__ticketRead=await response.clone().json();if(String(input)==='/api/trading/side-schedules')window.__scheduleReads.push(JSON.parse(init.body));if(String(input)==='/api/tickets/book')window.__indexBooked={body:JSON.parse(init.body),receipt:await response.clone().json()};return response;};}`);
 await click(ticket+' .ticket-book');await wait('!!window.__indexBooked?.receipt?.tradeId',60000);
 await check('the ticket books a physical Side and an indexed Side through the shared business operation',
  "window.__indexBooked.body.draft.sides.map(s=>s.kind).join(',')==='physical,floating'&&window.__indexBooked.receipt.revision===1");
 const saved=await evaluate('window.__indexBooked.receipt');
 const row=`#desk-positions [data-row-key="${saved.tradeId}:${saved.instrumentId}"] .desk-reference`;
 await wait(`!!document.querySelector(${q(row)})&&`+idle,30000);await click(row);
 await wait("!!document.querySelector('#position-details-view .physical-instrument')&&"+idle);
 await check('indexed physical overview renders retained index and monthly payment terms',"document.querySelector('#position-details-tabs-terms').textContent==='Physical overview'&&(()=>{const section=[...document.querySelectorAll('#position-details-view .side-panel-section')].find(s=>s.querySelector('h3')?.textContent==='floating-1'),value=label=>[...section.querySelectorAll('.side-term')].find(r=>r.querySelector('dt').textContent===label)?.querySelector('dd').textContent;return value('Pricing index')==='test_price'&&value('Spread')==='0'&&value('Payment day')==='10'&&value('Payment convention')==='next_month_day_utc_v1'&&value('Fixed unit price')===undefined&&value('Payment')===undefined;})()");
 await click('#position-details-tabs-schedules');
 const inspector='#position-details .ticket-grid .side-schedule-inspection';
 await wait(`!!document.querySelector(${q(inspector+' .inspection-calculation')})`);
 await check('the selected Side uses one expanded grid without an extra disclosure or selector',
  `document.querySelectorAll('#position-details .ticket-grid .inspection-rows').length===1&&document.querySelectorAll('#position-details .ticket-grid .tabulator').length===0&&document.querySelector(${q(inspector+' > summary')}).hidden&&document.querySelector(${q(inspector+' .inspection-controls')}).hidden&&__scheduleReads.every(r=>r.position.context.knowledgeMode==='retained'&&r.position.context.contextId===__ticketRead.context.contextId)`);
 await click('#position-details .ticket-tab[data-tab="floating-1"]');await wait(`!!document.querySelector(${q(inspector+' .inspection-calculation')})`);
 await check('the native indexed Side shows signed quantity, DF/PV, payment dates and separate cash state',
  `document.querySelector(${q(inspector+' .inspection-rows')}).textContent.includes('-100')&&document.querySelector(${q(inspector+' .inspection-rows')}).textContent.includes('Cash: Not tracked')&&document.querySelector(${q(inspector+' [role=status]')}).textContent.includes('1–32 of 59')`);
 await click(inspector+' .inspection-calculation');await wait(`!!document.querySelector(${q(inspector+' .inspection-steps')})`);
 await check('calculation detail exposes the normal stored averaging and source tenor rows',
  `document.querySelector(${q(inspector+' .inspection-steps')}).textContent.includes('Weighted price')&&document.querySelector(${q(inspector+' .inspection-steps')}).textContent.includes('2021-01-01')&&document.querySelector(${q(inspector+' .inspection-evidence')}).textContent.includes('Unit Side')`);
 await click(inspector+' > .inspection-pager button:last-child');await wait(`document.querySelector(${q(inspector+' [role=status]')})?.textContent.includes('33–59 of 59')`);
 await check('the next bounded page keeps the selected Side and financial context',`document.querySelector('#position-details .ticket-tab[aria-selected=true]').dataset.tab==='floating-1'&&!document.querySelector(${q(inspector+' .inspection-steps')})`);

}
