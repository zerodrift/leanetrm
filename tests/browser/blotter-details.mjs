// Opening a position shows it in the docked Details pane below the grid, never inside the grid's rows: the trader keeps their place. The
// keyboard moves the selection and the details follow it; the last chosen tab is kept from position to position; unsaved edits are asked
// about before the details show another position or collapse; Escape, on the row or in the details, collapses them and gives focus back to
// the row. The open details keep room of their own beside the grid's, taking the Charts pane's for as long as they need it.
export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
  const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
  // 1440 by 1050 is the shortest desktop window whose dock holds the summary strip, a 250 px grid and 240 px of open details at once.
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  // A held read, released by the scenario, shows what the details show while the Blotter is read again; a count of the positions can be
  // made to fail once.
  await evaluate(`(()=>{const send=window.fetch;window.fetch=async(input,init)=>{if(String(input)==='/api/trading/workspace'&&window.__detailsHold){window.__detailsHold=false;await new Promise(resolve=>{window.__detailsRelease=resolve;});}
    if(String(input)==='/api/trading/positions'&&window.__failCount){window.__failCount=false;return new Response(JSON.stringify({code:'request_failed',message:'Synthetic count failure.'}),{status:500,headers:{'Content-Type':'application/json'}});}
    return send(input,init);};})()`);
  const press=async key=>{const codes={ArrowDown:40,ArrowUp:38,Enter:13,Escape:27};for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type,key,code:key,windowsVirtualKeyCode:codes[key]});};
  // The trader can amend; the standard fixture holds the swaps the earlier suites booked beside its futures.
  await persona('trader');
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");
  await set('#snapshot',frame);await wait(idle);
  await click('#nav-futures');
  await wait(`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`);
  // Flat rows in the order on screen, the first shown, so stepping along them stays within the rows in view.
  await set('#blotter-row-group','');await wait(idle);
  await evaluate(`(async()=>{document.querySelector('#desk-positions .tabulator-tableholder').scrollTop=0;await ${frames};})()`);
  const grid="document.querySelector('#desk-positions .standard-data-grid')",holder="document.querySelector('#desk-positions .tabulator-tableholder')";
  const rows=`[...${holder}.querySelectorAll('.tabulator-row[data-row-key]')].sort((a,b)=>a.getBoundingClientRect().top-b.getBoundingClientRect().top)`;
  const row=key=>`${holder}.querySelector('.tabulator-row[data-row-key="'+${key}+'"]')`;
  const reference=key=>`${row(key)}?.querySelector('.desk-reference')?.textContent`;
  // The key of the row drawn after (or before) the selected one, in the order on screen; the rows drawn change as the grid scrolls and resizes.
  const beside=(direction=1)=>evaluate(`(()=>{const list=${rows},at=list.findIndex(r=>r.dataset.rowKey===${grid}.dataset.selectedRow);return list[at+${direction}]?.dataset.rowKey;})()`);
  const selected=`${grid}.dataset.selectedRow`;
  // What a failed wait observed: the selection, the details' title and mode, the focused element and any question or error.
  const observedState=`({selected:${grid}.dataset.selectedRow,title:document.querySelector('#details-title')?.textContent,mode:document.querySelector('#position-details')?.dataset.mode,prompt:document.querySelector('#position-details-prompt')?.hidden===false,error:document.querySelector('#error').hidden?'':document.querySelector('#error').textContent})`;
  const observed=async error=>{throw Error(error.message+' · observed '+JSON.stringify(await evaluate(`({selected:${grid}.dataset.selectedRow,title:document.querySelector('#details-title')?.textContent,mode:document.querySelector('#position-details')?.dataset.mode,
    focus:[document.activeElement?.tagName,document.activeElement?.id,document.activeElement?.dataset?.rowKey],prompt:document.querySelector('#position-details-prompt')?.hidden===false,form:document.querySelector('#position-details-form')?.hidden===false,reason:[document.querySelector('#swap-reason')?.value,document.querySelector('#trade-reason')?.value],error:document.querySelector('#error').hidden?'':document.querySelector('#error').textContent})`)));};
  // The panes' room: the details' and the grid's groups, whether Charts is put away, and whether the page itself scrolls.
  const group=pane=>`document.querySelector('#blotter-pane-${pane}').closest('.dock-group')`;
  const room=`({groups:[...document.querySelectorAll('#blotter-dock .dock-group')].map(g=>[g.querySelector('.dock-tab[aria-selected=true],.dock-title')?.textContent,Math.round(g.getBoundingClientRect().top),Math.round(g.getBoundingClientRect().bottom)]),dock:Math.round(document.querySelector('#blotter-dock').getBoundingClientRect().bottom),details:${group('details')}.getBoundingClientRect().height,grid:${group('grid')}.getBoundingClientRect().height,charts:${group('charts')}.hasAttribute('data-collapsed'),page:[document.scrollingElement.scrollHeight,innerHeight]})`;
  const roomy=`(()=>{const r=${room};return r.details>=240&&r.grid>=250&&r.page[0]<=r.page[1]&&r.groups.every(g=>g[2]<=r.dock+1);})()`;
  const whole=key=>`(()=>{const r=${row(key)}?.getBoundingClientRect(),h=${holder}.getBoundingClientRect();return !!r&&r.top>=h.top-0.5&&r.bottom<=h.bottom+0.5;})()`;
  const anchor=`(()=>{const top=${holder}.getBoundingClientRect().top,first=${rows}.find(r=>r.getBoundingClientRect().top>=top-0.5);return {key:first.dataset.rowKey,top:first.getBoundingClientRect().top-top};})()`;
  const collapsed="!!document.querySelector('#blotter-dock .dock-group[data-collapsed] .dock-bar')&&document.querySelector('#blotter-pane-details').hidden";
  await check('the details start collapsed and no row holds a position view',`${collapsed}&&!document.querySelector('#desk-positions #position-details')`);
  // The newest trades first, which puts the swaps the earlier suites booked among the first rows, with rows beneath them to step to.
  for(let i=0;i<2;i++){await click('#desk-positions [role=columnheader][tabulator-field=c0]');await wait(idle);}
  await wait("document.querySelector('#desk-positions [role=columnheader][tabulator-field=c0]').getAttribute('aria-sort')==='descending'");
  // A sort keeps the trader's place a frame after it draws: the rows are read once that place is restored and the grid holds still.
  await evaluate(`(async()=>{const h=${holder};let last=-1;for(let i=0;i<20&&h.scrollTop!==last;i++){last=h.scrollTop;await ${frames};}})()`);
  // A swap with a row above it, brought to the top of the grid's viewport, so that the rows stepped to below it stay in view.
  const found=await evaluate(`(async()=>{const h=${holder};
    for(let top=0;;top+=Math.floor(h.clientHeight/2)){h.scrollTop=top;await ${frames};const list=${rows},at=list.findIndex((r,i)=>i>0&&r.querySelector('.desk-pill')?.textContent==='SWAP');
      if(at>0)return {swap:list[at].dataset.rowKey,above:list[at-1].dataset.rowKey};if(h.scrollTop+h.clientHeight>=h.scrollHeight-1)return null;}})()`);
  if(!found)throw Error('No swap row with a row above it: the suites before this one book the swaps it opens. Rows: '+JSON.stringify(await evaluate(`(async()=>({rows:${rows}.map(r=>r.querySelector('.desk-pill')?.textContent+' '+r.querySelector('.desk-reference')?.textContent),views:await(await fetch('/api/table-views/blotter')).text(),books:document.querySelector('#blotter-books').selectedOptions.length,portfolios:document.querySelector('#blotter-portfolios').selectedOptions.length,snapshot:document.querySelector('#snapshot').value,summary:document.querySelector('#futures-summary').textContent}))()`)));
  const swapKey=found.swap;
  await evaluate(`(async()=>{const h=${holder};for(let i=0;i<4;i++){const r=${row(JSON.stringify(found.above))};if(r){const d=r.getBoundingClientRect().top-h.getBoundingClientRect().top;if(Math.abs(d)<1)break;h.scrollTop+=d;}await ${frames};}})()`);
  await evaluate(`${grid}.dataset.probe='kept'`);const kept=`${grid}?.dataset.probe==='kept'`;
  // ↓ from the row above selects the swap without opening it.
  await evaluate(`${row(JSON.stringify(found.above))}.focus({preventScroll:true})`);await press('ArrowDown');
  await wait(`${selected}===${JSON.stringify(swapKey)}&&document.activeElement===${row(JSON.stringify(swapKey))}`);
  await check('↓ selects the next row and focuses it, and the details stay collapsed',`${collapsed}&&document.querySelector('#position-details')?.hidden!==false`);
  const before=await evaluate(anchor);
  await press('Enter');
  await wait(`document.querySelector('#position-details')?.hidden===false&&!!document.querySelector('#position-details-view .swap-side-schedules')&&${idle}`);
  await evaluate(frames);
  await check('Enter expands the details pane below the grid on the swap\'s Terms, with its sides, and the grid keeps its rows',
    `(()=>{const pane=document.querySelector('#blotter-pane-details'),details=document.querySelector('#position-details'),gridBox=${holder}.getBoundingClientRect();
      return !pane.hidden&&pane.contains(details)&&!${holder}.contains(details)&&pane.parentElement.getBoundingClientRect().top>=gridBox.bottom&&
        document.querySelector('#position-details-tabs-terms').getAttribute('aria-selected')==='true'&&document.querySelector('#details-title').textContent.startsWith(${reference(JSON.stringify(swapKey))}+' · ')&&
        !document.querySelector('#blotter-dock .dock-group[data-collapsed]:has(#blotter-pane-details)');})()`,
    "[document.querySelector('#details-title')?.textContent,document.querySelector('#position-details-tabs [aria-selected=true]')?.id,document.querySelector('#blotter-pane-details').hidden]");
  await wait(`document.activeElement===${row(JSON.stringify(swapKey))}`).catch(()=>undefined);
  await check('focus stays on the row Enter opened',`document.activeElement===${row(JSON.stringify(swapKey))}`,'[document.activeElement?.className,document.activeElement?.dataset?.rowKey]');
  await check('the open details have at least 240 px and the grid at least 250 px, Charts making way, and the page does not scroll',`${roomy}&&${room}.charts`,room);
  await check('the grid is the same one, its first row where it was, and the swap whole in view',
    `${kept}&&(()=>{const a=${row(JSON.stringify(before.key))};return !!a&&Math.abs(a.getBoundingClientRect().top-${holder}.getBoundingClientRect().top-${before.top})<=1;})()&&${whole(JSON.stringify(swapKey))}`,anchor);
  // ↓ selects each next row, shown in the details, focused and whole in view; the grid scrolls only when the row is beyond it.
  for(let step=1;step<=5;step++){
    const next=await beside();
    await press('ArrowDown');
    await wait(`${selected}===${JSON.stringify(next)}&&document.querySelector('#details-title').textContent.startsWith(${reference(JSON.stringify(next))}+' · ')&&document.activeElement===${row(JSON.stringify(next))}&&${idle}`).catch(observed);
    await evaluate(frames);
    await check(`↓ ${step} shows the selected position in the details, its row focused and whole in view, the details keeping their room`,`${whole(JSON.stringify(next))}&&${roomy}&&${kept}`,room);
  }
  // The Risk tab, once chosen, is the tab of the next position too.
  await click('#position-details-tabs-risk');await wait(`document.querySelector('#position-details-tabs-risk').getAttribute('aria-selected')==='true'`);
  const shown=await evaluate(selected),after=await beside();
  await click('#details-next');
  await wait(`${selected}===${JSON.stringify(after)}&&document.querySelector('#details-title').textContent.startsWith(${reference(JSON.stringify(after))}+' · ')&&${idle}`);
  await check('▸ shows the next position on the Risk tab the trader chose',`${selected}!==${JSON.stringify(shown)}&&document.querySelector('#position-details-tabs-risk').getAttribute('aria-selected')==='true'`);
  await click('#details-previous');
  await wait(`${selected}===${JSON.stringify(shown)}&&${idle}`);
  await check('◂ shows the previous position, still on its Risk tab',`document.querySelector('#position-details-tabs-risk').getAttribute('aria-selected')==='true'`);
  // Escape in the details, which hold no edits, collapses them, gives Charts its room back and focus back to the row.
  await evaluate("document.querySelector('#position-details').focus()");await press('Escape');
  await wait(`${collapsed}&&document.activeElement===${row(JSON.stringify(shown))}&&${idle}`).catch(observed);
  await check('Escape in the details collapses them, Charts comes back, and focus is on the row',`!${room}.charts&&${selected}===${JSON.stringify(shown)}&&${kept}`,room);
  await press('Enter');await wait(`document.querySelector('#position-details')?.hidden===false&&${idle}`);
  await check('Enter opens the row again on the Risk tab last chosen',`document.querySelector('#position-details-tabs-risk').getAttribute('aria-selected')==='true'&&document.activeElement===${row(JSON.stringify(shown))}`);
  // An edit in the amendment holds the selection until the trader answers.
  await click('#side-panel-amend');
  const reason="document.querySelector('#position-details-form #swap-reason,#position-details-form #trade-reason')";
  await wait(`!!${reason}&&!${reason}.disabled&&${reason}.getClientRects().length>0&&${idle}`);
  // Typed as a person types: only trusted input marks a form as edited.
  await evaluate(`${reason}.focus()`);await command('Input.insertText',{text:'Synthetic amendment in progress'});
  await evaluate(`${row(JSON.stringify(shown))}.focus()`);await press('ArrowDown');
  const prompt="document.querySelector('#position-details-prompt')";
  await wait(`!${prompt}.hidden`).catch(observed);
  await check('↓ with an unsaved amendment asks first, and the selection stays',`${selected}===${JSON.stringify(shown)}&&${prompt}.textContent.includes('unsaved changes')`);
  await press('Escape');await wait(`${prompt}.hidden&&${idle}`);
  await check('keeping the edits leaves the selection and the amendment as they were',`${selected}===${JSON.stringify(shown)}&&${reason}?.value==='Synthetic amendment in progress'&&document.querySelector('#position-details')?.hidden===false`);
  // Reset layout, whose default has the details collapsed, asks first about the edits they hold, as collapsing them does; keeping them keeps
  // the details, the amendment and the arrangement as they were.
  const arrangement="JSON.stringify([...document.querySelectorAll('#blotter-dock .dock-group')].map(g=>[g.querySelector('.dock-tab[aria-selected=true],.dock-title')?.textContent,g.hasAttribute('data-collapsed')]))";
  const arranged=await evaluate(arrangement);
  await click('#blotter-reset-layout');await wait(`!${prompt}.hidden`).catch(()=>undefined);
  await check('Reset layout with an unsaved amendment in the details asks first, the details still open',`!${prompt}.hidden&&${prompt}.textContent.includes('unsaved changes')&&document.querySelector('#position-details')?.hidden===false`,observedState);
  await press('Escape');await wait(`${prompt}.hidden&&${idle}`).catch(()=>undefined);
  await check('keeping the edits leaves the details, the amendment and the arrangement as they were',
    `document.querySelector('#position-details')?.hidden===false&&${reason}?.value==='Synthetic amendment in progress'&&${arrangement}===${JSON.stringify(arranged)}`,arrangement);
  // Escape on the row collapses the details too, asking first about the edits; discarding them collapses the pane and focuses the row.
  await evaluate(`${row(JSON.stringify(shown))}.focus()`);await press('Escape');await wait(`!${prompt}.hidden`).catch(observed);
  await check('Escape on the row with an unsaved amendment asks before the details collapse',`document.querySelector('#position-details')?.hidden===false`);
  await evaluate(`[...${prompt}.querySelectorAll('button')].find(b=>b.textContent==='Discard changes').click()`);
  await wait(`${collapsed}&&document.activeElement===${row(JSON.stringify(shown))}&&${idle}`).catch(observed);
  await check('discarding collapses the details and gives focus back to the selected row',`${selected}===${JSON.stringify(shown)}&&document.querySelector('#position-details').hidden&&!${room}.charts`);
  // A new trade collapsed from the details gives focus back to the selected row, as no position row is open.
  await click('#futures-new');await wait(`document.querySelector('#position-details')?.dataset.mode==='new'&&${idle}`);
  await press('Escape');
  await wait(`${collapsed}&&document.activeElement===${row(JSON.stringify(shown))}&&${idle}`).catch(observed);
  await check('collapsing a new trade gives focus back to the selected row',`document.activeElement===${row(JSON.stringify(shown))}`);
  // A new read shows the selected position again in the details, on the tab they were on; one that no longer holds it collapses them.
  await press('Enter');await wait(`document.querySelector('#position-details')?.hidden===false&&${idle}`);
  await click('#position-details-tabs-history');await wait(`document.querySelector('#position-details-tabs-history').getAttribute('aria-selected')==='true'&&${idle}`);
  const title=await evaluate("document.querySelector('#details-title').textContent");
  await evaluate("window.__detailsHold=true;document.querySelector('#futures-refresh').click()");await wait("typeof window.__detailsRelease==='function'");
  await check('while the Blotter is read again, the details keep the position on screen marked stale, with the reason',
    `(()=>{const note=document.querySelector('#details-stale-note');return document.querySelector('#position-details').classList.contains('stale')&&!document.querySelector('#position-details').hidden&&!!note&&!note.hidden&&note.getClientRects().length>0&&note.textContent.includes('previous read');})()`,
    "[document.querySelector('#position-details')?.className,document.querySelector('#details-stale-note')?.hidden,document.querySelector('#details-stale-note')?.textContent]");
  await evaluate('window.__detailsRelease();window.__detailsRelease=undefined');
  await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`);
  await wait(`document.querySelector('#position-details')?.hidden===false&&document.querySelector('#details-title').textContent===${JSON.stringify(title)}`).catch(observed);
  await check('once the read lands the details are current again',"!document.querySelector('#position-details').classList.contains('stale')&&document.querySelector('#details-stale-note').hidden");
  await check('a new read shows the selected position again in the details, on its History tab',`document.querySelector('#position-details-tabs-history').getAttribute('aria-selected')==='true'&&${selected}===${JSON.stringify(shown)}&&${kept}`);
  const otherBook=await evaluate(`(()=>{const book=document.querySelector('#position-details-head .side-panel-meta').textContent.split(' · ').at(-1);return [...document.querySelector('#blotter-books').options].find(o=>o.value&&o.textContent!==book)?.value??null;})()`);
  if(!otherBook)throw Error('The fixture needs a second book to read the Blotter without the selected position.');
  await evaluate(`(()=>{for(const o of document.querySelector('#blotter-books').options)o.selected=o.value===${JSON.stringify(otherBook)};})()`);
  await click('#blotter-apply');await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`);
  await wait(collapsed).catch(observed);
  await check('a new read that no longer holds the selected position collapses the details, never leaving the pane empty',`${collapsed}&&document.querySelector('#position-details').hidden`,room);
  await evaluate("(()=>{for(const o of document.querySelector('#blotter-books').options)o.selected=false;})()");
  await click('#blotter-apply');await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`);
  // A read for the priced contracts, as a future's New trade makes, keeps every summary strip item on screen marked stale until it lands.
  await click('#futures-new');await wait(`document.querySelector('#position-details')?.dataset.mode==='new'&&!!document.querySelector('#new-trade-futures')&&${idle}`);
  await evaluate("window.__detailsHold=true;document.querySelector('#new-trade-futures').click()");await wait("typeof window.__detailsRelease==='function'");
  const strip="[...document.querySelectorAll('#blotter-strip .strip-item')]";
  await check('a read for the priced contracts keeps every summary strip item on screen, each marked stale',
    `${strip}.length>0&&${strip}.every(i=>i.hasAttribute('data-stale')&&!!i.querySelector('.strip-stale'))`,`${strip}.map(i=>[i.textContent,i.hasAttribute('data-stale')])`);
  await evaluate('window.__detailsRelease();window.__detailsRelease=undefined');
  await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&!!document.querySelector('#position-details-form #futures-new-trade')&&${idle}`).catch(observed);
  await click('#details-collapse');await wait(`${collapsed}&&${idle}`).catch(observed);
  // A count of the positions that fails beside the valuation is said as the count's own failure, never as the valuation's.
  await evaluate("window.__failCount=true;document.querySelector('#futures-refresh').click()");
  await wait(`document.querySelector('#futures-page').dataset.pricingState==='failed'&&${idle}`).catch(()=>undefined);
  const loadText="document.querySelector('#blotter-load-text').textContent";
  await check('a failed count of the positions says so with its reason, and not that the positions could not be valued, with Retry',
    `${loadText}.includes('Synthetic count failure.')&&!/valu/i.test(${loadText})&&!document.querySelector('#blotter-retry').hidden`,loadText);
  await click('#blotter-retry');await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`).catch(observed);
  // A row low in the grid stays in view when the details it opens take the lower part of the window.
  await evaluate(`${grid}.dataset.probe='kept'`);await evaluate(frames);
  const low=await evaluate(`(()=>{const h=${holder}.getBoundingClientRect(),list=${rows}.filter(r=>r.getBoundingClientRect().top>=h.top-0.5&&r.getBoundingClientRect().bottom<=h.bottom+0.5);return list.at(-1).dataset.rowKey;})()`);
  await click(`#desk-positions .tabulator-row[data-row-key="${low}"]`);
  await wait(`document.querySelector('#position-details')?.hidden===false&&${idle}`);await evaluate(frames);
  await wait(whole(JSON.stringify(low))).catch(observed);
  await check('a row opened near the foot of the grid is whole in view once the details have taken their room, in the same grid',`${whole(JSON.stringify(low))}&&${kept}&&${roomy}`,room);
  await click('#details-collapse');await wait(`${collapsed}&&${idle}`);
  // Where the window leaves the grid room for them, stepping through the rows beside the open details keeps the first row in view where it is.
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1400,deviceScaleFactor:1,mobile:false});await evaluate(frames);
  await evaluate(`(async()=>{const h=${holder};h.scrollTop=0;await ${frames};${rows}[0].focus({preventScroll:true});})()`);
  await press('Enter');await wait(`document.querySelector('#position-details')?.hidden===false&&${idle}`);await evaluate(frames);
  const first=await evaluate(anchor);
  for(let step=1;step<=5;step++){
    const next=await beside();await press('ArrowDown');
    await wait(`${selected}===${JSON.stringify(next)}&&document.querySelector('#details-title').textContent.startsWith(${reference(JSON.stringify(next))}+' · ')&&${idle}`).catch(observed);
  }
  await evaluate(frames);
  await check('at 1440 by 1400, ↓ five times beside the open details leaves the first row in view where it was',
    `(()=>{const a=${row(JSON.stringify(first.key))};return !!a&&Math.abs(a.getBoundingClientRect().top-${holder}.getBoundingClientRect().top-${first.top})<=1;})()`,room);
  await click('#details-collapse');await wait(`${collapsed}&&${idle}`);
  // At 1440 by 900 the dock cannot hold a 250 px grid and 240 px of details beside the summary strip: the two share what is left evenly,
  // each at least 160 px, and no pane reaches past the dock into the band beneath it.
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});await evaluate(frames);
  await evaluate(`${rows}.find(r=>{const h=${holder}.getBoundingClientRect(),b=r.getBoundingClientRect();return b.top>=h.top-0.5&&b.bottom<=h.bottom+0.5;}).focus({preventScroll:true})`);
  await press('Enter');await wait(`document.querySelector('#position-details')?.hidden===false&&${idle}`);await evaluate(frames);
  const shared=`(()=>{const r=${room};return r.details>=160&&r.grid>=160&&Math.abs(r.details-r.grid)<=2&&r.page[0]<=r.page[1]&&r.groups.every(g=>g[2]<=r.dock+1);})()`;
  await wait(shared,3000).catch(()=>undefined);
  await check('at 1440 by 900 the grid and the open details share the room evenly, each at least 160 px, and every pane stays inside the dock',shared,room);
  // Grown back to 1440 by 1050 with the details open, the panes take their own minimums again.
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await wait(roomy,3000).catch(()=>undefined);
  await check('a taller window with the details open gives the grid 250 px and the details 240 px again, inside the dock',roomy,room);
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
  await wait(shared,3000).catch(()=>undefined);
  await check('and a shorter one has them share the room again',shared,room);
  await click('#details-collapse');await wait(`${collapsed}&&${idle}`);
  await check('collapsing them there gives the panes back their own sizes inside the dock',`(()=>{const r=${room};return !r.charts&&r.grid>=250&&r.groups.every(g=>g[2]<=r.dock+1);})()`,room);
  // Dragging the divider between the grid and the open details at 1440 by 900 leaves every pane inside the dock, each of the two at least
  // 160 px.
  await evaluate(`${rows}.find(r=>{const h=${holder}.getBoundingClientRect(),b=r.getBoundingClientRect();return b.top>=h.top-0.5&&b.bottom<=h.bottom+0.5;}).focus({preventScroll:true})`);
  await press('Enter');await wait(`document.querySelector('#position-details')?.hidden===false&&${idle}`);await wait(shared,3000).catch(()=>undefined);
  const divider=await evaluate(`(()=>{const d=[...document.querySelectorAll('#blotter-dock .dock-divider')].find(d=>d.previousElementSibling?.querySelector('#blotter-pane-grid')&&d.nextElementSibling?.querySelector('#blotter-pane-details'));const b=d.getBoundingClientRect();return {x:b.left+b.width/2,y:b.top+b.height/2};})()`);
  await command('Input.dispatchMouseEvent',{type:'mouseMoved',...divider});
  await command('Input.dispatchMouseEvent',{type:'mousePressed',...divider,button:'left',buttons:1,clickCount:1});
  for(const dy of [40,80,120])await command('Input.dispatchMouseEvent',{type:'mouseMoved',x:divider.x,y:divider.y+dy,button:'left',buttons:1});
  await command('Input.dispatchMouseEvent',{type:'mouseReleased',x:divider.x,y:divider.y+120,button:'left',buttons:0,clickCount:1});
  await evaluate(frames);await wait(shared,3000).catch(()=>undefined);
  await check('dragging the divider between the grid and the open details keeps every pane inside the dock, each of the two at least 160 px',shared,room);
  // Bringing the Charts pane back beside the open details shares the dock again: every pane stays inside it, the grid and details at 160 px or more.
  await evaluate("document.querySelector('#blotter-dock [data-collapse=charts]').click()");await evaluate(frames);
  const inside=`(()=>{const r=${room};return !r.charts&&r.details>=160&&r.grid>=160&&r.page[0]<=r.page[1]&&r.groups.every(g=>g[2]<=r.dock+1);})()`;
  await wait(inside,3000).catch(()=>undefined);
  await check('bringing Charts back beside the open details keeps every pane inside the dock, the grid and the details at least 160 px',inside,`[${room},document.querySelector('#blotter-dock').style.cssText]`);
  await click('#details-collapse');await wait(`${collapsed}&&${idle}`);await click('#blotter-reset-layout');await evaluate(frames);
  // A press on a row opens it and leaves keyboard focus on it as the details take their room: ↓ and Escape work straight after.
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});await evaluate(frames);
  const pointAt=key=>evaluate(`(()=>{const b=${row(key)}.querySelector('.tabulator-cell').getBoundingClientRect();return {x:b.left+20,y:b.top+b.height/2};})()`);
  const mouse=async(point,count=1)=>{for(let c=1;c<=count;c++){await command('Input.dispatchMouseEvent',{type:'mousePressed',...point,button:'left',buttons:1,clickCount:c});await command('Input.dispatchMouseEvent',{type:'mouseReleased',...point,button:'left',buttons:0,clickCount:c});}};
  const pressedKey=await evaluate(`(()=>{const h=${holder}.getBoundingClientRect();return ${rows}.find(r=>{const b=r.getBoundingClientRect();return b.top>=h.top-0.5&&b.bottom<=h.bottom+0.5;}).dataset.rowKey;})()`);
  await command('Input.dispatchMouseEvent',{type:'mouseMoved',...await pointAt(JSON.stringify(pressedKey))});await mouse(await pointAt(JSON.stringify(pressedKey)));
  await wait(`document.querySelector('#position-details')?.hidden===false&&${idle}`);await evaluate(frames);
  const nextKey=await beside();
  await press('ArrowDown');
  await wait(`${selected}===${JSON.stringify(nextKey)}&&document.activeElement===${row(JSON.stringify(nextKey))}&&${idle}`).catch(observed);
  await check('↓ straight after a press on a row selects the next row, with focus on it',`${selected}===${JSON.stringify(nextKey)}&&document.activeElement===${row(JSON.stringify(nextKey))}`);
  await mouse(await pointAt(JSON.stringify(pressedKey)));await wait(`${selected}===${JSON.stringify(pressedKey)}&&${idle}`);await evaluate(frames);
  await press('Escape');
  await wait(`${collapsed}&&document.activeElement===${row(JSON.stringify(pressedKey))}&&${idle}`).catch(observed);
  await check('Escape straight after a press on a row collapses the details, with focus on the row',`${collapsed}&&document.activeElement===${row(JSON.stringify(pressedKey))}`);
  // A press on a row in the upper grid opens its details at once, with no wait for a second press: the details take their room below the
  // grid, whose top stays put, so the row stays under the pointer and the grid does not scroll.
  const settled=`(async()=>{const h=${holder};let last=-1;for(let i=0;i<20&&h.scrollTop!==last;i++){last=h.scrollTop;await ${frames};}})()`;
  const upper=JSON.stringify(await evaluate(`(()=>{const h=${holder}.getBoundingClientRect();return ${rows}.filter(r=>{const b=r.getBoundingClientRect();return b.top>=h.top-0.5&&b.bottom<=h.bottom+0.5;})[1].dataset.rowKey;})()`));
  const placed=`(()=>({top:${row(upper)}?.getBoundingClientRect().top,scroll:${holder}.scrollTop}))()`,start=await evaluate(placed);
  await evaluate(`(()=>{const details=document.querySelector('#blotter-pane-details');window.__upAt=window.__expandedAt=undefined;
    document.addEventListener('mouseup',()=>{window.__upAt=performance.now();},{capture:true,once:true});
    const seen=new MutationObserver(()=>{if(!details.hidden){window.__expandedAt=performance.now();seen.disconnect();}});seen.observe(details,{attributes:true,attributeFilter:['hidden']});})()`);
  const upperPoint=await pointAt(upper);
  await command('Input.dispatchMouseEvent',{type:'mouseMoved',...upperPoint});await mouse(upperPoint);
  await wait(`document.querySelector('#position-details')?.hidden===false&&${idle}`);await evaluate(settled);
  await check('a press on a row opens its details at once, with no wait for a second press','window.__expandedAt-window.__upAt<200','[window.__upAt,window.__expandedAt]');
  await check('a press on a row in the upper grid leaves that row where it was on screen and does not scroll the grid',
    `(()=>{const now=${placed};return Math.abs(now.top-${start.top})<=1&&now.scroll===${start.scroll};})()`,`[${placed},${JSON.stringify(start)}]`);
  // A double click like a person's, its second press a moment after the first at the same point, on a row that still fits beside the
  // details its first press opens, opens the position's own window once, and the details show that row.
  await click('#details-collapse');await wait(`${collapsed}&&${idle}`);await evaluate(settled);
  await evaluate("window.__opened=[];window.open=(...args)=>{window.__opened.push(args[0]);return null;}");
  const upperReference=await evaluate(reference(upper)),twice=await pointAt(upper);
  await command('Input.dispatchMouseEvent',{type:'mouseMoved',...twice});
  await command('Input.dispatchMouseEvent',{type:'mousePressed',...twice,button:'left',buttons:1,clickCount:1});await command('Input.dispatchMouseEvent',{type:'mouseReleased',...twice,button:'left',buttons:0,clickCount:1});
  await evaluate('new Promise(r=>setTimeout(r,120))');
  await command('Input.dispatchMouseEvent',{type:'mousePressed',...twice,button:'left',buttons:1,clickCount:2});await command('Input.dispatchMouseEvent',{type:'mouseReleased',...twice,button:'left',buttons:0,clickCount:2});
  await wait('window.__opened.length>0',3000).catch(()=>undefined);await wait(`document.querySelector('#position-details')?.hidden===false&&${idle}`);await evaluate('new Promise(r=>setTimeout(r,600))');
  await check("a person's double click on a row that still fits beside the details it opens opens the position's own window once, and the details show that row",
    `window.__opened.length===1&&${selected}===${upper}&&document.querySelector('#details-title').textContent.startsWith(${JSON.stringify(upperReference)})`,`[window.__opened.length,${selected},document.querySelector('#details-title').textContent]`);
  await click('#details-collapse');await wait(`${collapsed}&&${idle}`);
  // A row whose focus was dropped while it could not take it back waits for it only until the trader moves on: a press outside the grid,
  // however long after, ends the wait, and the row does not take focus later.
  const waiting=JSON.stringify(await evaluate(`(()=>{const h=${holder}.getBoundingClientRect();return ${rows}.find(r=>{const b=r.getBoundingClientRect();return b.top>=h.top-0.5&&b.bottom<=h.bottom+0.5;}).dataset.rowKey;})()`));
  await evaluate(`(()=>{const r=${row(waiting)};r.focus({preventScroll:true});r.blur();${grid}.inert=true;})()`);
  await evaluate('new Promise(r=>setTimeout(r,700))');
  const outside=await evaluate("(()=>{const b=document.querySelector('#blotter-strip').getBoundingClientRect();return {x:Math.round(b.left+4),y:Math.round(b.top+b.height/2)};})()");
  await command('Input.dispatchMouseEvent',{type:'mousePressed',...outside,button:'left',buttons:1,clickCount:1});await command('Input.dispatchMouseEvent',{type:'mouseReleased',...outside,button:'left',buttons:0,clickCount:1});
  await evaluate(`(async()=>{${grid}.inert=false;for(let i=0;i<6;i++)await ${frames};})()`);
  await check('a press outside the grid ends a row\'s wait for its focus: the row does not take it back later',`document.activeElement!==${row(waiting)}`,'[document.activeElement?.tagName,document.activeElement?.className]');
}
