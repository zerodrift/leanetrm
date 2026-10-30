/** D220 at Dec 31, 2020, a close that values some positions and not others: every total says what it leaves out. */
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
  const menu=async id=>{if(!await evaluate(`document.querySelector(${JSON.stringify('#'+id)}).open`))await click('#'+id+'>summary');};
  const loaded="document.querySelector('#desk-positions .tabulator-row')&&!document.querySelector('#blotter-export').disabled";
  // The desk's rows and summaries come from the workspace read; a pivot's chart reads its own pivot.
  const read="window.__replies.filter(r=>r.path==='/api/trading/workspace').at(-1)?.data";
  const table=read+".blotter";
  const chart="window.__replies.filter(r=>r.path==='/api/trading/blotter').at(-1)?.data";
  const note=count=>`(${count}===1?"Excludes 1 position that can't be valued at this close":"Excludes "+${count}+" positions that can't be valued at this close")`;
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  await evaluate(`(()=>{const send=window.fetch;window.__replies=[];window.fetch=async(input,init)=>{const path=typeof input==='string'?input:input.url,response=await send(input,init);
    try{window.__replies.push({path,data:await response.clone().json()});}catch{}return response;};
    const create=URL.createObjectURL;URL.createObjectURL=b=>{window.__blotterCsv=b;return create(b);};
    const click=HTMLAnchorElement.prototype.click;HTMLAnchorElement.prototype.click=function(){if(this.download==='leanetrm-blotter.csv')return;return click.call(this);};})()`);
  await persona('trader');await wait(loaded);
  await check('the Blotter opens on Dec 31, 2020, a close that values some positions and not others',
    `(()=>{const r=${table};return r.asOf.startsWith('2020-12-31')&&r.unvaluedPositions>0&&r.holdings.some(h=>h.unavailableReason===null)&&document.querySelector('#error').hidden;})()`,table+'.asOf');
  await check('an unvalued position shows the Not valued chip with its reason, and blank value cells, never zero',
    "(()=>{const cell=[...document.querySelectorAll('#desk-positions .desk-unvalued')].find(c=>c.textContent.includes('Contract expired · awaiting settlement')),row=cell?.closest('.tabulator-row');return !!cell&&cell.querySelector('.desk-chip').textContent==='Not valued'&&!cell.querySelector('.desk-figure')&&row.querySelector('[tabulator-field=c4]').textContent==='';})()");
  // The side panel says the same in place of a value, and has no mark minus price for a position with no mark.
  await click("#desk-positions .tabulator-row:not(.tabulator-group):has(.desk-unvalued)");
  await wait("document.querySelector('#position-details')?.hidden===false");
  await check('an unvalued position opens in the side panel with the same reason in place of its value, and no mark minus price',
    "(()=>{const cell=document.querySelector('#desk-positions .open-row .desk-unvalued'),figure=document.querySelector('#position-details .side-panel-figure');return !!cell&&figure.querySelector('.desk-chip').textContent==='Not valued'&&figure.querySelector('#position-details .side-panel-reason').textContent===cell.querySelector('.desk-sub').textContent&&!figure.querySelector('.kpi-value')&&!document.querySelector('#position-details .side-panel-delta');})()",
    "document.querySelector('#position-details-head').innerText");
  await click('#position-details .position-close');
  // Each tile states the positions its own total leaves out, from its own backend group, never a sum of other counts.
  const own=type=>`${read}.summaries[0].groups.find(g=>g.instrumentType==='${type}').unvaluedPositions`;
  await check('each money tile notes the positions its own total leaves out, and the position count includes them, flagged',
    `(()=>{const tileNote=key=>document.querySelector('#desk-tile-'+key+' > .desk-tile-note')?.textContent;return ${own('future')}>0&&${own('financial_swap')}>0&&tileNote('futuresPnl')===${note(own('future'))}&&tileNote('swapPv')===${note(own('financial_swap'))}&&document.querySelector('#desk-tile-positions .kpi-value').firstChild.textContent===String(${table}.holdings.length)&&tileNote('positions')==='Includes '+${table}.unvaluedPositions+' positions not valued at this close';})()`,
    "[...document.querySelectorAll('.desk-tile')].map(t=>t.innerText)");
  await check('a book subtotal that leaves positions out says how many',
    "[...document.querySelectorAll('#desk-positions .tabulator-group')].some(h=>/\\d+ not valued/.test(h.querySelector('.desk-group-totals').textContent))");
  await check('the swap PV chart notes what it leaves out, and the long and short chart, which counts booked quantity, claims nothing',
    `document.querySelector('#desk-chart-pv .chart-toolbar').textContent.includes(${note(own('financial_swap'))})&&!document.querySelector('#desk-chart-position').textContent.includes('Excludes')`);
  await wait("document.querySelectorAll('#desk-positions .sparkline-loading').length===0");
  await check('a curve the close cannot shape stays a quiet tooltip in its own row, and never blanks the positions',
    "(()=>{const rows=document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)'),lines=document.querySelectorAll('#desk-positions svg.sparkline');return rows.length>0&&lines.length===rows.length&&[...document.querySelectorAll('#desk-positions svg.sparkline-unavailable title')].some(t=>t.textContent.startsWith('Curve shape unavailable'));})()");
  await click('#blotter-export');await wait('!!window.__blotterCsv');
  await check('the export states its grouping, the positions it cannot value and why each value is missing',
    `(async()=>{const s=await window.__blotterCsv.text();return s.includes('"Grouping","Book"')&&s.includes('"Positions not valued at this close","'+${table}.unvaluedPositions+'"')&&s.includes('"Not valued · Contract expired · awaiting settlement"')&&s.includes('"Not valued · Missing price at this close"');})()`);
  await menu('blotter-pivot');await set('#blotter-mode','pivot');await click('#blotter-dimensions input[value="book"]');
  for(const measure of ['presentValue','executionPnl','tradeCount'])await click(`#blotter-measures input[value="${measure}"]`);
  await click('#blotter-apply');await wait(`${table}?.view.mode==='pivot'&&${loaded}`);
  const counts=`(()=>{const header=[...document.querySelectorAll('#desk-positions .tabulator-col')].find(c=>c.textContent.trim()==='Not valued');if(!header)return null;
    return [...document.querySelectorAll('#desk-positions .tabulator-row [tabulator-field='+header.getAttribute('tabulator-field')+']')].map(c=>c.textContent.trim()).sort().join();})()`;
  await check('each pivot subtotal shows how many positions it leaves out',
    `${counts}===${table}.groups.map(g=>String(g.unvaluedPositions)).sort().join()&&${table}.groups.some(g=>g.unvaluedPositions>0)`,counts);
  // A pivot keeps its configurable chart.
  await menu('blotter-chart-settings');await set('#blotter-chart-measure','executionPnl');await click('#blotter-show-chart');await click('#blotter-chart-apply');
  await wait(`!document.querySelector('#blotter-chart-card').hidden&&${chart}?.view.measures[0]==='executionPnl'&&${loaded}`);
  await check('a pivot chart of a valuation measure notes the positions it leaves out',
    `document.querySelector('#blotter-chart-scope').textContent.endsWith(${note(chart+'.unvaluedPositions')})`,"document.querySelector('#blotter-chart-scope').textContent");
  await menu('blotter-chart-settings');await set('#blotter-chart-measure','quantity');await click('#blotter-chart-apply');
  await wait(`${chart}?.view.measures[0]==='quantity'&&${loaded}`);
  await check('a quantity chart claims no exclusion, because booked quantity counts every position',"!document.querySelector('#blotter-chart-scope').textContent.includes('Excludes')");
  await menu('blotter-settings');await set('#blotter-name','Not valued pivot');await click('#blotter-save');await wait(loaded);
  await check('a view saved with the count column shown saves no layout for it',
    "(()=>{const saved=window.__replies.filter(r=>r.path==='/api/table-views/blotter'&&r.data.layout).at(-1)?.data.layout;return document.querySelector('#error').hidden&&saved?.name==='Not valued pivot'&&!JSON.stringify(saved).includes('unvaluedPositions');})()");
  await menu('blotter-pivot');await click('#blotter-dimensions input[value="market"]');await click('#blotter-pivot button[aria-label="Move Book to columns"]');await click('#blotter-apply');
  await wait(`${table}?.view.groupBy.length===2&&${loaded}`);
  await check('a cross-tab shows the count beside each column group',
    "document.querySelector('#blotter-table-title').textContent==='Pivot table'&&[...document.querySelectorAll('#desk-positions .tabulator-col-title')].filter(t=>t.textContent.trim()==='Not valued').length===2");
  // The risk report says which positions this close cannot value, each with its reason, and a run's results say they leave them out.
  await persona('risk');await wait(loaded+"&&!document.querySelector('#desk-risk-run').disabled");
  await click('#desk-risk-run');await wait("document.querySelector('#side-panel').dataset.mode==='risk'&&!document.querySelector('#risk-fields').disabled&&"+idle);
  const catalog="window.__replies.filter(r=>r.path==='/api/portfolio/risk/catalog').at(-1).data";
  await check('the risk report lists the positions this close cannot value, each with its reason, which a run leaves out',
    `(()=>{const left=${catalog}.excludedPositions,shown=document.querySelector('#risk-excluded');return left.length>1&&!shown.hidden&&shown.textContent.startsWith('This close cannot value '+left.length+' positions, so a run leaves them out: ')&&left.every(e=>shown.textContent.includes(e.tradeReference+' ('))&&shown.textContent.includes('Contract expired · awaiting settlement');})()`,
    "document.querySelector('#risk-excluded').textContent");
  await click('input[name=risk-choice][value=custom]');
  await set('#risk-factor',await evaluate(`${catalog}.scenarioTargets.find(t=>t.kind==='contract').id`));await set('#risk-bump','0.1');
  await submit('#risk-form',60000);await wait("!document.querySelector('#desk-run').hidden&&document.querySelector('#desk-lens [data-lens=risk]').getAttribute('aria-pressed')==='true'&&"+idle,60000);
  await check('the run bar says which positions the run leaves out, as the close cannot value them',
    `(()=>{const left=${catalog}.excludedPositions,line=[...document.querySelectorAll('#desk-run-notes li')].find(p=>p.textContent.startsWith('Leaves out '));return !!line&&line.textContent.startsWith('Leaves out '+left.length+' positions this close cannot value: ')&&left.every(e=>line.textContent.includes(e.tradeReference+' ('));})()`,
    "[...document.querySelectorAll('#desk-run-notes li')].map(p=>p.textContent)");
  await check('the Risk lens marks each position the run leaves out with the reason its own row states',
    `(()=>{const left=${catalog}.excludedPositions,rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.querySelector('[tabulator-field=c7] .desk-chip')?.textContent==='Left out');return rows.length===left.length&&rows.every(r=>left.some(e=>e.tradeReference===r.querySelector('.desk-reference').textContent))&&rows.some(r=>r.querySelector('[tabulator-field=c7] .desk-sub').textContent==='Contract expired · awaiting settlement')&&rows.every(r=>r.querySelector('[tabulator-field=c8]').textContent==='');})()`);
  await check('a book\'s header in the Risk lens says how many of its positions the run leaves out, by the book the run names for each',
    `(()=>{const left=${catalog}.excludedPositions,books=new Map(${table}.holdings.map(h=>[h.book,h.bookId])),headers=[...document.querySelectorAll('#desk-positions .tabulator-group')];return headers.length>0&&headers.every(h=>{const count=left.filter(e=>e.bookId===books.get(h.querySelector('.desk-group-label').textContent)).length,said=h.querySelector('.desk-group-left');return count?said?.textContent===' · '+count+' left out of this run':!said;})&&headers.some(h=>h.querySelector('.desk-group-left'));})()`,
    "[...document.querySelectorAll('#desk-positions .tabulator-group')].map(h=>h.textContent)");
  await check('the risk tile and the change chart say how many positions the run leaves out',
    `(()=>{const left=${catalog}.excludedPositions.length;return document.querySelector('#desk-tile-risk .desk-tile-note')?.textContent==='Leaves out '+left+' positions this close cannot value'&&[...document.querySelectorAll('#desk-chart-position .desk-chart-note')].some(p=>p.textContent===${note(`${catalog}.excludedPositions.length`)});})()`,
    "[document.querySelector('#desk-tile-risk').textContent,document.querySelector('#desk-chart-position').textContent]");
}
