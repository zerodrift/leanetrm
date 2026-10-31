// The Blotter fills the window beside the navigation with docked panes, the summary strip, the positions grid, details and charts, and
// the page itself never scrolls. The trader collapses, moves and resizes the panes; the arrangement, collapsed panes included, is saved in
// the named view, Reset layout restores the default, and a narrow window shows the panes as tabs of one group until it is wide again,
// still without the page scrolling; a phone's page, with the navigation above it, scrolls.
export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
  const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
  const size=async(width,height)=>{await command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});await wait(`innerWidth===${width}&&innerHeight===${height}`);await evaluate(frames);await evaluate(frames);};
  const pageFits='document.scrollingElement.scrollHeight===innerHeight';
  const gridHeight="document.querySelector('#blotter-pane-grid').getBoundingClientRect().height";
  // The arrangement as drawn: each split's direction and its two sides' shares, each group's tabs, and whether it is collapsed to a bar.
  const drawn=`(()=>{const walk=n=>n.classList.contains('dock-group')?[...n.querySelectorAll(':scope>.dock-head [role=tab],:scope>.dock-bar .dock-title')].map(t=>t.textContent).join(',')+(n.hasAttribute('data-collapsed')?' (collapsed)':'')
    :[n.dataset.direction,...[...n.children].filter(c=>!c.classList.contains('dock-divider')).map(c=>[c.style.flexGrow,walk(c)])];
    return JSON.stringify(walk(document.querySelector('#blotter-dock>.dock-split,#blotter-dock>.dock-group')));})()`;
  const groupOf=pane=>`document.querySelector('#blotter-pane-${pane}').closest('.dock-group')`;
  // At least one whole position row is in the grid's viewport.
  const rowInView="(()=>{const h=document.querySelector('#desk-positions .tabulator-tableholder');if(!h)return false;const b=h.getBoundingClientRect();return [...h.querySelectorAll('.tabulator-row[data-row-key]')].some(r=>{const x=r.getBoundingClientRect();return x.height>0&&x.top>=b.top-0.5&&x.bottom<=b.bottom+0.5;});})()";
  const loaded=`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`;
  const view='Synthetic shell layout';
  await size(1440,1050);
  await command('Page.navigate',{url});await wait(idle);
  await persona('risk');
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')&&!f.originCloseId).id;})()");
  const open=async()=>{await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait(loaded);await evaluate(frames);};
  await open();
  const defaultShape=await evaluate(drawn);
  await check('the default arrangement is the summary strip over the positions over collapsed details and charts',
    `JSON.stringify(JSON.parse(${drawn}).flat(Infinity).filter(x=>/[a-z]/i.test(x)))===JSON.stringify(['column','Summary','column','Positions','column','Details (collapsed)','Charts'])`,drawn);
  await check('at 1440 by 1050 the page fills the window and does not scroll, and the grid shows a row',`${pageFits}&&${rowInView}`,`[document.scrollingElement.scrollHeight,innerHeight]`);
  const small=await evaluate(gridHeight);
  await size(1920,1200);
  await check('at 1920 by 1200 the page still does not scroll, and the positions pane is at least 100 px taller',`${pageFits}&&${gridHeight}>=${small+100}&&${rowInView}`,`[document.scrollingElement.scrollHeight,innerHeight,${small},${gridHeight}]`);
  await size(1440,1050);
  await check('the summary strip states swap PV, futures execution P&L, positions, not valued and risk in one pane',
    "(()=>{const labels=[...document.querySelectorAll('#blotter-pane-summary .strip-item .strip-label')].map(n=>n.textContent);return ['Swap PV','Futures execution P&L','Positions','Not valued','Risk'].every(l=>labels.includes(l));})()",
    "[...document.querySelectorAll('#blotter-pane-summary .strip-item')].map(n=>n.textContent)");
  await check('the charts pane holds the market charts, the per-book bars and the position mix',
    "['desk-chart-pv','desk-chart-position','desk-chart-books','desk-chart-mix'].every(id=>document.querySelector('#blotter-pane-charts #'+id)?.getClientRects().length>0)");
  await evaluate("document.querySelector('#desk-positions .standard-data-grid').dataset.probe='kept'");
  // Collapsing a pane gives its room to its neighbours: the positions grow, and the grid is the same one, redrawn, not built again.
  const before=await evaluate(gridHeight);
  await click('#blotter-dock [data-collapse=charts]');await evaluate(frames);
  await check('collapsing the charts grows the positions pane and keeps the grid',`${gridHeight}>${before}&&!!${groupOf('charts')}.hasAttribute('data-collapsed')&&document.querySelector('#desk-positions .standard-data-grid').dataset.probe==='kept'&&${rowInView}`,
    `[${before},${gridHeight},${drawn}]`);
  await check('a split between two collapsed bars has no divider to drag',"[...document.querySelectorAll('#blotter-dock .dock-divider')].filter(d=>d.getAttribute('aria-disabled')==='true').length>=1");
  await click('#blotter-dock [data-collapse=charts]');await evaluate(frames);
  // HTML drag and drop, as the page receives it: one transfer carried from the charts tab to the positions group's right edge.
  await evaluate(`(()=>{const data=new DataTransfer(),fire=(node,type)=>node.dispatchEvent(new DragEvent(type,{bubbles:true,cancelable:true,dataTransfer:data}));
    const tab=document.querySelector('#blotter-dock-charts');fire(tab,'dragstart');
    const zone=${groupOf('grid')}.querySelector('.dock-zone[data-edge=right]');for(const type of ['dragenter','dragover','drop'])fire(zone,type);fire(tab,'dragend');})()`);
  await wait(idle);await evaluate(frames);
  await check('dragging the charts header onto the positions\' right edge places the charts beside the grid',
    `(()=>{const g=${groupOf('grid')}.getBoundingClientRect(),c=${groupOf('charts')}.getBoundingClientRect();return c.left>=g.right-1&&Math.abs(c.top-g.top)<2&&Math.abs(c.height-g.height)<2&&document.querySelector('#desk-positions .standard-data-grid').dataset.probe==='kept';})()`,drawn);
  await check('the grid still shows a row beside the charts',rowInView);
  // Collapsed beside the grid, the charts are a narrow upright bar, and the divider between them cannot be dragged.
  await click('#blotter-dock [data-collapse=charts]');await evaluate(frames);
  await check('charts collapsed beside the grid are an upright bar whose divider cannot be dragged',
    `(()=>{const bar=${groupOf('charts')}.getBoundingClientRect(),divider=${groupOf('charts')}.previousElementSibling;return bar.width<60&&bar.height>200&&divider.classList.contains('dock-divider')&&divider.getAttribute('aria-disabled')==='true'&&divider.tabIndex===-1;})()`,
    `[${groupOf('charts')}.getBoundingClientRect().toJSON(),${groupOf('charts')}.previousElementSibling.outerHTML]`);
  const arranged=await evaluate(drawn);
  // A narrow window shows the panes as tabs of one group, positions in view, and keeps the arrangement for when it is wide again.
  await size(700,1050);
  await wait(`document.querySelectorAll('#blotter-dock .dock-group').length===1&&${idle}`);await evaluate(frames);
  await check('at 700 px the panes are tabs of one group, with the positions shown, and the page still does not scroll',
    `JSON.stringify([...document.querySelectorAll('#blotter-dock [role=tab]')].map(t=>t.textContent).sort())==='["Charts","Details","Positions","Summary"]'&&!document.querySelector('#blotter-pane-grid').hidden&&${rowInView}&&${pageFits}`,
    `[[...document.querySelectorAll('#blotter-dock [role=tab]')].map(t=>t.textContent),document.querySelector('#blotter-pane-grid').hidden,document.scrollingElement.scrollHeight,innerHeight]`);
  await size(1440,1050);
  await wait(`${drawn}===${JSON.stringify(arranged)}`,3000).catch(()=>undefined);
  await check('widening the window again restores the arrangement',`${drawn}===${JSON.stringify(arranged)}&&${rowInView}`,drawn);
  // The arrangement, the collapsed charts with it, is saved in the view and comes back with it after a reload.
  await set('#blotter-name',view);await click('#blotter-save');await wait(loaded);
  const reloaded=await evaluate('performance.timeOrigin');await command('Page.reload');await wait(`performance.timeOrigin!==${reloaded}&&${idle}`);
  await open();
  await check('after a reload the Blotter starts from the default arrangement',`${drawn}===${JSON.stringify(defaultShape)}`,drawn);
  await set('#blotter-saved',view);await wait(loaded);await evaluate(frames);
  await check('choosing the saved view brings back its arrangement, with the charts still collapsed beside the grid',`${drawn}===${JSON.stringify(arranged)}`,drawn);
  await click('#blotter-reset-layout');await evaluate(frames);
  await check('Reset layout restores the default arrangement',`${drawn}===${JSON.stringify(defaultShape)}&&${rowInView}`,drawn);
  // Focus the person moves out of the details stays out: sorting by a click on a column header draws the rows again, and does not put
  // focus back into the details.
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("!!document.querySelector('#desk-positions .open-row')&&!!document.querySelector('#position-details-tabs-terms')&&"+idle);
  await evaluate("document.querySelector('#position-details-tabs-terms').focus()");
  const header=await evaluate("(()=>{const r=document.querySelector('#desk-positions .tabulator-col[tabulator-field=c1] .tabulator-col-title').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()");
  for(const type of ['mousePressed','mouseReleased'])await command('Input.dispatchMouseEvent',{type,button:'left',clickCount:1,...header});
  await wait(`document.querySelector('#desk-positions .tabulator-col[tabulator-field=c1]').getAttribute('aria-sort')!=='none'&&${idle}`);await evaluate(frames);await evaluate('new Promise(r=>setTimeout(r,300))');
  await check('sorting by a column header leaves focus out of the details',
    "!document.querySelector('#position-details')?.contains(document.activeElement)","[document.activeElement?.id,document.activeElement?.className]");
  await click('#details-collapse');
  await click('#blotter-delete');await wait(idle);
}
