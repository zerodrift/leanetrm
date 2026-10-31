// Opening a position shows it in the docked Details pane below the grid, never inside the grid's rows: the trader keeps their place. The
// keyboard moves the selection and the details follow it; the last chosen tab is kept from position to position; unsaved edits are asked
// about before the details show another position or collapse; Escape, on the row or in the details, collapses them and gives focus back to
// the row. The open details keep room of their own beside the grid's, taking the Charts pane's for as long as they need it.
export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
  const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
  // 1440 by 1050 is the shortest desktop window whose dock holds the summary strip, a 250 px grid and 240 px of open details at once.
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
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
  await click('#futures-refresh');await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`);
  await wait(`document.querySelector('#position-details')?.hidden===false&&document.querySelector('#details-title').textContent===${JSON.stringify(title)}`).catch(observed);
  await check('a new read shows the selected position again in the details, on its History tab',`document.querySelector('#position-details-tabs-history').getAttribute('aria-selected')==='true'&&${selected}===${JSON.stringify(shown)}&&${kept}`);
  const otherBook=await evaluate(`(()=>{const book=document.querySelector('#position-details-head .side-panel-meta').textContent.split(' · ').at(-1);return [...document.querySelector('#blotter-books').options].find(o=>o.value&&o.textContent!==book)?.value??null;})()`);
  if(!otherBook)throw Error('The fixture needs a second book to read the Blotter without the selected position.');
  await evaluate(`(()=>{for(const o of document.querySelector('#blotter-books').options)o.selected=o.value===${JSON.stringify(otherBook)};})()`);
  await click('#blotter-apply');await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`);
  await wait(collapsed).catch(observed);
  await check('a new read that no longer holds the selected position collapses the details, never leaving the pane empty',`${collapsed}&&document.querySelector('#position-details').hidden`,room);
  await evaluate("(()=>{for(const o of document.querySelector('#blotter-books').options)o.selected=false;})()");
  await click('#blotter-apply');await wait(`document.querySelector('#futures-page').dataset.pricingState==='complete'&&!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`);
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
}
