// The market board of Curves & prices (docs/markets-desk.md): opened on the newest close with no curve chosen, a card for each listed
// curve in the library's groups and order, from curve-shapes reads of at most 64 curves and no evaluate; each card states the backend's
// front contract, exact price, unit and count, or why the close prices none; the library rows keep their markup and gain the curve's
// commodity dot and sparkline; neither shows a previous close's or context's figures while another is read; a read that fails says so on
// its cards and is read again; and a card opens its curve as the library does. The selected curve shows the backend's summary in
// tiles and returns to the board through All markets; a contract row opens the contract panel, docked on a wide screen and a sheet on
// a phone, whose History tab reads under the revision policy the switch shows, draws the backend's overlay values with their
// definition and window, and states when each value was known; changing the close closes it.
export async function run({command,evaluate,wait,click,set,check,shot,idle,fitsWidth,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
 const {identifier}=await command('Page.addScriptToEvaluateOnNewDocument',{source:`
  window.__shapeReads=[];window.__evaluates=0;const send=window.fetch;window.fetch=async(input,init)=>{
   const path=new URL(typeof input==='string'?input:input.url,location.href).pathname;
   if(path==='/api/evaluate')window.__evaluates++;
   if(path==='/api/history'){let response=await send(input,init);
    // A series with different samples at one instant, as /history states it: its points kept, the conflict instead of a chart, no overlays.
    if(window.__conflictHistory&&response.ok){const data=await response.json();data.chart={conflict:{at:data.points[0].at,message:'Several closes recorded different prices at '+data.points[0].at+'.'}};delete data.overlays;response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}
    window.__historyReads=[...(window.__historyReads??[]),{body:JSON.parse(init.body),data:response.ok?await response.clone().json():null}];return response;}
   if(path==='/api/market/curve-shapes'&&window.__holdShapes)await new Promise(resolve=>window.__releaseShapes=()=>{window.__holdShapes=false;window.__releaseShapes=undefined;resolve();});
   if(path==='/api/market/curve-shapes'&&window.__failShapes){window.__failShapes=false;window.__shapeReads.push({curves:JSON.parse(init.body).curves,snapshotId:JSON.parse(init.body).snapshotId,shapes:null});return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}
   const response=await send(input,init);
   if(path==='/api/market/curve-shapes')window.__shapeReads.push({curves:JSON.parse(init.body).curves,snapshotId:JSON.parse(init.body).snapshotId,shapes:response.ok?(await response.clone().json()).curves:null});
   return response;
  };`});
 try{
  await command('Page.navigate',{url:url+'/'});
  await wait(idle+"&&document.querySelectorAll('.curve-button').length>0&&performance.getEntriesByName('leanetrm:workspace-ready').length===1");
  await click('#nav-market');await wait(idle);
  await check('Curves & prices opens on the market board at the newest close, with no curve chosen',
   "(async()=>{const frames=(await(await fetch('/api/workspace/navigation')).json()).frames,newest=Math.max(...frames.map(f=>Date.parse(f.at))),latest=frames.filter(f=>Date.parse(f.at)===newest);"+
   "return latest.length===1&&document.querySelector('#snapshot').value===latest[0].id&&!document.querySelector('#market-board').hidden&&document.querySelector('#market-board-empty').hidden&&document.querySelector('#curve-detail').hidden&&document.querySelector('#market-board-status').textContent.startsWith('Prices at the close of ')&&document.querySelector('#error').hidden;})()");
  await check('the board has a card for each listed curve, in the library\'s groups and order',
   "(()=>{const library=[...document.querySelectorAll('#curves .curve-group')].map(g=>[g.querySelector('summary').textContent,...[...g.querySelectorAll('.curve-button')].map(b=>b.querySelector('span').textContent)]);"+
   "const board=[...document.querySelectorAll('.market-board-group')].map(g=>[g.querySelector('h3').textContent,...[...g.querySelectorAll('.market-card-open')].map(b=>b.textContent)]);"+
   "return library.length>1&&JSON.stringify(board)===JSON.stringify(library);})()",
   "[...document.querySelectorAll('.market-board-group h3')].map(h=>h.textContent)");
  await check('the board reads the shapes of its listed curves at the close in one read per 64 curves, rates aside, and evaluates nothing before a curve is chosen',
   "(()=>{const shaped=[...document.querySelectorAll('.market-card')].filter(c=>c.dataset.state!=='rates').map(c=>c.dataset.curve),read=window.__shapeReads.flatMap(r=>r.curves);"+
   "return shaped.length>0&&window.__shapeReads.length===Math.ceil(shaped.length/64)&&window.__shapeReads.every(r=>r.curves.length<=64&&r.shapes)&&read.length===shaped.length&&shaped.every(c=>read.includes(c))&&window.__evaluates===0;})()",
   "({reads:window.__shapeReads.map(r=>r.curves.length),evaluates:window.__evaluates})");
  await check('each priced card shows the backend\'s unit, front contract, exact front price and contract count, with its curve drawn',
   "(()=>{const shapes=new Map(window.__shapeReads.flatMap(r=>r.shapes).map(s=>[s.curve,s])),priced=[...document.querySelectorAll('.market-card[data-state=priced]')];"+
   "return priced.length>0&&priced.every(card=>{const s=shapes.get(card.dataset.curve);return s.front&&card.querySelector('.kpi-figure').textContent===s.front.price&&card.querySelector('.market-card-unit').textContent===s.unit&&"+
   "card.querySelector('.market-card-label').textContent.startsWith('Front · ')&&card.querySelector('.market-card-count').textContent===s.contractCount+(s.contractCount===1?' contract':' contracts')&&card.querySelector('svg.sparkline path.sparkline-line')&&card.querySelector('svg.sparkline').getAttribute('aria-label').startsWith(card.querySelector('.market-card-open').textContent+' · forward curve');});})()");
  await check('library rows keep their name and type text and gain the commodity dot and, for a priced curve, a decorative sparkline',
   "(()=>{const rows=[...document.querySelectorAll('.curve-button')],cards=new Map([...document.querySelectorAll('.market-card')].map(c=>[c.querySelector('.market-card-open').textContent,c]));"+
   "return rows.length===cards.size&&rows.every(b=>{const name=b.querySelector('span').textContent,card=cards.get(name),line=b.querySelector('svg.sparkline');"+
   "return card&&b.firstElementChild.classList.contains('commodity-dot')&&b.textContent===name+b.querySelector('small').textContent&&(card.dataset.state==='priced'?line?.getAttribute('aria-hidden')==='true'&&!!line.querySelector('path'):!line);});})()");
  await check('a curve\'s dot on the board and in the library share its commodity color',
   "[...document.querySelectorAll('.market-card')].every(card=>{const row=[...document.querySelectorAll('.curve-button')].find(b=>b.querySelector('span').textContent===card.querySelector('.market-card-open').textContent);return row.querySelector('.commodity-dot').style.getPropertyValue('--commodity')===card.querySelector('.commodity-dot').style.getPropertyValue('--commodity');})");
  await evaluate('window.scrollTo(0,0)');await shot('markets-board-desktop');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate('window.scrollTo(0,0)');
  await check('the board fits a 390px screen without page-wide horizontal scrolling',fitsWidth);
  await evaluate("document.querySelector('#market-board').scrollIntoView()");await shot('markets-board-mobile');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
  // Nothing of the previous close is shown while another close is read: every card waits for its read, and no library row has a line.
  const newest=await evaluate("document.querySelector('#snapshot').value"),shownAt=await evaluate("document.querySelector('#market-board-status').textContent");
  const older=await evaluate("[...document.querySelector('#snapshot').options].find(o=>o.value&&o.value!==document.querySelector('#snapshot').value).value");
  const nothingShown="[...document.querySelectorAll('.market-card')].length>0&&!document.querySelector('.market-card .kpi-figure')&&!document.querySelector('.market-card .sparkline')&&!document.querySelector('.curve-button .sparkline')";
  await evaluate('window.__holdShapes=true');await set('#snapshot',older);await wait("typeof window.__releaseShapes==='function'");
  await check('while a newly chosen close is read, neither the board nor the library shows the previous close\'s figures or lines',
   `document.querySelector('#snapshot').value===${JSON.stringify(older)}&&document.querySelector('#market-board-status').textContent!==${JSON.stringify(shownAt)}&&[...document.querySelectorAll('.market-card')].every(c=>['unread','rates'].includes(c.dataset.state))&&${nothingShown}`,
   "[document.querySelector('#market-board-status').textContent,[...document.querySelectorAll('.market-card')].map(c=>c.dataset.state)]");
  await evaluate('window.__releaseShapes()');await wait(idle);
  await check('the newly chosen close then fills the board and the library from its own read',
   `window.__shapeReads.at(-1).snapshotId===${JSON.stringify(older)}&&document.querySelectorAll('.market-card[data-state=priced]').length>0&&document.querySelectorAll('.curve-button .sparkline').length===document.querySelectorAll('.market-card[data-state=priced]').length&&document.querySelector('#error').hidden`);
  // An unapplied context change puts away what the applied context showed.
  await evaluate("document.querySelector('#date-context-on').value='2020-12-17';document.querySelector('#date-context-on').dispatchEvent(new Event('input',{bubbles:true}))");
  await check('an unapplied context change shows no figures or lines of the applied context, and says to apply it',
   "!document.querySelector('#market-board-empty').hidden&&document.querySelector('#market-board-empty-title').textContent==='Apply the changed context'&&document.querySelector('#market-board-groups').hidden&&!document.querySelector('.market-card')&&!document.querySelector('.curve-button .sparkline')");
  await click('#return-working');
  await check('returning to working data reads the newest close for the board again',
   `document.querySelector('#snapshot').value===${JSON.stringify(newest)}&&window.__shapeReads.at(-1).snapshotId===${JSON.stringify(newest)}&&document.querySelectorAll('.market-card[data-state=priced]').length>0&&document.querySelector('#error').hidden`);
  // A read that fails says so on each card it was for, draws nothing, and is read again by the next load of the board.
  await evaluate('window.__failShapes=true');await set('#snapshot',older);await wait(idle);
  await check('a curve-shapes read that fails says why on each of its cards, with no price or line',
   "(()=>{const cards=[...document.querySelectorAll('.market-card')].filter(c=>c.dataset.state!=='rates');return cards.length>0&&cards.every(c=>c.dataset.state==='failed'&&c.querySelector('.market-card-reason').textContent==='Curve shape unavailable · the curve read failed'&&!c.querySelector('.kpi-figure,.sparkline'))&&!document.querySelector('.curve-button .sparkline');})()");
  const readsBeforeRetry=await evaluate('window.__shapeReads.length');
  await click('#nav-market');
  await check('opening the board again reads the curves whose read failed, and draws them',
   `window.__shapeReads.length===${readsBeforeRetry}+1&&window.__shapeReads.at(-1).shapes&&window.__shapeReads.at(-1).snapshotId===${JSON.stringify(older)}&&!document.querySelector('.market-card[data-state=failed]')&&document.querySelectorAll('.market-card[data-state=priced]').length>0`);
  await evaluate("[...document.querySelectorAll('.market-card-open')].find(b=>b.textContent==='Local power forwards').click()");await wait(idle);
  await check('choosing a card opens its curve as the library does: one evaluate, the curve shown and selected in the library, the board put away',
   "window.__evaluates===1&&document.querySelector('#market-board').hidden&&!document.querySelector('#curve-detail').hidden&&document.querySelector('#curve-title').textContent==='Local power forwards'&&"+
   "document.querySelector('.curve-button[aria-pressed=true] span')?.textContent==='Local power forwards'&&document.querySelector('#curve-chart canvas')&&document.querySelector('#error').hidden");
  await check('the selected curve\'s tiles state the evaluate summary: its contract count, front contract and exact front price',
   "(async()=>{const tiles=[...document.querySelectorAll('#curve-summary .desk-tile')].map(t=>[t.querySelector('.desk-tile-label').textContent,t.querySelector('.kpi-figure').textContent]);"+
   "return tiles.length===4&&tiles.map(t=>t[0]).join('|')==='Contracts|Front contract|Front price|Settlement'&&!document.querySelector('#curve-summary').hidden&&tiles[0][1]===document.querySelector('#build-info').textContent.split(' ')[0];})()",
   "[...document.querySelectorAll('#curve-summary .desk-tile')].map(t=>t.textContent)");
  await click('#curve-all-markets');
  await check('All markets in the curve\'s header returns to the market board with no curve selected',
   "!document.querySelector('#market-board').hidden&&document.querySelector('#curve-detail').hidden&&!document.querySelector('.curve-button[aria-pressed=true]')&&document.querySelectorAll('.market-card[data-state=priced]').length>0&&document.querySelector('#error').hidden");
  await evaluate("[...document.querySelectorAll('.market-card-open')].find(b=>b.textContent==='Gas futures').click()");await wait(idle);
  await click('#contracts tbody tr:nth-child(2)');
  const history="window.__historyReads.at(-1)";
  await check('choosing a contract row opens it in the panel, docked beside the curve at 1440 pixels, on History at exact closes',
   `(()=>{const read=${history},panel=document.querySelector('#contract-panel'),row=document.querySelector('#contracts tbody tr:nth-child(2)');return !panel.hidden&&panel.dataset.placement==='docked'&&row.getAttribute('aria-current')==='true'&&`+
   "document.querySelector('#contract-panel-head .side-panel-title').textContent===row.cells[0].textContent&&read.body.revisionPolicy==='exact_snapshot_pins_v1'&&!read.body.overlays&&"+
   "document.querySelector('#history-policy [aria-pressed=true]').textContent==='Exact closes'&&document.querySelector('#history-basis').textContent==='Each close with the prices it was saved with'&&"+
   "document.querySelector('#contract-panel-tabs [aria-selected=true]').textContent==='History'&&document.querySelector('#history-chart .time-chart-canvas canvas')&&document.querySelector('#error').hidden;})()",
   `[document.querySelector('#contract-panel').dataset.placement,document.querySelector('#history-basis').textContent,document.querySelector('#error').textContent]`);
  await check('the contracts table is described by the line saying that choosing a contract opens it',
   "document.querySelector('#contracts table').getAttribute('aria-describedby')==='contracts-help'&&document.querySelector('#contracts-help').textContent.startsWith('Choose a contract')");
  await check('the values table lists every point the read returned, with when each was known',
   `(()=>{const read=${history}.data,rows=[...document.querySelectorAll('#history-values tbody tr')];return read.points.length>1&&rows.length===read.points.length&&[...document.querySelectorAll('#history-values thead th')].map(th=>th.textContent).slice(0,3).join('|')==='Market date|Price|Known at'&&rows.every(r=>r.cells[2].textContent.length>0);})()`);
  // An overlay is asked for with an explicit window; the chart draws the values the backend returned, named by definition and window.
  await evaluate("document.querySelector('#history-overlays').open=true");
  await evaluate("(()=>{const input=[...document.querySelectorAll('#history-overlay-menu fieldset')].find(f=>f.querySelector('legend').textContent==='Simple moving average').querySelector('input');input.value='3';input.dispatchEvent(new Event('input',{bubbles:true}));})()");
  await evaluate("document.querySelector('#history-overlay-menu form').requestSubmit()");await wait(idle);
  await check('a simple moving average with window 3 is read from the backend and drawn with its definition and window, showing its exact latest value',
   `(()=>{const read=${history},overlay=read.data.overlays?.[0],latest=[...document.querySelectorAll('#history-chart .time-chart-latest dt')].map(dt=>[dt.textContent,dt.nextElementSibling.textContent]);`+
   "const sma=latest.find(([name])=>name==='Simple moving average · 3 · value'),last=overlay?.series.value.at(-1);"+
   "return JSON.stringify(read.body.overlays)===JSON.stringify([{code:'simple_moving_average_v1',parameters:{window:3}}])&&read.body.revisionPolicy==='exact_snapshot_pins_v1'&&overlay&&sma&&sma[1]===(last.value??'No value ('+last.reason.replaceAll('_',' ')+')')&&"+
   "document.querySelector('#history-overlays summary').textContent==='Overlays · 1 shown'&&document.querySelector('#error').hidden;})()",
   `[JSON.stringify(${history}.body),[...document.querySelectorAll('#history-chart .time-chart-latest')].map(n=>n.textContent),document.querySelector('#error').textContent]`);
  await evaluate("(()=>{window.scrollTo(0,document.querySelector('#contract-panel').getBoundingClientRect().top+scrollY-8);const body=document.querySelector('#contract-panel-body');body.scrollTop+=document.querySelector('#history-basis').getBoundingClientRect().top-body.getBoundingClientRect().top-8;})()");
  await shot('markets-contract-panel-desktop');
  await evaluate('window.__conflictHistory=true');await click('#contracts tbody tr:nth-child(3)');
  await check('a series the backend states as conflicting shows its message and why no overlay is drawn, no chart, and every close in the values',
   `(()=>{const read=${history},note=document.querySelector('#history-chart .contract-chart-note');return read.body.overlays?.length===1&&!('overlays' in read.data)&&note&&note.textContent.startsWith(read.data.chart.conflict.message)&&`+
   "note.textContent.includes('Overlays cannot be drawn over a series with different prices at one instant.')&&!document.querySelector('#history-chart canvas')&&document.querySelectorAll('#history-values tbody tr').length===read.data.points.length&&document.querySelector('#error').hidden;})()",
   "[document.querySelector('#history-chart').textContent,document.querySelector('#error').textContent]");
  await evaluate('window.__conflictHistory=false');await click('#contracts tbody tr:nth-child(2)');
  await check('the chart returns with the overlay when the series has one point per instant again, with only the open contract marked',
   "!!document.querySelector('#history-chart .time-chart-canvas canvas')&&[...document.querySelectorAll('#history-chart .time-chart-latest dt')].some(dt=>dt.textContent==='Simple moving average · 3 · value')&&document.querySelectorAll('#contracts tbody tr[aria-current]').length===1&&document.querySelector('#contracts tbody tr:nth-child(2)').getAttribute('aria-current')==='true'");
  await evaluate("[...document.querySelectorAll('#history-policy button')].find(b=>b.textContent==='As known at cutoff').click()");await wait(idle);
  await check('As known at cutoff reads under the cutoff policy, keeps the overlay, and states the knowledge the read used',
   `(()=>{const read=${history};return read.body.revisionPolicy==='as_known_at_cutoff_v1'&&read.data.revisionPolicy==='as_known_at_cutoff_v1'&&read.body.overlays?.length===1&&document.querySelector('#history-policy [aria-pressed=true]').textContent==='As known at cutoff'&&`+
   "document.querySelector('#history-basis').textContent==='Latest revisions known now · current knowledge in working data'&&read.data.knowledgeCutoff===null&&read.data.knowledgeBasis==='current_knowledge'&&document.querySelectorAll('#history-values tbody tr').length===read.data.points.length&&document.querySelector('#error').hidden;})()",
   "[document.querySelector('#history-basis').textContent,document.querySelector('#error').textContent]");
  await click('#contract-panel-tabs-pricing');
  await check("How it's priced shows the contract's pricing graph in the panel",
   "document.querySelector('#contract-history').hidden&&!document.querySelector('#contract-pricing').hidden&&document.querySelector('#contract-pricing .flow-summary').textContent.startsWith('Gas futures · ')");
  await click('#contract-panel-tabs-history');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await wait("document.querySelector('#contract-panel').dataset.placement==='overlay'");
  await check('at 390 pixels the panel is a sheet over the page, which fits the screen and is held out of reach',
   "(()=>{const panel=document.querySelector('#contract-panel'),box=panel.getBoundingClientRect();return panel.dataset.placement==='overlay'&&box.left===0&&box.width===innerWidth&&document.querySelector('#contracts').closest('[inert]')!==null&&document.querySelector('#nav-market').closest('[inert]')!==null&&!panel.closest('[inert]');})()",
   "(()=>{const panel=document.querySelector('#contract-panel'),box=panel.getBoundingClientRect();return [panel.dataset.placement,box.left,box.width,innerWidth,document.querySelectorAll('[inert]').length];})()");
  await check('the sheet fits a 390px screen without page-wide horizontal scrolling',fitsWidth);
  await evaluate("document.querySelector('#contract-panel-body').scrollTop=0");await shot('markets-contract-panel-mobile');
  await check('the sheet lies over the floating As of and Context docks, so its foot is in view',
   "(()=>{const panel=document.querySelector('#contract-panel'),dock=document.querySelector('.context-dock'),r=dock.getBoundingClientRect(),hit=document.elementFromPoint(r.left+r.width/2,r.top+r.height/2);return Number(getComputedStyle(panel).zIndex)>Number(getComputedStyle(dock).zIndex)&&panel.contains(hit)&&panel.getBoundingClientRect().bottom===innerHeight;})()");
  await click('#contract-panel-close');
  await check('closing the sheet gives the page back, with focus on the contract\'s row',"document.querySelector('#contract-panel').hidden&&document.querySelectorAll('[inert]').length===0&&document.activeElement===document.querySelector('#contracts tbody tr:nth-child(2)')&&!document.querySelector('#contracts tbody tr[aria-current]')");
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
  await click('#contracts tbody tr:first-child');
  await check('the panel opens again on exact closes with no overlay',`!document.querySelector('#contract-panel').hidden&&${history}.body.revisionPolicy==='exact_snapshot_pins_v1'&&!${history}.body.overlays&&document.querySelector('#history-overlays summary').textContent==='Overlays'`);
  await set('#snapshot',newest);await wait(idle);
  await check('changing the close closes the panel',"document.querySelector('#contract-panel').hidden&&document.querySelector('#error').hidden");
 }finally{await command('Page.removeScriptToEvaluateOnNewDocument',{identifier});await command('Emulation.clearDeviceMetricsOverride');}
}
