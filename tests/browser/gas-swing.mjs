import {newTrade} from './new-trade.mjs';
import {blotterAt,bookedTicket,decemberClose,gasTicket,openBookedSchedules,q} from './gas-ticket.mjs';

const at=selector=>'document.querySelector('+q(selector)+')';
const all=selector=>'[...document.querySelectorAll('+q(selector)+')]';
const days=['2021-01-31','2021-02-01','2021-02-02'];
const policy='swing_elected_delivery_quantity_v1';
const model='gas_swing_one_factor_lognormal_v1',grid='swing_trinomial_6h_half_even_18_v1';

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
 await click(section+' .ticket-add-term');await t.until(all(row+' .ticket-control')+'.length===12');
 await check('all twelve swing controls require explicit input, including the single supported model and grid',
  all(row+' .ticket-control')+'.every(c=>!c.value)');
 const choose=async(id,value)=>{await t.until(all(control(id)+' option')+'.some(o=>o.value==='+q(value)+')');await set(control(id),value);};
 await choose('side','physical-1');await choose('holder','us');await choose('quantity_policy',policy);
 await t.choose('physical-1','quantity_policy',policy);await choose('model',model);await choose('numerical_policy',grid);
 await t.until(all(control('entries')+' option')+'.filter(o=>o.value).length===2');
 await evaluate('{const field='+at(control('entries'))+';for(const o of field.options)o.selected=!!o.value;field.dispatchEvent(new Event("change",{bubbles:true}));}');
 for(const [id,value] of Object.entries({increment:'100',daily_min:'700',daily_max:'1100',total_min:'3000',total_max:'3100',lead_hours:'6'}))await set(control(id),value);
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
     window.swingWrites.push({body:JSON.parse(init.body),status:response.status});
     if(response.ok&&window.loseSwingReply){window.loseSwingReply=false;return new Response('Incomplete',{status:502});}
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
 await check('the booked three-day purchase reopens with one joint value and contingent commercial days',
  all(detail+' .swing-heading strong')+'.length===1&&'+at(detail+' .swing-heading strong')+'.textContent==="310 usd"&&'+
  all(detail+' > .swing-table tbody tr')+'.map(r=>r.cells[2].textContent).join("|")==="Undecided · contingent|Undecided · contingent|Undecided · contingent"');
 const covered=bookedTicket+' [data-field="swing_entries"] .ticket-value';
 await check('both covered Sides read on one line in a desktop term field',
  `(()=>{const [a,b]=${all(covered)};return a?.textContent==='physical-1'&&b?.textContent==='cash-1'&&
   Math.abs(a.getBoundingClientRect().top-b.getBoundingClientRect().top)<2;})()`);
 await click('#blotter-dock [data-maximize=details]');
 // Begin on the focusable daily schedule; the rest of the quant review uses native keyboard navigation.
 await evaluate(at(detail+' > .swing-table')+'.focus()');await key('Tab',9);
 await check('Tab reaches the quant disclosure from the daily schedule','document.activeElement==='+at(drawer+' summary'));
 await key('Enter',13);await wait(at(drawer)+'.open');await key('Tab',9);
 await check('keyboard review reaches the monthly volatility table and distinguishes the intrinsic plan from saved elections',
  'document.activeElement.getAttribute("aria-label")==="Monthly volatility marks"&&'+at(drawer)+'.textContent.includes('+q(model)+')&&'+
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
 await reveal(detail+' > .swing-table');await shot('swing-narrow-days');
 await check('the narrow swing review contains horizontal scrolling inside its tables',fitsWidth+'&&'+at(detail)+'.scrollWidth<='+at(detail)+'.clientWidth+1');
 await screen(1440,1000);await reveal(drawer+' summary');await click(drawer+' summary');
 const open=async()=>{await wait(at(trigger)+'?.checkVisibility()&&!'+at(trigger)+'.disabled',60000);await click(trigger);};
 const chooseDays=async(first,last,decision,quantity)=>{
  await set(input('firstDay'),first);await set(input('lastDay'),last);await set(input('decision'),decision);
  if(decision==='elect')await set(input('quantity'),quantity);
  await set(input('exercisingParty'),'us');
  for(const name of ['submittedAt','effectiveAt'])await set(input(name),'2020-12-18T16:00:00Z');
  await set(input('evidence'),'Synthetic browser instruction');
 };
 const submit=()=>click(form+' .ticket-submit-swing',60000);
 const focused=id=>'document.activeElement==='+at(input(id))+'&&document.activeElement.getAttribute("aria-invalid")==="true"';
 const shown=(i,state)=>wait(all(detail+' > .swing-table tbody tr')+'['+i+']?.cells[2].textContent==='+q(state)+'&&!'+at(form)+'&&'+idle,60000);
 await open();await check('the action starts on the first day with empty instruction fields',
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
    JSON.stringify(first.body)===JSON.stringify(retry.body)&&command.expectedHeads.length===3&&command.expectedHeads.every(h=>h.revisionId===null)&&
    command.elections.map(e=>e.day+'='+e.quantity).join(',')==='2021-01-31=1000,2021-02-01=1000';})()`);
 await click('#blotter-dock [data-maximize=details]');await openTrade(fixture.optionalTrade);
 await open();await chooseDays(days[0],days[0],'elect','0');await submit();await shown(0,'Elected · 0');
 await check('the optional right displays the explicit zero with commercial evidence',at(detail)+'.textContent.includes("Synthetic browser instruction")&&window.swingWrites.at(-1).body.elections[0].quantity==="0"');
 await open();await chooseDays(days[0],days[0],'withdraw');await submit();await shown(0,'Withdrawn · contingent');
 await check('withdrawing zero retains its predecessor and an undecided delivery obligation',
  'window.swingWrites.at(-1).body.elections[0].quantity===null&&!!window.swingWrites.at(-1).body.expectedHeads[0].revisionId');
}
