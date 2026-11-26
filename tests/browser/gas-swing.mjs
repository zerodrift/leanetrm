import {newTrade} from './new-trade.mjs';
import {blotterAt,bookedTicket,closeOn,decemberClose,gasTicket,openBookedSchedules,q} from './gas-ticket.mjs';

const at=selector=>'document.querySelector('+q(selector)+')';
const all=selector=>'[...document.querySelectorAll('+q(selector)+')]';
const days=['2021-01-31','2021-02-01','2021-02-02'];
const policy='delivery_schedule_v1';
const model='gas_swing_one_factor_lognormal_v1',grid='swing_trinomial_6h_unit_reference_v2';

export async function run(actions){
 const {command,evaluate,wait,set,click,check,persona,idle,url,fixture,shot,fitsWidth}=actions;
 const screen=(width,height)=>command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:width<600});
 const key=async(key,code)=>{
  for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{
   type,key,code:key,windowsVirtualKeyCode:code,...(key==='Enter'&&type==='keyDown'?{text:'\r'}:{})});
 };
 const reveal=selector=>evaluate('new Promise(resolve=>{'+at(selector)+'.scrollIntoView({block:"center"});requestAnimationFrame(()=>requestAnimationFrame(resolve));})');
 await screen(1440,1000);await command('Page.navigate',{url});
 await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 await blotterAt(actions,await decemberClose(evaluate));await newTrade(actions,'gas');
 const t=gasTicket(actions),section=t.ticket+' [data-term-section="swingRights"]',row=section+' [data-term-row="0"]';
 const control=id=>row+' [data-field="swing_'+id+'"] .ticket-control';
 await t.until('!!'+at(section));
 await check('a new ticket offers Swing right with no financial terms selected',at(section)+'.checkVisibility()&&!'+at(row));
 await t.tradeTerms();await t.physicalTerms('physical-1','receive','1000',{point:'synthetic_swing_point'});
 await set(t.control('physical-1','delivery_from'),days[0]);await set(t.control('physical-1','delivery_to'),'2021-02-03');
 await t.choose('physical-1','forecast_curve','assigned');
 await t.cashTerms('cash-1','cash','pay','physical-1','3');await t.setInstant('cash-1','cash_payment_at','2021-04-10T00:00:00Z');
 await click(section+' .ticket-add-term');await t.until(all(row+' .ticket-control')+'.length===17');
 await check('the swing term section starts with no selections, including model and grid',
  all(row+' .ticket-control')+'.every(c=>!c.value)');
 const choose=async(id,value)=>{await t.until(all(control(id)+' option')+'.some(o=>o.value==='+q(value)+')');await set(control(id),value);};
 await choose('side','physical-1');await choose('holder','us');await choose('limit_period','term');await choose('total_basis','per_period_v1');
 await choose('plan_rule','best_plan_on_forwards_v1');await choose('deadline_rule','lead_before_first_gas_day_v1');
 await t.choose('physical-1','quantity_policy',policy);await choose('model',model);await choose('numerical_policy',grid);
 await t.until(all(control('entries')+' option')+'.filter(o=>o.value).length===2');
 await evaluate('{const field='+at(control('entries'))+';for(const o of field.options)o.selected=!!o.value;field.dispatchEvent(new Event("change",{bubbles:true}));}');
 for(const [id,value] of Object.entries({planning_step:'100',daily_min:'700',daily_max:'1100',total_min:'3000',total_max:'3100',lead_hours:'6'}))await set(control(id),value);
 await t.until(t.current());
 await evaluate(`{const send=window.fetch;window.swingWrites=[];window.swingPrepares=[];
   window.fetch=async(input,init)=>{
    const path=String(input),response=await send(input,init);
    if(path==='/api/tickets/book'&&response.ok)window.swingBooked=await response.clone().json();
    if(path==='/api/gas/swing/prepare'&&response.ok){
     window.swingPrepares.push((await response.clone().json()).command);
     if(window.holdSwingPrepare){window.holdSwingPrepare=false;await new Promise(resolve=>window.releaseSwingPrepare=resolve);}
    }
    if(path==='/api/gas/swing/record'){
     window.swingWrites.push({wire:init.body,body:JSON.parse(init.body),status:response.status,outcome:await response.clone().json()});
     if(response.ok&&window.loseSwingReply){window.loseSwingReply=false;throw new TypeError('Synthetic lost response after commit');}
    }
    return response;
   };
  }`);
 await click(t.ticket+' .ticket-book',60000);await wait('!!window.swingBooked?.tradeId&&'+idle,60000);
 const detail=bookedTicket+' .ticket-swing-detail',drawer=detail+' details',trigger=bookedTicket+' .ticket-record-swing';
 const form=bookedTicket+' .ticket-swing-action',input=id=>form+' [name="'+id+'"]',retry=bookedTicket+' .ticket-retry-swing';
 const openTrade=async trade=>{
  const position='#desk-positions [data-row-key="'+trade.tradeId+':'+trade.instrumentId+'"] .desk-reference';
  await wait(at(position)+'?.checkVisibility()&&'+idle,60000);await click(position,60000);await openBookedSchedules(actions);
  await wait(at(trigger)+'?.checkVisibility()&&!'+at(trigger)+'.disabled&&!!'+at(drawer),60000);
 };
 await openTrade(await evaluate('window.swingBooked'));
 const savedField=id=>at(bookedTicket+' [data-term-section="swingRights"] [data-field="swing_'+id+'"] .ticket-value');
 await check('retained policy labels show versions on demand and distinguish an absent count',savedField('model')+'.title==='+q(model)+'&&'+
  savedField('model')+'.textContent.startsWith("One-factor lognormal")&&'+savedField('change_count')+'.textContent==="No changed-day limit"');
 await check('the booked three-day purchase reopens with one joint value and contingent commercial days',
  all(detail+' .swing-heading strong')+'.length===1&&'+at(detail+' .swing-heading strong')+'.textContent==="310 usd"&&'+
  all(detail+' [aria-label="Daily elections and delivery"] tbody tr')+'.map(r=>r.cells[2].textContent).join("|")==="Undecided · contingent|Undecided · contingent|Undecided · contingent"');
 const covered=bookedTicket+' [data-field="swing_entries"] .ticket-value';
 await check('both covered Sides read on one line in a desktop term field',
  `(()=>{const [a,b]=${all(covered)};return a?.textContent==='physical-1'&&b?.textContent==='cash-1'&&
   Math.abs(a.getBoundingClientRect().top-b.getBoundingClientRect().top)<2;})()`);
 await click('#blotter-dock [data-maximize=details]');
 // Begin on the focusable daily schedule; the rest of the quant review uses native keyboard navigation.
 await evaluate(at(detail+' [aria-label="Daily elections and delivery"]')+'.focus()');await key('Tab',9);
 await check('Tab reaches the quant disclosure from the daily schedule','document.activeElement==='+at(drawer+' summary'));
 await key('Enter',13);await wait(at(drawer)+'.open');await key('Tab',9);
 await check('keyboard review reaches the forward-input table and distinguishes the intrinsic plan from saved elections',
  'document.activeElement.getAttribute("aria-label")==="Forward inputs"&&'+at(drawer)+'.textContent.includes('+q(model)+')&&'+
  at(drawer)+'.textContent.includes('+q(grid)+')&&'+at(drawer)+'.textContent.includes("Intrinsic comparison")&&!'+at(drawer+' input')+'&&'+
  at(detail)+'.textContent.includes("Knowledge cutoff")&&'+at(drawer)+'.textContent.includes("Mark revision")');
 const marks=drawer+' [aria-label="Monthly volatility marks"] tbody tr';
 await check('January and February retain their distinct delivery months and mark revisions',
  `(()=>{const rows=${all(marks)};return rows.map(r=>r.cells[0].textContent).join('|')==='2021-01-01|2021-02-01'&&
   rows.every(r=>r.cells[1].textContent==='synthetic_swing_location'&&r.cells[3].textContent==='0'&&r.cells[4].textContent.length===36)&&
   rows[0].cells[4].textContent!==rows[1].cells[4].textContent;})()`,all(marks)+'.map(r=>r.textContent)');
 await reveal(detail+' h3');await shot('swing-desktop');
 await reveal(drawer+' h4');await shot('swing-intrinsic');
 await screen(390,844);await reveal(detail+' h3');await shot('swing-narrow');
 await reveal(detail+' [aria-label="Daily elections and delivery"]');await shot('swing-narrow-days');
 await check('the narrow swing review contains horizontal scrolling inside its tables',fitsWidth+'&&'+at(detail)+'.scrollWidth<='+at(detail)+'.clientWidth+1');
 await screen(1440,1000);await reveal(drawer+' summary');await click(drawer+' summary');
 const open=async()=>{
  // A Blotter refresh can replace the booked ticket between browser evaluations.
  await wait(`(()=>{const control=${at(trigger)};
   if(!control?.checkVisibility()||control.disabled||!(${idle}))return false;
   control.click();return true;
  })()`,60000);
  await wait(idle,60000);
 };
 const chooseDays=async(first,last,decision,quantity)=>{
  await set(input('firstDay'),first);await set(input('lastDay'),last);await set(input('decision'),decision);
  if(decision==='elect')await set(input('quantity'),quantity);
  await set(input('exercisingParty'),'us');
  for(const name of ['submittedAt','effectiveAt'])await set(input(name),'2020-12-18T16:00:00Z');
  await set(input('evidence'),'Synthetic browser instruction');
 };
 const submit=()=>click(form+' .ticket-submit-swing',60000);
 const focused=id=>'document.activeElement==='+at(input(id))+'&&document.activeElement.getAttribute("aria-invalid")==="true"';
 const shown=(i,state)=>wait(all(detail+' [aria-label="Daily elections and delivery"] tbody tr')+'['+i+']?.cells[2].textContent==='+q(state)+'&&!'+at(form)+'&&'+idle,60000);
 await open();await check('the priced ticket’s action shows the same three contingent days as the booked schedule',
  all(form+' [aria-label="Retained election days"] tbody tr')+'.map(r=>r.cells[r.cells.length-1].textContent).join("|")==='+
  all(detail+' [aria-label="Daily elections and delivery"] tbody tr')+'.map(r=>r.cells[r.cells.length-1].textContent).join("|")');
 await check('the action starts on the first day with empty instruction fields',
  'document.activeElement==='+at(input('firstDay'))+'&&'+all(form+' :is(input,select)')+'.every(c=>!c.value)');
 await chooseDays(days[1],days[0],'elect','700');await submit();await wait(focused('lastDay'));
 await check('the end date owns a reversed-range refusal before recording',focused('lastDay')+'&&window.swingWrites.length===0');
 await set(input('lastDay'),days[1]);
 await check('correcting the last date clears its invalid state','!'+at(input('lastDay'))+'.hasAttribute("aria-invalid")');
 await set(input('firstDay'),days[0]);await set(input('lastDay'),days[0]);
 await evaluate('window.holdSwingPrepare=true;'+at(form)+'.requestSubmit()');
 await wait('typeof window.releaseSwingPrepare==="function"');
 await check('a pending range preparation locks the form and suspends further recording',at(form)+'.inert&&'+at(trigger)+'.disabled&&window.swingWrites.length===0');
 await evaluate('window.releaseSwingPrepare()');await wait(focused('quantity'));
 await check('700 on the first day cannot reach the 3,000 minimum; the quantity owns the refusal',
  focused('quantity')+'&&window.swingWrites.at(-1).status===422&&'+at(bookedTicket+' .ticket-exercise-controls:has(.ticket-record-swing)')+'.textContent.includes("no feasible completion")');
 await reveal(input('quantity'));await shot('swing-refusal');
 await key('Backspace',8);await key('Backspace',8);await key('Backspace',8);
 await command('Input.insertText',{text:'1000'});
 await check('typing a corrected quantity clears the focused field’s invalid state',
  at(input('quantity'))+'.value==="1000"&&!'+at(input('quantity'))+'.hasAttribute("aria-invalid")');
 await set(input('lastDay'),days[1]);await evaluate('window.loseSwingReply=true');await submit();
 await wait(at(retry)+'?.checkVisibility()&&!'+at(retry)+'.disabled',60000);
 await click('#position-details-tabs-terms');await wait('!'+at(bookedTicket));
 await openBookedSchedules(actions);await wait(at(retry)+'?.checkVisibility()&&!'+at(retry)+'.disabled',60000);
 await check('remount offers recovery for the original range and disables a replacement',at(trigger)+'.disabled&&!'+at(form));
 await click(retry,60000);await wait('!'+at(retry)+'&&'+at(trigger)+'?.checkVisibility()&&!'+at(trigger)+'.disabled',60000);
 await shown(0,'Elected · 1,000');await shown(1,'Elected · 1,000');await shown(2,'Undecided · contingent');
 await check('the recovered range records both days atomically with one operation and the three retained heads',
  `(()=>{const [first,retry]=window.swingWrites.slice(-2),command=window.swingPrepares.at(-1);
   return first.status===200&&retry.status===200&&JSON.stringify(command)===JSON.stringify(first.body)&&
    first.wire===retry.wire&&JSON.stringify(first.outcome)===JSON.stringify(retry.outcome)&&first.body.operationId===command.operationId&&command.expectedHeads.length===3&&command.expectedHeads.every(h=>h.revisionId===null)&&
    command.elections.map(e=>e.day+'='+e.quantity).join(',')==='2021-01-31=1000,2021-02-01=1000';})()`);
 await click('#blotter-dock [data-maximize=details]');await openTrade(fixture.optionalTrade);
 await open();await chooseDays(days[0],days[0],'elect','0');await submit();await shown(0,'Elected · 0');
 await check('the optional right displays the explicit zero with commercial evidence',at(detail)+'.textContent.includes("Synthetic browser instruction")&&window.swingWrites.at(-1).body.elections[0].quantity==="0"');
 await open();await chooseDays(days[0],days[0],'withdraw');await submit();await shown(0,'Withdrawn');
 await check('withdrawing zero retains its predecessor and an undecided delivery obligation',
  'window.swingWrites.at(-1).body.elections[0].quantity===null&&!!window.swingWrites.at(-1).body.expectedHeads[0].revisionId');
 const periodRows=all(detail+' [aria-label="Period limits and elections"] tbody tr');
 await check('a one-day January and two-day February reset their applied per-day maximum and changed-day allowance',
  `${periodRows}.map(r=>r.cells[2].textContent).join('|')==='— – 1,005|— – 2,010'&&${periodRows}.every(r=>r.cells[6].textContent.startsWith('0 / 1'))`);
 await open();await chooseDays(days[0],days[0],'elect','975');await submit();await shown(0,'Elected · 975');
 await open();await chooseDays(days[1],days[1],'elect','1005');await submit();await shown(1,'Elected · 1,005');
 await check('the off-grid January and February quantities each consume one change in their own period',
  `${periodRows}.every(r=>r.cells[6].textContent.startsWith('1 / 1'))`);
 await open();await chooseDays(days[2],days[2],'elect','975');await submit();await wait(focused('quantity'));
 await check('an exhausted February count refuses another change at the exact quantity control',
  focused('quantity')+'&&window.swingWrites.at(-1).outcome.code==="swing_feasibility"');
 await blotterAt(actions,await closeOn(evaluate,'2021-01-31'));
 // The full fixture has more trades than the Blotter mounts in the viewport. Find the position by its public reference.
 const cashout=fixture.cashoutTrade;
 const gridState=()=>evaluate(`(()=>{const host=document.querySelector('#desk-positions'),holder=host.querySelector('.tabulator-tableholder');
  return {reference:${q(cashout.tradeReference)},mounted:host.querySelectorAll('[data-row-key]').length,gridHeight:holder?.clientHeight,
   scrollHeight:holder?.scrollHeight,displayed:!!host.querySelector('[data-row-key="${cashout.tradeId}:${cashout.instrumentId}"] .desk-reference'),
   books:Array.from(document.querySelector('#blotter-books').selectedOptions,o=>o.text),summary:document.querySelector('#futures-summary').textContent};})()`);
 await set('#blotter-filters .table-search',cashout.tradeReference);
 await openTrade(cashout).catch(async error=>{throw Error(error.message+' · filtered grid '+JSON.stringify(await gridState()));});
 const balance=bookedTicket+' [data-tab="imbalance"]',balanceRows=all(bookedTicket+' .ticket-imbalance tbody tr');
 const report=async()=>{await click(balance);await wait(balanceRows+'.length===6');return evaluate(balanceRows+'.map(r=>r.textContent)');};
 const original=await report();
 await check('the settled balance shows the reported statement and its independent paid cash',
  balanceRows+'.some(r=>r.textContent.includes("990")&&r.textContent.includes("500"))');
 await open();await chooseDays('2021-01-15','2021-01-15','withdraw');await submit();await shown(2,'Withdrawn');
 await check('the elapsed withdrawn day retains the missing election and unknown joint amount',
  at(detail)+'.textContent.includes("Missing election")&&'+at(detail+' .swing-counted strong')+'.textContent==="Unassessed"');
 const missing=await report();
 await open();await chooseDays('2021-01-15','2021-01-15','elect','1000');await submit();await shown(2,'Elected · 1,000');
 const corrected=await report();
 await check('a timely correction restores commercial coverage while the published cash-out remains identical',
  q(original)+' .join("|")==='+q(missing)+'.join("|")&&'+q(missing)+'.join("|")==='+q(corrected)+'.join("|")');
 const coverage=fixture.coverageTrade;
 await set('#blotter-filters .table-search',coverage.tradeReference);
 const coverageRow='#desk-positions [data-row-key="'+coverage.tradeId+':'+coverage.bindingId+'"] .desk-reference';
 await wait(at(coverageRow)+'?.checkVisibility()&&'+idle,60000);await click(coverageRow,60000);
 await openBookedSchedules(actions);await wait(at(detail+' [aria-label="Retained election days"]')+'?.checkVisibility()&&'+idle,60000);
 const assessed=root=>all(root+' [aria-label="Retained election days"] tbody tr')+
  '.map(r=>[r.cells[0].textContent,r.cells[2].textContent,r.cells[r.cells.length-1].textContent])';
 const expected=[['2021-01-30','Withdrawn','Missing election'],['2021-01-31','No election','Unassessed period'],['2021-02-01','No election','Undecided · contingent']];
 const matches=root=>'JSON.stringify('+assessed(root)+')==='+q(JSON.stringify(expected));
 await check('the unpriced ticket retains the withdrawal and distinguishes missed, blocked and open days',
  matches(detail)+'&&'+at(bookedTicket+' .ticket-status')+'.textContent.includes("exercise_marks")&&'+at(detail)+'.textContent.includes('+q(coverage.withdrawal)+')',assessed(detail));
 await click('#blotter-dock [data-maximize=details]');
 await reveal(detail+' [aria-label="Retained election days"]');await shot('swing-unpriced-coverage');
 await open();await check('the election form retains the same three day assessments',matches(form),assessed(form));
}
