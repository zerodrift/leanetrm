import {openPhysicalTrade} from './physical-operations-grid.mjs';
// A complete delivery month is reviewed and issued as a bill beside the daily determinations it gathers.
export async function run(actions){
 const {wait,click,set,submit,check,shot,idle,fixture}=actions;
 await click('#nav-physical-operations');await openPhysicalTrade(actions,fixture.bills.tradeId);
 await wait("!document.querySelector('#physical-operations-tabs-cash').disabled&&"+idle);await click('#physical-operations-tabs-cash');
 const root='.gas-bills',field=name=>root+' [data-bills="'+name+'"]',node=name=>`document.querySelector(${JSON.stringify(field(name))})`;
 await wait(`${node('month')}?.options.length>1&&`+idle);
 await check('a complete month is offered for its bill and every payment is accounted for',
  `[...${node('month')}.options].some(o=>o.value==='2021-01-01'&&!o.disabled&&o.textContent.includes('ready for its bill'))&&${node('coverage')}.textContent.includes('payment · covered')&&${node('coverage')}.textContent.includes('fee · covered')`);
 for(const [name,value] of [['month','2021-01-01'],['scope','gas_trade_month_gross_v1'],['evidence','Synthetic browser bill review']])await set(field(name),value);
 await submit(root+' form');await wait(`!${node('confirm')}.hidden`);
 await check('the reviewed bill states the managed pay and receive totals separately',
  `['Bill for 2021-01 · 4 lines','Payments we pay: -600 usd','Payments we receive: 20 usd'].every(t=>${node('review')}.textContent.includes(t))&&${node('confirm')}.textContent==='Issue reviewed bill'`);
 await click(field('confirm'));
 await wait(`document.querySelector('#physical-operations-pending').hidden&&${node('documents')}?.textContent.includes('Synthetic browser bill review')&&`+idle,60000);
 await check('the issued bill is listed with its lines and the month accepts no second bill',
  `['Bill','-600','20'].every(t=>${node('documents')}.textContent.includes(t))&&${node('lines')}.querySelectorAll('tbody tr').length===4&&[...${node('month')}.options].some(o=>o.value==='2021-01-01'&&o.disabled&&o.textContent.includes('issued'))`);
 await check('monthly bills fit the viewport','document.documentElement.scrollWidth<=innerWidth');await shot('gas-monthly-bill');
}
