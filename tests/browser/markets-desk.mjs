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
  // The backend names each curve's kind (curve_kinds_v1); the badge carries its label as the accessible name and tooltip.
  await check('each card and library row shows the backend\'s curve kind as a badge named by its label, a settlement curve as Market settlement',
   "(async()=>{const kinds=new Map((await(await fetch('/api/workspace/navigation')).json()).curves.map(c=>[c.code,c.kind])),cards=[...document.querySelectorAll('.market-card')];"+
   "const badges=cards.map(card=>[card,card.querySelector('.market-card-head .curve-kind[role=img]')]),row=card=>[...document.querySelectorAll('.curve-button')].find(b=>b.querySelector('span').textContent===card.querySelector('.market-card-open').textContent);"+
   "return cards.length>0&&badges.every(([card,badge])=>badge&&badge.dataset.kind===kinds.get(card.dataset.curve)&&badge.getAttribute('aria-label').length>0&&badge.title===badge.getAttribute('aria-label')&&badge.querySelector('svg[aria-hidden=true]')&&row(card).querySelector('.curve-kind').getAttribute('aria-label')===badge.getAttribute('aria-label'))&&"+
   "badges.some(([,badge])=>badge.dataset.kind==='market_settlement'&&badge.getAttribute('aria-label')==='Market settlement');})()",
   "[...document.querySelectorAll('.market-card')].map(c=>[c.dataset.curve,c.querySelector('.curve-kind')?.getAttribute('aria-label')])");
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
  await check('the curve\'s header shows the same kind badge as its card',
   "(async()=>{const curve=(await(await fetch('/api/workspace/navigation')).json()).curves.find(c=>c.name==='Local power forwards'),badge=document.querySelector('#curve-kind-badge .curve-kind-md[role=img]'),row=document.querySelector('.curve-button[aria-pressed=true] .curve-kind');"+
   "return badge&&badge.dataset.kind===curve.kind&&badge.getAttribute('aria-label').length>0&&badge.getAttribute('aria-label')===row.getAttribute('aria-label');})()");
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

// Delivery prices as a desk (docs/markets-desk.md, Delivery prices), over the preview's seeded week of hourly and quarter-hour prices: the
// page opens on the catalog's opening and says so, whatever another source's days; its tiles state the period summary and the catalog's
// collection as the backend wrote them; a person's change marks the shown range as theirs and chooses nothing for them; the 2026-11-01
// fall-back day shows its 100 quarter-hours with distinct labels; an overlay is drawn with its definition and window; a catalog with no
// opening says there are no delivery prices yet; and the page fits a 390px screen.
export async function deliveryDesk({command,evaluate,wait,click,set,submit,check,shot,idle,fitsWidth,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
 const {identifier}=await command('Page.addScriptToEvaluateOnNewDocument',{source:`
  window.__catalogs=[];window.__periods=[];const send=window.fetch;window.fetch=async(input,init)=>{
   const path=new URL(typeof input==='string'?input:input.url,location.href).pathname;
   if(path==='/api/power-imports'){const response=await send(input,init),catalog=await response.json();window.__catalogs.push(catalog);
    // The opening the page is given: the catalog's own, another source's oldest day, or none.
    let opening=catalog.opening;
    if(window.__openOlder){const other=catalog.sources.find(s=>s.id!==catalog.opening.sourceId),day=other.availableDates.slice().sort()[0];opening={...catalog.opening,sourceId:other.id,fromDate:day,throughDate:day};}
    if(window.__noOpening)opening=null;
    return new Response(JSON.stringify({...catalog,opening}),{status:response.status,headers:{'Content-Type':'application/json'}});}
   // The overlay catalog refused, as for an account that may not read it.
   if(path==='/api/analytics/overlays'&&window.__denyOverlays)return new Response(JSON.stringify({code:'forbidden',message:'Synthetic refusal of the overlay catalog.'}),{status:403,headers:{'Content-Type':'application/json'}});
   const response=await send(input,init);
   if(path==='/api/power-imports/period'){
    let data=response.ok?await response.clone().json():null;
    // The series the read states, with its last interval unpublished, or with no series at all; the rows are left as they were.
    if(data&&window.__lastUnpublished)data.chart.points[data.chart.points.length-1]={...data.chart.points.at(-1),value:null,unavailableReason:'not_published_at_cutoff'};
    if(data&&window.__noChart)delete data.chart;
    window.__periods.push({body:JSON.parse(init.body),data});
    if(data&&(window.__lastUnpublished||window.__noChart))return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});
   }
   return response;
  };`});
 const period='window.__periods.at(-1)',catalog='window.__catalogs.at(-1)';
 const figures="[...document.querySelectorAll('#delivery-price-tiles .desk-tile')].map(t=>[t.querySelector('.desk-tile-label').firstChild.textContent,t.querySelector('.kpi-figure').textContent])";
 // Opens a date picker and chooses a day, moving through the months that have data.
 const pick=async(name,date)=>{await click(`#delivery-price-${name}-calendar summary`);
  await evaluate(`(()=>{const host=document.querySelector('#delivery-price-${name}-calendar');for(let i=0;i<24&&!host.querySelector('button[data-date="${date}"]');i++)host.querySelector('[aria-label="Next month with data"]').click();host.querySelector('button[data-date="${date}"]').click();})()`);};
 try{
  await command('Page.navigate',{url:url+'/'});
  await wait(idle+"&&document.querySelectorAll('.curve-button').length>0&&performance.getEntriesByName('leanetrm:workspace-ready').length===1");
  // Interval labels in a zone whose clock repeats an hour on 2026-11-01.
  const zone=await evaluate("document.querySelector('#zone').value");
  await click('#profile-open');await set('#zone','America/Chicago');await submit('#preferences-form');
  await evaluate('window.__openOlder=true');await click('#nav-delivery');await wait(`${idle}&&window.__periods.length===1`);
  await check('Delivery prices opens on the catalog\'s opening, though another source has a newer day, and marks it the newest delivery day',
   `(()=>{const raw=${catalog},read=${period},shown=raw.sources.find(s=>s.id!==raw.opening.sourceId),day=shown.availableDates.slice().sort()[0];`+
   "return day<raw.opening.fromDate&&read.body.sourceId===shown.id&&read.body.fromDate===day&&read.body.throughDate===day&&document.querySelector('#delivery-price-source').value===shown.id&&"+
   "document.querySelector('#delivery-price-from').value===day&&document.querySelector('#delivery-price-through').value===day&&document.querySelector('#delivery-price-badge').textContent==='Newest delivery day'&&"+
   "document.querySelector('#delivery-price-title').textContent===shown.name&&document.querySelector('#error').hidden;})()",
   `[${period}?.body,document.querySelector('#delivery-price-badge')?.textContent,document.querySelector('#error').textContent]`);
  await evaluate('window.__openOlder=false');await click('#nav-market');await click('#nav-delivery');await wait(`${idle}&&window.__periods.length===2`);
  await check('opened again, the page reads exactly the catalog\'s opening source and day',
   `(()=>{const o=${catalog}.opening,read=${period}.body;return o.policy==='newest_available_delivery_day_v1'&&read.sourceId===o.sourceId&&read.fromDate===o.fromDate&&read.throughDate===o.throughDate&&!read.overlays&&document.querySelector('#delivery-price-badge').textContent==='Newest delivery day';})()`);
  await check('the tiles state the period summary and the catalog\'s collection exactly as the backend wrote them',
   `(()=>{const s=${period}.data.summary,c=${catalog}.collection,tiles=${figures};`+
   "return JSON.stringify(tiles)===JSON.stringify([['Intervals',String(s.intervals)],['Low',s.low.price],['High',s.high.price],['Average',s.average],['Latest knowledge',tiles[4][1]],['Collection',c.latestAttempt.state]])&&"+
   "s.intervals===s.expectedIntervals&&s.missingIntervals===0&&c.jobs>0&&tiles[4][1].length>0;})()",figures);
  await check('the low and the high name the interval each starts, as the table labels it',
   `(()=>{const s=${period}.data.summary,rows=${period}.data.rows,cells=[...document.querySelectorAll('#delivery-price-results tbody tr')].map(r=>r.cells[0].textContent),aside=label=>[...document.querySelectorAll('#delivery-price-tiles .desk-tile')].find(t=>t.querySelector('.desk-tile-label').firstChild.textContent===label).querySelector('.desk-tile-label').lastChild.textContent;`+
   "return cells.length===rows.length&&aside('Low')===cells[rows.findIndex(r=>r.start===s.low.start)]&&aside('High')===cells[rows.findIndex(r=>r.start===s.high.start)];})()");
  await check('the Prices tab draws the interval prices over time above the table of every interval',
   `!!document.querySelector('#delivery-price-chart .time-chart-canvas canvas')&&document.querySelectorAll('#delivery-price-results tbody tr').length===${period}.data.rows.length&&document.querySelector('#delivery-price-tabs-prices').getAttribute('aria-selected')==='true'&&document.querySelector('#delivery-price-collection').hidden`);
  await check('the knowledge cutoff line is announced as status',"document.querySelector('#delivery-price-known').getAttribute('role')==='status'");
  // The overlay catalog is read when the menu opens; a refusal shows in the menu only, and the prices stay.
  await evaluate('window.__denyOverlays=true');await evaluate("document.querySelector('#delivery-price-overlays').open=true");
  await wait("document.querySelector('#delivery-price-overlay-menu').textContent.includes('could not be read')");
  await check('a refused overlay catalog says so in the overlay menu and leaves the prices and the page usable',
   `document.querySelector('#delivery-price-overlay-menu').textContent.startsWith('Overlays could not be read: ')&&!document.querySelector('#delivery-price-overlay-menu form')&&document.querySelector('#error').hidden&&!!document.querySelector('#delivery-price-chart canvas')&&document.querySelectorAll('#delivery-price-results tbody tr').length===${period}.data.rows.length&&window.__periods.length===2`,
   "[document.querySelector('#delivery-price-overlay-menu').textContent,document.querySelector('#error').textContent,window.__periods.length]");
  await evaluate("window.__denyOverlays=false;document.querySelector('#delivery-price-overlays').open=false");
  // An overlay is asked for with an explicit window; the chart draws the values the backend returned, named by definition and window.
  await evaluate("document.querySelector('#delivery-price-overlays').open=true");await wait("!!document.querySelector('#delivery-price-overlay-menu fieldset')");
  await evaluate("(()=>{const input=[...document.querySelectorAll('#delivery-price-overlay-menu fieldset')].find(f=>f.querySelector('legend').textContent==='Simple moving average').querySelector('input');input.value='4';input.dispatchEvent(new Event('input',{bubbles:true}));})()");
  await evaluate("document.querySelector('#delivery-price-overlay-menu form').requestSubmit()");await wait(`${idle}&&window.__periods.length===3`);
  await check('a simple moving average with window 4 is read with the same range and drawn with its definition and window, showing its exact latest value',
   `(()=>{const read=${period},o=${catalog}.opening,last=read.data.overlays[0].series.value.at(-1),latest=[...document.querySelectorAll('#delivery-price-chart .time-chart-latest dt')].map(dt=>[dt.textContent,dt.nextElementSibling.textContent]).find(([name])=>name==='Simple moving average · 4 · value');`+
   "return JSON.stringify(read.body.overlays)===JSON.stringify([{code:'simple_moving_average_v1',parameters:{window:4}}])&&read.body.sourceId===o.sourceId&&read.body.fromDate===o.fromDate&&latest&&latest[1]===last.value&&"+
   "document.querySelector('#delivery-price-overlays summary').textContent==='Overlays · 1 shown'&&document.querySelector('#delivery-price-badge').textContent==='Newest delivery day'&&document.querySelector('#error').hidden;})()",
   `[JSON.stringify(${period}.body),document.querySelector('#delivery-price-chart .time-chart-latest')?.textContent,document.querySelector('#error').textContent]`);
  await evaluate('window.scrollTo(0,0)');await shot('markets-delivery-desktop');
  await click('#delivery-price-tabs-collection');
  await check('Collection history lists each attempt the catalog returned, with the jobs',
   `!document.querySelector('#delivery-price-collection').hidden&&document.querySelector('#delivery-price-prices').hidden&&document.querySelectorAll('#delivery-price-attempts tbody tr').length===${catalog}.attempts.length&&${catalog}.attempts.length>0&&document.querySelector('#delivery-price-jobs').textContent.length>0`);
  await click('#delivery-price-tabs-prices');
  // A person's change: dependent choices wait for them, nothing is read and the shown prices are put away until Show prices.
  const quarter=await evaluate(`${catalog}.sources.find(s=>s.minutes===15)`);
  const reads=await evaluate('window.__periods.length');
  await set('#delivery-price-commodity',quarter.commodityKey);await wait(idle);
  await check('changing a filter chooses nothing further, reads nothing and puts the shown prices away',
   `['provider','market','location','source'].every(x=>document.querySelector('#delivery-price-'+x).value==='')&&document.querySelector('#delivery-price-from').value===''&&window.__periods.length===${reads}&&!document.querySelector('#delivery-price-results tbody tr')&&!document.querySelector('#delivery-price-chart canvas')&&document.querySelector('#delivery-price-head').hidden&&!document.querySelector('#delivery-price-tiles [data-tile=intervals]')&&!document.querySelector('#delivery-price-empty').hidden`);
  await set('#delivery-price-provider',quarter.provider);await set('#delivery-price-market',quarter.market);await set('#delivery-price-location',quarter.locationKey);await set('#delivery-price-source',quarter.id);
  await pick('from','2026-11-01');await pick('through','2026-11-01');
  await check('choosing the dates reads nothing until Show prices',`window.__periods.length===${reads}&&document.querySelector('#delivery-price-from').value==='2026-11-01'&&document.querySelector('#delivery-price-through').value==='2026-11-01'`);
  await submit('#delivery-price-form');
  await check('the fall-back day shows its 100 quarter-hours with distinct labels, and the range is marked as the person\'s selection',
   `(()=>{const read=${period},rows=[...document.querySelectorAll('#delivery-price-results tbody tr')],starts=rows.map(r=>r.cells[0].textContent);`+
   `return read.body.sourceId===${JSON.stringify(quarter.id)}&&read.body.fromDate==='2026-11-01'&&read.body.throughDate==='2026-11-01'&&read.data.summary.intervals===100&&read.data.summary.expectedIntervals===100&&`+
   "rows.length===100&&new Set(starts).size===100&&new Set(rows.map(r=>r.cells[1].textContent)).size===100&&document.querySelector('#delivery-price-badge').textContent==='Selected range'&&"+
   "!document.querySelector('#delivery-price-head').hidden&&!!document.querySelector('#delivery-price-chart canvas')&&document.querySelector('#error').hidden;})()",
   "[document.querySelectorAll('#delivery-price-results tbody tr').length,document.querySelector('#delivery-price-badge')?.textContent,document.querySelector('#error').textContent]");
  await check('the overlay chosen before is read again with the selected range',`JSON.stringify(${period}.body.overlays)===JSON.stringify([{code:'simple_moving_average_v1',parameters:{window:4}}])&&[...document.querySelectorAll('#delivery-price-chart .time-chart-latest dt')].some(dt=>dt.textContent==='Simple moving average · 4 · value')`);
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate('window.scrollTo(0,0)');
  await check('Delivery prices fits a 390px screen without page-wide horizontal scrolling',fitsWidth);
  await evaluate("document.querySelector('#delivery-price-form').scrollIntoView()");await shot('markets-delivery-mobile');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
  // The chart draws the series the period read states: an interval the read leaves unpublished is no price, though the rows are unchanged.
  await evaluate('window.__lastUnpublished=true');let count=await evaluate('window.__periods.length');await submit('#delivery-price-form');await wait(`window.__periods.length===${count+1}`);
  await check('the chart draws the period read\'s own series: its last interval unpublished shows no latest price, whatever the rows hold',
   `(()=>{const read=${period},price=[...document.querySelectorAll('#delivery-price-chart .time-chart-latest dt')].find(dt=>dt.textContent==='Price');`+
   "return read.data.rows.length===100&&read.data.chart.points.length===100&&price&&price.nextElementSibling.textContent==='No price'&&document.querySelectorAll('#delivery-price-results tbody tr').length===100&&document.querySelector('#error').hidden;})()",
   "[...document.querySelectorAll('#delivery-price-chart .time-chart-latest')].map(n=>n.textContent)");
  await evaluate('window.__lastUnpublished=false;window.__noChart=true');count=await evaluate('window.__periods.length');await submit('#delivery-price-form');await wait(`window.__periods.length===${count+1}`);
  await check('a period read that states no chart series is refused, and nothing is drawn from its rows',
   "!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('chart')&&!document.querySelector('#delivery-price-chart canvas')","document.querySelector('#error').textContent");
  await evaluate('window.__noChart=false');
  await evaluate('window.__noOpening=true');const before=await evaluate('window.__periods.length');
  await click('#nav-market');await click('#nav-delivery');
  await check('a catalog with no opening says there are no delivery prices yet and reads none',
   `window.__periods.length===${before}&&document.querySelector('#delivery-price-empty-title').textContent==='No delivery prices yet'&&!document.querySelector('#delivery-price-empty').hidden&&document.querySelector('#delivery-price-head').hidden&&!document.querySelector('#delivery-price-results tbody tr')&&document.querySelector('#error').hidden`);
  await evaluate('window.__noOpening=false');
  await click('#profile-open');await set('#zone',zone);await submit('#preferences-form');
 }finally{await command('Page.removeScriptToEvaluateOnNewDocument',{identifier});await command('Emulation.clearDeviceMetricsOverride');}
}
