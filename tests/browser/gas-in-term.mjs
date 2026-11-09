// A started-delivery gas trade's measures, and its Sides' delivery, settlement and quantity-basis states, as the trade view draws them.
import {closeOn,openBookedSchedules,q} from './gas-ticket.mjs';

export async function run(actions){
 const {command,evaluate,wait,click,set,check,persona,idle,url,fixture}=actions;
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 // The Blotter prices every trade of the fixture, so its settling gets longer than the ordinary wait.
 const frame=await closeOn(evaluate,'2026-11-06');
 await set('#close-session',frame.session);await wait(idle,60000);await set('#snapshot',frame.id);await wait(idle,60000);
 await click('#nav-futures');await wait("document.querySelector('#page-title').textContent==='Blotter'&&"+idle,60000);
 const open=async holding=>{
  await set('#blotter-filters .table-search',holding.tradeReference);
  const row=`#desk-positions [data-row-key="${holding.tradeId}:${holding.instrumentId}"] .desk-reference`;
  await wait(`!!document.querySelector(${q(row)})`,30000);await click(row);
  await wait("!!document.querySelector('#position-details-tabs-schedules')&&"+idle,30000);
 };
 const inspector='#position-details .ticket-grid .side-schedule-inspection',rows=inspector+' .inspection-rows tbody tr';
 const sideRows=async code=>{
  const tab=`#position-details .ticket-tab[data-tab="${code}"]`;
  await wait(`!!document.querySelector(${q(tab)})`,30000);await click(tab);
  await wait(`!!document.querySelector(${q(rows)})&&document.querySelector(${q(inspector+' [role=status]')})?.textContent.startsWith('1–')`,30000);
 };
 // Each row's text, from the page nearest the first row; a later page replaces it.
 const page=()=>evaluate(`[...document.querySelectorAll(${q(rows)})].map(r=>r.textContent)`);
 const nextPage=async()=>{
  const before=await evaluate(`document.querySelector(${q(inspector+' [role=status]')}).textContent`);
  await click(inspector+' > .inspection-pager button:last-child');
  await wait(`document.querySelector(${q(inspector+' [role=status]')}).textContent!==${q(before)}&&!!document.querySelector(${q(rows)})`,30000);
 };

 await open(fixture);
 await click('#position-details-tabs-terms');await wait("!!document.querySelector('#position-details-view .side-term')",30000);
 await check('the trade overview names its value measure and discounting basis',
  `(()=>{const row=[...document.querySelectorAll('#position-details-view .side-term')].find(r=>r.querySelector('dt').textContent==='Value measure');return row?.querySelector('dd').textContent==='Unrealized + realized unsettled · undiscounted';})()`,
  "[...document.querySelectorAll('#position-details-view .side-term')].slice(0,6).map(r=>r.textContent)");
 await openBookedSchedules(actions);
 await wait("!!document.querySelector('#position-details .ticket-measures')",30000);
 const {currency}=fixture,figures=[`Unrealized (remaining delivery)${fixture.unrealized} ${currency}`,`Realized, unsettled (delivered, not paid)${fixture.realizedUnsettled} ${currency}`,
  `Unrealized + realized unsettled${fixture.total} ${currency}`,'Discountingundiscounted'];
 await check('unrealized, realized unsettled and their labelled sum are separate undiscounted figures',
  `JSON.stringify([...document.querySelectorAll('#position-details .ticket-measures .ticket-summary-line')].slice(0,4).map(l=>l.textContent))===${q(JSON.stringify(figures))}`,
  "[...document.querySelectorAll('#position-details .ticket-measures .ticket-summary-line')].map(l=>l.textContent)");
 await check('expected cash is listed by due date, labelled as not added, and the measures never call themselves discounted',
  `(()=>{const text=document.querySelector('#position-details .ticket-measures').textContent,lines=[...document.querySelectorAll('#position-details .ticket-measures .ticket-summary-line')].map(l=>l.textContent);
   return lines[4]==='Expected cash by due date · not added to the total'&&${q(fixture.expectedCash)}.every((amount,n)=>lines[5+n].endsWith(amount+' '+${q(currency)}))&&lines.length===5+${fixture.expectedCash.length}&&!/(?<!un)discounted|present value/i.test(text);})()`);

 await click('#position-details .ticket-tab[data-tab="instrument"]');
 const tags="[...document.querySelectorAll('#position-details .ticket-grid .grid-source')].map(tag=>tag.textContent)";
 await wait(`${tags}.length>0`,30000);
 await check('the Instrument schedule marks ended gas days delivered',`${tags}.includes('Delivered')`,tags);

 await sideRows('payment');
 await check('a paid cash day says when it was paid and an accrued unpaid one gives its due date',
  `(()=>{const r=[...document.querySelectorAll(${q(rows)})].map(x=>x.textContent),paid=r.filter(t=>t.includes('Cash: Paid')),unpaid=r.filter(t=>t.includes('Cash: Accrued unpaid'));
   return paid.length>0&&unpaid.length>0&&paid.every(t=>/Paid on Oct 10, 2026/.test(t)&&!t.includes('Due date'))&&unpaid.every(t=>t.includes('Due date')&&!t.includes('Paid on')&&t.includes('Realized, unsettled (delivered, not paid)'));})()`,
  `(()=>{const r=[...document.querySelectorAll(${q(rows)})].map(x=>x.textContent);return [r[0],r[r.length-1]];})()`);
 await nextPage();await nextPage();
 await check('undelivered cash days are remaining, due on a date, never paid',
  `(()=>{const r=[...document.querySelectorAll(${q(rows)})].map(x=>x.textContent),remaining=r.filter(t=>t.includes('Cash: Remaining'));
   return remaining.length>0&&remaining.every(t=>t.includes('Due date')&&!t.includes('Paid on')&&t.includes('Unrealized (remaining delivery)'));})()`);
 await sideRows('gas');
 await check('the physical Side shows its delivery state and quantity basis, and no settlement state',
  `(()=>{const text=document.querySelector(${q(inspector+' .inspection-rows')}).textContent;
   return text.includes('Ended')&&text.includes('estimated from contract; allocation pending')&&text.includes('Settlement: not applicable (physical delivery)')&&!/Cash:|Paid|Due date/.test(text);})()`);

 await open(fixture.determined);await openBookedSchedules(actions);
 await sideRows('payment');
 await check('a final determination appears beside the accrual, which stays unchanged',
  `document.querySelector(${q(rows)}).textContent.includes('Final determination: ${fixture.determined.amount} · accrual unchanged')&&document.querySelector(${q(rows)}).textContent.includes('Accrued: -310')`);
}
