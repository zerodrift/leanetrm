// Amend an existing synthetic gas trade through the ordinary Blotter controls.
import {blotterAt,decemberClose,gasTicket,q} from './gas-ticket.mjs';

export async function run(actions){
 const {command,evaluate,wait,click,set,check,persona,idle,url,gasAmendment:fixture}=actions;
 const ticket='#gas-ticket .deal-ticket',helpers=gasTicket(actions,ticket);
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await persona('trader');
 await blotterAt(actions,await decemberClose(evaluate));
 await set('#blotter-filters .table-search',fixture.tradeReference);
 const row=`#desk-positions [data-row-key="${fixture.tradeId}:${fixture.instrumentId}"] .desk-reference`;
 await wait(`!!document.querySelector(${q(row)})`,30000);await click(row);
 await wait("document.querySelector('#side-panel-amend')?.textContent==='Amend'&&!document.querySelector('#side-panel-amend').hidden",30000);
 await click('#position-details-tabs-schedules');await wait("!!document.querySelector('#position-details .deal-ticket[data-mode=booked] .ticket-action')",30000);
 await evaluate(`window.__amendAgain=document.querySelector('#position-details .deal-ticket[data-mode=booked] .ticket-action').onclick;
  {const send=window.fetch;window.__gasAmends=[];window.fetch=async(input,init)=>{
   const response=await send(input,init);if(String(input)==='/api/tickets/amend'){
    window.__gasAmends.push({body:JSON.parse(init.body),receipt:await response.clone().json()});
    if(window.__loseGasAmend){window.__loseGasAmend=false;return new Response('incomplete',{status:502});}
   }return response;};}`);
 await click('#side-panel-amend');await wait(`!!document.querySelector(${q(ticket+'[data-mode=amend]')})`,30000);
 await check('Blotter Amend opens retained gas terms in the common ticket',`document.querySelector(${q(helpers.control('payment','cash_price'))}).value==='3.2'&&document.querySelector(${q(ticket+' .ticket-book')}).textContent==='Save amendment'`);
 await set(helpers.control('payment','cash_price'),'3.25');
 await set('#gas-ticket [data-role=amendment-reason]','Synthetic browser correction');
 await helpers.until(helpers.current());
 await evaluate('window.__amendAgain()');
 await wait("!![...document.querySelectorAll('#position-details button')].find(b=>b.textContent==='Keep editing')");
 await evaluate("[...document.querySelectorAll('#position-details button')].find(b=>b.textContent==='Keep editing').click()");
 await check('Keep editing preserves the amendment terms and reason',`!!document.querySelector(${q(ticket+'[data-mode=amend]')})&&document.querySelector(${q(helpers.control('payment','cash_price'))}).value==='3.25'&&document.querySelector('#gas-ticket [data-role=amendment-reason]').value==='Synthetic browser correction'`);
 await evaluate('window.__loseGasAmend=true');await click(ticket+' .ticket-book');
 const recovery="[...document.querySelectorAll('#gas-ticket .ticket-booking button')].find(b=>b.textContent==='Check amendment status')";
 await wait(`!!${recovery}&&!${recovery}.disabled&&document.querySelector('#gas-ticket .ticket-booking').textContent.includes('not confirmed')`,60000);
 await check('a lost amendment reply retains its exact request',"window.__gasAmends.length===1&&document.querySelector('#gas-ticket .ticket-booking').textContent.includes('not confirmed')","({sent:window.__gasAmends,status:document.querySelector('#gas-ticket .ticket-booking').textContent})");
 await evaluate(`${recovery}.click()`);
 await wait("document.querySelector('#position-details-head .side-panel-meta')?.textContent.startsWith('Revision 4')&&"+idle,30000);
 await check('Check status follows the same trade to its new revision without a second write',`window.__gasAmends.length===1&&window.__gasAmends[0].receipt.tradeId===${q(fixture.tradeId)}&&window.__gasAmends[0].receipt.revision===4`);
 await click('#position-details-tabs-schedules');await wait("!!document.querySelector('#position-details .deal-ticket[data-mode=booked]')",30000);
 await check('the amended physical trade reopens with its new payment price',"document.querySelector('#position-details .ticket-card[data-side=payment] [data-field=cash_price]').textContent.includes('3.25')");
}
