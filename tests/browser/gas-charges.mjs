import {blotterAt,bookedTicket,closeOn,gasTicket,openBookedSchedules,q} from './gas-ticket.mjs';
import {newTrade} from './new-trade.mjs';
import {chooseCalendars} from './side-calendars.mjs';

export async function run(actions){
 const {command,evaluate,wait,set,click,check,persona,idle,url}=actions;
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});
 await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await persona('trader');
 const bookingClose=await closeOn(evaluate,'2021-04-01');
 const accrualClose=await closeOn(evaluate,'2021-04-11');
 await blotterAt(actions,bookingClose);
 await newTrade(actions,'gas');

 const t=gasTicket(actions),{ticket,card,field,control,choose,until}=t;
 const at=selector=>'document.querySelector('+q(selector)+')';
 const visible=selector=>at(selector)+'?.checkVisibility()';
 const label=(side,id)=>at(field(side,id)+' .ticket-label')+'?.textContent';
 const price='charge_price',basis='charge_basis',paymentDate='charge_payment_at',paymentDay='charge_payment_day';
 const capacityFields=['charge_daily_quantity','charge_start','charge_end',paymentDay];
 const addCharge=`[...document.querySelectorAll(${q(ticket+' .ticket-add-kind')})].find(button=>button.textContent==='Charge')`;
 const book=ticket+' .ticket-book';

 await until('!!'+at(ticket));
 await check('Add side offers Charge',addCharge+'?.checkVisibility()&&!'+addCharge+'.disabled');
 await t.addSide('Charge','charge-1');
 const priceLabel=(side,value)=>`${label(side,price)}===${q(value)}&&${at(control(side,price))}.getAttribute('aria-label')===${q(value)}`;
 await check('the price has a neutral label until the basis is chosen',priceLabel('charge-1','Amount or rate'));
 await choose('charge-1',basis,'lump_sum');
 const lumpFields=`${label('charge-1',paymentDate)}==='Payment date'&&${visible(control('charge-1',paymentDate))}&&${capacityFields.map(id=>'!'+visible(control('charge-1',id))).join('&&')}`;
 await check('A lump sum shows Amount and Payment date and hides the capacity fields',
  `${at(control('charge-1',basis))}.selectedOptions[0].textContent==='A lump sum'&&${priceLabel('charge-1','Amount')}&&${lumpFields}`,
  `[${label('charge-1',price)},${label('charge-1',paymentDate)}]`);
 await set(control('charge-1',price),'5000');
 await choose('charge-1',basis,'capacity');
 await check('A daily capacity shows the rate and its four capacity fields',
  `${at(control('charge-1',basis))}.selectedOptions[0].textContent==='A daily capacity'&&${priceLabel('charge-1','Rate per unit per day')}&&${capacityFields.map(id=>visible(control('charge-1',id))).join('&&')}&&!${visible(control('charge-1',paymentDate))}`);
 await check('switching the basis clears the entered amount with a notice',
  `${at(control('charge-1',price))}.value===''&&/was cleared because Charged on changed/.test(${at(ticket+' .ticket-notice')}?.textContent)`);

 await t.tradeTerms();
 await t.physicalTerms('physical-1','receive','10000',{point:'synthetic_charge_reference_point'});
 await set(control('physical-1','delivery_from'),'2021-04-02');
 await set(control('physical-1','delivery_to'),'2021-05-05');
 await click(card('cash-1')+' .ticket-remove');
 const commonTerms=async side=>{
  await choose(side,'charge_direction','pay');
  await choose(side,'charge_clock_side','physical-1');
  await chooseCalendars(actions,card(side),'test_utc_every_day');
 };
 await choose('charge-1',basis,'lump_sum');
 await set(control('charge-1',price),'5000');
 await t.setInstant('charge-1',paymentDate,'2021-04-01T00:00:00Z');
 await commonTerms('charge-1');

 await t.addSide('Charge','charge-2');
 await choose('charge-2',basis,'capacity');
 await set(control('charge-2',price),'0.02');
 await set(control('charge-2','charge_daily_quantity'),'10000');
 await set(control('charge-2','charge_start'),'2021-04-01');
 await set(control('charge-2','charge_end'),'2021-06-11');
 await choose('charge-2',paymentDay,'2');
 await commonTerms('charge-2');
 await until(t.current());
 await check('each charge gets the label for its own selected basis',
  priceLabel('charge-1','Amount')+'&&'+priceLabel('charge-2','Rate per unit per day'));
 await check('owning charges count completed native schedule rows in the draft',
  `${at(ticket+' .ticket-progress-text')}?.textContent==='1 of 37 schedule rows completed'`);

 await evaluate(`{const send=window.fetch;window.chargeReplies={};window.fetch=async(input,init)=>{
  const response=await send(input,init),path=String(input);
  if(['/api/tickets/preview','/api/tickets/book'].includes(path))
   window.chargeReplies[path]={status:response.status,body:await response.clone().json()};
  return response;
 };}`);
 const preview="window.chargeReplies['/api/tickets/preview']";
 const refusal=(code,id)=>`${preview}?.status===422&&${preview}.body.code===${q(code)}&&${preview}.body.side==='charge-2'&&${preview}.body.field===${q(id)}`;
 const errorAt=selector=>`${at(selector+' .ticket-error[role=alert]')}?.textContent===${preview}.body.message&&${at(book)}.disabled`;

 const calendar=card('charge-2')+' .side-calendar';
 const paymentCalendar='#ticket-calendar-charge-2-payment';
 await set(paymentCalendar,'');
 await until(refusal('side_calendar_policy','calendar_policy')+'&&'+t.status()+"?.dataset.kind==='stale'",preview);
 await check('a structured calendar policy refusal appears at the charge Schedule calendars control',
  `${errorAt(calendar)}&&document.getElementById(${at(calendar)}.getAttribute('aria-describedby'))===${at(calendar+' .ticket-error')}&&!${at(card('charge-1')+' .side-calendar .ticket-error')}`,t.observed);
 await chooseCalendars(actions,card('charge-2'),'test_utc_every_day');
 await until(t.current());

 const day=at(control('charge-2',paymentDay));
 await check('Payment day offers 1 through 28',
  `JSON.stringify([...(${day}).options].slice(1).map(option=>option.value))===${q(JSON.stringify(Array.from({length:28},(_,i)=>String(i+1))))}`);
 // Enter a tampered choice into the real control to exercise the managed refusal and its field mapping.
 await evaluate(`${day}.add(new Option('29','29'))`);
 await set(control('charge-2',paymentDay),'29');
 await until(refusal('gas_composition_input',paymentDay)+'&&'+t.status()+"?.dataset.kind==='stale'",preview);
 await check('payment day 29 is refused on Payment day',errorAt(field('charge-2',paymentDay)),t.observed);
 await choose('charge-2',paymentDay,'2');
 await until(t.current());

 await click(book);
 const receipt="window.chargeReplies['/api/tickets/book']?.body";
 await wait(`!!${receipt}?.tradeId`,60000);
 const saved=await evaluate(receipt),position=`#desk-positions [data-row-key="${saved.tradeId}:${saved.instrumentId}"] .desk-reference`;
 await check('the gas trade with both charges books',`${receipt}.revision===1`);

 const referenceControl='#position-details-head .side-panel-reference';
 await wait("document.querySelector('#page-title').textContent==='Blotter'&&"+visible(referenceControl)+'&&'+idle,60000);
 const reference=await evaluate(at(referenceControl)+'.textContent');
 await blotterAt(actions,accrualClose);
 await check('the accrual close uses current knowledge after booking',
  `${at('#snapshot')}.value===${q(accrualClose.id)}&&${at('#date-context')}.dataset.contextMode==='current'&&
   ${at('#workspace-mode-label')}.textContent==='Working data · current knowledge'`);
 await set('#blotter-filters .table-search',reference);
 await wait(visible(position)+'&&'+idle,30000);
 await click(position);
 const term=name=>at(`#position-details-view .physical-instrument .side-term[data-field="${name}"] dd`);
 await wait(term('Rate per unit per day')+'?.checkVisibility()&&'+idle,30000);
 await check('the booked Terms show both stated prices without converting the capacity rate',
  `${term('Amount')}?.textContent==='5000'&&${term('Rate per unit per day')}?.textContent==='0.02'`);
 await openBookedSchedules(actions);
 const inspector=bookedTicket+' .inspection-rows';
 const rows=inspector+' tbody tr:has(.inspection-calculation)';
 const rowValues=`[...document.querySelectorAll(${q(rows)})].map(row=>[
  row.cells[2].textContent,row.cells[6].querySelector('span').textContent,
  ...[...row.cells[8].querySelectorAll('small')].map(line=>line.textContent).filter(text=>/^(Accrued|Outstanding|To come):/.test(text))
 ])`;
 const inspect=async(side,expected,label)=>{
  const tab=bookedTicket+` [data-tab="${side}"]`;
  await wait(visible(tab),30000);
  await click(tab);
  await wait(`document.querySelectorAll(${q(rows)}).length===${expected.length}&&!${at(inspector)}.hasAttribute('aria-busy')`,30000);
  await check(label,`JSON.stringify(${rowValues})===${q(JSON.stringify(expected))}`,rowValues);
 };
 await inspect('charge-1',[['Apr 1, 2021','-5,000','Accrued: -5,000','Outstanding: -5,000','To come: 0']],
  'the booked lump sum has one row with 5,000 accrued and zero to come');
 await inspect('charge-2',[
  ['May 2, 2021','-6,000','Accrued: -2,000','Outstanding: -2,000','To come: -4,000'],
  ['Jun 2, 2021','-6,200','Accrued: 0','Outstanding: 0','To come: -6,200'],
  ['Jul 2, 2021','-2,000','Accrued: 0','Outstanding: 0','To come: -2,000']
 ],'the booked capacity charge has one row per month with exact accrued and to-come cash');
}
