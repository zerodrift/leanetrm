// The desk's side panel (blotter-desk.md, Side panel), checked inside the desk's own scenario. A row opens its position in the
// panel, beside the charts and positions when the main area has room and over them when it has not, and the panel keeps
// the page whole: the tiles do not move, the first position stays above the fold, and nothing overflows the page.
import {chooseProduct,newTrade,startNewTrade} from './new-trade.mjs';
const panel="document.querySelector('#side-panel')";
const tiles="JSON.stringify([...document.querySelectorAll('.desk-tile')].map(t=>{const r=t.getBoundingClientRect();return [r.left,r.top,r.width,r.height].map(Math.round);}))";
const rowSelector=kind=>`#desk-positions .tabulator-row:not(.tabulator-group):has(.desk-pill-${kind})`;
const referenceOf=kind=>`document.querySelector(${JSON.stringify(rowSelector(kind))}).querySelector('.desk-reference').textContent`;
const panelReference="document.querySelector('#side-panel-head .side-panel-reference').textContent";
// A desk drawn again: its read is in, and its first position is on screen.
const redrawn=idle=>`${idle}&&!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')`;
// The desk's seven columns by field (Trade, Market, Position, Price, Mark, Value, Curve): whether the grid shows one, and which of them.
const shows=field=>`document.querySelector('#desk-positions [role=columnheader][tabulator-field=${field}]').style.display!=='none'`;
const shownColumns="['c0','c1','c2','c3','c4','c5','c6'].filter(f=>document.querySelector('#desk-positions [role=columnheader][tabulator-field='+f+']').style.display!=='none').join()";
const gridFits="(()=>{const holder=document.querySelector('#desk-positions .tabulator-tableholder');return holder.scrollWidth<=holder.clientWidth+1;})()";
const gridWidths="(()=>{const holder=document.querySelector('#desk-positions .tabulator-tableholder');return [holder.scrollWidth,holder.clientWidth];})()";

/**
 * The mark minus price is a price move: its sign is in its text, and its ink is the panel's own whichever way the position cuts,
 * because the browser does not combine a price move with a direction into a gain or a loss. Opens the position of `reference`.
 */
export async function deltaInkCheck({evaluate,wait,click,set,check,idle},{what,reference}){
  await set('#blotter-filters .table-search',reference);
  await wait(`(()=>{const rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(row=>row.getClientRects().length);return ${idle}&&rows.length===1&&rows[0].querySelector('.desk-reference')?.textContent===${JSON.stringify(reference)};})()`);
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');
  await wait(`!${panel}.hidden&&${panelReference}===${JSON.stringify(reference)}`);
  await check(`${what} shows its mark minus price, signed, in the panel's own ink and in neither a gain nor a loss color`,
    `(()=>{const ink=value=>{const probe=document.createElement('i');probe.style.color=value;document.body.append(probe);const color=getComputedStyle(probe).color;probe.remove();return color;},delta=document.querySelector('.side-panel-delta b');return !!delta&&/^[+−]/.test(delta.textContent)&&getComputedStyle(delta).color===ink('var(--text)')&&getComputedStyle(delta).color!==ink('var(--gain-ink)')&&getComputedStyle(delta).color!==ink('var(--loss-ink)');})()`,
    "[document.querySelector('#side-panel-head').innerText,document.querySelector('.side-panel-delta b')&&getComputedStyle(document.querySelector('.side-panel-delta b')).color]");
  await click('#side-panel-close');
  await set('#blotter-filters .table-search','');
}

export async function sidePanelChecks({command,evaluate,wait,click,set,check,idle,press,settle,rowsRead,persona}){
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
  // The panel shows a position's mark and curve, so a docked grid gives up its Curve column, and then its Mark column, one at a time and
  // only while the columns it shows do not fit the room it has. How much room that takes depends on what the rows say, so these checks
  // state the rule: the columns it keeps fit, Curve goes before Mark, and more room never shows fewer columns.
  const narrow=await evaluate(shownColumns);
  await check('with the navigation open at 1440 the grid has no room for all seven columns beside the docked panel: it gives up Curve, and what it keeps fits',
    `!${shows('c6')}&&${gridFits}`,`[${shownColumns},${gridWidths}]`);
  await click('#sidebar-toggle');await settle(1440,900);
  await check('with the navigation collapsed the same window has room for Mark: the grid keeps it, and shows every column it showed before',
    `${shows('c4')}&&${gridFits}&&${JSON.stringify(narrow)}.split(',').every(f=>${shownColumns}.split(',').includes(f))`,`[${shownColumns},${gridWidths}]`);
  await click('#sidebar-toggle');await settle(1700,1000);
  await check('in a window of about 1700 all seven columns fit beside the docked panel, so the grid gives up none',
    `${shownColumns}==='c0,c1,c2,c3,c4,c5,c6'&&${gridFits}`,`[${shownColumns},${gridWidths}]`);
  await settle(1440,900);
  await check('and it takes them back down to the room it has, when the window narrows again',`${shownColumns}===${JSON.stringify(narrow)}&&${gridFits}`,`[${shownColumns},${gridWidths}]`);
  await check('the panel\'s width is one token the stage and the panel both read, and it sizes the docked column',
    `getComputedStyle(document.querySelector('.table-workspace-stage')).getPropertyValue('--panel-width').trim()==='340px'&&Math.round(${panel}.getBoundingClientRect().width)===340`,
    `[getComputedStyle(document.querySelector('.table-workspace-stage')).getPropertyValue('--panel-width'),${panel}.getBoundingClientRect().width]`);
  await evaluate("document.querySelector('.table-workspace').style.setProperty('--panel-width','400px')");
  await check('a wider token makes a wider docked panel',`Math.round(${panel}.getBoundingClientRect().width)===400`,`${panel}.getBoundingClientRect().width`);
  await evaluate("document.querySelector('.table-workspace').style.removeProperty('--panel-width')");
  // The room shrinks with a wider panel, so the same window shows how the grid gives its columns up as it has less: down to a width
  // that holds neither Mark nor Curve, whatever the rows say. Curve goes first, a column given up stays given up, and what is kept fits.
  const sweep=[];
  for(const width of [340,420,500,580,660]){
    await evaluate(`document.querySelector('.table-workspace').style.setProperty('--panel-width','${width}px')`);await settle(1440,900);
    sweep.push(await evaluate(`[${width},${shows('c4')},${shows('c6')},${gridFits},${gridWidths}]`));
  }
  await evaluate("document.querySelector('.table-workspace').style.removeProperty('--panel-width')");await settle(1440,900);
  const [last]=sweep.slice(-1);
  await check('as the docked grid gets narrower it gives up Curve before Mark, never takes a column back, keeps what fits, and in the end has neither',
    `${JSON.stringify(sweep.every(([,mark,curve])=>mark||!curve)&&sweep.every((step,at)=>at===0||(sweep[at-1][1]||!step[1])&&(sweep[at-1][2]||!step[2]))&&sweep.every(([,mark,curve,fits])=>fits||(!mark&&!curve))&&!last[1]&&!last[2])}`,
    JSON.stringify(JSON.stringify(sweep)));
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
    `(()=>{const read=window.__futuresResponses.filter(r=>r.path==='/api/futures/editor'),h=${held(futureReference)},terms=Object.fromEntries([...document.querySelectorAll('#side-panel-body .side-term')].map(r=>[r.children[0].textContent,r.children[1].textContent]));return read.length===${reads}+1&&read.at(-1).data.state.tradeReference===${JSON.stringify(futureReference)}&&terms.Lots===h.lots.replace('-','−')&&terms['Execution price']?.endsWith(' '+h.currency+'/'+h.unit)&&!!terms.Product&&terms.Trade.startsWith(${JSON.stringify(futureReference)})&&[...document.querySelectorAll('#side-panel-body h3')].map(n=>n.textContent).join()==='Trade,Position,Contract';})()`,
    `[${editorReads},${reads},document.querySelector('#side-panel-view').innerText.slice(0,300)]`);
  await click(rowSelector('fut'));await wait(`document.activeElement===${panel}`);
  await check('pressing the row that is already open keeps the panel on its tab and its read: nothing is drawn or read again',
    `document.querySelector('#side-panel-tabs-terms').getAttribute('aria-selected')==='true'&&!!document.querySelector('#side-panel-body .side-panel-section')&&${editorReads}===${reads}+1&&${panelReference}===${JSON.stringify(futureReference)}`,
    `[document.querySelector('#side-panel-tabs [aria-selected=true]').textContent,${editorReads},${reads}]`);
  // The tab strip's keys below start from Terms, whichever tab the press left the panel on.
  await click('#side-panel-tabs-terms');await evaluate("document.querySelector('#side-panel-tabs-terms').focus()");
  await press('ArrowRight','ArrowRight',39);
  await check('Risk says its results come from the risk run on the desk, without claiming one, and points to the Risk report',`document.activeElement.id==='side-panel-tabs-risk'&&document.querySelector('#side-panel-view').textContent==="This position's risk comes from the risk run on the desk, and there is none yet. Run one from the Risk report."`);
  await press('ArrowRight','ArrowRight',39);
  await wait("!!document.querySelector('#side-panel-view .side-panel-section')");
  await check('History lists the trade\'s revisions one by one, each with its facts, from the same read, and in the width of the panel',
    `(()=>{const read=window.__futuresResponses.filter(r=>r.path==='/api/futures/editor').at(-1).data,view=document.querySelector('#side-panel-view'),sections=[...view.querySelectorAll('.side-panel-section')];return document.activeElement.id==='side-panel-tabs-history'&&sections.length===read.history.length&&sections.every((section,i)=>section.querySelector('h3').textContent===\`Revision \${read.history[i].revision} · \${read.history[i].kind==='booked'?'Booked':'Amended'}\`&&[...section.querySelectorAll('dt')].map(n=>n.textContent).join()==='Effective,Recorded,Book,Instrument,Lots,Execution price,Trader ID,Memo,Trade date,Legal entity,Counterparty,Reason')&&!view.querySelector('table')&&view.scrollWidth<=view.clientWidth&&${editorReads}===${reads}+1;})()`,
    `[${editorReads},${reads},document.querySelector('#side-panel-view').innerText.slice(0,300)]`);
  await press('ArrowRight','ArrowRight',39);
  await check('the arrow keys wrap from the last tab to the first, which draws the curve again',`document.activeElement.id==='side-panel-tabs-curve'&&!!document.querySelector('#side-panel-body .chart canvas')`);
  await press('End','End',35);await press('Home','Home',36);
  await check('End and Home go to the last and the first tab',`document.activeElement.id==='side-panel-tabs-curve'&&document.querySelector('#side-panel-tabs-curve').getAttribute('aria-selected')==='true'`);

  // Amend puts the form of the position the panel shows in its Terms tab, in place of the read view, and the panel stays on its position.
  await click('#side-panel-amend');
  await wait("!document.querySelector('#trade-editing').hidden");
  const formShown=`(()=>{const form=document.querySelector('#side-panel-form');return !form.hidden&&form.contains(document.querySelector('#trade-editing'))&&document.querySelector('#side-panel-view').hidden;})()`;
  await check('Amend puts the position\'s form in the Terms tab in place of its read view, names the trade, and moves focus into it',
    `${formShown}&&document.querySelector('#side-panel-tabs-terms').getAttribute('aria-selected')==='true'&&document.querySelector('#trade-editor-reference').textContent.includes(${JSON.stringify(futureReference)})&&document.querySelector('#side-panel-form').contains(document.activeElement)&&!${panel}.hidden&&${panelReference}===${JSON.stringify(futureReference)}&&document.querySelector('#side-panel-amend').textContent==='Close editor'`,
    `[${panel}.hidden,document.activeElement.id,document.querySelector('#side-panel-amend').textContent]`);
  await check('a docked panel is scrolled up to the top of the window for the form, which has the window\'s height there',
    `${panel}.dataset.placement==='docked'&&Math.abs(${panel}.getBoundingClientRect().top)<=1&&${panel}.getBoundingClientRect().height>=innerHeight-24`,
    `[${panel}.dataset.placement,${panel}.getBoundingClientRect().top,${panel}.getBoundingClientRect().height,innerHeight]`);
  await check('the form fits the panel: its fields take the one column and nothing scrolls sideways, and no dialog is open anywhere',
    `(()=>{const form=document.querySelector('#side-panel-form'),box=${panel}.getBoundingClientRect();return form.scrollWidth<=form.clientWidth&&[...form.querySelectorAll('input:not([type=hidden]),select,textarea')].filter(n=>n.getClientRects().length).every(n=>n.getBoundingClientRect().right<=box.right+1)&&!document.querySelector('dialog[open]');})()`);
  // Another tab keeps the form out of sight, and what it holds.
  await evaluate("document.querySelector('#trade-lots').value='11'");
  await click('#side-panel-tabs-curve');
  await check('another tab shows its own view, and keeps the form and what it holds out of sight',
    `document.querySelector('#side-panel-form').hidden&&!document.querySelector('#side-panel-view').hidden&&!!document.querySelector('#side-panel-view .chart canvas')&&document.querySelector('#side-panel-form').contains(document.querySelector('#trade-editing'))&&document.querySelector('#trade-lots').value==='11'&&document.querySelector('#side-panel-amend').textContent==='Close editor'`);
  await click('#side-panel-tabs-terms');
  await check('and Terms shows the form again as it was left',`${formShown}&&document.querySelector('#trade-lots').value==='11'`);
  await click('#side-panel-amend');
  await check('Close editor gives the Terms tab its read view again, and the head its Amend, on the same position',
    `document.querySelector('#side-panel-form').hidden&&!document.querySelector('#side-panel-view').hidden&&!!document.querySelector('#side-panel-view .side-panel-section')&&document.querySelector('#trade-editing').hidden&&!document.querySelector('#side-panel-form').contains(document.querySelector('#trade-editing'))&&document.querySelector('#side-panel-amend').textContent==='Amend'&&!${panel}.hidden&&${panelReference}===${JSON.stringify(futureReference)}`);
  await click('#side-panel-tabs-curve');

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
    `document.querySelector('#side-panel-view').textContent.startsWith('Loading curve shape')&&!document.querySelector('#side-panel-body .chart canvas')&&!document.querySelector('#side-panel-open-curve').disabled`);
  const shapeReadsHeld=await evaluate(shapeReads);
  await evaluate("document.querySelector('#side-panel-amend').focus()");
  await evaluate('window.__releaseShapes()');
  await wait("!!document.querySelector('#side-panel-body .chart canvas')");
  await check('the panel drawn again, as its curve arrives, keeps keyboard focus on the control it was on',`document.activeElement.id==='side-panel-amend'`,'[document.activeElement.id,document.activeElement.tagName]');
  // The held read is the desk's own, and completes when it is released: the panel adds none to it.
  await check('and draws it when it arrives, from the desk\'s read and no other',`${shapeReads}===${shapeReadsHeld}+1&&!document.querySelector('#side-panel-view').textContent.startsWith('Loading curve shape')`,`[${shapeReads},${shapeReadsHeld}]`);
  await evaluate(`${panel}.focus()`);await press('Escape','Escape',27);
  await wait(`${panel}.hidden`);

  // Overlay: below the width that lets it dock the panel covers the positions' right edge, and never widens the page.
  // A fixed panel ends at the viewport's content edge, which a classic scrollbar moves left of innerWidth.
  await settle(1100,900);
  const widthClosed=await evaluate('document.documentElement.scrollWidth');
  await open('fut');
  await check('below the width that docks it the panel is an overlay at the window\'s right edge, over the grid, and does not widen the page',
    `${panel}.dataset.placement==='overlay'&&getComputedStyle(${panel}).position==='fixed'&&Math.round(${panel}.getBoundingClientRect().right)===document.documentElement.clientWidth&&${panel}.getBoundingClientRect().width<=440&&document.documentElement.scrollWidth===${widthClosed}&&document.querySelector('#desk-positions [role=columnheader][tabulator-field=c6]').style.display!=='none'`,
    `[${panel}.dataset.placement,getComputedStyle(${panel}).position,${panel}.getBoundingClientRect().right,document.documentElement.clientWidth,document.documentElement.scrollWidth,${widthClosed}]`);
  await press('Escape','Escape',27);
  await wait(`${panel}.hidden`);
  await settle(1440,900);
  await open('fut');
  await evaluate("document.body.classList.add('assistant-open')");
  await wait(`${panel}.dataset.placement==='overlay'`);
  await check('with the assistant open the panel is an overlay beside it, not docked',
    `${panel}.dataset.placement==='overlay'&&Math.round(${panel}.getBoundingClientRect().right)===document.documentElement.clientWidth-parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--assistant-width'))&&document.querySelector('#desk-positions [role=columnheader][tabulator-field=c6]').style.display!=='none'`,
    `[${panel}.dataset.placement,${panel}.getBoundingClientRect().right,document.documentElement.clientWidth,getComputedStyle(document.documentElement).getPropertyValue('--assistant-width')]`);
  await evaluate("document.body.classList.remove('assistant-open')");
  await wait(`${panel}.dataset.placement==='docked'`);
  await check('and docks again when the assistant closes',`${panel}.dataset.placement==='docked'&&document.querySelector('#desk-positions [role=columnheader][tabulator-field=c6]').style.display==='none'`);
  await press('Escape','Escape',27);
  await wait(`${panel}.hidden`);

  // Unsaved edits: the keystrokes a person makes in the form of an amendment are edits the panel holds, and leaving them asks inside it.
  await settle(1440,900);
  const prompt="document.querySelector('#side-panel-prompt')",futures=`document.querySelectorAll(${JSON.stringify(rowSelector('fut'))})`;
  if(await evaluate(`${futures}.length`)<2)throw Error('The unsaved-edit checks need two futures positions.');
  const [refA,refB]=[await evaluate(`${futures}[0].querySelector('.desk-reference').textContent`),await evaluate(`${futures}[1].querySelector('.desk-reference').textContent`)];
  const typeInto=async(selector,text)=>{await evaluate(`(()=>{const n=document.querySelector(${JSON.stringify(selector)});n.focus();n.select();})()`);await command('Input.insertText',{text});};
  // Opens the position of a row of futures and then its form, unless they are open already.
  const amend=async(index,reference)=>{
    await evaluate(`${futures}[${index}].click()`);await wait(`${panelReference}===${JSON.stringify(reference)}`);
    if(await evaluate("document.querySelector('#trade-editing').hidden"))await click('#side-panel-amend');
    await wait("!document.querySelector('#trade-editing').hidden");
  };
  // Answers the question that is asked; the check before it says whether one was.
  const answer=async index=>{await evaluate(`${prompt}.querySelectorAll('button')[${index}]?.click()`);await wait(`${prompt}.hidden`);};
  // A route that asks shows its question at once; one that does not is told by the check that follows, and not by a timeout.
  const asks=()=>evaluate(`new Promise(resolve=>{const start=Date.now(),poll=()=>{if(!${prompt}.hidden||Date.now()-start>1500)resolve();else setTimeout(poll,25);};poll();})`);
  await amend(0,refA);
  await evaluate(`${futures}[1].click()`);await wait(`${panelReference}===${JSON.stringify(refB)}`);
  await check('a form with no edits is left at once: another row opens its position, nothing is asked, and the first position\'s form is gone',
    `${prompt}.hidden&&document.querySelector('#trade-editing').hidden&&!document.querySelector('#side-panel-form').contains(document.querySelector('#trade-editing'))&&document.querySelector('#side-panel-amend').textContent==='Amend'&&document.querySelectorAll('#desk-positions .open-row').length===1`);
  await amend(0,refA);await typeInto('#trade-lots','9');
  await evaluate(`${futures}[1].click()`);await wait(`!${prompt}.hidden`);
  await check('switching to another position with unsaved edits asks inside the panel first, focused on the answer that keeps them',
    `(()=>{const buttons=[...${prompt}.querySelectorAll('button')];return ${prompt}.getAttribute('role')==='alertdialog'&&${prompt}.textContent.includes('unsaved changes')&&buttons.map(b=>b.textContent).join()==='Keep editing,Discard changes'&&document.activeElement===buttons[0]&&${panelReference}===${JSON.stringify(refA)}&&document.querySelector('#trade-lots').value==='9';})()`,
    `[${prompt}.textContent,document.activeElement.textContent,${panelReference},document.querySelector('#trade-lots').value]`);
  await press('Escape','Escape',27);
  await check('Escape keeps the edits: the panel stays on its position and its form, the question closes, and the panel is still open',
    `${prompt}.hidden&&${prompt}.children.length===0&&!${panel}.hidden&&${panelReference}===${JSON.stringify(refA)}&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value==='9'&&document.activeElement===${panel}`);
  await evaluate(`${futures}[1].click()`);await wait(`!${prompt}.hidden`);
  await answer(1);await wait(`${panelReference}===${JSON.stringify(refB)}`);
  await check('Discard changes switches, closes the question and puts focus in the panel, with the edits and the form gone',
    `${prompt}.hidden&&document.activeElement===${panel}&&document.querySelector('#trade-editing').hidden&&document.querySelector('#side-panel-amend').textContent==='Amend'&&document.querySelectorAll('#desk-positions .open-row').length===1&&document.querySelector('#desk-positions .open-row .desk-reference').textContent===${JSON.stringify(refB)}`);
  await amend(1,refB);
  await check('the other position\'s form opens as that position is, without the edits the first one held',`document.querySelector('#trade-lots').value!=='9'&&document.querySelector('#trade-editor-reference').textContent.includes(${JSON.stringify(refB)})`);
  await typeInto('#trade-lots','8');
  await click('#side-panel-close');await wait(`!${prompt}.hidden`);
  await check('closing the panel with unsaved edits asks first too',`${prompt}.querySelectorAll('button').length===2&&!${panel}.hidden&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value==='8'`);
  await answer(0);
  await check('and keeping them closes the question and leaves the panel open on its form, with the edits',`!${panel}.hidden&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value==='8'`);
  await click('#side-panel-amend');await wait(`!${prompt}.hidden`);
  await check('so does Close editor, which is how a person gives an amendment up',`${prompt}.querySelectorAll('button').length===2&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value==='8'`);
  await answer(1);
  await check('and discarding closes the form and keeps the panel on its position',`document.querySelector('#trade-editing').hidden&&!${panel}.hidden&&${panelReference}===${JSON.stringify(refB)}&&document.querySelector('#side-panel-amend').textContent==='Amend'`);
  await amend(1,refB);await typeInto('#trade-lots','7');
  await click('#futures-new');await wait(`!${prompt}.hidden`);
  await check('New trade over a form with unsaved edits asks first, before the page is busy, and keeping them leaves the form and the panel as they are',
    `(()=>{const buttons=[...${prompt}.querySelectorAll('button')];return buttons.length===2&&buttons.every(b=>!b.disabled)&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value==='7'&&${panel}.dataset.mode==='position'&&document.querySelector('#swap-editing').hidden;})()`);
  await answer(0);
  await click('#side-panel-close');await wait(`!${prompt}.hidden`);await answer(1);
  await wait(`${panel}.hidden&&document.activeElement.classList.contains('tabulator-row')`);
  await check('discarding what a closing panel held closes it, with its form, and gives focus back to the row it was opened from',
    `${panel}.hidden&&document.querySelector('#trade-editing').hidden&&document.activeElement.classList.contains('tabulator-row')&&document.activeElement.querySelector('.desk-reference').textContent===${JSON.stringify(refB)}`);
  await amend(1,refB);await typeInto('#trade-lots','6');
  await click('#futures-refresh');await wait(`!${prompt}.hidden`);
  await check('a new read, which closes the panel, asks first about the edits it would give up',
    `${prompt}.querySelectorAll('button').length===2&&!${panel}.hidden&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value==='6'`);
  const readsBefore=await evaluate("window.__futuresResponses.filter(r=>r.path==='/api/trading/workspace').length");
  await answer(0);await wait(idle);
  await check('keeping them means no read was made, and the panel and the form are as they were',`!${panel}.hidden&&document.querySelector('#trade-lots').value==='6'&&window.__futuresResponses.filter(r=>r.path==='/api/trading/workspace').length===${readsBefore}`);
  await click('#futures-refresh');await wait(`!${prompt}.hidden`);await answer(1);
  await wait(`${panel}.hidden&&${redrawn(idle)}`);
  await check('and discarding them lets the read happen: the panel closes with its form, and the desk is drawn again',`${panel}.hidden&&document.querySelector('#trade-editing').hidden&&document.querySelector('#error').hidden&&window.__futuresResponses.filter(r=>r.path==='/api/trading/workspace').length>${readsBefore}`);

  // Every route that would give the form up asks first, as a switch of position does: the sidebar, a new As of date, Open curve, the
  // sidebar's own Blotter and the header's time controls. A form with no edits is left by each of them without a question.
  const title="document.querySelector('#page-title').textContent",editing="!document.querySelector('#trade-editing').hidden",newForm="!document.querySelector('#futures-new-trade').hidden";
  // The browser's own question, as the page answers the event it sends before the page is left.
  const unloadAsks="(()=>{const event=new Event('beforeunload',{cancelable:true});window.dispatchEvent(event);return event.defaultPrevented;})()";
  const back=async()=>{await click('#nav-futures');await wait(`${title}==='Blotter'&&${redrawn(idle)}`);};
  const workspaceReads="window.__futuresResponses.filter(r=>r.path==='/api/trading/workspace').length";
  await amend(1,refB);
  await click('#nav-market');
  await check('the sidebar leaves a form with no edits without a question',`${prompt}.hidden&&${title}==='Curves & prices'`,`[${prompt}.hidden,${title}]`);
  await back();
  await amend(1,refB);await typeInto('#trade-lots','5');
  await click('#nav-market');await asks();
  await check('the sidebar asks before it leaves a form with edits, and nothing moves while it does',
    `${prompt}.querySelectorAll('button').length===2&&${title}==='Blotter'&&!${panel}.hidden&&${editing}&&document.querySelector('#trade-lots').value==='5'`,`[${prompt}.textContent,${title}]`);
  await answer(0);
  await check('keeping the edits leaves the page and the form as they were',`${title}==='Blotter'&&${editing}&&document.querySelector('#trade-lots').value==='5'&&!${panel}.hidden`);
  await click('#nav-market');await asks();await answer(1);await wait(`${title}==='Curves & prices'&&${idle}`);
  await check('discarding them goes to the page chosen',`${title}==='Curves & prices'`);
  await back();
  await check('and the Blotter comes back without the form: it starts fresh',`${panel}.hidden&&document.querySelector('#trade-editing').hidden`);
  // The sidebar's own Blotter, pressed on the Blotter, reads it again and closes the panel.
  await amend(1,refB);
  await click('#nav-futures');await wait(`${panel}.hidden&&${redrawn(idle)}`);
  await check('the sidebar\'s Blotter, pressed on the Blotter, reads it again without a question when the form holds no edits',`${panel}.hidden&&${prompt}.hidden&&${title}==='Blotter'`);
  await amend(1,refB);await typeInto('#trade-lots','5');
  const readsBeforeBlotter=await evaluate(workspaceReads);
  await click('#nav-futures');await asks();
  await check('and asks first when it does, before it reads anything',`${prompt}.querySelectorAll('button').length===2&&${editing}&&${workspaceReads}===${readsBeforeBlotter}&&${title}==='Blotter'`,`[${workspaceReads},${readsBeforeBlotter}]`);
  await answer(0);
  await check('and keeping them reads nothing: the panel, its form and the edits stay',`${editing}&&document.querySelector('#trade-lots').value==='5'&&${workspaceReads}===${readsBeforeBlotter}`);
  await click('#nav-futures');await asks();await answer(1);await wait(`${panel}.hidden&&${redrawn(idle)}`);
  await check('discarding them reads the Blotter again, which closes the panel with its form',`${panel}.hidden&&document.querySelector('#trade-editing').hidden&&${workspaceReads}>${readsBeforeBlotter}`);
  // A new As of date.
  const closes=await evaluate("[...document.querySelector('#snapshot').options].map(o=>o.value).filter(Boolean)"),closeNow=await evaluate("document.querySelector('#snapshot').value"),closeOther=closes.find(close=>close!==closeNow);
  if(!closeOther)throw Error('The As of checks need a second close.');
  await amend(1,refB);
  await set('#snapshot',closeOther);await wait(`${redrawn(idle)}&&${panel}.hidden`);
  await check('another As of date is taken without a question when the form holds no edits',`${prompt}.hidden&&document.querySelector('#snapshot').value===${JSON.stringify(closeOther)}`);
  await set('#snapshot',closeNow);await wait(redrawn(idle));
  await amend(1,refB);await typeInto('#trade-lots','5');
  await set('#snapshot',closeOther);await asks();
  await check('a new As of date asks before it discards a form with edits',`${prompt}.querySelectorAll('button').length===2&&${editing}&&document.querySelector('#trade-lots').value==='5'`,`[${prompt}.textContent]`);
  const readsBeforeClose=await evaluate(workspaceReads);
  await answer(0);
  await check('keeping them puts the As of date back as it was, and reads nothing',`document.querySelector('#snapshot').value===${JSON.stringify(closeNow)}&&${editing}&&document.querySelector('#trade-lots').value==='5'&&${workspaceReads}===${readsBeforeClose}`,`[document.querySelector('#snapshot').value,${workspaceReads},${readsBeforeClose}]`);
  await set('#snapshot',closeOther);await asks();await answer(1);await wait(`${panel}.hidden&&${redrawn(idle)}`);
  await check('and discarding them takes the new date: the Blotter is read at it, and the panel is closed with its form',`document.querySelector('#snapshot').value===${JSON.stringify(closeOther)}&&document.querySelector('#trade-editing').hidden&&${workspaceReads}>${readsBeforeClose}`);
  await set('#snapshot',closeNow);await wait(redrawn(idle));
  // Open sample prices selects its close without anyone choosing it from the As of control. Keeping an amendment puts the control back on the
  // close the Blotter shows, which that one is, and not on the close that was chosen before it.
  await click('#nav-market');await click('#example');
  const sampleClose=await evaluate("document.querySelector('#snapshot').value");
  const [chosenBefore,chosenAfter]=closes.filter(close=>close!==sampleClose);
  if(!chosenAfter)throw Error('The sample prices check needs three closes.');
  await set('#snapshot',chosenBefore);await wait(idle);await click('#example');
  await check('Open sample prices selects the sample close, whichever close was chosen before it',`document.querySelector('#snapshot').value===${JSON.stringify(sampleClose)}`);
  await back();
  await amend(1,refB);await typeInto('#trade-lots','5');
  await set('#snapshot',chosenAfter);await asks();
  await check('another As of date, over the sample close, asks before it discards a form with edits',`${prompt}.querySelectorAll('button').length===2&&${editing}&&document.querySelector('#trade-lots').value==='5'`,`[${prompt}.textContent]`);
  await answer(0);
  await check('and keeping them puts the control back on the close the Blotter shows, the sample\'s, and not on the one chosen before it',`document.querySelector('#snapshot').value===${JSON.stringify(sampleClose)}&&${editing}&&document.querySelector('#trade-lots').value==='5'`,`[document.querySelector('#snapshot').value,${JSON.stringify(sampleClose)},${JSON.stringify(chosenBefore)}]`);
  await click('#side-panel-amend');await asks();await answer(1);
  await set('#snapshot',closeNow);await wait(redrawn(idle));
  // Open curve.
  await amend(0,refA);
  await click('#side-panel-open-curve');
  await check('Open curve leaves a form with no edits without a question',`${prompt}.hidden&&${title}==='Curves & prices'`,`[${prompt}.hidden,${title}]`);
  await back();
  await amend(0,refA);await typeInto('#trade-lots','5');
  await click('#side-panel-open-curve');await asks();
  await check('Open curve asks before it leaves a form with edits, and keeping them leaves everything as it was',`${prompt}.querySelectorAll('button').length===2&&${title}==='Blotter'&&${editing}&&document.querySelector('#trade-lots').value==='5'`);
  await answer(0);
  await click('#side-panel-open-curve');await asks();await answer(1);await wait(`${title}==='Curves & prices'&&${idle}`);
  await check('and discarding them opens the curve',`${title}==='Curves & prices'`);
  await back();
  // The header's time controls.
  await amend(1,refB);
  await click('#date-context>summary');
  await check('the time controls open without a question when the form holds no edits, and the form stays',`${prompt}.hidden&&document.querySelector('#date-context').open&&${editing}`);
  await click('#date-context>summary');
  await typeInto('#trade-lots','5');
  await click('#date-context>summary');await asks();
  await check('opening the time controls, which change the context the Blotter is read in, asks first, and keeping the edits leaves them closed',`${prompt}.querySelectorAll('button').length===2&&!document.querySelector('#date-context').open&&${editing}`);
  await answer(0);
  await check('the form is as it was',`${editing}&&document.querySelector('#trade-lots').value==='5'&&!document.querySelector('#date-context').open`);
  await click('#date-context>summary');await asks();await answer(1);
  await check('and agreeing opens them and gives the form up',`document.querySelector('#date-context').open&&document.querySelector('#trade-editing').hidden&&${prompt}.hidden`);
  // Open already, the controls ask before each change of theirs, and put what they held back when the amendment is kept.
  const contextInputs="[document.querySelector('#date-context-on').value,document.querySelector('#date-context-view').value]",contextNote="document.querySelector('#environment-context-note').textContent";
  const inputsBefore=await evaluate(contextInputs),noteBefore=await evaluate(contextNote),otherDate=inputsBefore[0]==='2020-12-16'?'2020-12-15':'2020-12-16';
  const positionsShown="document.querySelectorAll('#desk-positions .tabulator-row').length";
  // A change of context clears the Blotter and its As of choice: this reads the working context again and chooses the close it showed.
  const restoreBlotter=async()=>{await click('#current-view-context');await set('#snapshot',closeNow);await wait(redrawn(idle));};
  await amend(1,refB);await typeInto('#trade-lots','5');
  await evaluate("document.querySelector('#date-context-on').focus()");
  await set('#date-context-on',otherDate);await asks();
  await check('a date typed in the open time controls asks before it clears the Blotter, and nothing has changed while it does',`${prompt}.querySelectorAll('button').length===2&&${editing}&&document.querySelector('#trade-lots').value==='5'&&${contextNote}===${JSON.stringify(noteBefore)}&&${positionsShown}>0`,`[${prompt}.textContent,${contextNote},${positionsShown}]`);
  await answer(0);
  await check('keeping the edits puts the controls back exactly as they were, and leaves the Blotter and the form',`JSON.stringify(${contextInputs})===${JSON.stringify(JSON.stringify(inputsBefore))}&&${contextNote}===${JSON.stringify(noteBefore)}&&document.querySelector('#date-context').open&&${editing}&&document.querySelector('#trade-lots').value==='5'&&${positionsShown}>0`,`[${contextInputs},${contextNote},${positionsShown}]`);
  // The change asks for that date's end-of-day knowledge (D217), which the page reads before anything else is done.
  await set('#date-context-on',otherDate);await asks();await answer(1);await wait(idle);
  await check('agreeing lets the change through: the context is changed, the Blotter is cleared and the form is given up',`document.querySelector('#date-context-on').value===${JSON.stringify(otherDate)}&&${contextNote}.includes('Context changed')&&document.querySelector('#trade-editing').hidden&&${positionsShown}===0`,`[${contextInputs},${contextNote},${positionsShown}]`);
  await restoreBlotter();
  // Use current knowledge changes the context as well. A date entered without an event, as a browser's autofill enters one, lets it run here.
  const knowledgeReads="window.__futuresResponses.filter(r=>r.path==='/api/date-schedules/context').length";
  await amend(1,refB);await typeInto('#trade-lots','5');
  await evaluate("document.querySelector('#date-context-on').value='2020-12-16'");
  const knowledgeBefore=await evaluate(knowledgeReads);
  await click('#date-context-latest');await asks();
  await check('Use current knowledge asks before it changes the context, and has read nothing while it does',`${prompt}.querySelectorAll('button').length===2&&${editing}&&${knowledgeReads}===${knowledgeBefore}`,`[${prompt}.textContent,${knowledgeReads},${knowledgeBefore}]`);
  await answer(0);
  await check('keeping the edits leaves the form, the date and the context as they were',`${editing}&&document.querySelector('#trade-lots').value==='5'&&document.querySelector('#date-context-on').value==='2020-12-16'&&${knowledgeReads}===${knowledgeBefore}&&${contextNote}===${JSON.stringify(noteBefore)}`);
  await click('#date-context-latest');await asks();await answer(1);await wait(idle);
  await check('and agreeing runs it: the context is read, and the form is given up',`${knowledgeReads}>${knowledgeBefore}&&document.querySelector('#trade-editing').hidden`,`[${knowledgeReads},${knowledgeBefore},${contextNote}]`);
  await restoreBlotter();
  // Return to current inputs reads the working context again.
  const currentReads="window.__futuresResponses.filter(r=>r.path==='/api/market/current').length";
  await amend(1,refB);await typeInto('#trade-lots','5');
  const currentBefore=await evaluate(currentReads);
  await click('#current-view-context');await asks();
  await check('Return to current inputs asks before it reads the working context again, and has read nothing while it does',`${prompt}.querySelectorAll('button').length===2&&${editing}&&${currentReads}===${currentBefore}`,`[${prompt}.textContent,${currentReads},${currentBefore}]`);
  await answer(0);
  await check('keeping the edits leaves the form and reads nothing',`${editing}&&document.querySelector('#trade-lots').value==='5'&&${currentReads}===${currentBefore}`);
  await click('#current-view-context');await asks();await answer(1);await wait(idle);
  await check('and agreeing reads the working context again, opens it on its newest close, and gives the form up',`${currentReads}>${currentBefore}&&document.querySelector('#trade-editing').hidden&&document.querySelector('#snapshot').value!==''&&document.querySelector('#market-date-badge').textContent==='Latest close'`,`[${currentReads},${currentBefore},document.querySelector('#snapshot').value,document.querySelector('#market-date-badge').textContent]`);
  await set('#snapshot',closeNow);await wait(redrawn(idle));
  // While the page is busy a question cannot be answered, so the controls stay closed and nothing is asked.
  await click('#close-view-context');
  await amend(1,refB);await typeInto('#trade-lots','5');await typeInto('#trade-reason','Synthetic busy check');
  await evaluate("document.querySelector('#trade-amend-form').requestSubmit();document.querySelector('#date-context>summary').click()");await wait(idle);
  await check('opening the time controls while the page is busy asks nothing and leaves them closed',`${prompt}.hidden&&!document.querySelector('#date-context').open&&${editing}&&document.querySelector('#trade-lots').value==='5'`,`[${prompt}.textContent,document.querySelector('#date-context').open]`);
  // A change made while the page is busy cannot be asked about either, so it is not made: the control goes back as it was.
  const [dateWhileBusy,noteWhileBusy]=await evaluate(`[document.querySelector('#date-context-on').value,${contextNote}]`);
  await evaluate("document.querySelector('#trade-amend-form').requestSubmit();const date=document.querySelector('#date-context-on');date.value='2020-12-16';date.dispatchEvent(new Event('input',{bubbles:true}));date.dispatchEvent(new Event('change',{bubbles:true}))");await wait(idle);
  await check('a change in the time controls while the page is busy is not made: nothing can be asked, so the control goes back and the form stays',`${prompt}.hidden&&document.querySelector('#date-context-on').value===${JSON.stringify(dateWhileBusy)}&&${contextNote}===${JSON.stringify(noteWhileBusy)}&&${editing}&&document.querySelector('#trade-lots').value==='5'`,`[${prompt}.textContent,document.querySelector('#date-context-on').value,${contextNote}]`);
  await click('#side-panel-amend');await asks();await answer(1);
  // A form with no edits is given up by a change of context without a question.
  await amend(1,refB);
  await click('#date-context>summary');
  await set('#date-context-on',otherDate);await wait(idle);
  await check('a change in the open time controls is made without a question when the form holds no edits',`${prompt}.hidden&&${contextNote}.includes('Context changed')&&document.querySelector('#trade-editing').hidden`,`[${prompt}.textContent,${contextNote}]`);
  await restoreBlotter();await click('#close-view-context');
  // New trade for a PPA leaves the Blotter for the PPA bookings page.
  await amend(1,refB);
  await startNewTrade({click,wait});await click('#new-trade-ppa');
  await check('New trade for a PPA leaves a form with no edits without a question',`${prompt}.hidden&&${title}==='PPA bookings'`,`[${prompt}.textContent,${title}]`);
  await back();
  await newTrade({click,wait},'futures');await typeInto('#futures-lots','5');
  await click('#new-trade-ppa');await asks();
  await check('New trade for a PPA, which leaves the Blotter, asks first, and keeping the edits leaves the Blotter and the new trade as they were',`${prompt}.querySelectorAll('button').length===2&&${title}==='Blotter'&&${newForm}&&document.querySelector('#futures-lots').value==='5'`,`[${prompt}.textContent,${title}]`);
  await answer(0);
  await click('#new-trade-ppa');await asks();await answer(1);await wait(idle);
  await check('and agreeing opens the PPA bookings page',`${title}==='PPA bookings'`,title);
  await back();
  await check('the Blotter comes back without the form: it starts fresh',`${panel}.hidden&&document.querySelector('#futures-new-trade').hidden&&document.querySelector('#trade-editing').hidden`);

  // The form of a new trade is the panel's too: what is typed in it is an edit like an amendment's, and a switch of product, a closing of the
  // panel and every other way out of the form ask first. A form with no edits is left at once, and a new trade starts empty each time.
  await newTrade({click,wait},'futures');
  await click('#new-trade-swaps');
  await check('a new trade with no edits is switched to the other product without a question, and the first product\'s form is gone',`${prompt}.hidden&&document.querySelector('#side-panel-title').textContent==='New swap'&&!document.querySelector('#swap-editing').hidden&&document.querySelector('#futures-new-trade').hidden&&!document.querySelector('#side-panel-form').contains(document.querySelector('#futures-form'))`);
  await typeInto('#swap-memo','Synthetic switch check');
  await check('what is typed in the new trade\'s form is an edit the browser is asked about when the page is left',unloadAsks);
  await click('#new-trade-futures');await asks();
  await check('switching Future and Swap with edits asks first, inside the panel, and nothing moves while it does',`(()=>{const buttons=[...${prompt}.querySelectorAll('button')];return buttons.map(b=>b.textContent).join()==='Keep editing,Discard changes'&&buttons.every(b=>!b.disabled)&&document.querySelector('#side-panel-title').textContent==='New swap'&&document.querySelector('#new-trade-swaps').getAttribute('aria-pressed')==='true'&&!document.querySelector('#swap-editing').hidden&&document.querySelector('#swap-memo').value==='Synthetic switch check';})()`);
  await answer(0);
  await check('keeping the edits leaves the new swap and what was typed in it as they were',`document.querySelector('#side-panel-title').textContent==='New swap'&&!document.querySelector('#swap-editing').hidden&&document.querySelector('#swap-memo').value==='Synthetic switch check'&&document.querySelector('#new-trade-swaps').getAttribute('aria-pressed')==='true'`);
  await click('#new-trade-futures');await asks();await answer(1);await wait(`document.querySelector('#side-panel-title')?.textContent==='New future'&&!document.querySelector('#futures-new-trade').hidden&&${idle}`);
  await check('discarding them opens the other product\'s form, and the swap form and what was typed in it are gone',`${prompt}.hidden&&document.querySelector('#swap-editing').hidden&&document.querySelector('#swap-memo').value===''&&document.querySelector('#new-trade-futures').getAttribute('aria-pressed')==='true'&&document.querySelector('#new-trade-swaps').getAttribute('aria-pressed')==='false'`);
  await check('and the browser is asked nothing when the page is left, because nothing is held now',`!${unloadAsks}`);
  await typeInto('#futures-lots','4');
  await click('#side-panel-close');await asks();
  await check('closing the panel with a new trade\'s edits asks first, and keeping them leaves the panel on its form',`${prompt}.querySelectorAll('button').length===2&&!${panel}.hidden&&${newForm}&&document.querySelector('#futures-lots').value==='4'`);
  await answer(0);
  await click('#futures-refresh');await asks();
  await check('a new read of the Blotter, which closes the panel, asks first about a new trade\'s edits too',`${prompt}.querySelectorAll('button').length===2&&!${panel}.hidden&&${newForm}&&document.querySelector('#futures-lots').value==='4'`);
  await answer(0);
  await click('#nav-market');await asks();
  await check('the sidebar asks before it leaves a new trade with edits, and nothing moves while it does',`${prompt}.querySelectorAll('button').length===2&&${title}==='Blotter'&&${newForm}&&document.querySelector('#futures-lots').value==='4'`,`[${prompt}.textContent,${title}]`);
  await answer(1);await wait(`${title}==='Curves & prices'&&${idle}`);
  await back();
  await check('the Blotter comes back without the new trade, which is given up whole: no panel, no form and nothing for the browser to ask about',`${panel}.hidden&&document.querySelector('#futures-new-trade').hidden&&!${unloadAsks}`);
  await newTrade({click,wait},'futures');
  await check('a new trade starts empty, with none of what the one before held',`document.querySelector('#futures-lots').value===''&&!document.querySelector('#futures-contract').value&&!document.querySelector('#futures-book').value&&!document.querySelector('#futures-execution').value`);
  await click('#side-panel-close');await asks();await answer(1);await wait(`${panel}.hidden&&${idle}`);

  // Escape inside a calendar closes only the calendar, and travel along the tabs is not an opening of the form: focus stays on the tab.
  await amend(0,refA);
  await click('#trade-contract-date');
  await evaluate("document.querySelector('#trade-contract-date').focus()");await press('Escape','Escape',27);
  await check('Escape inside a calendar closes only the calendar: the panel and its form stay open',`!document.querySelector('#trade-contract-date').parentElement.open&&!${panel}.hidden&&${editing}&&${prompt}.hidden`);
  const editorReadsBeforeReload=await evaluate(editorReads);
  await click('#trade-reload');await wait(idle);
  await check('Reload latest revision reloads a form with no edits without a question: the editor is read again, and nothing is asked',`${prompt}.hidden&&${editing}&&${editorReads}>${editorReadsBeforeReload}`,`[${prompt}.textContent,${editorReads},${editorReadsBeforeReload}]`);
  await amend(0,refA);await typeInto('#trade-lots','17');
  await click('#side-panel-tabs-curve');await evaluate("document.querySelector('#side-panel-tabs-curve').focus()");await press('ArrowRight','ArrowRight',39);
  await check('arrow keys travelling to Terms leave focus on the tab, and the form keeps what it held',`document.activeElement.id==='side-panel-tabs-terms'&&!document.querySelector('#side-panel-form').hidden&&document.querySelector('#trade-lots').value==='17'`,`[document.activeElement.id,document.querySelector('#trade-lots').value]`);
  await click('#trade-reload');await asks();
  await check('Reload latest revision, which replaces what the form holds, asks first as every other route does',`${prompt}.querySelectorAll('button').length===2&&${editing}&&document.querySelector('#trade-lots').value==='17'`,`[${prompt}.textContent,document.querySelector('#trade-lots').value]`);
  await answer(0);
  await check('keeping the edits leaves the form as it was',`${editing}&&document.querySelector('#trade-lots').value==='17'`);
  await click('#trade-reload');await asks();await answer(1);await wait(idle);
  await check('and discarding them reloads the revision: the form shows the position\'s lots again',`${editing}&&document.querySelector('#trade-lots').value!=='17'`,`document.querySelector('#trade-lots').value`);
  await click('#side-panel-close');await wait(`${panel}.hidden`);

  // Leaving the page altogether, by a reload, closing the tab or going back, is asked about by the browser while the form holds edits: its
  // question is the page's answer to an event the browser sends, and the check sends the event.
  await amend(1,refB);
  await check('leaving the page asks nothing of the browser while the form holds no edits',`!${unloadAsks}`);
  await typeInto('#trade-lots','5');
  await check('and asks it to confirm while the form holds edits',unloadAsks);
  await click('#side-panel-amend');await asks();await answer(1);
  await check('and no longer once they are given up',`!${unloadAsks}`);
  // Save preferences reads the Blotter again, which closes the panel.
  const savePreferences=async()=>{await click('#profile-open');await evaluate("document.querySelector('#preferences-form').requestSubmit()");};
  await amend(1,refB);
  const readsBeforeNoEdits=await evaluate(workspaceReads);
  await savePreferences();await wait(`${redrawn(idle)}&&${workspaceReads}>${readsBeforeNoEdits}`);
  await check('Save preferences reads the Blotter again without a question when the form holds no edits',`${prompt}.hidden&&${workspaceReads}>${readsBeforeNoEdits}&&!document.querySelector('#profile-dialog').open`);
  await amend(1,refB);await typeInto('#trade-lots','5');
  const readsBeforePreferences=await evaluate(workspaceReads);
  await savePreferences();await asks();
  await check('Save preferences asks before it reads the Blotter again over a form with edits, and the dialog has made way for the question',`${prompt}.querySelectorAll('button').length===2&&!document.querySelector('#profile-dialog').open&&${editing}&&document.querySelector('#trade-lots').value==='5'&&${workspaceReads}===${readsBeforePreferences}`,`[${prompt}.textContent,document.querySelector('#profile-dialog').open,${workspaceReads},${readsBeforePreferences}]`);
  await answer(0);
  await check('keeping the edits saves nothing, says so, and leaves the form',`${editing}&&document.querySelector('#trade-lots').value==='5'&&${workspaceReads}===${readsBeforePreferences}&&!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes('Your preferences were not saved')`,`[document.querySelector('#notice').hidden,document.querySelector('#notice').textContent]`);
  await savePreferences();await asks();await answer(1);await wait(`${redrawn(idle)}&&${workspaceReads}>${readsBeforePreferences}`);
  await check('and agreeing saves them: the Blotter is read again, and the form is given up',`${workspaceReads}>${readsBeforePreferences}&&document.querySelector('#trade-editing').hidden&&!document.querySelector('#profile-dialog').open`);
  // A newer close arriving while a form is open is offered, and taking it asks first. The calendar's newest date is held back until the window is
  // focused again, as a close that becomes available during a session is.
  await evaluate(`(()=>{const send=window.fetch;window.fetch=async(input,init)=>{const path=typeof input==='string'?input:input.url;let response=await send(input,init);
    if(window.__hideNewestDate&&path.endsWith('/market/dates')&&response.ok&&JSON.parse(init.body).curve===null){const data=await response.json();data.dates.pop();data.maxDate=data.dates.at(-1)?.date??null;response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}
    return response;};})()`);
  await evaluate('window.__hideNewestDate=true');await persona('trader');await wait(redrawn(idle));
  const olderClose=await evaluate("document.querySelector('#snapshot').value");
  // A close chosen in the calendar waits for the person to agree to what choosing it gives up. Until they do, the control that every read and command
  // takes its close from, the calendar and the badge stay on the close the Blotter shows, and so does what the form sends while the question is open.
  await evaluate(`(()=>{const send=window.fetch;window.__snapshotsSent=[];window.fetch=async(input,init)=>{const path=typeof input==='string'?input:input.url;try{const body=JSON.parse(init?.body??'null');if(body?.snapshotId)window.__snapshotsSent.push([path,body.snapshotId]);}catch{}return send(input,init);};})()`);
  const closeLabel="document.querySelector('#market-date-open').textContent",labelBefore=await evaluate(closeLabel);
  const chosenClose=closes.find(close=>close!==olderClose&&close!==closeNow);
  const chosenDate=await evaluate(`window.__futuresResponses.filter(r=>r.path==='/api/market/dates').at(-1).data.dates.find(group=>group.frames.some(frame=>frame.id===${JSON.stringify(chosenClose)})).date`);
  await amend(1,refB);await typeInto('#trade-lots','5');
  const readsBeforeChoice=await evaluate(workspaceReads);
  await click('#market-date-open');await click(`#market-date-grid button[data-date="${chosenDate}"]`);await asks();
  // The calendar closes as the close is chosen, and its closing comes after the question has taken the focus: the question keeps it.
  await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
  await check('the question a close chosen in the calendar asks keeps the focus once the calendar has closed',`${prompt}.contains(document.activeElement)&&!document.querySelector('#market-date-dialog').open`,
    '[document.activeElement?.id,document.activeElement?.textContent,document.querySelector(\'#market-date-dialog\').open]');
  await check('a close chosen in the calendar asks before it is applied, and the control, the calendar and the Blotter are on the close they showed while it does',`${prompt}.querySelectorAll('button').length===2&&document.querySelector('#snapshot').value===${JSON.stringify(olderClose)}&&${closeLabel}===${JSON.stringify(labelBefore)}&&${editing}&&document.querySelector('#trade-lots').value==='5'&&${workspaceReads}===${readsBeforeChoice}`,`[${prompt}.textContent,document.querySelector('#snapshot').value,${JSON.stringify(olderClose)},${closeLabel},${JSON.stringify(labelBefore)},${workspaceReads},${readsBeforeChoice}]`);
  await typeInto('#trade-reason','Synthetic close question');
  await evaluate("document.querySelector('#trade-amend-form').requestSubmit()");await wait(idle);
  await check('what the form sends while the question is open is sent at the close the Blotter shows, and not at the one waiting to be agreed to',`window.__snapshotsSent.some(([path])=>path==='/api/futures/amend-preview')&&window.__snapshotsSent.every(([,id])=>id===${JSON.stringify(olderClose)})`,'JSON.stringify(window.__snapshotsSent)');
  await evaluate("window.__hideNewestDate=false;window.dispatchEvent(new Event('focus'))");
  await wait("!!document.querySelector('#market-date-badge button')");
  await check('a newer close found while the question is open is offered against the close the Blotter shows, and not against the one waiting to be agreed to',`!!document.querySelector('#market-date-badge button')&&document.querySelector('#snapshot').value===${JSON.stringify(olderClose)}`,`[document.querySelector('#market-date-badge').textContent,document.querySelector('#snapshot').value]`);
  await answer(0);
  await check('and keeping the amendment leaves the control, the calendar, the Blotter and the offer as they were',`document.querySelector('#snapshot').value===${JSON.stringify(olderClose)}&&${closeLabel}===${JSON.stringify(labelBefore)}&&${editing}&&document.querySelector('#trade-lots').value==='5'&&${workspaceReads}===${readsBeforeChoice}&&!!document.querySelector('#market-date-badge button')`,`[document.querySelector('#snapshot').value,${closeLabel},${workspaceReads},${readsBeforeChoice},document.querySelector('#market-date-badge').textContent]`);
  // The offer is judged by the close the Blotter shows, so the next check of the calendar still finds it due.
  const dateReads="window.__futuresResponses.filter(r=>r.path==='/api/market/dates').length",dateReadsBefore=await evaluate(dateReads);
  await evaluate("window.dispatchEvent(new Event('focus'))");
  await wait(`${dateReads}>${dateReadsBefore}`);await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
  await check('and the next check of the calendar still finds the newer close due: the offer is judged by the close the Blotter shows',`!!document.querySelector('#market-date-badge button')&&document.querySelector('#snapshot').value===${JSON.stringify(olderClose)}`,`[document.querySelector('#market-date-badge').textContent,document.querySelector('#snapshot').value]`);
  await amend(1,refB);await typeInto('#trade-lots','5');
  await click('#market-date-badge button');await asks();
  await check('Newer close available asks before it changes the close, and the close is the one it was',`${prompt}.querySelectorAll('button').length===2&&${editing}&&document.querySelector('#snapshot').value===${JSON.stringify(olderClose)}&&document.querySelector('#trade-lots').value==='5'`,`[${prompt}.textContent,document.querySelector('#snapshot').value,${JSON.stringify(olderClose)}]`);
  await answer(0);
  await check('keeping the edits leaves the close, the form and the offer as they were',`${editing}&&document.querySelector('#snapshot').value===${JSON.stringify(olderClose)}&&document.querySelector('#trade-lots').value==='5'&&!!document.querySelector('#market-date-badge button')`);
  await click('#market-date-badge button');await asks();await answer(1);await wait(redrawn(idle));
  await check('and agreeing selects the newest close and gives the form up',`document.querySelector('#snapshot').value===${JSON.stringify(closeNow)}&&document.querySelector('#trade-editing').hidden`,`[document.querySelector('#snapshot').value,${JSON.stringify(closeNow)}]`);
  // Another user is another workspace: the profile dialog makes way for the question, as it does for Save preferences.
  const userName="document.querySelector('#profile-label').textContent",profileCalls="window.__futuresResponses.filter(r=>r.path==='/api/profile').length";
  const userBefore=await evaluate(userName),profilesBefore=await evaluate(profileCalls);
  await amend(1,refB);await typeInto('#trade-lots','5');
  await click('#profile-open');await set('#profile','risk');await click('#open');await asks();
  await check('switching to another user asks before it discards a form with edits, and the dialog has made way for the question',`${prompt}.querySelectorAll('button').length===2&&!document.querySelector('#profile-dialog').open&&${userName}===${JSON.stringify(userBefore)}&&${editing}&&${profileCalls}===${profilesBefore}`,`[${prompt}.textContent,${userName},${profileCalls},${profilesBefore}]`);
  await answer(0);
  await check('keeping the edits leaves the user and the form as they were, and says that the user was not switched',`${userName}===${JSON.stringify(userBefore)}&&${editing}&&document.querySelector('#trade-lots').value==='5'&&${profileCalls}===${profilesBefore}&&!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes('The user was not switched')`,`[document.querySelector('#notice').hidden,document.querySelector('#notice').textContent]`);
  // Sign out, which the preview hides, asks the same way: its button answers a click as any other does.
  await click('#profile-open');await evaluate("document.querySelector('#sign-out').click()");await asks();
  await check('signing out asks before it ends the session over a form with edits, and the dialog has made way for the question',`${prompt}.querySelectorAll('button').length===2&&!document.querySelector('#profile-dialog').open&&${editing}&&document.querySelector('#trade-lots').value==='5'`,`[${prompt}.textContent,document.querySelector('#profile-dialog').open]`);
  await answer(0);
  await check('keeping the edits ends nothing, says that nobody was signed out, and leaves the form',`${editing}&&document.querySelector('#trade-lots').value==='5'&&!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes('You were not signed out')&&location.pathname!=='/auth'`,`[document.querySelector('#notice').textContent,location.pathname]`);
  await click('#profile-open');await set('#profile','risk');await click('#open');await asks();await answer(1);await wait(idle);
  await check('and agreeing switches to the other user and gives the form up',`${userName}!==${JSON.stringify(userBefore)}&&${profileCalls}>${profilesBefore}&&document.querySelector('#trade-editing').hidden`,`[${userName},${profileCalls},${profilesBefore}]`);
  await persona('trader');await wait(redrawn(idle));

  // A phone: the sheet fills the window, the page does not scroll sideways because of it, and nothing behind it can take focus.
  await settle(760,900);
  const phoneWidth=await evaluate('document.documentElement.scrollWidth');
  await open('fut');
  await check('at 760 the panel is a full-width sheet and the page does not scroll sideways',
    `${panel}.dataset.placement==='overlay'&&Math.round(${panel}.getBoundingClientRect().left)===0&&Math.round(${panel}.getBoundingClientRect().width)===innerWidth&&document.documentElement.scrollWidth<=document.documentElement.clientWidth+1&&document.documentElement.scrollWidth===${phoneWidth}`,
    `[${panel}.getBoundingClientRect().width,innerWidth,document.documentElement.scrollWidth,${phoneWidth}]`);
  const behind="['#futures-refresh','#nav-futures','#page-title']";
  await check('the sheet contains focus: the page behind it is inert and takes none, and its own controls do',
    `(()=>{const nodes=${behind}.map(s=>document.querySelector(s));nodes.forEach(n=>n.focus());const amend=document.querySelector('#side-panel-amend');amend.focus();return nodes.every(n=>n.closest('[inert]')!==null)&&!${panel}.closest('[inert]')&&document.activeElement===amend;})()`,
    `[${behind}.map(s=>!!document.querySelector(s).closest('[inert]')),document.activeElement.id]`);
  await click('#side-panel-amend');await wait("!document.querySelector('#trade-editing').hidden");
  await check('Amend opens the form in the sheet, whose own controls take focus and are not inert, with the page still out of reach behind it',
    `(()=>{const form=document.querySelector('#side-panel-form');return !form.hidden&&!form.closest('[inert]')&&form.contains(document.activeElement)&&document.querySelector('#futures-refresh').closest('[inert]')!==null;})()`);
  await click('#side-panel-amend');
  await check('and when the form closes the sheet is still open, still containing focus',`!${panel}.hidden&&document.querySelector('#futures-refresh').closest('[inert]')!==null`);
  await click('#side-panel-close');
  await wait(`${panel}.hidden`);
  await check('closing the sheet gives the page back',`document.querySelectorAll('[inert]').length===0`,`document.querySelectorAll('[inert]').length`);
  await settle(1100,900);await open('fut');
  await check('a drawer that leaves the page usable beside it makes none of it inert',`${panel}.dataset.placement==='overlay'&&document.querySelectorAll('[inert]').length===0`);
  await press('Escape','Escape',27);
  await wait(`${panel}.hidden`);
  await settle(760,900);await open('fut');
  await click('#side-panel-open-curve');
  await wait("document.querySelector('#nav-market').classList.contains('active')&&!document.querySelector('#history-card').hidden");
  await check('leaving the Blotter from the sheet leaves the page it opens usable, not inert',`document.querySelectorAll('[inert]').length===0&&!document.querySelector('#nav-market').closest('[inert]')`,`document.querySelectorAll('[inert]').length`);
  await click('#nav-futures');
  await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')");
  await check('and the Blotter, read again, has no panel and nothing inert',`${panel}.hidden&&document.querySelectorAll('[inert]').length===0`);
  await settle(1440,1050);
}
