/** D213 at Dec 31, 2020, a close that values some positions and not others: every total says what it leaves out. */
export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
  const menu=async id=>{if(!await evaluate(`document.querySelector(${JSON.stringify('#'+id)}).open`))await click('#'+id+'>summary');};
  const loaded="document.querySelector('#futures-holdings .tabulator-row')&&!document.querySelector('#blotter-export').disabled";
  // The table's report comes from the workspace read; the chart reads its own pivot.
  const table="window.__replies.filter(r=>r.path==='/api/trading/workspace').at(-1)?.data.blotter";
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
  await check('an unvalued position says why in its value cells',
    "[...document.querySelectorAll('#futures-holdings .tabulator-cell')].some(c=>c.textContent.trim()==='Not valued · Contract expired · awaiting settlement')");
  await check('the summary notes how many positions its totals leave out',`document.querySelector('#blotter-summary-note').textContent===${note(table+'.unvaluedPositions')}`);
  await click('#blotter-export');await wait('!!window.__blotterCsv');
  await check('the export states the positions it cannot value and why each value is missing',
    `(async()=>{const s=await window.__blotterCsv.text();return s.includes('"Positions not valued at this close","'+${table}.unvaluedPositions+'"')&&s.includes('"Not valued · Contract expired · awaiting settlement"')&&s.includes('"Not valued · Missing price at this close"');})()`);
  await menu('blotter-chart-settings');await set('#blotter-chart-measure','executionPnl');await click('#blotter-show-chart');await click('#blotter-chart-apply');
  await wait(`!document.querySelector('#blotter-chart-card').hidden&&${chart}?.view.measures[0]==='executionPnl'&&${loaded}`);
  await check('a chart of a valuation measure notes the positions it leaves out',
    `document.querySelector('#blotter-chart-scope').textContent.endsWith(${note(chart+'.unvaluedPositions')})`,"document.querySelector('#blotter-chart-scope').textContent");
  await menu('blotter-chart-settings');await set('#blotter-chart-measure','quantity');await click('#blotter-chart-apply');
  await wait(`${chart}?.view.measures[0]==='quantity'&&${loaded}`);
  await check('a quantity chart claims no exclusion, because booked quantity counts every position',"!document.querySelector('#blotter-chart-scope').textContent.includes('Excludes')");
  await menu('blotter-pivot');await set('#blotter-mode','pivot');await click('#blotter-dimensions input[value="book"]');
  for(const measure of ['presentValue','executionPnl','tradeCount'])await click(`#blotter-measures input[value="${measure}"]`);
  await click('#blotter-apply');await wait(`${table}?.view.mode==='pivot'&&${loaded}`);
  const counts=`(()=>{const header=[...document.querySelectorAll('#futures-holdings .tabulator-col')].find(c=>c.textContent.trim()==='Not valued');if(!header)return null;
    return [...document.querySelectorAll('#futures-holdings .tabulator-row [tabulator-field='+header.getAttribute('tabulator-field')+']')].map(c=>c.textContent.trim()).sort().join();})()`;
  await check('each pivot subtotal shows how many positions it leaves out',
    `${counts}===${table}.groups.map(g=>String(g.unvaluedPositions)).sort().join()&&${table}.groups.some(g=>g.unvaluedPositions>0)`,counts);
  await menu('blotter-settings');await set('#blotter-name','Not valued pivot');await click('#blotter-save');await wait(loaded);
  await check('a view saved with the count column shown saves no layout for it',
    "(()=>{const saved=window.__replies.filter(r=>r.path==='/api/table-views/blotter'&&r.data.layout).at(-1)?.data.layout;return document.querySelector('#error').hidden&&saved?.name==='Not valued pivot'&&!JSON.stringify(saved).includes('unvaluedPositions');})()");
  await menu('blotter-pivot');await click('#blotter-dimensions input[value="market"]');await click('#blotter-pivot button[aria-label="Move Book to columns"]');await click('#blotter-apply');
  await wait(`${table}?.view.groupBy.length===2&&${loaded}`);
  await check('a cross-tab shows the count beside each column group',
    "document.querySelector('#blotter-table-title').textContent==='Pivot table'&&[...document.querySelectorAll('#futures-holdings .tabulator-col-title')].filter(t=>t.textContent.trim()==='Not valued').length===2");
}
