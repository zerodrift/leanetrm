// New trade's page (D421): a sidebar entry above the Blotter that shows the Blotter's entry pane alone, at the page's width, on its
// instrument chooser. Leaving a ticket asks first, the Blotter's own button and the chooser's link open what they name, and the page is
// never left showing nothing.
import {chooseProduct} from './new-trade.mjs';

export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
  const width=1440,height=1050;
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);await persona('trader');
  await wait("document.querySelector('#page-title').textContent==='Blotter'&&document.querySelector('#futures-page').dataset.pricingState==='ready'&&"+idle);
  const shown="(selector=>{const node=document.querySelector(selector);return !!node&&node.checkVisibility();})";
  const title="document.querySelector('#page-title').textContent";
  const entry="document.querySelector('#futures-page').hasAttribute('data-entry')";
  const state=`({title:${title},entry:${entry},mode:document.querySelector('#position-details').dataset.mode,error:document.querySelector('#error').textContent})`;

  await check('New trade is listed above the Blotter in the sidebar',
    "(()=>{const entries=[...document.querySelectorAll('#nav-group-trading .nav-item:not(.nav-view)')].filter(n=>!n.hidden).map(n=>n.id);return entries.indexOf('nav-new-trade')===0&&entries.indexOf('nav-futures')===1;})()");
  await click('#nav-new-trade');await wait(`${title}==='New trade'&&`+idle);
  await check('the sidebar entry opens the page on the chooser, with no instrument chosen and none of the Blotter around it',
    `${entry}&&document.querySelector('#new-trade-product').dataset.state==='choose'&&!document.querySelector('#new-trade-product [aria-pressed=true]')&&${shown}('#new-trade-product')&&!${shown}('#desk-positions')&&!${shown}('#futures-new')&&!${shown}('#details-collapse')&&!${shown}('#details-empty')`,state);
  await check('the sidebar marks New trade and not the Blotter',"document.querySelector('#nav-new-trade').classList.contains('active')&&!document.querySelector('#nav-futures').classList.contains('active')");
  await check('the page fills the width the Blotter has',
    "(()=>{const pane=document.querySelector('#position-details').getBoundingClientRect(),main=document.querySelector('main').getBoundingClientRect();return pane.width>main.width*.9&&document.documentElement.scrollWidth<=document.documentElement.clientWidth+1;})()");

  await chooseProduct({click,wait},'swaps');
  await check('choosing an instrument names the page for it and puts its form in the page',`${title}==='New swap'&&${entry}&&document.querySelector('#new-trade-product').dataset.state==='chosen'&&document.querySelector('#new-trade-swaps').getAttribute('aria-pressed')==='true'`,state);
  // An edit is the person's own input: a key typed into the first text field of the form.
  await evaluate("(()=>{const field=[...document.querySelectorAll('#position-details-form input:enabled,#position-details-form textarea:enabled')].find(n=>!['hidden','date','checkbox','radio','button','submit'].includes(n.type)&&n.checkVisibility());field.scrollIntoView({block:'center'});field.focus();window.__entryEdited=field;})()");
  await command('Input.insertText',{text:'7'});
  await click('#nav-futures');await wait("!document.querySelector('#position-details-prompt').hidden");
  await evaluate("document.querySelector('#position-details-prompt').querySelectorAll('button')[0].click()");await wait(idle);
  await check('leaving a ticket with entries asks first, and keeping them keeps the page, its title and the entries',
    `${title}==='New swap'&&${entry}&&window.__entryEdited.isConnected&&window.__entryEdited.value.includes('7')&&document.querySelector('#nav-new-trade').classList.contains('active')`,state);
  await click('#nav-futures');await wait("!document.querySelector('#position-details-prompt').hidden");
  await evaluate("document.querySelector('#position-details-prompt').querySelectorAll('button')[1].click()");
  await wait(`${title}==='Blotter'&&`+idle);
  await check('giving the entries up shows the Blotter again, with its positions and nothing held in the details',
    `!${entry}&&${shown}('#desk-positions')&&${shown}('#futures-new')&&document.querySelector('#position-details').hidden&&document.querySelector('#nav-futures').classList.contains('active')&&!document.querySelector('#nav-new-trade').classList.contains('active')`,state);

  await click('#futures-new');await wait(`${title}==='New trade'&&`+idle);
  await check('the Blotter\'s New trade button opens the same page',`${entry}&&document.querySelector('#new-trade-product').dataset.state==='choose'`,state);
  // Future reads the whole Blotter for its priced contracts: the page says so while it does, also from a ticket that was open.
  await chooseProduct({click,wait},'swaps');
  await evaluate(`(()=>{const send=window.fetch;window.__entryFetch=send;window.fetch=async(input,init)=>{if(String(input)==='/api/trading/workspace'&&!window.__entryHeld){window.__entryHeld=true;await new Promise(resolve=>{window.__entryRelease=resolve;});}return send(input,init);};})()`);
  await click('#new-trade-futures');await wait('window.__entryHeld===true');
  await check('while a new future\'s contracts are read the page says so, and offers no other choice',
    `${entry}&&${shown}('#position-details-view [role=status]')&&document.querySelector('#position-details-view [role=status]').textContent==='Loading the futures contracts…'&&[...document.querySelectorAll('#new-trade-product button')].every(b=>b.disabled)&&!${shown}('#new-trade-availability')`,
    "document.querySelector('#position-details-view').textContent");
  await evaluate("window.__entryRelease();window.fetch=window.__entryFetch");
  await wait(`${title}==='New future'&&document.querySelector('#position-details-form').contains(document.querySelector('#futures-new-trade'))&&!document.querySelector('#futures-new-trade').hidden&&`+idle);
  await check('the future\'s form then opens in the page',`${entry}&&document.querySelector('#new-trade-futures').getAttribute('aria-pressed')==='true'`,state);

  // A read of the Blotter that fails takes the booking directory with it: the page reads it again, so the chooser is back with the failure said.
  await click('#new-trade-swaps');await wait(`${title}==='New swap'&&`+idle);
  await evaluate(`(()=>{const send=window.fetch;window.fetch=async(input,init)=>{if(String(input)==='/api/trading/workspace'){window.fetch=send;return new Response(JSON.stringify({error:'Synthetic failure of the Blotter read.'}),{status:500,headers:{'content-type':'application/json'}});}return send(input,init);};})()`);
  await click('#new-trade-futures');await wait(`${title}==='New trade'&&!document.querySelector('#error').hidden&&`+idle);
  await check('a failed read of the contracts brings the chooser back with the failure said',
    `${entry}&&${shown}('#new-trade-product')&&document.querySelector('#new-trade-product').dataset.state==='choose'&&!${shown}('#new-trade-availability')&&document.querySelector('#error').textContent.length>0`,state);
  // Another As of date: the page keeps showing the chooser, or says why booking is unavailable.
  const other=await evaluate("(()=>{const s=document.querySelector('#snapshot');return [...s.options].map(o=>o.value).find(v=>v&&v!==s.value)??null;})()");
  if(other){
    await set('#snapshot',other);await wait(idle);
    await check('another As of date leaves the page showing the chooser or why booking is unavailable, never nothing',
      `${entry}&&(${shown}('#new-trade-product')||${shown}('#new-trade-availability'))&&!${shown}('#details-empty')&&!${shown}('#desk-positions')`,state);
  }
  await command('Emulation.setDeviceMetricsOverride',{width:700,height,deviceScaleFactor:1,mobile:false});await wait(idle);
  await check('a narrow window still shows the chooser alone',`${entry}&&(${shown}('#new-trade-product')||${shown}('#new-trade-availability'))&&!${shown}('#desk-positions')&&document.documentElement.scrollWidth<=document.documentElement.clientWidth+1`,state);
  await command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});await wait(idle);

  if(await evaluate("!!document.querySelector('#new-trade-operating-right')")){
    await click('#new-trade-operating-right');await wait(`${title}==='Operating facilities'&&`+idle);
    await check('the operating right goes to Operating facilities and chooses nothing there',"!document.querySelector('#physical-power-page').hidden&&document.querySelector('#futures-page').hidden");
  }
}
