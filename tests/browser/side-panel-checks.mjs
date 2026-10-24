// The desk's side panel (blotter-desk.md, Side panel), checked inside the desk's own scenario. A row opens its position in the
// panel, beside the charts and positions when the main area has room and over them when it has not, and the panel keeps
// the page whole: the tiles do not move, the first position stays above the fold, and nothing overflows the page.
const panel="document.querySelector('#side-panel')";
const tiles="JSON.stringify([...document.querySelectorAll('.desk-tile')].map(t=>{const r=t.getBoundingClientRect();return [r.left,r.top,r.width,r.height].map(Math.round);}))";
const rowSelector=kind=>`#desk-positions .tabulator-row:not(.tabulator-group):has(.desk-pill-${kind})`;
const referenceOf=kind=>`document.querySelector(${JSON.stringify(rowSelector(kind))}).querySelector('.desk-reference').textContent`;
const panelReference="document.querySelector('#side-panel-head .side-panel-reference').textContent";
// A desk drawn again: its read is in, and its first position is on screen.
const redrawn=idle=>`${idle}&&!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')`;

export async function sidePanelChecks({evaluate,wait,click,set,check,idle,press,settle,rowsRead}){
  const held=reference=>`${rowsRead}.find(h=>h.tradeReference===${JSON.stringify(reference)})`;
  const shapeReads="window.__futuresResponses.filter(r=>r.path==='/api/market/curve-shapes').length";
  const editorReads="window.__futuresResponses.filter(r=>r.path==='/api/futures/editor').length";
  const open=async kind=>{await click(rowSelector(kind));await wait(`!${panel}.hidden&&${panelReference}===${referenceOf(kind)}`);};
  await settle(1440,900);
  const futureReference=await evaluate(referenceOf('fut')),tilesBefore=await evaluate(tiles),shapesBefore=await evaluate(shapeReads);
  await open('fut');
  await check('a row opens its position in the side panel: the trade reference in its header, the curve drawn, and focus inside it',
    `!${panel}.hidden&&${panel}.dataset.mode==='position'&&${panelReference}===${JSON.stringify(futureReference)}&&!!document.querySelector('#side-panel-body .chart canvas')&&document.activeElement===${panel}`,
    `[${panel}.hidden,${panelReference},document.activeElement.id]`);
  await check('the panel docks beside the charts and the positions, under the tiles, and the tiles do not move',
    `${panel}.dataset.placement==='docked'&&(()=>{const p=${panel}.getBoundingClientRect(),g=document.querySelector('#desk-positions').getBoundingClientRect(),c=document.querySelector('#desk-charts').getBoundingClientRect(),t=document.querySelector('.desk-tiles').getBoundingClientRect(),w=document.querySelector('.table-workspace').getBoundingClientRect();return p.left>=g.right-1&&p.left>=c.right-1&&p.top>=t.bottom-1&&p.right<=w.right+1;})()&&${tiles}===${JSON.stringify(tilesBefore)}`,
    `[${tiles},${JSON.stringify(tilesBefore)}]`);
  await check('with the panel docked at 1440 by 900 the first position is above the fold, and the grid fits its column without a horizontal scroll',
    `(()=>{const row=document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)'),holder=document.querySelector('#desk-positions .tabulator-tableholder');return row.getBoundingClientRect().top<innerHeight&&holder.scrollWidth<=holder.clientWidth+1;})()`,
    `[document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)').getBoundingClientRect().top,innerHeight,document.querySelector('#desk-positions .tabulator-tableholder').scrollWidth,document.querySelector('#desk-positions .tabulator-tableholder').clientWidth]`);
  await check('while the panel is docked the grid leaves out the mark and curve columns, which the panel shows for its position',
    `['c4','c6'].every(f=>document.querySelector('#desk-positions [role=columnheader][tabulator-field='+f+']').style.display==='none')&&['c0','c1','c2','c3','c5'].every(f=>document.querySelector('#desk-positions [role=columnheader][tabulator-field='+f+']').style.display!=='none')`);
  await check('the row the panel shows is marked in the grid',
    `(()=>{const rows=[...document.querySelectorAll('#desk-positions .open-row')];return rows.length===1&&rows[0].getAttribute('aria-current')==='true'&&rows[0].querySelector('.desk-reference').textContent===${JSON.stringify(futureReference)};})()`);
  await check('the panel states the position\'s backend value and measure, and its mark minus price labeled by what it was done at',
    `(()=>{const h=${held(futureReference)},delta=document.querySelector('.side-panel-delta'),value=document.querySelector('.side-panel-value .kpi-value');return document.querySelector('.side-panel-figure .side-panel-label').textContent==='Execution P&L'&&value.title===h.executionPnl+' '+h.currency&&(h.markMinusPrice===null?!delta:delta.querySelector('.side-panel-label').textContent==='Mark − executed'&&delta.title===h.markMinusPrice+' '+h.currency+'/'+h.unit&&delta.querySelector('b').textContent.replace(/^[+−]/,'').length>0);})()`,
    `document.querySelector('#side-panel-head').innerText`);
  await check('the curve is the desk\'s own read of its market, with the delivery period marked, and it is not read again for the panel',
    `${shapeReads}===${shapesBefore}&&document.querySelector('#side-panel-body .chart-toolbar small').textContent==='Marked: delivery '+document.querySelector(${JSON.stringify(rowSelector('fut'))}+' [tabulator-field=c1] .desk-sub').textContent&&document.querySelector('#side-panel-body .side-panel-facts').children.length>=3`,
    `[${shapeReads},${shapesBefore},document.querySelector('#side-panel-body .chart-toolbar').textContent]`);

  // Keyboard: Escape closes the panel and gives focus back to the row; Enter on a focused row opens it again.
  await press('Escape','Escape',27);
  await wait(`${panel}.hidden&&document.activeElement.classList.contains('tabulator-row')`);
  await check('Escape closes the panel and returns focus to the row it was opened from',
    `${panel}.hidden&&document.activeElement.classList.contains('tabulator-row')&&document.activeElement.querySelector('.desk-reference').textContent===${JSON.stringify(futureReference)}&&!document.querySelector('#desk-positions .open-row')&&document.querySelector('#desk-positions [role=columnheader][tabulator-field=c6]').style.display!=='none'`,
    `[${panel}.hidden,document.activeElement.className,document.activeElement.textContent.slice(0,40)]`);
  await press('Enter','Enter',13,'\r');
  await wait(`!${panel}.hidden`);
  await check('Enter on a focused row opens the position',`!${panel}.hidden&&${panelReference}===${JSON.stringify(futureReference)}&&document.activeElement===${panel}`);

  // Tabs: a tablist the arrow keys move along; Terms and History share one read of the trade, in the desk's context.
  const reads=await evaluate(editorReads);
  await evaluate("document.querySelector('#side-panel-tabs-curve').focus()");await press('ArrowRight','ArrowRight',39);
  await wait("!!document.querySelector('#side-panel-body .side-panel-section')");
  await check('the tabs are a tablist, the arrow keys move along it and choose what they reach, and the tab panel is named by the selected tab',
    `(()=>{const tabs=[...document.querySelectorAll('#side-panel-tabs [role=tab]')];return document.querySelector('#side-panel-tabs').getAttribute('role')==='tablist'&&tabs.map(t=>t.textContent).join()==='Curve,Terms,Risk,History'&&tabs.filter(t=>t.getAttribute('aria-selected')==='true').length===1&&document.activeElement.id==='side-panel-tabs-terms'&&document.activeElement.getAttribute('aria-selected')==='true'&&tabs.filter(t=>t.tabIndex===0).length===1&&document.querySelector('#side-panel-body').getAttribute('role')==='tabpanel'&&document.querySelector('#side-panel-body').getAttribute('aria-labelledby')==='side-panel-tabs-terms';})()`,
    `[document.activeElement.id,document.querySelector('#side-panel-body').getAttribute('aria-labelledby')]`);
  await check('Terms reads the trade revision once and lists its terms: the booking facts, the position and the dated contract',
    `(()=>{const read=window.__futuresResponses.filter(r=>r.path==='/api/futures/editor'),h=${held(futureReference)},terms=Object.fromEntries([...document.querySelectorAll('#side-panel-body .side-term')].map(r=>[r.children[0].textContent,r.children[1].textContent]));return read.length===${reads}+1&&read.at(-1).data.state.tradeReference===${JSON.stringify(futureReference)}&&terms.Lots===h.lots.replace('-','−')&&!!terms['Execution price']&&!!terms.Product&&terms.Trade.startsWith(${JSON.stringify(futureReference)})&&[...document.querySelectorAll('#side-panel-body h3')].map(n=>n.textContent).join()==='Trade,Position,Contract';})()`,
    `[${editorReads},${reads},document.querySelector('#side-panel-body').innerText.slice(0,300)]`);
  await press('ArrowRight','ArrowRight',39);
  await check('Risk says its results will show here, without claiming a run',`document.activeElement.id==='side-panel-tabs-risk'&&document.querySelector('#side-panel-body').textContent==="A risk run's results for this position will show here."`);
  await press('ArrowRight','ArrowRight',39);
  await wait("!!document.querySelector('#side-panel-body .regular-table-scroll')");
  await check('History is the editor\'s revisions table for the trade, from the same read',
    `(()=>{const read=window.__futuresResponses.filter(r=>r.path==='/api/futures/editor').at(-1).data;return document.activeElement.id==='side-panel-tabs-history'&&document.querySelectorAll('#side-panel-body tbody tr').length===read.history.length&&[...document.querySelectorAll('#side-panel-body thead th')].map(n=>n.textContent).slice(0,6).join()==='Revision,Event,Effective,Recorded,Book,Lots'&&${editorReads}===${reads}+1;})()`,
    `[${editorReads},${reads},document.querySelector('#side-panel-body').innerText.slice(0,200)]`);
  await press('ArrowRight','ArrowRight',39);
  await check('the arrow keys wrap from the last tab to the first, which draws the curve again',`document.activeElement.id==='side-panel-tabs-curve'&&!!document.querySelector('#side-panel-body .chart canvas')`);
  await press('End','End',35);await press('Home','Home',36);
  await check('End and Home go to the last and the first tab',`document.activeElement.id==='side-panel-tabs-curve'&&document.querySelector('#side-panel-tabs-curve').getAttribute('aria-selected')==='true'`);

  // Amend still reaches today's dialog for the position the panel shows, and the panel stays while it is open.
  await click('#side-panel-amend');
  await wait("document.querySelector('#trade-editor').open");
  await check('Amend opens the amend dialog of the position the panel shows',
    `document.querySelector('#trade-editor').open&&document.querySelector('#trade-editor-reference').textContent.includes(${JSON.stringify(futureReference)})&&!${panel}.hidden`);
  await click('#trade-editor-close');
  await check('the panel stays open on its position when the dialog closes',`!document.querySelector('#trade-editor').open&&!${panel}.hidden&&${panelReference}===${JSON.stringify(futureReference)}`);

  // The panel follows the desk drawing its read again, and a new read closes it.
  await set('#blotter-row-group','market');
  await wait(redrawn(idle));
  await check('regrouping the positions keeps the panel on its position, and marks its row in the new grid',
    `!${panel}.hidden&&${panelReference}===${JSON.stringify(futureReference)}&&document.querySelectorAll('#desk-positions .open-row').length===1&&document.querySelector('#desk-positions .open-row .desk-reference').textContent===${JSON.stringify(futureReference)}&&document.querySelector('#blotter-row-group').value==='market'`);
  await set('#blotter-row-group','book');
  await wait(redrawn(idle));

  // Open curve: Curves & prices on the position's curve, with its contract's price history.
  const market=await evaluate("document.querySelector('#side-panel-title').textContent");
  await click('#side-panel-open-curve');
  await wait("document.querySelector('#nav-market').classList.contains('active')&&!document.querySelector('#history-card').hidden");
  await check('Open curve opens Curves & prices on the position\'s curve with its contract\'s price history shown',
    `document.querySelector('#nav-market').classList.contains('active')&&document.querySelector('#curve-title').textContent===${JSON.stringify(market)}&&!document.querySelector('#history-card').hidden&&document.querySelector('#history-title').textContent.includes('price history')&&document.querySelector('#error').hidden`,
    `[document.querySelector('#curve-title').textContent,document.querySelector('#history-title').textContent,document.querySelector('#error').textContent,document.querySelector('#notice').textContent]`);
  await click('#nav-futures');
  await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')");
  await check('coming back to the Blotter reads it afresh, with no panel left open',`${panel}.hidden&&!document.querySelector('#futures-page').hidden`);

  // A new read closes the panel, as it closes every other surface over the read it replaces.
  await open('fut');
  await click('#futures-refresh');
  await wait(redrawn(idle));
  await check('a new read closes the panel',`${panel}.hidden&&document.querySelector('#error').hidden`);

  // A curve the desk is still reading shows as loading in the panel, and is drawn there when it arrives, from the same read.
  await evaluate('window.__holdShapes=true');await click('#futures-refresh');
  await wait("typeof window.__releaseShapes==='function'&&!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group) .desk-pill')");
  await open('fut');
  await check('while the desk is still reading the curves the panel says its curve is loading',
    `document.querySelector('#side-panel-body').textContent.startsWith('Loading curve shape')&&!document.querySelector('#side-panel-body .chart canvas')&&!document.querySelector('#side-panel-open-curve').disabled`);
  const shapeReadsHeld=await evaluate(shapeReads);
  await evaluate('window.__releaseShapes()');
  await wait("!!document.querySelector('#side-panel-body .chart canvas')");
  // The held read is the desk's own, and completes when it is released: the panel adds none to it.
  await check('and draws it when it arrives, from the desk\'s read and no other',`${shapeReads}===${shapeReadsHeld}+1&&!document.querySelector('#side-panel-body').textContent.startsWith('Loading curve shape')`,`[${shapeReads},${shapeReadsHeld}]`);
  await press('Escape','Escape',27);
  await wait(`${panel}.hidden`);

  // Overlay: below the width that lets it dock the panel covers the positions' right edge, and never widens the page.
  await settle(1100,900);
  const widthClosed=await evaluate('document.documentElement.scrollWidth');
  await open('fut');
  await check('below the width that docks it the panel is an overlay at the window\'s right edge, over the grid, and does not widen the page',
    `${panel}.dataset.placement==='overlay'&&getComputedStyle(${panel}).position==='fixed'&&Math.round(${panel}.getBoundingClientRect().right)===innerWidth&&${panel}.getBoundingClientRect().width<=440&&document.documentElement.scrollWidth===${widthClosed}&&document.querySelector('#desk-positions [role=columnheader][tabulator-field=c6]').style.display!=='none'`,
    `[${panel}.dataset.placement,getComputedStyle(${panel}).position,${panel}.getBoundingClientRect().right,innerWidth,document.documentElement.scrollWidth,${widthClosed}]`);
  await press('Escape','Escape',27);
  await wait(`${panel}.hidden`);
  await settle(1440,900);
  await open('fut');
  await evaluate("document.body.classList.add('assistant-open')");
  await wait(`${panel}.dataset.placement==='overlay'`);
  await check('with the assistant open the panel is an overlay beside it, not docked',
    `${panel}.dataset.placement==='overlay'&&Math.round(${panel}.getBoundingClientRect().right)===innerWidth-parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--assistant-width'))&&document.querySelector('#desk-positions [role=columnheader][tabulator-field=c6]').style.display!=='none'`,
    `[${panel}.dataset.placement,${panel}.getBoundingClientRect().right,innerWidth,getComputedStyle(document.documentElement).getPropertyValue('--assistant-width')]`);
  await evaluate("document.body.classList.remove('assistant-open')");
  await wait(`${panel}.dataset.placement==='docked'`);
  await check('and docks again when the assistant closes',`${panel}.dataset.placement==='docked'&&document.querySelector('#desk-positions [role=columnheader][tabulator-field=c6]').style.display==='none'`);
  await press('Escape','Escape',27);
  await wait(`${panel}.hidden`);

  // Unsaved edits: nothing in the panel is editable yet, so a copy of its markup, held by a shell that says it has edits, is asked.
  await evaluate(`(async()=>{
    const {setupSidePanel,askLeave}=await import('/side-panel.js');
    const host=document.querySelector('#side-panel').cloneNode(true);host.id='scratch-panel';host.hidden=true;document.body.append(host);
    const shell=setupSidePanel(host,{unsaved:()=>true,confirmLeave:askLeave(host),docked:()=>false});
    window.__scratch={host,shell};await shell.show({kind:'position',tradeId:'t1',revision:1});
    window.__scratch.pending=shell.show({kind:'position',tradeId:'t2',revision:1});
  })()`);
  await wait("!document.querySelector('#scratch-panel #side-panel-prompt').hidden");
  await check('switching what a panel holds with unsaved edits asks inside the panel first, focused on the answer that keeps them',
    `(()=>{const prompt=document.querySelector('#scratch-panel #side-panel-prompt'),buttons=[...prompt.querySelectorAll('button')];return prompt.getAttribute('role')==='alertdialog'&&prompt.textContent.includes('unsaved changes')&&buttons.map(b=>b.textContent).join()==='Keep editing,Discard changes'&&document.activeElement===buttons[0]&&window.__scratch.shell.current().tradeId==='t1';})()`);
  await press('Escape','Escape',27);
  await check('Escape keeps the edits: the panel stays on its position, the question closes, and the panel is still open',
    `(async()=>{const answer=await window.__scratch.pending,prompt=document.querySelector('#scratch-panel #side-panel-prompt');return answer===false&&prompt.hidden&&prompt.children.length===0&&window.__scratch.shell.current().tradeId==='t1'&&!window.__scratch.host.hidden;})()`);
  await evaluate("window.__scratch.pending=window.__scratch.shell.show({kind:'position',tradeId:'t2',revision:1});0");
  await wait("!document.querySelector('#scratch-panel #side-panel-prompt').hidden");
  await evaluate("document.querySelector('#scratch-panel #side-panel-prompt').querySelectorAll('button')[1].click()");
  await check('Discard changes switches, closes the question and puts focus in the panel',
    `(async()=>{const answer=await window.__scratch.pending,prompt=document.querySelector('#scratch-panel #side-panel-prompt');return answer===true&&prompt.hidden&&window.__scratch.shell.current().tradeId==='t2'&&document.activeElement===window.__scratch.host;})()`);
  await evaluate("window.__scratch.host.remove();delete window.__scratch");

  // A phone: the sheet fills the window and the page does not scroll sideways because of it.
  await settle(760,900);
  const phoneWidth=await evaluate('document.documentElement.scrollWidth');
  await open('fut');
  await check('at 760 the panel is a full-width sheet and the page does not scroll sideways',
    `${panel}.dataset.placement==='overlay'&&Math.round(${panel}.getBoundingClientRect().left)===0&&Math.round(${panel}.getBoundingClientRect().width)===innerWidth&&document.documentElement.scrollWidth<=document.documentElement.clientWidth+1&&document.documentElement.scrollWidth===${phoneWidth}`,
    `[${panel}.getBoundingClientRect().width,innerWidth,document.documentElement.scrollWidth,${phoneWidth}]`);
  await press('Escape','Escape',27);
  await wait(`${panel}.hidden`);
  await settle(1440,1050);
}
