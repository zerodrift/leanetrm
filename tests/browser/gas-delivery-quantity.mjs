import {newTrade,closeNewTrade} from './new-trade.mjs';
import {gasTicket,blotterAt,decemberClose,openBookedSchedules,q} from './gas-ticket.mjs';

export async function run(actions){
 const {command,evaluate,wait,click,set,check,persona,idle,url,fixture}=actions;
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 await blotterAt(actions,await decemberClose(evaluate));await newTrade(actions,'gas');
 const t=gasTicket(actions),{control,field,choose,until}=t;
 await until(`document.querySelector(${q(control('physical-1','quantity_policy'))})?.value==='delivery_schedule_v1'`);
 await check('a new physical Side visibly proposes Delivery schedule with Contract quantity also available',
  `(()=>{const select=document.querySelector(${q(control('physical-1','quantity_policy'))});return select.selectedOptions[0].textContent==='Delivery schedule'&&[...select.options].some(o=>o.value==='contract'&&o.textContent==='Contract quantity');})()`);
 await t.tradeTerms();await t.physicalTerms('physical-1','receive','1000');await t.cashTerms('cash-1','cash','pay','physical-1','3');
 await t.addSide('Physical','physical-2');await t.physicalTerms('physical-2','deliver',null,{zone:'synthetic_gas_zone_b',point:'synthetic_gas_point_b'});
 await choose('physical-2','link_source','physical-1');await choose('physical-2','link_formula','gross_less_fraction');await set(control('physical-2','link_fraction'),'0.02');
 await choose('physical-1','quantity_policy','delivery_schedule_v1');
 await until(`document.querySelector(${q(field('physical-1','quantity_policy')+' .ticket-error')})?.textContent.includes('quantity link')`);
 await check('a linked composition refuses the opt-in on its valuation quantity field',
  `document.querySelector(${q(field('physical-1','quantity_policy')+' .ticket-error')}).textContent.includes('Contract quantity')`);
 await closeNewTrade(actions);
 const frame=await evaluate(`(async()=>{const w=await(await window.workspaceFetch('/api/workspace')).json();const f=w.frames.find(f=>f.id===${q(fixture.snapshotId)});return {id:f.id,session:f.close?.session??null};})()`);
 await blotterAt(actions,frame);
 await set('#blotter-filters .table-search',fixture.tradeReference);
 const target=`#desk-positions [data-row-key="${fixture.tradeId}:${fixture.instrumentId}"] .desk-reference`;
 await wait(`!!document.querySelector(${q(target)})`,30000);await click(target);await openBookedSchedules(actions);
 await wait("!!document.querySelector('#position-details .ticket-tab[data-tab=instrument]')",30000);
 await click('#position-details .ticket-tab[data-tab="instrument"]');await set('#position-details .ticket-level','day');
 const grid='#position-details .ticket-grid';
 await wait(`document.querySelector(${q(grid+' .tabulator-row[data-row-key="2021-01-13"]')})?.textContent.includes('Allocated, final')`,30000);
 await check('the schedule labels selected allocations and explicit contract fallback',
  `(()=>{const text=document.querySelector(${q(grid)}).textContent;return text.includes('Allocated, final')&&text.includes('Contract')&&text.includes('Scheduled');})()`);
 await check('selected quantities expose their recorded evidence',
  `[...document.querySelectorAll(${q(grid+' .grid-decorated')})].some(e=>e.title.includes('Synthetic selected delivery statement'))`);
 await wait("!!document.querySelector('#position-details .ticket-tab[data-tab=payment]')",30000);
 await click('#position-details .ticket-tab[data-tab="payment"]');
 const rows='#position-details .side-schedule-inspection .inspection-rows';
 await wait(`document.querySelector(${q(rows)})?.textContent.includes('Overpaid')`,30000);
 await check('cash retains the full overpaid balance and explains the selected quantity basis',
  `(()=>{const text=document.querySelector(${q(rows)}).textContent;return text.includes('2,940')&&text.includes('follows selected delivery quantity')&&text.includes('Contract: -4,000')&&text.includes('selected ratio: 0.475');})()`);
}
