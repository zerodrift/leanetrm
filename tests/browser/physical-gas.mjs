// Physical gas is entered in the deal ticket (docs/deal-ticket.md). These are its entry checks beside the deal ticket scenario's: one gas
// entry in New trade, the side the direction belongs to, the market date, points filtered by their zone, no point without a valuation mapping
// and a usage tariff proposed for a fee. The gas setup editor follows.
import {closeNewTrade,newTrade} from './new-trade.mjs';
import {blotterAt,decemberClose,gasTicket,q} from './gas-ticket.mjs';
import {run as gasSetup} from './gas-setup.mjs';

export async function run(actions){
 const {command,evaluate,wait,set,check,persona,shot,idle,url}=actions;
 const {ticket,card,field,control,choose,current,until,tradeTerms,physicalTerms,cashTerms,addSide}=gasTicket(actions);
 const listed=(code,id)=>`[...document.querySelectorAll(${q(control(code,id)+' option')})].map(o=>o.value).filter(Boolean)`;
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 await blotterAt(actions,await decemberClose(evaluate));
 await newTrade(actions,'gas');
 await wait(`document.querySelectorAll(${q(ticket+' .ticket-card')}).length===2`);
 await check('New trade lists physical gas once, as the deal ticket, and Transport, Park and loan and Storage as coming soon',
  `[...document.querySelectorAll('#new-trade-product button')].filter(b=>b.textContent==='Physical gas').length===1&&
   [...document.querySelectorAll('.product-soon button')].map(b=>b.disabled+':'+b.textContent).join()==='true:TransportComing soon,true:Park and loanComing soon,true:StorageComing soon'&&
   /shipper contract/.test(document.querySelector('.product-soon button').title)&&!document.querySelector('#gas-page form')`);
 await check('gas entry exposes its market date without asking for a curve selection',"!document.querySelector('#snapshot').closest('.date-control').hidden&&!document.querySelector('#status').textContent.includes('Choose a curve')");
 await tradeTerms();
 await wait(`${listed('physical-1','direction')}.length===2`);
 await check('buy or sell belongs to the physical side rather than the trade strip',
  `${listed('physical-1','direction')}.join()==='receive,deliver'&&!document.querySelector(${q(ticket+' .ticket-strip [data-field="direction"]')})`);

 for(const [id,value] of [['delivery_from','2021-01-01'],['delivery_to','2021-02-01']])await set(control('physical-1',id),value);
 for(const [id,value] of [['product','test_gas'],['pipeline','test_pipeline_a'],['zone','synthetic_gas_zone_b']])await choose('physical-1',id,value);
 await wait(`${listed('physical-1','delivery_point')}.length>0`);
 await check('a zone lists only its own delivery points and keeps the delivery dates',
  `${listed('physical-1','delivery_point')}.join()==='synthetic_gas_point_b'&&document.querySelector(${q(control('physical-1','delivery_from'))}).value==='2021-01-01'&&document.querySelector(${q(control('physical-1','delivery_to'))}).value==='2021-02-01'`,
  listed('physical-1','delivery_point'));

 await choose('physical-1','zone','synthetic_gas_zone_a');
 await wait(`${listed('physical-1','delivery_point')}.includes('synthetic_gas_point_a')`);
 await check('a point without a valuation mapping is not offered, so no gas side is entered that cannot be priced',
  `!${listed('physical-1','delivery_point')}.includes('synthetic_gas_point_unpriced')`,listed('physical-1','delivery_point'));

 // A delivery-stage fee without a manual price is offered the one usage tariff that covers its side, by the tariff's configured name.
 await physicalTerms('physical-1','receive','1000');
 await cashTerms('cash-1','cash','pay','physical-1','3');
 await addSide('Fee','fee-1');
 for(const [id,value] of [['fee_direction','pay'],['fee_source','physical-1'],['fee_stage','delivery']])await choose('fee-1',id,value);
 await set(control('fee-1','fee_payment_at'),'2021-02-05T00:00:00Z');
 await until(`!!document.querySelector(${q(field('fee-1','fee_tariff')+' .ticket-chip')})&&`+current());
 await check('a delivery-stage fee is offered its usage tariff by name, and the schedule prices it',
  `document.querySelector(${q(field('fee-1','fee_tariff')+' .ticket-chip')}).textContent.includes('synthetic_gas_usage_a')&&!!document.querySelector(${q(card('fee-1')+' .ticket-summary')})`,
  `({chip:document.querySelector(${q(field('fee-1','fee_tariff')+' .ticket-chip')})?.textContent})`);
 await check('the gas ticket fits the viewport',"document.documentElement.scrollWidth<=innerWidth");
 await shot('physical-gas-booking');
 await closeNewTrade(actions);
 await gasSetup(actions);
}
