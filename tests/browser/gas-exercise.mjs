import {newTrade} from './new-trade.mjs';
import {blotterAt,bookedTicket,closeOn,decemberClose,gasTicket,openBookedSchedules,q} from './gas-ticket.mjs';

export async function run(actions){
 const {command,evaluate,wait,set,click,check,persona,idle,url,fixture,tab}=actions;
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 const close=await decemberClose(evaluate);
 await blotterAt(actions,close);await newTrade(actions,'gas');
 const t=gasTicket(actions),{ticket,control,until}=t;
 await until('!!document.querySelector('+q(ticket)+')');
 const section=ticket+' [data-term-section="exerciseRights"]';
 const row=n=>section+' [data-term-row="'+n+'"]';
 const right=(n,id)=>row(n)+' [data-field="'+id+'"]';
 const input=(n,id)=>right(n,id)+' .ticket-control';
 const at=selector=>'document.querySelector('+q(selector)+')';
 const choose=async(n,id,value)=>{
  await until('[...document.querySelectorAll('+q(input(n,id)+' option')+')].some(o=>o.value==='+q(value)+')');
  await set(input(n,id),value);
 };
 const covered=async n=>{
  const selector=input(n,'right_sides');
  await until('document.querySelectorAll('+q(selector+' option')+').length===2');
  await evaluate('{const select='+at(selector)+';for(const option of select.options)option.selected=true;select.dispatchEvent(new Event("change",{bubbles:true}));}');
 };
 const addRight=async n=>{await click(section+' .ticket-add-term');await until('!!'+at(row(n)));};
 await check('a new gas ticket has an empty Exercise right section',
  at(section)+'?.checkVisibility()&&'+at(section)+'?.textContent.includes("Exercise right")&&!'+at(row(0)),t.observed);
 await addRight(0);
 await until('[...document.querySelectorAll('+q(row(0)+' select')+')].length===4&&[...document.querySelectorAll('+q(row(0)+' select')+')].every(s=>s.options.length>1)');
 await check('an added right starts without any selected terms',
  '[...document.querySelectorAll('+q(row(0)+' select')+')].every(s=>[...s.selectedOptions].every(o=>o.value===""))&&!'+at(input(0,'right_lead_hours')));
 await choose(0,'right_settlement','physical');
 await check('Physical, by election shows an empty lead field',
  at(input(0,'right_settlement'))+'?.selectedOptions[0].textContent==="Physical, by election"&&'+at(input(0,'right_lead_hours'))+'?.checkVisibility()&&'+at(input(0,'right_lead_hours'))+'.value===""');
 await set(input(0,'right_lead_hours'),'8');await choose(0,'right_settlement','cash_automatic');
 await check('Cash, by published prices hides the lead field',
  at(input(0,'right_settlement'))+'?.selectedOptions[0].textContent==="Cash, by published prices"&&!'+at(input(0,'right_lead_hours'))+'?.checkVisibility()');
 await choose(0,'right_settlement','physical');await set(input(0,'right_lead_hours'),'8');
 await t.tradeTerms();await t.physicalTerms('physical-1','receive','10000',{point:'synthetic_worked_point'});
 await set(control('physical-1','delivery_from'),'2021-01-16');await set(control('physical-1','delivery_to'),'2021-01-19');
 const forecast='[...document.querySelectorAll('+q(control('physical-1','forecast_curve')+' option')+')].find(o=>o.value.startsWith("{")&&JSON.parse(o.value).curveId==='+q(fixture.curveId)+')';
 await until('!!'+forecast);await t.choose('physical-1','forecast_curve',await evaluate(forecast+'?.value'));
 await t.cashTerms('cash-1','cash','pay','physical-1','3');await t.setInstant('cash-1','cash_payment_at','2021-02-10T00:00:00Z');
 for(const n of [0,1]){
  if(n===1){await addRight(n);await choose(n,'right_settlement','physical');await set(input(n,'right_lead_hours'),'8');}
  await covered(n);await choose(n,'right_period','delivery_day');await choose(n,'right_holder','us');
 }
 const error=right(1,'right_sides')+' .ticket-error';
 await until('!!'+at(error));
 await check('a Side covered twice is refused on the second Sides covered field',
  at(right(1,'right_sides')+' .ticket-label')+'?.textContent==="Sides covered"&&/entries claimed exactly once/.test('+at(error)+'?.textContent)&&'+at(ticket+' .ticket-book')+'.disabled',t.observed);
 await click(row(1)+' .ticket-remove-term');await until(t.current());
 await evaluate(`{const send=window.fetch;window.__exerciseBooking=null;window.fetch=async(input,init)=>{
  const response=await send(input,init);if(String(input)==='/api/tickets/book')window.__exerciseBooking={body:JSON.parse(init.body),receipt:await response.clone().json()};return response;};}`);
 await click(ticket+' .ticket-book');await wait('!!window.__exerciseBooking?.receipt?.tradeId',60000);
 await check('the three-day physical call books with the two selected Sides and no extra right',
  `JSON.stringify(window.__exerciseBooking.body.draft.exerciseRights)===JSON.stringify([{right_sides:['physical-1','cash-1'],right_period:'delivery_day',right_holder:'us',right_settlement:'physical',right_lead_hours:'8'}])`);
 const saved=await evaluate('window.__exerciseBooking.receipt');
 await wait("document.querySelector('#page-title').textContent==='Blotter'&&"+idle,60000);
 const position='#desk-positions [data-row-key="'+saved.tradeId+':'+saved.instrumentId+'"] .desk-reference';
 await wait('!!'+at(position)+'&&'+idle,30000);await click(position);await openBookedSchedules(actions);
 await evaluate(`{const send=window.fetch;window.__electionPrepares=[];window.__electionsSent=[];window.__loseElection=true;window.__raceElection=false;
  window.fetch=async(input,init)=>{
   const path=String(input),response=await send(input,init);
   if(path==='/api/gas/exercise/prepare'&&response.ok){
    const prepared=(await response.clone().json()).command,request=JSON.parse(init.body);
    window.__electionPrepares.push({request,prepared,proof:JSON.parse(init.headers['X-OpenETRM-Read-References']??'{}')[request.readContextId]});
    if(window.__raceElection){
     window.__raceElection=false;
     const competing={...prepared,operationId:crypto.randomUUID(),elections:prepared.elections.map(e=>({...e,outcome:'exercised',evidence:'Synthetic competing instruction'}))};
     const written=await send('/api/gas/exercise/record',{...init,body:JSON.stringify(competing)});
     if(!written.ok)throw Error('The synthetic competing instruction was refused');
    }
   }
   if(path==='/api/gas/exercise/record'){
    window.__electionsSent.push({body:JSON.parse(init.body),status:response.status});
    if(response.ok&&window.__loseElection){window.__loseElection=false;return new Response('incomplete',{status:502});}
   }
   return response;
  };
 }`);
 const action=bookedTicket+' .ticket-record-exercise',form=bookedTicket+' .ticket-exercise-action';
 const bookInput=id=>form+' [name="'+id+'"]';
 const open=async()=>{
  await wait(at(action)+'?.checkVisibility()&&!'+at(action)+'.disabled',30000);await click(action);
 };
 const selectPeriod=async(date,outcome)=>{
  await set(bookInput('right'),'0');await set(bookInput('firstPeriod'),date);await set(bookInput('lastPeriod'),date);
  await set(bookInput('outcome'),outcome);await set(bookInput('effectiveAt'),'2020-12-18T16:00:00Z');
  await set(bookInput('evidence'),'Synthetic browser election '+date);
 };
 const submit=()=>click(form+' .ticket-submit-exercise');
 const outcomeShown=(date,outcome)=>wait('!'+at(form)+'&&'+at(bookedTicket+' .ticket-exercise-groups')+'?.querySelector('+q('[data-from="'+date+'"][data-state="'+outcome+'"]')+')',30000);
 await open();
 await check('exercise entry starts with no selections or effective instant',
  '[...document.querySelectorAll('+q(form+' select,'+bookInput('effectiveAt'))+')].every(n=>!n.value)');
 await evaluate(`{
  const setItem=Storage.prototype.setItem;window.__exerciseStorageRefusals=0;
  Storage.prototype.setItem=function(key,value){
   if(key.startsWith('deal-ticket-exercise:')){window.__exerciseStorageRefusals++;throw new DOMException('Synthetic full storage','QuotaExceededError');}
   return setItem.call(this,key,value);
  };
  window.__restoreExerciseStorage=()=>{Storage.prototype.setItem=setItem;};
 }`);
 await selectPeriod('2021-01-16','exercised');await submit();
 const retry=bookedTicket+' .ticket-retry-exercise';
 await wait(at(retry)+'?.checkVisibility()&&!'+at(retry)+'.disabled',30000);
 const reopenSchedules=async()=>{
  await click('#position-details-tabs-terms');await wait('!'+at(bookedTicket));
  await openBookedSchedules(actions);await wait(at(action)+'?.checkVisibility()',30000);
 };
 await reopenSchedules();
 await check('the remounted schedule retains the unconfirmed election when storage is full',
  'window.__exerciseStorageRefusals>0&&'+at(action)+'.disabled&&'+at(retry)+'?.checkVisibility()');
 await click(retry);
 // The election already appears after the lost reply. Wait for the retry receipt to clear the pending command.
 await wait('!'+at(retry)+'&&'+at(action)+'?.checkVisibility()&&!'+at(action)+'.disabled',30000);
 await outcomeShown('2021-01-16','exercised');
 await reopenSchedules();
 await check('a second remount offers a fresh election after the original receipt is confirmed',
  '!'+at(action)+'.disabled&&!'+at(retry));
 await evaluate('window.__restoreExerciseStorage()');
 await check('recovery retries the exact prepared command under the working ticket and its issued read proof',
  `(()=>{const [a]=window.__electionPrepares,[first,retry]=window.__electionsSent;
   return !!a.proof&&a.request.context.knowledgeMode==='current'&&a.request.context.snapshotId===${q(close.id)}&&
    JSON.stringify(first.body)===JSON.stringify(a.prepared)&&JSON.stringify(retry.body)===JSON.stringify(a.prepared);})()`);
 await open();await selectPeriod('2021-01-17','abandoned');await evaluate('window.__raceElection=true');await submit();
 const refresh=bookedTicket+' .ticket-refresh-exercise';
 await wait(at(refresh)+'?.checkVisibility()&&window.__electionsSent.at(-1)?.status===409',30000);
 await check('a stale predecessor requires explicit refresh before another election',at(action)+'.disabled&&!'+at(form)+'&&!'+at(retry));
 await click(refresh);await open();await selectPeriod('2021-01-17','abandoned');
 await check('the refreshed group choice shows its known predecessor and effective instant',
  at(bookInput('firstPeriod'))+'?.selectedOptions[0].textContent.includes("known Exercised effective 2020-12-18T16:00:00")');
 await submit();await outcomeShown('2021-01-17','abandoned');
 const groups=bookedTicket+' .ticket-exercise-groups tbody tr';
 const shownGroups=`[...document.querySelectorAll(${q(groups)})].map(row=>[row.dataset.from,...[...row.cells].slice(3,6).map(cell=>cell.textContent)])`;
 await check('the schedule shows the three exercise states and exact gross amounts',
  `JSON.stringify(${shownGroups})===${q(JSON.stringify([
   ['2021-01-16','exercised','1,000','1,000'],['2021-01-17','abandoned','-500','0'],
   ['2021-01-18','not yet exercised','2,000','2,947.441896609978803165']
  ]))}`,shownGroups);
 const heading='#position-details-head .kpi-value';
 await wait(at(heading)+'?.title.startsWith("3947.441896609978803165 ")&&'+idle,30000);
 await wait(`[...document.querySelectorAll(${q(bookedTicket+' .inspection-rows tbody tr')})].some(r=>r.textContent.includes('Operating day: 2021-01-18'))`,30000);
 await check('the third gas day in the selected Side schedule remains contingent',
  `[...document.querySelectorAll(${q(bookedTicket+' .inspection-rows tbody tr')})].some(r=>r.textContent.includes('Operating day: 2021-01-18')&&r.textContent.includes('contingent'))`);

 const reference=await evaluate(at('#position-details-head .side-panel-reference')+'.textContent');
 await wait(at(position)+'?.checkVisibility()&&'+idle,30000);
 await evaluate(`{
  window.__opened=[];window.open=(...args)=>{window.__opened.push(args[0]);return null;};
  const row=${at(position)}.closest('.tabulator-row');row.focus();
  row.dispatchEvent(new KeyboardEvent('keydown',{key:'F10',shiftKey:true,bubbles:true,cancelable:true}));
 }`);
 const openWindow="[...document.querySelectorAll('.blotter-row-menu:not([hidden]) [role=menuitem]')].find(i=>i.firstChild.textContent==='Open in its own window')";
 await wait('!!'+openWindow);await evaluate(openWindow+'.click()');await wait('window.__opened.length===1');
 const own=await tab();
 try{
  await own.command('Page.navigate',{url:await evaluate('window.__opened[0]')});
  await own.wait("!!document.querySelector('#position-window-tabs-schedules')",30000);await own.click('#position-window-tabs-schedules');
  const ownAction='#position-window-view .ticket-record-exercise';
  await own.wait(at(ownAction)+'?.checkVisibility()',30000);
  await check('the separate retained trade window shows the groups with a disabled exercise action',
   q(await own.evaluate(at(ownAction)+'.disabled&&!!'+at('#position-window-view .ticket-exercise-groups'))));
 }finally{await own.close();}

 await blotterAt(actions,await closeOn(evaluate,'2021-01-17'));
 await set('#blotter-filters .table-search',reference);await wait(at(position)+'?.checkVisibility()',30000);await click(position);await openBookedSchedules(actions);
 const status=bookedTicket+' .ticket-status';
 await wait(at(status)+'?.textContent.includes("Schedule refused")&&'+at(action)+'?.checkVisibility()',30000);
 await check('at an elapsed undecided deadline the trade states its schedule refusal and still offers Record exercise',
  at(status)+'?.textContent.includes("elapsed physical exercise group requires its determination")&&!'+at(action)+'.disabled&&!!'+at(bookedTicket+' [data-term-row="0"]')+'&&!'+at(bookedTicket+' .ticket-exercise-groups'));

 await blotterAt(actions,close);await newTrade(actions,'gas');
 await until('!!'+at(ticket));
 await t.tradeTerms();await t.physicalTerms('physical-1','receive','10000',{point:'synthetic_quantity_point'});
 await set(control('physical-1','delivery_from'),'2021-01-13');await set(control('physical-1','delivery_to'),'2021-01-16');
 await t.choose('physical-1','forecast_curve','assigned');await t.cashTerms('cash-1','cash','pay','physical-1','3');
 await addRight(0);await choose(0,'right_settlement','physical');await set(input(0,'right_lead_hours'),'8');
 await covered(0);await choose(0,'right_period','delivery_day');await choose(0,'right_holder','us');
 await until(`${t.status()}?.textContent.includes('Schedule refused')&&!${at(ticket+' .ticket-book')}.disabled`);
 await check('a structurally valid right without marks has an explicit unavailable schedule and can be booked',
  `['refused','stale'].includes(${t.status()}?.dataset.kind)&&${t.status()}.textContent.includes('Supply distinct curve and delivery-month marks')&&!${at(ticket+' .ticket-book')}.disabled`,t.observed);
 await evaluate('window.__exerciseBooking=null');await click(ticket+' .ticket-book');
 await wait('!!window.__exerciseBooking?.receipt?.tradeId',60000);
 const unpriced=await evaluate('window.__exerciseBooking.receipt');
 await wait("document.querySelector('#page-title').textContent==='Blotter'&&"+idle,60000);
 await set('#blotter-filters .table-search','');
 const unpricedRow='#desk-positions [data-row-key="'+unpriced.tradeId+':'+unpriced.instrumentId+'"] .desk-reference';
 await wait(at(unpricedRow)+'?.checkVisibility()&&'+idle,30000);await click(unpricedRow);await openBookedSchedules(actions);
 await wait(at(status)+'?.textContent.includes("Supply distinct curve and delivery-month marks")&&'+at(action)+'?.checkVisibility()',30000);
 await check('the booking receipt opens the same unpriced right without fabricating a valuation',
  `!${at(bookedTicket+' .ticket-exercise-groups')}&&!${at(bookedTicket+' .ticket-measures')}&&!${at(action)}.disabled`);
}
