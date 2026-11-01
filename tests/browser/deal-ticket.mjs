// The deal ticket (docs/deal-ticket.md) enters a physical gas trade from New trade: live previews of the whole draft, lookups that say
// where their values came from, refusals that keep the last schedule, a booking whose response is lost reconciled after a reload, and the
// booked trade opened read-only in the Blotter's Details pane and in the trade's own window, a day's quantity recorded in its physical side's
// tab, whose lost response is checked once, the coming-soon items drawn and never sent, and the quantity links of a linked side.
import {newTrade} from './new-trade.mjs';
import {blotterAt,bookedTicket as booked,decemberClose,gasTicket,q} from './gas-ticket.mjs';

export async function run(actions){
 const {command,evaluate,wait,set,click,check,persona,shot,idle,url,tab}=actions;
 const {ticket,card,field,control,choose,status,current,month,observed,until,tradeTerms,physicalTerms,cashTerms,addSide}=gasTicket(actions);

 // A desk-sized window shows the Blotter's grid beside the Details pane; the last step narrows it.
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 const frame=await decemberClose(evaluate);await blotterAt(actions,frame);
 await newTrade(actions,'gas');
 await wait(`document.querySelectorAll(${q(ticket+' .ticket-card')}).length===2`);
 await check('New trade opens physical gas in the deal ticket with one physical and one payment card',
  `[...document.querySelectorAll(${q(ticket+' .ticket-card')})].map(c=>c.dataset.kind).join()==='physical,cash'`);
 await check('the common band rows line up across the cards',`(()=>{const cards=[...document.querySelectorAll(${q(ticket+' .ticket-card')})],rows=[...cards[0].querySelectorAll('[data-band]')].map(r=>r.dataset.band);
  return rows.length>0&&rows.every(band=>new Set(cards.map(c=>Math.round(c.querySelector('[data-band="'+band+'"]').getBoundingClientRect().top))).size===1);})()`);

 // Coming-soon items are drawn with their hover text and never sent: every preview and booking request is kept to be read later.
 await evaluate(`{const original=window.fetch;window.ticketBodies=[];window.fetch=async(...args)=>{const sent=/[/]tickets[/](preview|book)$/.exec(String(args[0]));if(sent)ticketBodies.push({path:sent[1],draft:JSON.parse(args[1].body).draft});return original(...args);};}`);
 const soonIds=await evaluate(`[...document.querySelectorAll(${q(ticket+' .ticket-field[data-state=soon]')})].map(f=>f.dataset.field)`);
 await check('a coming-soon field is disabled, tagged Coming soon and says what it will do on hover',
  `(()=>{const f=document.querySelector(${q(field('physical-1','heat_content'))}),c=f?.querySelector('.ticket-control');
   return !!c&&c.disabled&&c.getAttribute('aria-disabled')==='true'&&f.title===c.title&&/Btu per cubic foot/.test(c.title)&&f.querySelector('.ticket-soon-tag')?.textContent==='Coming soon';})()`,q(soonIds));
 await check('the floating side and the Imbalance tab are offered as coming soon, and cannot be chosen',
  `(()=>{const add=[...document.querySelectorAll(${q(ticket+' .ticket-add-kind')})].find(b=>b.textContent.startsWith('Floating')),tab=document.querySelector(${q(ticket+' .ticket-tab[data-tab="imbalance"]')});
   return add.disabled&&/does not accept it yet/.test(add.title)&&tab.disabled&&tab.getAttribute('aria-disabled')==='true'&&/cash-out/.test(tab.title);})()`);

 await tradeTerms();await physicalTerms('physical-1','deliver','5000');
 await wait(`!!document.querySelector(${q(field('physical-1','gas_day_start')+' .ticket-chip')})&&!!document.querySelector(${q(field('physical-1','price')+' .ticket-chip')})`);
 await check('choosing the delivery point fills the gas-day start and market price from labelled lookups',
  `document.querySelector(${q(field('physical-1','price')+' .ticket-chip')}).textContent.includes('close 2020-12-18')&&/^\\d\\d:\\d\\d$/.test(document.querySelector(${q(field('physical-1','gas_day_start')+' .ticket-value')}).textContent)`);
 await cashTerms('cash-1','cash','receive','physical-1','3.412');
 await until(current()+`&&${month()}.includes('-155,000')`);
 await set(`${ticket} .ticket-level`,'day');
 await wait(`document.querySelectorAll(${q(ticket+' .tabulator-row[data-row-key^="2021-01-"]')}).length>0`);
 // The holder draws only the rows in view: scrolling it through collects every drawn day row.
 const days=await evaluate(`(async()=>{const holder=document.querySelector(${q(ticket+' .tabulator-tableholder')}),seen=new Set();
  for(let top=0;top<=holder.scrollHeight;top+=200){holder.scrollTop=top;await new Promise(r=>setTimeout(r,60));for(const row of holder.querySelectorAll('.tabulator-row[data-row-key^="2021-01-"]'))seen.add(row.dataset.rowKey);}
  holder.scrollTop=0;return [...seen];})()`);
 await check('the day level shows the 31 January gas days under their month',`${days.length}===31&&${month()}.length>0`,q(days));
 await check('a new trade\'s schedule takes no entries',`!document.querySelector(${q(ticket+' .ticket-cell-input')})`);
 await set(`${ticket} .ticket-level`,'month');await wait(`!document.querySelector(${q(ticket+' .tabulator-row[data-row-key^="2021-01-"]')})`);

 await set(control('physical-1','quantity'),'10000');
 await until(current()+`&&${month()}.includes('-310,000')`);
 await check('changing the quantity updates the month amount from the backend preview',`${month()}.includes('1,057,720')`);
 await check('cells the change moved are marked',`!!document.querySelector(${q(ticket+' .tabulator-row[data-row-key="2021-01"] .grid-changed')})`);

 // A burst of edits: the first preview's response is held until the last one has been drawn, then arrives late and is discarded.
 await evaluate(`{const original=window.fetch;window.ticketFetch=original;window.previewHold={count:0,released:false};
  window.fetch=async(...args)=>{const response=await original(...args);if(!String(args[0]).endsWith('/tickets/preview'))return response;
   const n=++previewHold.count;
   if(n===1)await new Promise(release=>{previewHold.release=()=>{previewHold.released=true;release();};});
   else if(n===5)setTimeout(()=>previewHold.release(),300);
   return response;};}`);
 for(const quantity of ['11000','12000','13000','14000','15000']){await set(control('physical-1','quantity'),quantity);await new Promise(r=>setTimeout(r,450));}
 await until("previewHold.count===5&&previewHold.released");await new Promise(r=>setTimeout(r,400));
 await evaluate('window.fetch=window.ticketFetch');
 await check('after a burst of edits the schedule is the last edit\'s, never a late earlier response',current()+`&&${month()}.includes('-465,000')`,observed);

 await set(control('physical-1','quantity'),'15,000');
 await until(`${status()}?.dataset.kind==='stale'`);
 await check('a refused quantity keeps the last schedule, marked stale, with the reason on Quantity',
  `${month()}.includes('-465,000')&&!!document.querySelector(${q(ticket+' .standard-data-grid.stale')})&&document.querySelector(${q(field('physical-1','quantity')+' .ticket-error')})?.textContent.length>0`,observed);
 await set(control('physical-1','quantity'),'10000');await until(current()+`&&${month()}.includes('-310,000')`);

 await addSide('Fee','fee-1');
 await wait(`!!document.querySelector(${q(ticket+' .ticket-tab[data-tab="fee-1"]')})`);
 await check('Add side · Fee adds a fee card and its schedule tab',`document.querySelectorAll(${q(ticket+' .ticket-card')}).length===3`);
 await click(card('fee-1'));
 await check('clicking the fee card selects its schedule tab',`document.querySelector(${q(ticket+' .ticket-tab[data-tab="fee-1"]')}).getAttribute('aria-selected')==='true'&&document.querySelector(${q(card('fee-1'))}).classList.contains('selected')`);
 await cashTerms('fee-1','fee','pay','physical-1','0.045');
 await click(`${ticket} .ticket-tab[data-tab="instrument"]`);
 await until(current()+`&&${month()}.includes('1,043,770')`);
 const previewed=await evaluate(month());
 await shot('deal-ticket');

 // The booking's response is lost: its request is kept and the ticket locked, and after a reload the Blotter offers to reconcile it.
 await evaluate(`{const original=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith('/tickets/book')&&response.ok)throw new TypeError('Synthetic lost booking response');return response;};}`);
 const reconcile=`[...document.querySelectorAll('#gas-ticket .ticket-booking button')].find(b=>b.textContent==='Check booking status'&&!b.disabled)`;
 await click(`${ticket} .ticket-book`);await wait(`!!${reconcile}`);
 await check('no preview or booking request carries a coming-soon field',
  `['preview','book'].every(path=>ticketBodies.some(b=>b.path===path))&&ticketBodies.every(b=>{const sent=[b.draft.trade,...b.draft.sides.map(s=>s.values)].flatMap(Object.keys);
   return ${q(soonIds)}.every(id=>!sent.includes(id))&&b.draft.sides.every(s=>s.kind!=='floating');})&&${q(soonIds)}.length===10`,`({soon:${q(soonIds)},bodies:ticketBodies.length})`);
 await check('a booking whose response is lost keeps its request and locks the ticket',`document.querySelector('#gas-ticket > div').inert&&document.querySelector('#gas-ticket .ticket-booking').textContent.includes('The booking is not confirmed')`,"document.querySelector('#gas-ticket .ticket-booking').textContent");
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait("document.querySelector('#page-title').textContent==='Blotter'&&"+idle);
 await evaluate("[...document.querySelectorAll('#product-trade-pending button')].find(b=>b.textContent==='Reconcile physical gas request').click()");
 await wait(`!!${reconcile}&&document.querySelectorAll(${q(ticket+' .ticket-card')}).length===3`);
 await check('the reconciled ticket shows the request\'s exact terms, locked',`document.querySelector(${q(control('physical-1','quantity'))}).value==='10000'&&document.querySelector('#gas-ticket > div').inert`);
 await evaluate(`${reconcile}.click()`);
 await until(`!!document.querySelector(${q(booked)})&&`+current(booked),`({error:document.querySelector('#error').textContent,booking:document.querySelector('#gas-ticket .ticket-booking')?.textContent,details:document.querySelector('#position-details').textContent.slice(0,300)})`);
 await check('the booked trade opens read-only in the Details pane with the schedule it was previewed with',
  `${month(booked)}===${q(previewed)}&&document.querySelectorAll(${q(booked+' .ticket-card')}).length===3&&!document.querySelector(${q(booked+' .ticket-control:not([disabled])')})&&!document.querySelector(${q(booked+' .ticket-book')})`,
  `({booked:${month(booked)},previewed:${q(previewed)}})`);
 // The row's own window opens the same read-only ticket.
 const reference=await evaluate("document.querySelector('#details-title').textContent.split(' · ')[0]"),rows="[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.getClientRects().length)";
 await set('#blotter-filters .table-search',reference);await wait(`${rows}.length===1&&${rows}[0].textContent.includes(${q(reference)})`);
 await evaluate("window.__opened=[];window.open=(...args)=>{window.__opened.push(args[0]);return null;}");
 const at=await evaluate(`(()=>{const r=${rows}[0].querySelector('.desk-reference').getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()`);
 for(const type of ['mousePressed','mouseReleased'])await command('Input.dispatchMouseEvent',{type,...at,button:'right',buttons:type==='mousePressed'?2:0,clickCount:1});
 await wait("!!document.querySelector('.blotter-row-menu:not([hidden])')");
 await evaluate("[...document.querySelectorAll('.blotter-row-menu:not([hidden]) [role=menuitem]')].find(i=>i.firstChild.textContent==='Open in its own window').click()");
 await wait('window.__opened.length===1');
 const own=await tab();
 try{
  await own.command('Page.navigate',{url:await evaluate('window.__opened[0]')});
  const windowTicket='#position-window-view .deal-ticket[data-mode=booked]';
  await own.wait(`!!document.querySelector(${q(windowTicket+' .tabulator-row[data-row-key="2021-01"]')})`,30000);
  await check('the trade\'s own window opens the same read-only ticket',`${q(await own.evaluate(month(windowTicket)))}===${q(previewed)}`);
 }finally{await own.close();}
 const stacked=`(()=>{const cards=[...document.querySelectorAll(${q(booked+' .ticket-card')})].map(c=>c.getBoundingClientRect());return cards.length===3&&new Set(cards.map(c=>Math.round(c.left))).size===1&&cards[0].bottom<=cards[1].top;})()`;
 await check('a wide Details pane draws the cards side by side',`!${stacked}`);
 await command('Emulation.setDeviceMetricsOverride',{width:700,height:1000,deviceScaleFactor:1,mobile:false});
 await wait(stacked);await check('narrowing the Details pane stacks the cards',stacked);
 await command('Emulation.clearDeviceMetricsOverride');

 // A booked physical side's day quantities are entered in its tab and recorded as daily quantity revisions; the ticket then reads the trade
 // again, and only that day and its month change.
 // A row's text, with the quantities its entries show: an entry opens empty, its drawn quantity its placeholder.
 const row=(key,root=booked)=>`(()=>{const r=document.querySelector(${q(root+' .tabulator-row[data-row-key="'+key+'"]')});return r?r.textContent+[...r.querySelectorAll('input')].map(i=>' '+(i.value||i.placeholder)).join(''):'';})()`;
 await set(`${booked} .ticket-level`,'day');
 await wait(`!!document.querySelector(${q(booked+' .tabulator-row[data-row-key="2021-01-05"]')})`);
 await check('the Instrument tab takes no entries',`!document.querySelector(${q(booked+' .ticket-cell-input')})`);
 await click(`${booked} .ticket-tab[data-tab="physical-1"]`);
 const cell=`${booked} .tabulator-row[data-row-key="2021-01-05"] .ticket-cell-input`;
 await wait(`!!document.querySelector(${q(cell)})`);
 await check('only the physical side\'s day quantities are entries',`document.querySelectorAll(${q(booked+' .tabulator-row[data-row-key="2021-01"] .ticket-cell-input')}).length===0&&document.querySelectorAll(${q(booked+' .tabulator-row[data-row-key="2021-01-05"] .ticket-cell-input')}).length===1`);
 const before={day:await evaluate(row('2021-01-04')),month:await evaluate(row('2021-01'))};
 await set(cell,'9000');
 const record=`${booked} .ticket-record`;
 await wait(`!!document.querySelector(${q(record)})`);
 for(const [id,value] of [['kind','nominated'],['status','provisional'],['evidence','Synthetic nomination']])await set(`${record} [data-field="${id}"] .ticket-control`,value);
 await click(`${record} .ticket-record-send`);
 await until(`${row('2021-01-05')}.includes('-9,000')&&!document.querySelector(${q(record)})`,`({record:document.querySelector(${q(record)})?.textContent,day:${row('2021-01-05')},status:${status(booked)}?.textContent})`);
 await check('a recorded day quantity changes that day and its month only, after the trade is read again',
  `${row('2021-01-05')}.includes('Nominated')&&${row('2021-01')}.includes('-309,000')&&${row('2021-01-04')}===${q(before.day)}&&
   [...document.querySelectorAll(${q(booked+' .tabulator-row .grid-changed')})].map(m=>m.closest('.tabulator-row').dataset.rowKey).every(k=>k==='2021-01-05'||k==='2021-01')&&
   !!document.querySelector(${q(booked+' .tabulator-row[data-row-key="2021-01-05"] .grid-changed')})`,`({day:${row('2021-01-05')},month:${row('2021-01')},before:${q(before)}})`);
  // The tab's columns are Period, Quantity, Market price and Amount: the recorded day's amount is still the contract's, and its source says so.
 const sources=(key,column)=>`[...document.querySelectorAll(${q(booked+' .tabulator-row[data-row-key="'+key+'"] .tabulator-cell[tabulator-field="'+column+'"] .grid-source')})].map(t=>t.textContent)`;
 await check('the recorded day\'s amount is tagged as the contract\'s while its quantity is tagged as recorded',
  `${sources('2021-01-05','c3')}.includes('Contract')&&${sources('2021-01-05','c1')}.join()==='Nominated'`,`({amount:${sources('2021-01-05','c3')},quantity:${sources('2021-01-05','c1')}})`);

 // A day record whose response is lost keeps its command, offers only Check and Retry, and Check finds the one record it made.
 await evaluate(`{const original=window.recordFetch=window.fetch;window.recordRequests=0;window.fetch=async(...args)=>{const response=await original(...args);
  if(String(args[0]).endsWith('/gas/quantities/record')){recordRequests++;if(response.ok)throw new TypeError('Synthetic lost record response');}return response;};}`);
 const later=`${booked} .tabulator-row[data-row-key="2021-01-06"] .ticket-cell-input`;
 await set(later,'8000');await wait(`!!document.querySelector(${q(record)})`);
 for(const [id,value] of [['kind','nominated'],['status','provisional'],['evidence','Synthetic nomination']])await set(`${record} [data-field="${id}"] .ticket-control`,value);
 await click(`${record} .ticket-record-send`);
 // While the record is being sent its Check and Retry are drawn disabled; they are offered once the response is lost.
 await until(`!!document.querySelector(${q(record+' .ticket-record-check:not([disabled])')})`,`document.querySelector(${q(record)})?.textContent`);
 await check('a day record whose response is lost keeps its command and offers only Check and Retry',
  `!document.querySelector(${q(record+' .ticket-record-send')})&&!!document.querySelector(${q(record+' .ticket-record-retry')})&&document.querySelector(${q(record+' [data-field="quantity"] .ticket-control')}).disabled`);
 await click(`${record} .ticket-record-check`);
 await until(`${row('2021-01-06')}.includes('-8,000')&&!document.querySelector(${q(record)})`,`({record:document.querySelector(${q(record)})?.textContent,day:${row('2021-01-06')}})`);
 await check('checking the lost record finds the one record it made',`recordRequests===1&&${row('2021-01-06')}.includes('Nominated')`);
 await evaluate('window.fetch=window.recordFetch');

 // A linked side's receipt, delivery and fuel are drawn in the Quantity links tab exactly as the preview returned them.
 await newTrade(actions,'gas');
 await wait(`document.querySelectorAll(${q(ticket+' .ticket-card')}).length===2`);
 await evaluate(`{const original=window.linksFetch=window.fetch;window.fetch=async(...args)=>{const response=await original(...args);if(String(args[0]).endsWith('/tickets/preview')&&response.ok)window.lastPreview=await response.clone().json();return response;};}`);
 await click(`${card('cash-1')} .ticket-remove`);
 await addSide('Physical','physical-2');
 await tradeTerms();await physicalTerms('physical-1','receive','10000');await physicalTerms('physical-2','deliver',null);
 await choose('physical-2','link_source','physical-1');await choose('physical-2','link_formula','gross_less_fraction');await set(control('physical-2','link_fraction'),'0.02');
 await until(current()+`&&window.lastPreview?.links?.length===1`);
 await click(`${ticket} .ticket-tab[data-tab="links"]`);
 const links=`(document.querySelector(${q(ticket+' .ticket-grid .tabulator-row')})?.textContent??'')`;
 await wait(`${links}.length>0`);
 await check('the Quantity links tab shows the linked side\'s receipt, delivery and fuel as the preview returned them',
  `(()=>{const l=window.lastPreview.links[0],text=${links};return l.source==='physical-1'&&l.target==='physical-2'&&l.receipt==='310000'&&l.delivery==='303800'&&l.fuel==='6200'&&
   ['physical-1','physical-2','310,000','303,800','6,200'].every(v=>text.includes(v));})()`,`({links:window.lastPreview?.links,text:${links}})`);
 // A fee charged on the linked side's receipt rather than its delivery is charged on the gas before its fuel.
 await addSide('Fee','fee-1');
 await cashTerms('fee-1','fee','pay','physical-2','0.1');
 const fee=`(document.querySelector(${q(card('fee-1')+' .ticket-summary')})?.textContent??'')`;
 await until(current()+`&&${fee}.includes('-30,380')`,`({fee:${fee},status:document.querySelector(${q(ticket+' .ticket-status')})?.textContent})`);
 await choose('fee-1','fee_stage','receipt');
 await until(current()+`&&${fee}.includes('-31,000')`,`({fee:${fee}})`);
 await check('a fee on the linked side\'s receipt rather than its delivery is charged before the fuel',`${fee}.includes('-31,000')`);
 await evaluate('window.fetch=window.linksFetch');
}
