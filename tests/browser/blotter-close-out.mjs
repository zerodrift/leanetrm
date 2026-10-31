import {closeNewTrade} from './new-trade.mjs';
// A Blotter row's menu (blotter-workbench.md, Row context menu) opens on a right-click and from the keyboard, and Close out… opens the
// backend's draft of the offsetting trade in the details' booking form, at the curve price of the close on screen, labelled with its
// source until the price is edited. Nothing books until Book is pressed. A partly settled swap's draft holds only its open periods; a PPA's
// and a heat rate's refusals are the backend's words, shown in the details.
const menu="document.querySelector('.blotter-row-menu:not([hidden])')";
const items=`[...(${menu}?.querySelectorAll('[role=menuitem]')??[])]`;
const labels=`${items}.map(i=>i.firstChild.textContent)`;
const order=['Close out…','Amend…','Open in its own window','Copy trade ID','Ask the assistant about this position'];
const visibleRows="[...document.querySelectorAll('#desk-positions .tabulator-row[data-row-key]')].filter(r=>r.getClientRects().length)";

// Records every trading request and draft the page sends, and the holdings each read returns, without changing them.
const recorder=`(()=>{const send=window.fetch;window.__closeOut={sent:[],drafts:[],holdings:[]};window.fetch=async(input,init)=>{const path=String(typeof input==='string'?input:input.url);
  if(/\\/api\\/(futures|swaps)\\/(book|preview)$/.test(path))window.__closeOut.sent.push({path,body:JSON.parse(init.body)});
  const response=await send(input,init);
  if(path==='/api/trading/close-out-draft')window.__closeOut.drafts.push({status:response.status,value:await response.clone().json()});
  if(path==='/api/trading/workspace'&&response.ok){const value=await response.clone().json();if(value.blotter)window.__closeOut.holdings=value.blotter.holdings;}
  return response;};})()`;

/** Opens the Blotter at the close whose instant starts with `at`, as `persona`, with the page's trading requests recorded. */
async function openBlotter({command,evaluate,wait,click,set,persona,idle,url,errors},at,who='trader',read='complete'){
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  // What the page showed when it did not settle: its busy state, status line, error and page.
  const observed=async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate("({busy:document.body?.getAttribute('aria-busy'),status:document.querySelector('#status')?.textContent,error:document.querySelector('#error')?.textContent,title:document.querySelector('#page-title')?.textContent,pricing:document.querySelector('#futures-page')?.dataset.pricingState,href:location.href,reads:performance.getEntriesByType('resource').filter(e=>e.name.includes('/api/')).map(e=>e.name.split('/api/')[1]+' '+e.responseStatus+' '+Math.round(e.duration))})").catch(e=>e.message))+' · errors '+JSON.stringify(errors));};
  await command('Page.navigate',{url});await wait(idle,30000).catch(observed);await persona(who);await evaluate(recorder);
  const frame=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith(${JSON.stringify(at)})).id;})()`);
  await set('#snapshot',frame);await wait(idle,30000).catch(observed);await click('#nav-futures',30000);
  await wait(read==='complete'?`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`
    :`document.querySelector('#futures-page').dataset.pricingState===${JSON.stringify(read)}&&${idle}`,30000).catch(observed);
}
/** Shows only the row of `reference`, and gives its key. */
async function onlyRow({evaluate,wait,set},reference){
  await set('#blotter-filters .table-search',reference);
  await wait(`${visibleRows}.length===1&&${visibleRows}[0].textContent.includes(${JSON.stringify(reference)})`);
  return evaluate(`${visibleRows}[0].dataset.rowKey`);
}
const row=key=>`document.querySelector('#desk-positions .tabulator-row[data-row-key="${key}"]')`;
async function rightClick({command,evaluate},key){
  const {x,y}=await evaluate(`(()=>{const r=${row(key)}.querySelector('.desk-reference').getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()`);
  for(const type of ['mousePressed','mouseReleased'])await command('Input.dispatchMouseEvent',{type,x,y,button:'right',buttons:type==='mousePressed'?2:0,clickCount:1});
  return {x,y};
}
async function chooseItem({evaluate,wait,idle},label){
  await evaluate(`${items}.find(i=>i.firstChild.textContent===${JSON.stringify(label)}).click()`);
  await wait(`!${menu}&&${idle}`,60000);
}
const trading=(h,reference)=>h.find(x=>x.tradeReference===reference);

export async function run(page){
  const {command,evaluate,wait,click,set,check,idle,fixture}=page;
  await openBlotter(page,fixture.frames.booking.at.slice(0,19));
  const future=fixture.positions.future,futureKey=`${future.tradeId}:${future.bindingId}`;
  const reference=await evaluate(`window.__closeOut.holdings.find(h=>h.tradeId===${JSON.stringify(future.tradeId)}).tradeReference`);
  await check('the future is a row of the Blotter',`${JSON.stringify(await onlyRow(page,reference))}===${JSON.stringify(futureKey)}`);

  // A right-click opens the row's menu at the pointer, its first item focused; Escape closes it and gives the row focus.
  const at=await rightClick(page,futureKey);
  await wait(`!!${menu}`);
  await check('a right-click on a row opens its menu at the pointer, with the trader’s actions in order and no hedge',
    `JSON.stringify(${labels})===${JSON.stringify(JSON.stringify(order))}&&!${items}.some(i=>/hedge/i.test(i.textContent))&&${items}.every(i=>!i.hasAttribute('aria-disabled'))&&
      document.activeElement===${items}[0]&&Math.abs(${menu}.getBoundingClientRect().left-${at.x})<=1&&${menu}.getAttribute('aria-label')===${JSON.stringify('Position '+reference)}`,
    `({labels:${labels},disabled:${items}.map(i=>i.getAttribute('aria-disabled')),box:${menu}?.getBoundingClientRect(),focus:document.activeElement?.textContent})`);
  for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type,key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
  await wait(`!${menu}&&document.activeElement===${row(futureKey)}`).catch(()=>undefined);
  await check('Escape closes the menu and gives the row focus',`!${menu}&&document.activeElement===${row(futureKey)}`,'[document.activeElement?.className]');
  // Shift+F10 on the focused row opens the same menu beneath it.
  for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type:type==='keyDown'?'rawKeyDown':type,key:'F10',code:'F10',windowsVirtualKeyCode:121,modifiers:8});
  await wait(`!!${menu}`);
  await check('Shift+F10 on the focused row opens its menu beneath the row',
    `JSON.stringify(${labels})===${JSON.stringify(JSON.stringify(order))}&&${menu}.getBoundingClientRect().top>=${row(futureKey)}.getBoundingClientRect().bottom`);

  // Close out… opens the backend's draft in the futures form of the details: the opposite side, the lots and the curve price, labelled.
  await chooseItem(page,'Close out…');
  const drafted=await evaluate('window.__closeOut.drafts.at(-1)');
  await check('the draft read answers the future’s close-out',`${JSON.stringify(drafted?.status)}===200&&${JSON.stringify(drafted?.value?.terms?.lots)}==='-40'`,'window.__closeOut.drafts');
  const form="document.querySelector('#position-details-form')";
  await wait(`${form}.contains(document.querySelector('#futures-new-trade'))&&!${form}.hidden&&!document.querySelector('#futures-new-trade').hidden&&${idle}`);
  const shownClose="document.querySelector('#snapshot').selectedOptions[0].textContent.split(' · ')[0]";
  await check('the details show the futures form with the drafted side, lots and price, labelled with the close it is the curve price at',
    `document.querySelector('#position-details').dataset.mode==='new'&&document.querySelector('#futures-lots').value==='-40'&&
      document.querySelector('#futures-execution').value===${JSON.stringify(drafted.value.price.value)}&&document.querySelector('#futures-execution').value===${JSON.stringify(drafted.value.terms.executionPrice)}&&
      document.querySelector('#futures-contract').value===${JSON.stringify(JSON.stringify([future.curve,future.contractId]))}&&document.querySelector('#futures-book').value===${JSON.stringify(future.bookId)}&&
      document.querySelector('#futures-close-out').textContent.includes('Close-out of '+${JSON.stringify(reference)})&&document.querySelector('#futures-close-out').textContent.includes('Sell 40 lots')&&
      !document.querySelector('#futures-price-source').hidden&&document.querySelector('#futures-price-source').textContent==='Curve price at '+${shownClose}+' close'`,
    `({lots:document.querySelector('#futures-lots').value,price:document.querySelector('#futures-execution').value,contract:document.querySelector('#futures-contract').value,head:document.querySelector('#futures-close-out').textContent,label:document.querySelector('#futures-price-source').textContent,close:${shownClose}})`);
  await check('nothing is sent to book while the draft is reviewed',`window.__closeOut.sent.length===0`,'window.__closeOut.sent');
  // Editing the price takes its source label away: the price is then the trader's.
  await evaluate("(()=>{const n=document.querySelector('#futures-execution');n.focus();n.select();})()");await command('Input.insertText',{text:'2.5'});
  await check('editing the price removes its curve-price label',"document.querySelector('#futures-price-source').hidden&&document.querySelector('#futures-execution').value==='2.5'");
  await check('and still nothing is sent to book',`window.__closeOut.sent.length===0`,'window.__closeOut.sent');
  await evaluate("document.querySelector('#futures-book-submit').click()");
  await wait(`window.__closeOut.sent.length>0&&${idle}`,60000);
  await check('Book sends the reviewed close-out once, as the trader left it',
    `(()=>{const sent=window.__closeOut.sent;return sent.length===1&&sent[0].path==='/api/futures/book'&&sent[0].body.lots==='-40'&&sent[0].body.executionPrice==='2.5'&&sent[0].body.contractId===${JSON.stringify(future.contractId)}&&sent[0].body.bookId===${JSON.stringify(future.bookId)};})()`,
    'window.__closeOut.sent');

  // A one-period swap opens in the swap ticket: the receive-fixed side for the pay-fixed one, its quantity, and the curve price as its
  // fixed price, labelled, with what the form says of par.
  const swap=fixture.positions.swap;
  const oneReference=await evaluate(`window.__closeOut.holdings.find(h=>h.tradeId===${JSON.stringify(swap.tradeId)}).tradeReference`);
  await rightClick(page,await onlyRow(page,oneReference));await wait(`!!${menu}`);
  await chooseItem(page,'Close out…');
  const oneDraft=await evaluate('window.__closeOut.drafts.at(-1)');
  await wait(`${form}.contains(document.querySelector('#swap-editing'))&&!document.querySelector('#swap-editing').hidden&&${idle}`,60000)
    .catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate(`({draft:window.__closeOut.drafts.at(-1),view:document.querySelector('#position-details-view').textContent,error:document.querySelector('#error').textContent,swap:document.querySelector('#swap-error').textContent})`)));});
  await check('a one-period swap’s close-out opens the swap ticket on the drafted side, quantity and curve price, labelled, saying it is not par',
    `${JSON.stringify(oneDraft.status)}===200&&document.querySelector('#swap-direction').value==='1'&&document.querySelector('#swap-quantity').value===${JSON.stringify(swap.quantity)}&&
      document.querySelector('#swap-price').value===${JSON.stringify(swap.mark)}&&document.querySelector('#swap-price-mode').value==='agreed'&&document.querySelector('#swap-book').value===${JSON.stringify(swap.bookId)}&&
      !document.querySelector('#swap-fixed-price-source').hidden&&document.querySelector('#swap-fixed-price-source').textContent==='Curve price at '+${shownClose}+' close'&&
      document.querySelector('#swap-close-out').textContent.includes('Close-out of '+${JSON.stringify(oneReference)})&&document.querySelector('#swap-close-out').textContent.includes('not at par')&&window.__closeOut.sent.length===1`,
    `({direction:document.querySelector('#swap-direction').value,quantity:document.querySelector('#swap-quantity').value,price:document.querySelector('#swap-price').value,label:document.querySelector('#swap-fixed-price-source').textContent,head:document.querySelector('#swap-close-out').textContent,sent:window.__closeOut.sent.length,draft:window.__closeOut.drafts.at(-1)})`);
  await evaluate("(()=>{const n=document.querySelector('#swap-price');n.focus();n.select();})()");await command('Input.insertText',{text:'3.3'});
  await check('editing the swap’s fixed price removes its label',"document.querySelector('#swap-fixed-price-source').hidden");
  // The edited draft is given up before the Blotter is opened again, which the page would otherwise ask about.
  await closeNewTrade(page);

  // A swap with two of its four periods settled at the February close drafts only its two open periods, each exact. That close values
  // only the first book's positions, the second's daily swap having no definition there, so the Blotter is scoped to the first.
  await openBlotter(page,fixture.frames.february.at.slice(0,19),'trader','failed');
  await set('#blotter-books',fixture.positions.settled.bookId);await click('#blotter-scope-apply',30000);
  await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`,30000)
    .catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate("({pricing:document.querySelector('#futures-page').dataset.pricingState,error:document.querySelector('#error').textContent,books:[...document.querySelector('#blotter-books').selectedOptions].map(o=>o.value),snapshot:document.querySelector('#snapshot').value,dialogs:[...document.querySelectorAll('dialog[open],[role=alertdialog]:not([hidden])')].map(d=>d.id+' '+d.textContent.slice(0,200)),status:document.querySelector('#status').textContent})")));});
  const settled=fixture.positions.settled;
  const swapReference=await evaluate(`window.__closeOut.holdings.find(h=>h.tradeId===${JSON.stringify(settled.tradeId)}).tradeReference`);
  const swapKey=await onlyRow(page,swapReference);
  await rightClick(page,swapKey);await wait(`!!${menu}`);
  await chooseItem(page,'Close out…');
  const swapDraft=await evaluate('window.__closeOut.drafts.at(-1)');
  await wait(`${form}.contains(document.querySelector('#scheduled-swap-editor'))&&!document.querySelector('#scheduled-swap-editor').hidden&&${idle}`,60000)
    .catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate(`({draft:window.__closeOut.drafts.at(-1),view:document.querySelector('#position-details-view').textContent,error:document.querySelector('#error').textContent,form:${form}.textContent.slice(0,400)})`)));});
  const legends="[...document.querySelectorAll('#scheduled-swap-editor .scheduled-period > legend')].map(l=>l.textContent)";
  const listed="[...document.querySelectorAll('#scheduled-close-out .close-out-periods tbody tr')].map(r=>[...r.cells].map(c=>c.textContent))";
  await check('a partly settled swap’s form lists only its open periods, the settled ones left untouched',
    `${JSON.stringify(swapDraft.status)}===200&&JSON.stringify(${legends})===${JSON.stringify(JSON.stringify(['2021-03-01 to 2021-04-01 · exclusive end','2021-04-01 to 2021-05-01 · exclusive end','2021-03-01 to 2021-04-01 · exclusive end','2021-04-01 to 2021-05-01 · exclusive end']))}&&
      JSON.stringify(${listed})===${JSON.stringify(JSON.stringify((swapDraft.value?.periods??[]).map(p=>[p.from,p.to,p.quantity,p.price])))}&&${listed}.length===2&&
      document.querySelector('#scheduled-close-out').textContent.includes('2 settled periods are left untouched')`,
    `({legends:${legends},listed:${listed},draft:window.__closeOut.drafts.at(-1)})`);
  await check('its fixed prices are the drafted curve prices, labelled, and the form says they are not par',
    `JSON.stringify([...document.querySelectorAll('#scheduled-swap-editor [data-role=fixed-price]')].map(n=>n.value))===${JSON.stringify(JSON.stringify((swapDraft.value?.periods??[]).map(p=>p.price)))}&&
      !document.querySelector('#scheduled-price-source').hidden&&document.querySelector('#scheduled-price-source').textContent==='Curve price at '+${shownClose}+' close'&&
      document.querySelector('#scheduled-close-out').textContent.includes('not at par')&&window.__closeOut.sent.length===0`,
    `({prices:[...document.querySelectorAll('#scheduled-swap-editor [data-role=fixed-price]')].map(n=>n.value),label:document.querySelector('#scheduled-price-source')?.textContent,sent:window.__closeOut.sent})`);
  await evaluate("(()=>{const n=document.querySelector('#scheduled-swap-editor [data-role=fixed-price]');n.focus();n.select();})()");await command('Input.insertText',{text:'3.25'});
  await check('editing a fixed price removes the label, and nothing is sent',"document.querySelector('#scheduled-price-source').hidden&&window.__closeOut.sent.length===0");
}

// In the mixed demo instruments, Close out stays offered on a PPA and a heat rate: what the backend cannot draft, it says in the details.
export async function refusals(page){
  const {evaluate,wait,check,idle}=page;
  await openBlotter(page,'2020-12-18T16:00:00');
  for(const [type,why] of [['ppa','Close-out drafts are not available for ppa yet.'],['heat_rate','Close-out drafts are not available for heat_rate yet.']]){
    const reference=await evaluate(`window.__closeOut.holdings.find(h=>h.instrumentType===${JSON.stringify(type)}).tradeReference`);
    const key=await onlyRow(page,reference);
    await rightClick(page,key);await wait(`!!${menu}`);
    await check(`a ${type} row keeps Close out offered`,`!${items}[0].hasAttribute('aria-disabled')`,labels);
    if(type==='ppa')await check('and has Amend disabled with the reason it is amended in PPA bookings',
      `${items}[1].getAttribute('aria-disabled')==='true'&&${items}[1].querySelector('.menu-reason').textContent==='PPA amendments and terminations are made in PPA bookings.'`,`${items}.map(i=>i.textContent)`);
    await chooseItem(page,'Close out…');
    await check(`a ${type} close-out shows the backend’s refusal in the details, and no form`,
      `(()=>{const said=document.querySelector('#position-details-view #close-out-refusal');return !!said&&!document.querySelector('#position-details').hidden&&said.textContent===${JSON.stringify(`Close-out of ${reference} was not drafted. ${why}`)}&&document.querySelector('#position-details-form').hidden&&window.__closeOut.drafts.at(-1).status===422;})()`,
      "({view:document.querySelector('#position-details-view')?.textContent,drafts:window.__closeOut.drafts})");
  }
}
