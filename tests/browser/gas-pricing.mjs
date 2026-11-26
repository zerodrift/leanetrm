import {chooseCalendars} from './side-calendars.mjs';
import {newTrade} from './new-trade.mjs';
import {blotterAt,decemberClose,gasTicket,openBookedSchedules,q} from './gas-ticket.mjs';

export async function run(actions){
 const {command,evaluate,wait,set,click,check,persona,idle,url}=actions;
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 await blotterAt(actions,await decemberClose(evaluate));await newTrade(actions,'gas');
 const t=gasTicket(actions),{ticket,card,control,field,choose,until}=t;
 await t.tradeTerms();await t.physicalTerms('physical-1','receive','10000',{point:'synthetic_quantity_point'});
 await set(control('physical-1','delivery_from'),'2021-01-13');await set(control('physical-1','delivery_to'),'2021-01-16');
 await until(`document.querySelector(${q(control('cash-1','cash_share'))})?.value==='1'&&document.querySelector(${q(control('cash-1','cash_part'))})?.value==='whole'`);
 await check('a payment visibly starts with share one and Whole quantity',
  `document.querySelector(${q(field('cash-1','cash_share')+' .ticket-label')}).textContent==='Share of quantity'&&document.querySelector(${q(control('cash-1','cash_part'))}).selectedOptions[0].textContent==='Whole quantity'`);
 await choose('cash-1','cash_part','between');
 await until(`document.querySelector(${q(control('cash-1','cash_part_lower'))})?.checkVisibility()&&document.querySelector(${q(control('cash-1','cash_part_upper'))})?.checkVisibility()`);
 await choose('cash-1','cash_part','whole');
 await check('part levels appear only for the selected quantity part',
  `!document.querySelector(${q(control('cash-1','cash_part_lower'))})?.checkVisibility()&&!document.querySelector(${q(control('cash-1','cash_part_upper'))})?.checkVisibility()`);
 // A linked composition reports the financial restriction on the selected part.
 await t.addSide('Physical','physical-2');await t.physicalTerms('physical-2','deliver',null,{point:'synthetic_quantity_point'});
 await set(control('physical-2','delivery_from'),'2021-01-13');await set(control('physical-2','delivery_to'),'2021-01-16');
 await choose('physical-2','link_source','physical-1');await choose('physical-2','link_formula','gross_less_fraction');await set(control('physical-2','link_fraction'),'0.02');
 await t.cashTerms('cash-1','cash','pay','physical-1','3');await choose('cash-1','cash_part','above');await set(control('cash-1','cash_part_lower'),'8000');
 await until(`document.querySelector(${q(field('cash-1','cash_part')+' .ticket-error')})?.textContent.includes('fuel links')`);
 await check('a part beside a fuel link refuses on its part field',`!!document.querySelector(${q(field('cash-1','cash_part')+' .ticket-error')})`);
 await click(card('physical-2')+' .ticket-remove');await click(card('cash-1')+' .ticket-remove');
 await choose('physical-1','quantity_policy','delivery_schedule_v1');
 const entries=[['pay','named_monthly','synthetic_entry_monthly','up_to',null,'8000','0'],
  ['pay','named_daily','synthetic_entry_daily','above','8000',null,'0.05'],
  ['receive','named_daily','synthetic_entry_daily','shortfall','10000',null,'0'],
  ['pay','named_monthly','synthetic_entry_monthly','shortfall','10000',null,'0']];
 for(const [n,[direction,pricing,index,part,lower,upper,spread]] of entries.entries()){
  const code=`floating-${n+1}`;await t.addSide('Index payment',code);
  await choose(code,'floating_source','physical-1');await choose(code,'floating_direction',direction);await choose(code,'floating_stage','delivery');
  if(n===0)await choose(code,'floating_pricing','point_publications');
  await choose(code,'floating_pricing',pricing);await choose(code,'floating_index',index);await choose(code,'floating_forecast_curve','assigned');
  await choose(code,'floating_part',part);if(lower!==null)await set(control(code,'floating_part_lower'),lower);if(upper!==null)await set(control(code,'floating_part_upper'),upper);
  await set(control(code,'floating_spread'),spread);await chooseCalendars(actions,card(code),'test_utc_every_day');
 }
 await until(t.current());
 await check('named monthly pricing is offered beside point publications and named daily pricing',
  `[...document.querySelectorAll(${q(control('floating-1','floating_pricing')+' option')})].filter(o=>o.value).map(o=>o.value).join(',')==='point_publications,named_daily,named_monthly'&&document.querySelector(${q(control('floating-1','floating_pricing'))}).value==='named_monthly'`);
 await evaluate(`{const send=window.fetch;window.__pricingBooked=null;window.fetch=async(input,init)=>{const response=await send(input,init);if(String(input)==='/api/tickets/book')window.__pricingBooked={body:JSON.parse(init.body),receipt:await response.clone().json()};return response;};}`);
 await click(ticket+' .ticket-book');await wait('!!window.__pricingBooked?.receipt?.tradeId',60000);
 const saved=await evaluate(`(async()=>{const saved=window.__pricingBooked,token=(await(await window.workspaceFetch('/api/bootstrap?includeFrames=false')).json()).csrfToken,call=async(path,body)=>{const r=await window.workspaceFetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});const v=await r.json();if(!r.ok)throw Error(JSON.stringify(v));return v;};
  if(saved.body.draft.sides.length!==5)throw Error('Expected five independent entries');
  const context=saved.body.context,tradeId=saved.receipt.tradeId;
  const quantities=await call('gas/quantities/read',{context,tradeId,selectionPolicy:'operational_projection_v1'});
  await call('gas/quantities/record',{context,tradeId,expectedRevision:1,operationId:crypto.randomUUID(),obligationId:quantities.rows[0].obligationId,kind:'scheduled',status:'provisional',day:'2021-01-14',quantity:'9000',evidence:'Synthetic browser pricing quantity',expectedPreviousId:null});return saved.receipt;})()`);
 await command('Page.navigate',{url});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await blotterAt(actions,await decemberClose(evaluate));
 const row=`#desk-positions [data-row-key="${saved.tradeId}:${saved.instrumentId}"] .desk-reference`;
 await wait(`!!document.querySelector(${q(row)})&&`+idle,30000);await click(row);await openBookedSchedules(actions);
 await wait("!!document.querySelector('#position-details .ticket-tab[data-tab=\"floating-2\"]')",30000);
 await click('#position-details .ticket-tab[data-tab="floating-2"]');
 const rows='#position-details .side-schedule-inspection .inspection-rows';
 await wait(`document.querySelector(${q(rows)})?.textContent.includes('Source quantity: 9,000')`,30000);
 await check('the five-entry booking shows its signed band quantity and scheduled source',
  `(()=>{const row=[...document.querySelectorAll(${q(rows+' tbody tr')})].find(r=>r.querySelector('td')?.textContent.includes('Jan 14, 2021'));return row?.textContent.includes('-1,000')&&row.textContent.includes('part of the day quantity')&&row.textContent.includes('Source quantity: 9,000 · scheduled');})()`,
  `[...document.querySelectorAll(${q(rows+' tbody tr')})].map(r=>r.textContent)`);
}
