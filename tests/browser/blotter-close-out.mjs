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

// Records every trading request and draft the page sends, the holdings each read returns and any request to ask the assistant. On demand
// (`window.__closeOut.next`) the next draft read fails, comes back as another instrument or for a book the catalog does not hold; the
// next priced read fails, or states another revision of a trade (`bump`); every read from now on states another revision (`amended`).
const recorder=`(()=>{const send=window.fetch;window.__closeOut={sent:[],drafts:[],holdings:[],asked:[],next:null,bump:null,amended:null,failPriced:false};window.fetch=async(input,init)=>{const path=String(typeof input==='string'?input:input.url),o=window.__closeOut;
  if(/\\/api\\/(futures|swaps)\\/(book|preview)$/.test(path))o.sent.push({path,body:JSON.parse(init.body)});
  if(path.startsWith('/api/assistant/turns'))o.asked.push(path);
  const priced=path==='/api/trading/workspace'&&JSON.parse(init.body).bookingCatalog==='priced_contracts';
  if(priced&&o.failPriced){o.failPriced=false;return new Response(JSON.stringify({code:'request_failed',message:'Synthetic priced read failure.'}),{status:500,headers:{'Content-Type':'application/json'}});}
  if(path==='/api/trading/close-out-draft'&&o.next==='fail'){o.next=null;o.drafts.push({status:500});return new Response(JSON.stringify({code:'request_failed',message:'Synthetic draft failure.'}),{status:500,headers:{'Content-Type':'application/json'}});}
  let response=await send(input,init);
  // An amendment that landed: every read of the trade's positions, their values and their facts alike, states its next revision.
  if(o.amended&&response.ok&&['/api/trading/workspace','/api/trading/positions'].includes(path)){const value=await response.clone().json();
    const amend=x=>{if(Array.isArray(x))x.forEach(amend);else if(x&&typeof x==='object'){if(x.tradeId===o.amended&&'revision' in x)x.revision=typeof x.revision==='number'?x.revision+1:String(Number(x.revision)+1);Object.values(x).forEach(amend);}};
    amend(value);response=new Response(JSON.stringify(value),{status:200,headers:{'Content-Type':'application/json'}});}
  if(path==='/api/trading/close-out-draft'){const value=await response.clone().json();
    if(response.ok&&o.next==='malformed'){value.kind=value.kind==='future'?'financial_swap':'future';response=new Response(JSON.stringify(value),{status:200,headers:{'Content-Type':'application/json'}});}
    if(response.ok&&o.next==='foreignBook'){value.terms.bookId=crypto.randomUUID();response=new Response(JSON.stringify(value),{status:200,headers:{'Content-Type':'application/json'}});}
    o.next=null;o.drafts.push({status:response.status,value});}
  if(path==='/api/trading/workspace'&&response.ok){const value=await response.clone().json();
    if(priced&&o.bump&&value.blotter){for(const h of value.blotter.holdings)if(h.tradeId===o.bump)h.revision=String(Number(h.revision)+1);o.bump=null;response=new Response(JSON.stringify(value),{status:200,headers:{'Content-Type':'application/json'}});}
    if(value.blotter)o.holdings=value.blotter.holdings;}
  return response;};})()`;

/** Opens the Blotter at the close whose instant starts with `at`, as `persona`, with the page's trading requests recorded. */
async function openBlotter({command,evaluate,wait,click,set,persona,idle,url,errors},at,who='trader'){
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  // What the page showed when it did not settle: its busy state, status line, error and page.
  const observed=async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate("({busy:document.body?.getAttribute('aria-busy'),status:document.querySelector('#status')?.textContent,error:document.querySelector('#error')?.textContent,title:document.querySelector('#page-title')?.textContent,pricing:document.querySelector('#futures-page')?.dataset.pricingState,href:location.href,reads:performance.getEntriesByType('resource').filter(e=>e.name.includes('/api/')).map(e=>e.name.split('/api/')[1]+' '+e.responseStatus+' '+Math.round(e.duration))})").catch(e=>e.message))+' · errors '+JSON.stringify(errors));};
  await command('Page.navigate',{url});await wait(idle,30000).catch(observed);await persona(who);await evaluate(recorder);
  const frame=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith(${JSON.stringify(at)})).id;})()`);
  await set('#snapshot',frame);await wait(idle,30000).catch(observed);await click('#nav-futures',30000);
  await wait(`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`,30000).catch(observed);
}
/** Shows only the row of `reference`, and gives its key. */
async function onlyRow({evaluate,wait,set},reference){
  await set('#blotter-filters .table-search',reference);
  await wait(`${visibleRows}.length===1&&${visibleRows}[0].textContent.includes(${JSON.stringify(reference)})`);
  return evaluate(`${visibleRows}[0].dataset.rowKey`);
}
const row=key=>`document.querySelector('#desk-positions .tabulator-row[data-row-key="${key}"]')`;
async function rightClick({command,evaluate,wait},key){
  await wait(`!!${row(key)}`).catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate(`({rows:${visibleRows}.map(r=>r.dataset.rowKey),pricing:document.querySelector('#futures-page').dataset.pricingState,error:document.querySelector('#error').textContent,search:document.querySelector('#blotter-filters .table-search')?.value})`)));});
  const {x,y}=await evaluate(`(()=>{const r=${row(key)}.querySelector('.desk-reference').getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()`);
  for(const type of ['mousePressed','mouseReleased'])await command('Input.dispatchMouseEvent',{type,x,y,button:'right',buttons:type==='mousePressed'?2:0,clickCount:1});
  return {x,y};
}
async function chooseItem({evaluate,wait,idle},label){
  await evaluate(`${items}.find(i=>i.firstChild.textContent===${JSON.stringify(label)}).click()`);
  await wait(`!${menu}&&${idle}`,60000);
}
const form="document.querySelector('#position-details-form')";
// A failure the page says in its banner, and the details holding no draft and no form that could be booked.
const bannerOnly=text=>`!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes(${JSON.stringify(text)})&&
  !document.querySelector('#close-out-refusal')&&!${form}.contains(document.querySelector('#futures-new-trade'))&&!${form}.contains(document.querySelector('#swap-editing'))`;
const banner="({error:document.querySelector('#error').textContent,refusal:document.querySelector('#close-out-refusal')?.textContent,mode:document.querySelector('#position-details').dataset.mode,form:document.querySelector('#position-details-form').textContent.slice(0,200)})";

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

  for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type,key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
  const closeOut=async()=>{await rightClick(page,futureKey);await wait(`!!${menu}`);await chooseItem(page,'Close out…');};
  const drafts=()=>evaluate('window.__closeOut.drafts.length');

  // Copy trade ID copies the trade reference, and says so.
  await evaluate("window.__copied=[];Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:async text=>{window.__copied.push(text);}}})");
  await rightClick(page,futureKey);await wait(`!!${menu}`);await chooseItem(page,'Copy trade ID');
  await check('Copy trade ID copies the trade reference and says so',`JSON.stringify(window.__copied)===${JSON.stringify(JSON.stringify([reference]))}&&document.querySelector('#notice').textContent.includes(${JSON.stringify(`Trade ID ${reference} copied.`)})`,
    "({copied:window.__copied,notice:document.querySelector('#notice').textContent})");
  // Ask opens the position in the details and puts a question about it in the assistant's composer, which is not sent.
  await rightClick(page,futureKey);await wait(`!!${menu}`);await chooseItem(page,'Ask the assistant about this position');
  await check('Ask shows the position in the details and drafts a question about it, sending nothing',
    `document.querySelector('#position-details').dataset.mode==='position'&&document.querySelector('#details-title').textContent.startsWith(${JSON.stringify(reference+' · ')})&&
      document.querySelector('#assistant-question').value.includes(${JSON.stringify(reference)})&&window.__closeOut.asked.length===0`,
    "({mode:document.querySelector('#position-details').dataset.mode,title:document.querySelector('#details-title').textContent,question:document.querySelector('#assistant-question').value,asked:window.__closeOut.asked})");

  // The open assistant leaves the page too narrow for the panes side by side; closed, the positions show again.
  await click('#assistant-close');await wait(`${visibleRows}.length===1&&${idle}`);

  // The priced read Close out makes for a future failing refuses the draft: none is read, and the banner says why.
  let before=await drafts();
  await evaluate('window.__closeOut.failPriced=true');await closeOut();
  await check('a priced read that fails reads no draft and says why in the banner',
    `${bannerOnly('')}&&/Synthetic priced read failure|could not be read with its priced contracts/.test(document.querySelector('#error').textContent)&&window.__closeOut.drafts.length===${before}`,banner);
  await click('#blotter-retry',30000);await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`,30000);await onlyRow(page,reference);
  // A priced read that holds another revision of the position refuses the draft of the revision chosen.
  before=await drafts();
  await evaluate(`window.__closeOut.bump=${JSON.stringify(future.tradeId)}`);await closeOut();
  await check('a priced read at another revision refuses the draft of the revision chosen, saying why',`${bannerOnly('is now at revision 2, not revision 1 that was chosen')}&&window.__closeOut.drafts.length===${before}`,banner);
  await click('#futures-refresh',30000);await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`,30000);await onlyRow(page,reference);
  // A refresh that lands while the menu is open, with another revision of the position, refuses the Close out chosen from that menu:
  // the menu was opened on the revision it showed then.
  before=await drafts();
  await rightClick(page,futureKey);await wait(`!!${menu}`);
  await evaluate(`window.__closeOut.amended=${JSON.stringify(future.tradeId)};document.querySelector('#futures-refresh').click()`);
  await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}&&window.__closeOut.holdings.find(h=>h.tradeId===${JSON.stringify(future.tradeId)})?.revision==='2'`,30000)
    .catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate(`({pricing:document.querySelector('#futures-page').dataset.pricingState,busy:document.body.getAttribute('aria-busy'),error:document.querySelector('#error').textContent,revision:window.__closeOut.holdings.find(h=>h.tradeId===${JSON.stringify(future.tradeId)})?.revision,menu:!!${menu},refresh:document.querySelector('#futures-refresh').disabled})`)));});
  await check('the menu stays open on the row it was opened for',`!!${menu}`);
  await chooseItem(page,'Close out…');
  await check('Close out from a menu opened on an earlier revision is refused, saying why, and no draft opens',`${bannerOnly('is now at revision 2, not revision 1 that was chosen')}&&window.__closeOut.drafts.length===${before}`,banner);
  await evaluate('window.__closeOut.amended=null');
  await click('#futures-refresh',30000);await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`,30000);await onlyRow(page,reference);
  // A draft read that fails, a draft of another instrument and one for a book the catalog does not hold are the page's failures: the
  // banner says them, and the details hold no draft and nothing that could be booked.
  for(const [next,text] of [['fail','Synthetic draft failure.'],['malformed','another instrument than the position it closes'],['foreignBook','The drafted book is not one you can book for.']]){
    await evaluate(`window.__closeOut.next=${JSON.stringify(next)}`);await closeOut();
    await check(`a ${next==='fail'?'failed draft read':next==='malformed'?'draft of another instrument':'draft for a book not held'} raises the banner and leaves no form`,bannerOnly(text),banner);
  }

  // Close out… opens the backend's draft in the futures form of the details: the opposite side, the lots and the curve price, labelled.
  await rightClick(page,futureKey);await wait(`!!${menu}`);
  await chooseItem(page,'Close out…');
  const drafted=await evaluate('window.__closeOut.drafts.at(-1)');
  await check('the draft read answers the future’s close-out',`${JSON.stringify(drafted?.status)}===200&&${JSON.stringify(drafted?.value?.terms?.lots)}==='-40'`,'window.__closeOut.drafts');
  await wait(`${form}.contains(document.querySelector('#futures-new-trade'))&&!${form}.hidden&&!document.querySelector('#futures-new-trade').hidden&&${idle}`);
  const shownClose="document.querySelector('#snapshot').selectedOptions[0].textContent.split(' · ')[0]";
  await check('the details show the futures form with the drafted side, lots and price, labelled with the close it is the curve price at',
    `document.querySelector('#position-details').dataset.mode==='new'&&document.querySelector('#futures-lots').value==='-40'&&
      document.querySelector('#futures-execution').value===${JSON.stringify(drafted.value.price.value)}&&document.querySelector('#futures-execution').value===${JSON.stringify(drafted.value.terms.executionPrice)}&&
      document.querySelector('#futures-contract').value===${JSON.stringify(JSON.stringify([future.curve,future.contractId]))}&&document.querySelector('#futures-book').value===${JSON.stringify(future.bookId)}&&
      document.querySelector('#futures-close-out').textContent.includes('Close-out of '+${JSON.stringify(reference)})&&
      !document.querySelector('#futures-price-source').hidden&&document.querySelector('#futures-price-source').textContent==='Curve price at '+${shownClose}+' close'`,
    `({lots:document.querySelector('#futures-lots').value,price:document.querySelector('#futures-execution').value,contract:document.querySelector('#futures-contract').value,head:document.querySelector('#futures-close-out').textContent,label:document.querySelector('#futures-price-source').textContent,close:${shownClose}})`);
  await check('nothing is sent to book while the draft is reviewed',`window.__closeOut.sent.length===0`,'window.__closeOut.sent');
  // Editing the price takes its source label away: the price is then the trader's.
  await evaluate("(()=>{const n=document.querySelector('#futures-execution');n.focus();n.select();})()");await command('Input.insertText',{text:'2.5'});
  await check('editing the price removes its curve-price label',"document.querySelector('#futures-price-source').hidden&&document.querySelector('#futures-execution').value==='2.5'");
  // Once the price and lots are the trader's, nothing on the form states the drafted ones as the trade.
  await evaluate("(()=>{const n=document.querySelector('#futures-lots');n.focus();n.select();})()");await command('Input.insertText',{text:'-30'});
  await check('after the price and lots are edited, the form states neither the drafted price nor the drafted lots',
    `(()=>{const said=document.querySelector('#futures-close-out').textContent+(document.querySelector('#futures-price-source').hidden?'':document.querySelector('#futures-price-source').textContent);
      return !said.includes(${JSON.stringify(drafted.value.price.value)})&&!/\b40\b/.test(said)&&!/curve price/i.test(said);})()`,"document.querySelector('#futures-close-out').textContent");
  await check('and still nothing is sent to book',`window.__closeOut.sent.length===0`,'window.__closeOut.sent');
  await evaluate("document.querySelector('#futures-book-submit').click()");
  await wait(`window.__closeOut.sent.length>0&&${idle}`,60000);
  await check('Book sends the reviewed close-out once, as the trader left it',
    `(()=>{const sent=window.__closeOut.sent;return sent.length===1&&sent[0].path==='/api/futures/book'&&sent[0].body.lots==='-30'&&sent[0].body.executionPrice==='2.5'&&sent[0].body.contractId===${JSON.stringify(future.contractId)}&&sent[0].body.bookId===${JSON.stringify(future.bookId)};})()`,
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
  // Previewed, confirmed and booked, the swap's request carries the drafted terms the trader left: its spread, payment rules and parties.
  await page.submit('#swap-form',60000);await wait(`!document.querySelector('#swap-confirmation').hidden&&${idle}`,60000)
    .catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate("({error:document.querySelector('#swap-error').textContent,banner:document.querySelector('#error').textContent})")));});
  await evaluate("(()=>{const n=document.querySelector('#swap-confirm');n.checked=true;n.dispatchEvent(new Event('change',{bubbles:true}));})()");
  await click('#swap-submit',60000);await wait(`window.__closeOut.sent.some(s=>s.path==='/api/swaps/book')&&${idle}`,60000);
  const t=oneDraft.value.terms;
  await check('Book sends the swap close-out with the drafted spread, payment rules, parties and side, at the trader’s price',
    `(()=>{const b=window.__closeOut.sent.find(s=>s.path==='/api/swaps/book').body,t=${JSON.stringify(t)};
      return b.floating.spread===t.floating.spread&&JSON.stringify(b.floating.paymentSchedule)===JSON.stringify(t.floating.paymentSchedule)&&JSON.stringify(b.fixed.paymentSchedule)===JSON.stringify(t.fixed.paymentSchedule)&&
        b.legalEntityId===t.legalEntityId&&b.legalEntityRevision===t.legalEntityRevision&&b.counterpartyId===t.counterpartyId&&b.counterpartyRevision===t.counterpartyRevision&&
        b.fixedCoefficient===t.fixedCoefficient&&b.bookId===t.bookId&&b.fixed.price==='3.3'&&b.floating.index===t.floating.index&&b.floating.curve===t.floating.curve;})()`,
    "window.__closeOut.sent.find(s=>s.path==='/api/swaps/book')?.body");
  if(await evaluate("document.querySelector('#position-details').dataset.mode==='new'"))await closeNewTrade(page);

  // A swap with two of its four periods settled at the February close drafts only its two open periods, each exact. The Blotter is
  // scoped to the first book, which holds it, and then read at that close.
  await set('#blotter-filters .table-search','');
  await set('#blotter-books',fixture.positions.settled.bookId);await click('#blotter-scope-apply',30000);
  await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`,30000);
  const february=await evaluate(`(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith(${JSON.stringify(fixture.frames.february.at.slice(0,19))})).id;})()`);
  await set('#snapshot',february);
  await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`,30000)
    .catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate("({pricing:document.querySelector('#futures-page').dataset.pricingState,error:document.querySelector('#error').textContent,books:[...document.querySelector('#blotter-books').selectedOptions].map(o=>o.value),snapshot:document.querySelector('#snapshot').value,dialogs:[...document.querySelectorAll('dialog[open],[role=alertdialog]:not([hidden])')].map(d=>d.id+' '+d.textContent.slice(0,200)),status:document.querySelector('#status').textContent})")));});
  const settled=fixture.positions.settled;
  const sentBefore=await evaluate('window.__closeOut.sent.length');
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
      document.querySelector('#scheduled-close-out').textContent.includes('not at par')&&window.__closeOut.sent.length===${sentBefore}`,
    `({prices:[...document.querySelectorAll('#scheduled-swap-editor [data-role=fixed-price]')].map(n=>n.value),label:document.querySelector('#scheduled-price-source')?.textContent,sent:window.__closeOut.sent})`);
  await evaluate("(()=>{const n=document.querySelector('#scheduled-swap-editor [data-role=fixed-price]');n.focus();n.select();})()");await command('Input.insertText',{text:'3.25'});
  await check('editing a fixed price removes the label, and nothing is sent',`document.querySelector('#scheduled-price-source').hidden&&window.__closeOut.sent.length===${sentBefore}`);
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
    await check(`a ${type} close-out shows the backend’s refusal in the details, no form and no banner repeating it`,
      `(()=>{const said=document.querySelector('#position-details-view #close-out-refusal');return !!said&&!document.querySelector('#position-details').hidden&&said.textContent===${JSON.stringify(`Close-out of ${reference} was not drafted. ${why}`)}&&document.querySelector('#position-details-form').hidden&&window.__closeOut.drafts.at(-1).status===422&&document.querySelector('#error').hidden;})()`,
      "({view:document.querySelector('#position-details-view')?.textContent,drafts:window.__closeOut.drafts,banner:document.querySelector('#error').hidden?'':document.querySelector('#error').textContent})");
  }
}
