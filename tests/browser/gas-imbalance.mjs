import {chooseCalendars} from './side-calendars.mjs';
import {newTrade} from './new-trade.mjs';
import {blotterAt,bookedTicket,closeOn,decemberClose,gasTicket,openBookedSchedules,q} from './gas-ticket.mjs';

export async function run(actions){
 const {command,evaluate,wait,set,click,check,persona,idle,url}=actions;
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 const ended=await closeOn(evaluate,'2021-01-05');
 await blotterAt(actions,await decemberClose(evaluate));await newTrade(actions,'gas');
 const t=gasTicket(actions),{ticket,card,control,choose,until}=t;
 await until(`[...document.querySelectorAll(${q(ticket+' .ticket-add-kind')})].some(button=>button.textContent==='Imbalance cash-out')`);
 await t.addSide('Imbalance cash-out','imbalance-1');
 const fields=['source','index','spread_to_us','spread_by_us','payment_day','statement_policy','statement_basis','account_netting'];
 await check('a new cash-out requires every field without proposing a financial choice',
  fields.map(field=>`document.querySelector(${q(control('imbalance-1','imbalance_'+field))})?.value===''`).join('&&'));
 await t.tradeTerms();await t.physicalTerms('physical-1','receive','10000',{point:'synthetic_quantity_point'});
 await choose('physical-1','quantity_policy','contract');
 await set(control('physical-1','delivery_from'),'2021-01-01');await set(control('physical-1','delivery_to'),'2021-01-05');
 await click(card('cash-1')+' .ticket-remove');
 for(const [field,value] of [['source','physical-1'],['index','synthetic_entry_daily'],['payment_day','15'],['statement_policy','final_statement_balance_v1'],['statement_basis','daily_imbalance_v1'],['account_netting','net_accounts_v1']])
  await choose('imbalance-1','imbalance_'+field,value);
 for(const field of ['spread_to_us','spread_by_us'])await set(control('imbalance-1','imbalance_'+field),'0');
 await chooseCalendars(actions,card('imbalance-1'),'test_utc_every_day');await until(t.current());
 await evaluate(`{const send=window.fetch;window.cashoutBooking=null;window.fetch=async(input,init)=>{const response=await send(input,init);if(String(input)==='/api/tickets/book')window.cashoutBooking={body:JSON.parse(init.body),receipt:await response.clone().json()};return response;};}`);
 await click(ticket+' .ticket-book');await wait('!!window.cashoutBooking?.receipt?.tradeId',60000);
 const saved=await evaluate('window.cashoutBooking');
 await check('the ticket books a complete cash-out pair','window.cashoutBooking.receipt.revision===1');
 const position=`#desk-positions [data-row-key="${saved.receipt.tradeId}:${saved.receipt.instrumentId}"] .desk-reference`;
 async function open(){
  await command('Page.navigate',{url});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
  await blotterAt(actions,ended);await wait(`!!document.querySelector(${q(position)})&&`+idle,30000);await click(position);await openBookedSchedules(actions);
  await wait(`document.querySelector(${q(bookedTicket+' [data-tab="imbalance"]')})?.checkVisibility()`,30000);
  await click(bookedTicket+' [data-tab="imbalance"]');
 }
 await open();
 const rows=bookedTicket+' .ticket-imbalance tbody tr';
 await wait(`document.querySelectorAll(${q(rows)}).length===8`,30000);
 await check('ended days without statements have no quantity or amount and the subtotal counts four days once',
  `document.querySelector(${q(bookedTicket)})?.textContent.includes('incomplete: 4 unassessed days')&&[...document.querySelectorAll(${q(rows)})].every(row=>row.textContent.includes('no statement yet')&&row.querySelector('[data-column="quantity"]').textContent==='—'&&row.querySelector('[data-column="amount"]').textContent==='—')`);
 await evaluate(`(async()=>{const token=(await(await window.workspaceFetch('/api/bootstrap?includeFrames=false')).json()).csrfToken;
  const call=async(path,body)=>{const response=await window.workspaceFetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':token},body:JSON.stringify(body)});const value=await response.json();if(!response.ok)throw Error(JSON.stringify(value));return value;};
  const context={convention:${q(saved.body.context.convention)},snapshotId:${q(ended.id)},knowledgeMode:'current'},tradeId=${q(saved.receipt.tradeId)};
  const write=async(kind,details,day='2021-01-01')=>{const preview={context,tradeId,expectedRevision:1,day,kind,details},review=await call('gas/operations/preview',preview);return call('gas/operations/'+(kind==='service'?'configure':'record'),{preview,operationId:crypto.randomUUID(),previewHash:review.previewHash});};
  const quantities=await call('gas/quantities/read',{context,tradeId,selectionPolicy:'operational_projection_v1'});
  const account=await write('service',{obligationId:quantities.rows[0].obligationId,holderRole:'legal_entity',externalReference:'synthetic-browser-cashout',expectedPreviousId:null,validFrom:'2021-01-01',validTo:'2021-01-05',active:true,statementPolicy:'final_statement_balance_v1',termsReference:'Synthetic browser service',evidence:'Synthetic browser service evidence'});
  const state=await call('gas/operations/read',{context,tradeId,day:'2021-01-01'}),version=state.state.accounts.find(row=>row.id===account.accountId).version_id;
  for(const [day,balance] of [['2021-01-01','300'],['2021-01-02','-100']])await write('statement',{accountId:account.accountId,serviceVersionId:version,externalReference:'synthetic-browser-'+day,expectedPreviousId:null,balance,status:'final',reportedAt:'2021-01-05T00:00:00Z',includedMovementId:null,evidence:'Synthetic browser final statement'},day);
 })()`);
 await open();await wait(`document.querySelectorAll(${q(rows)}).length===8`,30000);
 const row=(day,entry)=>`document.querySelector(${q(rows+`[data-day="${day}"][data-entry="${entry}"]`)})`;
 await check('the Imbalance tab reports each direction, balance, price and exact amount from the backend',
  `${row('2021-01-01','imbalance-1.to_us')}.querySelector('[data-column="reported"]').textContent==='300'&&${row('2021-01-01','imbalance-1.to_us')}.querySelector('[data-column="amount"]').textContent==='900'&&${row('2021-01-02','imbalance-1.by_us')}.querySelector('[data-column="amount"]').textContent==='-310'&&document.querySelector(${q(bookedTicket)}).textContent.includes('incomplete: 2 unassessed days')`);
 await click(bookedTicket+' [data-tab="imbalance-1.to_us"]');
 await wait(`document.querySelector(${q(bookedTicket+' .inspection-rows')})?.textContent.includes('300')`,30000);
 await check('each payment of the pair opens its exact native Side schedule',
  `document.querySelector(${q(bookedTicket+' .inspection-rows')}).textContent.includes('900')&&!!document.querySelector(${q(bookedTicket+' [data-tab="imbalance-1.by_us"]')})`);
}
