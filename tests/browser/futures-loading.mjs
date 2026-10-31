import {chooseProduct,newTrade,startNewTrade} from './new-trade.mjs';
export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
  const menu=async id=>{if(!await evaluate(`document.querySelector(${JSON.stringify('#'+id)}).open`))await click('#'+id+'>summary');};
  // Something on the page a person can see: not hidden, and drawn.
  const shown=selector=>`(()=>{const n=document.querySelector(${JSON.stringify(selector)});return !!n&&!n.hidden&&n.getClientRects().length>0;})()`;
  const valuedRows="document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)').length";
  const strip="[...document.querySelectorAll('#blotter-strip .strip-item')].map(i=>({label:i.querySelector('.strip-label').textContent,value:i.querySelector('.strip-value').textContent,state:i.dataset.state,reason:i.querySelector('.strip-reason')?.textContent??null,flag:i.querySelector('.strip-stale')?.textContent??null}))";
  const moneyTotals=['Swap PV','Futures execution P&L','Heat-rate PV','PPA value excluding overdue cash'];
  // The person's place in the rows: the selected row, the grid's scroll offset and the first row in its view.
  const place="(()=>{const h=document.querySelector('#desk-positions .tabulator-tableholder'),top=h.getBoundingClientRect().top;return {selected:document.querySelector('#desk-positions .selected-row')?.dataset.rowKey??null,scroll:h.scrollTop,first:[...h.querySelectorAll('.tabulator-row[data-row-key]')].find(r=>r.getBoundingClientRect().bottom>top+1)?.dataset.rowKey??null};})()";
  // The toolbar a failed read leaves usable: book scope, the As-of date, New trade, Refresh and Retry.
  const toolbarUsable="!document.querySelector('#blotter-scope').inert&&!document.querySelector('#snapshot').disabled&&!document.querySelector('#futures-new').disabled&&!document.querySelector('#futures-refresh').disabled&&!document.querySelector('#blotter-retry').disabled";
  const notOffered="document.querySelector('#desk-positions [role=grid]')?.getAttribute('aria-disabled')==='true'&&getComputedStyle(document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')).cursor==='default'";
  // A panel left on the choice of a product: asking for one, with neither chosen, and with no form of either in it.
  const choice="(()=>{const panel=document.querySelector('#position-details');return !panel.hidden&&panel.dataset.mode==='new'&&panel.getAttribute('aria-label')==='New trade'&&[...document.querySelectorAll('#new-trade-product button')].every(b=>b.getAttribute('aria-pressed')==='false')&&document.querySelector('#position-details-form').hidden&&document.querySelector('#futures-new-trade').hidden&&document.querySelector('#swap-editing').hidden&&document.querySelector('#position-details-view').textContent.includes('Choose an instrument type');})()";
  // Wide enough that the positions and the details show side by side, as the place checks need.
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});await command('Page.navigate',{url});
  await wait(idle+"&&document.querySelectorAll('.curve-button').length>0");
  await evaluate(`(()=>{const send=window.fetch;window.__loadingOriginalFetch=send;window.__loadingReads=[];window.fetch=async(input,init)=>{
    if(String(input)==='/api/trading/positions'){if(window.__holdFacts){window.__holdFacts=false;await new Promise(resolve=>{window.__releaseFacts=resolve;});}const response=await send(input,init);window.__loadingFacts=await response.clone().json();return response;}
    if(String(input)==='/api/trading/blotter'&&window.__holdChart){window.__holdChart=false;await new Promise(resolve=>{window.__releaseChart=resolve;});}
    if(String(input)!=='/api/trading/workspace')return send(input,init);
    const body=JSON.parse(init.body);window.__loadingReads.push({body});
    if(body.bookingCatalog==='directory'&&window.__holdValuation){window.__holdValuation=false;await new Promise(resolve=>{window.__releaseValuation=resolve;});}
    if(body.bookingCatalog==='directory'&&window.__failValuation){window.__failValuation=false;return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}
    if(body.bookingCatalog==='priced_contracts'&&window.__failFullBooking){window.__failFullBooking=false;return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}
    if(body.bookingCatalog==='priced_contracts'&&window.__denyFullBooking){window.__denyFullBooking=false;return new Response(JSON.stringify({code:'access_denied'}),{status:403,headers:{'Content-Type':'application/json'}});}
    const response=await send(input,init),data=await response.clone().json();
    if(body.bookingCatalog==='priced_contracts'&&window.__holdFullBooking){window.__holdFullBooking=false;await new Promise(resolve=>{window.__releaseFullBooking=resolve;});}
    if(body.bookingCatalog==='priced_contracts'&&window.__mismatchFullBooking&&response.ok){window.__mismatchFullBooking=false;data.blotter.context.contextId='00000000-0000-0000-0000-000000000002';return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}
    window.__loadingReads.at(-1).data=data;return response;
  };})()`);
  try{
    await persona('trader');
    const selected=await evaluate("[...document.querySelector('#snapshot').options].at(-1).value");
    await set('#snapshot',selected);
    // A first load whose valuation fails draws no row, never a zero total, and says why with a Retry; the toolbar stays usable.
    await evaluate("window.__failValuation=true;document.querySelector('#nav-futures').click()");
    await wait("document.querySelector('#futures-page').dataset.pricingState==='failed'&&"+idle);
    await check('a first load whose valuation fails draws no row and says why, with a Retry in the grid pane',
      `${valuedRows}===0&&${shown('#blotter-load-status')}&&document.querySelector('#blotter-load-text').textContent.startsWith('Valuation failed · ')&&${shown('#blotter-retry')}&&document.querySelector('#blotter-pane-grid').contains(document.querySelector('#blotter-retry'))&&document.querySelector('#blotter-progress').hidden`,
      "document.querySelector('#blotter-load-status').textContent");
    await check('and its summary strip lists every money total Unavailable, never zero, pointing at Retry',`(()=>{const items=${strip},totals=${JSON.stringify(moneyTotals)};return totals.every(label=>items.some(i=>i.label===label&&i.value==='Unavailable'&&i.state==='unavailable'&&i.reason==='Retry to value again'))&&items.filter(i=>totals.includes(i.label)).every(i=>i.value==='Unavailable');})()`,strip);
    await check('and the toolbar stays usable: book scope, As of, New trade, Refresh and Retry',toolbarUsable,`[...document.querySelectorAll('#blotter-scope,#snapshot,#futures-new,#futures-refresh,#blotter-retry')].map(n=>[n.id,n.inert,n.disabled])`);
    // First load: no row is drawn before its values arrive; the booked count and the valuation are read together, and the status counts the
    // positions being valued once the count is in.
    await evaluate("window.__holdFacts=true;window.__holdValuation=true;document.querySelector('#blotter-retry').click()");
    await wait("typeof window.__releaseFacts==='function'&&typeof window.__releaseValuation==='function'");
    await check('the count of booked positions and their valuation are read together, the status saying Valuing positions until the count is in',
      `${valuedRows}===0&&${shown('#blotter-progress')}&&document.querySelector('#blotter-load-text').textContent==='Valuing positions'`,"document.querySelector('#blotter-load-status').textContent");
    await evaluate('window.__releaseFacts();window.__releaseFacts=null');
    await wait("!!window.__loadingFacts&&document.querySelector('#blotter-load-text').textContent==='Valuing '+window.__loadingFacts.total+' positions'&&document.body.getAttribute('aria-busy')==='false'");
    await check('while the first valuation runs, no row is drawn, the progress bar shows and the status counts the positions being valued',
      `${valuedRows}===0&&${shown('#blotter-progress')}&&${shown('#blotter-load-status')}&&window.__loadingFacts.total>0`,
      "({rows:"+valuedRows+",progress:document.querySelector('#blotter-progress')?.hidden,status:document.querySelector('#blotter-load-status')?.textContent,total:window.__loadingFacts?.total})");
    await check('and the summary strip, on screen, lists every money total pending',`(()=>{const items=${strip};return ${shown('#blotter-strip')}&&${JSON.stringify(moneyTotals)}.every(label=>items.some(i=>i.label===label&&i.value==='Pending'&&i.state==='pending'));})()`,strip);
    await evaluate('window.__releaseValuation();window.__releaseValuation=null');
    await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&!document.querySelector('#blotter-export').disabled&&"+idle);
    await check('once valued, the rows appear and the progress bar and status go',`${valuedRows}>0&&document.querySelector('#blotter-progress').hidden&&document.querySelector('#blotter-load-status').hidden&&document.querySelector('#desk-positions [role=grid]').getAttribute('aria-disabled')!=='true'`);
    // A refresh that fails keeps the rows and summary it had, marked stale, with the reason and a Retry, and the person's place in them.
    const pressed=await evaluate(`(async()=>{const h=document.querySelector('#desk-positions .tabulator-tableholder');h.scrollTop=Math.floor((h.scrollHeight-h.clientHeight)/2);
      await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));const box=h.getBoundingClientRect();
      return [...h.querySelectorAll('.tabulator-row[data-row-key]')].find(r=>{const b=r.getBoundingClientRect();return b.top>=box.top&&b.bottom<=box.bottom;}).dataset.rowKey;})()`);
    await click(`#desk-positions .tabulator-row[data-row-key="${pressed}"]`);
    await wait("document.querySelector('#position-details')?.hidden===false&&"+idle);
    const at=await evaluate(place),before=await evaluate(strip);
    await evaluate("window.__failValuation=true;document.querySelector('#futures-refresh').click()");
    await wait("document.querySelector('#futures-page').dataset.pricingState==='failed'&&"+idle);
    await check('a failed refresh keeps the rows on screen, marked stale, with the reason and a Retry in the grid pane',
      `${valuedRows}>0&&document.querySelector('#desk-positions .standard-data-grid').classList.contains('stale')&&${shown('#blotter-load-status')}&&document.querySelector('#blotter-load-text').textContent.startsWith('Valuation failed · ')&&document.querySelector('#blotter-load-text').textContent.length>'Valuation failed · '.length&&${shown('#blotter-retry')}&&document.querySelector('#blotter-pane-grid').contains(document.querySelector('#blotter-load-status'))&&document.querySelector('#blotter-progress').hidden`,
      "({status:document.querySelector('#blotter-load-status').textContent,stale:document.querySelector('#desk-positions .standard-data-grid')?.className,progress:document.querySelector('#blotter-progress').hidden})");
    await check('and those rows are not offered: the grid says so, and they show no pointer',notOffered,"({disabled:document.querySelector('#desk-positions [role=grid]')?.getAttribute('aria-disabled'),cursor:getComputedStyle(document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')).cursor})");
    await check('and the toolbar stays usable',toolbarUsable,"[...document.querySelectorAll('#blotter-scope,#snapshot,#futures-new,#futures-refresh,#blotter-retry')].map(n=>[n.id,n.inert,n.disabled])");
    await check('and the summary strip keeps its last values and their own reasons, each flagged Stale',`(()=>{const items=${strip},last=${JSON.stringify(before)};return items.length===last.length&&items.every((i,n)=>i.label===last[n].label&&i.value===last[n].value&&i.state===last[n].state&&i.reason===last[n].reason&&i.flag==='Stale');})()`,strip);
    await check('and the person keeps their place: the selected row, the scroll offset and the first row in view',`JSON.stringify(${place})===${JSON.stringify(JSON.stringify(at))}`,place);
    await evaluate("window.__failValuation=true;document.querySelector('#blotter-retry').click()");
    await wait("document.querySelector('#futures-page').dataset.pricingState==='failed'&&"+idle);
    await check('a Retry that fails again still keeps the rows stale, in place, with the earlier strip',`${valuedRows}>0&&document.querySelector('#desk-positions .standard-data-grid').classList.contains('stale')&&JSON.stringify(${place})===${JSON.stringify(JSON.stringify(at))}&&(()=>{const items=${strip};return items.length===${before.length}&&items.every(i=>i.flag==='Stale');})()`,`[${place},${strip}]`);
    await evaluate("window.__holdValuation=true;document.querySelector('#blotter-retry').click()");await wait("typeof window.__releaseValuation==='function'");
    await check('while the Retry is read, the rows stay on screen stale, in place',`${valuedRows}>0&&document.querySelector('#desk-positions .standard-data-grid').classList.contains('stale')&&JSON.stringify(${place})===${JSON.stringify(JSON.stringify(at))}`,place);
    await evaluate('window.__releaseValuation();window.__releaseValuation=null');
    await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    await check('and once valued, the values replace the stale ones in place: the same selected row and first row in view',`(()=>{const now=${place},was=${JSON.stringify(at)};return now.selected===was.selected&&now.first===was.first;})()`,place);
    await click('#details-collapse');
    await check('Retry values the Blotter again, and nothing stays marked stale',`${valuedRows}>0&&!document.querySelector('#desk-positions .standard-data-grid').classList.contains('stale')&&document.querySelector('#blotter-load-status').hidden&&(${strip}).every(i=>i.flag===null)&&document.querySelector('#desk-positions [role=grid]').getAttribute('aria-disabled')!=='true'`);
    await check('Blotter reads a directory and usable holdings without pricing booking choices',"(()=>{const read=window.__loadingReads.at(-1);return read.body.bookingCatalog==='directory'&&read.data.catalog.kind==='directory'&&!Object.hasOwn(read.data.catalog,'contracts')&&document.querySelector('#futures-contract').options.length===1&&!document.querySelector('#futures-new').disabled;})()");
    const count=await evaluate('window.__loadingReads.length');
    await startNewTrade({click,wait});
    await check('New trade opens all product choices with none selected, without requesting futures prices',`document.querySelector('#position-details').dataset.mode==='new'&&[...document.querySelectorAll('#new-trade-product button')].every(b=>b.getAttribute('aria-pressed')==='false')&&document.querySelector('#position-details-form').hidden&&window.__loadingReads.length===${count}`);await click('#details-collapse');
    await newTrade({click,wait},'swaps');
    await check('choosing a swap does not load a futures booking catalog',`!document.querySelector('#swap-editing').hidden&&!document.querySelector('#position-details').hidden&&window.__loadingReads.length===${count}`);await click('#details-collapse');
    await newTrade({click,wait},'futures');
    await check('choosing futures reloads catalog and holdings under one fresh matching context',`(()=>{const read=window.__loadingReads.at(-1),c=read.data.catalog,b=read.data.blotter;return window.__loadingReads.length===${count+1}&&read.body.bookingCatalog==='priced_contracts'&&read.body.context.snapshotId===${JSON.stringify(selected)}&&c.kind==='priced_contracts'&&c.contracts.length>0&&c.context.contextId===b.context.contextId&&c.context.knownAt===b.context.knownAt&&c.asOf===b.asOf&&!document.querySelector('#futures-new-trade').hidden&&document.querySelector('#position-details-form').contains(document.querySelector('#futures-new-trade'))&&document.querySelector('#futures-contract').options.length===1&&document.querySelector('#futures-contract-commodity').options.length>1;})()`);
    await click('#details-collapse');await startNewTrade({click,wait});
    await evaluate("window.__holdFullBooking=true;document.querySelector('#new-trade-futures').click()");await wait("typeof window.__releaseFullBooking==='function'");
    await evaluate("(()=>{const snapshot=document.querySelector('#snapshot');snapshot.value=[...snapshot.options].find(option=>option.value&&option.value!==snapshot.value).value;window.__releaseFullBooking();})()");await wait(idle);
    await check('late full catalog cannot open a form for a different selected snapshot, and leaves no panel on a product with nothing in it',"!document.querySelector('#error').hidden&&document.querySelector('#futures-new-trade').hidden&&document.querySelector('#position-details').hidden&&document.querySelector('#desk-positions').children.length===0");
    await set('#snapshot',selected);await wait(idle);
    await click('#futures-refresh');await newTrade({click,wait},'futures');await click('#futures-refresh');
    await check('refresh returns to directory mode and discards prior booking choices, closing the panel and its form',"window.__loadingReads.at(-1).body.bookingCatalog==='directory'&&document.querySelector('#futures-contract').options.length===1&&document.querySelector('#futures-new-trade').hidden&&document.querySelector('#position-details').hidden");
    // A futures catalog that cannot be read leaves the panel on the choice of a product, as a swap's does, and never on a product with nothing in it.
    await evaluate('window.__failFullBooking=true');await startNewTrade({click,wait});await click('#new-trade-futures');
    await check('a futures catalog that cannot be read leaves the panel asking for a product, with the failure said, and never an empty New future',`!document.querySelector('#error').hidden&&${choice}`,"[document.querySelector('#position-details').dataset.mode,document.querySelector('#error').textContent,document.querySelector('#position-details-view').textContent]");
    await chooseProduct({click,wait},'futures');
    await check('and the product can then be chosen again',"document.querySelector('#side-panel-title').textContent==='New future'&&!document.querySelector('#futures-new-trade').hidden&&document.querySelector('#error').hidden");
    await click('#futures-refresh');
    await evaluate('window.__mismatchFullBooking=true');await startNewTrade({click,wait});await click('#new-trade-futures');
    await check('mismatched full booking context clears rows, keeps the form closed and leaves the panel on the choice of a product',`!document.querySelector('#error').hidden&&document.querySelector('#desk-positions').children.length===0&&document.querySelector('#futures-contract').options.length===1&&${choice}`);
    await click('#futures-refresh');await evaluate('window.__denyFullBooking=true');await startNewTrade({click,wait});await click('#new-trade-futures');
    await check('revoked full booking read clears both workspace data and booking controls, and leaves the panel on the choice of a product',`!document.querySelector('#error').hidden&&document.querySelector('#desk-positions').children.length===0&&document.querySelector('#futures-form').hidden&&document.querySelector('#futures-new').disabled&&${choice}`);
    // A pivot's chart is read after its rows: while it is read again, the chart on screen is marked stale too.
    await click('#futures-refresh');await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    await menu('blotter-pivot');await set('#blotter-mode','pivot');await click('#blotter-dimensions input[value="book"]');await click('#blotter-measures input[value="tradeCount"]');
    await click('#blotter-apply');await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    await menu('blotter-chart-settings');await set('#blotter-chart-measure','quantity');await click('#blotter-show-chart');await click('#blotter-chart-apply');
    await wait("!document.querySelector('#blotter-chart-card').hidden&&document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    await evaluate("window.__holdChart=true;document.querySelector('#futures-refresh').click()");await wait("typeof window.__releaseChart==='function'");
    await check('while a pivot chart is read again its card stays marked stale, with the reason in its pane',"document.querySelector('#blotter-chart-card').classList.contains('stale')&&!document.querySelector('#charts-stale-note').hidden&&document.querySelector('#charts-stale-note').textContent.includes('previous read')");
    await evaluate('window.__releaseChart();window.__releaseChart=null');
    await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    await check('and once it is read, the chart is no longer stale',"!document.querySelector('#blotter-chart-card').hidden&&!document.querySelector('#blotter-chart-card').classList.contains('stale')&&document.querySelector('#charts-stale-note').hidden");
  }finally{await evaluate('window.fetch=window.__loadingOriginalFetch');}
}
