// The Dashboard (docs/market-dashboard.md): one widget of each kind added through Add widget, the same widgets under Board and Morning,
// the layout kept across a reload, a Positions definition run now until its ladder shows, a past context read only with no Run now, and a
// configured curve the close does not return refused in its own tile while the other widgets render.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,errors,url}){
  await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  // The curve-shapes replies, and the curve a check asks to be left out of them.
  await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.__shapeReads=[];const send=window.fetch;window.fetch=async(input,init)=>{
    const response=await send(input,init);
    if(String(input)!=='/api/market/curve-shapes'||!response.ok)return response;
    const data=await response.json();
    if(window.__withoutCurve)data.curves=data.curves.filter(c=>c.curve!==window.__withoutCurve);
    window.__shapeReads.push({curves:JSON.parse(init.body).curves,shapes:data.curves});
    return new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});};`});
  await command('Page.navigate',{url:url+'/'});await wait(idle);
  await persona('risk');
  await click('#nav-dashboard');
  await check('the Dashboard opens with no widgets, says what it reads and offers the three kinds',
    "!document.querySelector('#dashboard-page').hidden&&document.querySelector('#status').textContent.startsWith('Dashboard · ')&&document.querySelector('#dashboard-widgets').textContent.includes('No widgets')&&[...document.querySelector('#dashboard-add select[aria-label=\"Widget kind\"]').options].map(o=>o.value).join()==='latest_levels,price_chart,positions'&&document.querySelector('#error').hidden");
  const add='#dashboard-add',kind=add+' select[aria-label="Widget kind"]',open=()=>evaluate("document.querySelector('#dashboard-add details').open=true");
  const widgets="[...document.querySelectorAll('#dashboard-widgets .dashboard-widget h3')].map(h=>h.textContent).sort().join()";
  const added=count=>wait(`document.querySelectorAll('#dashboard-widgets .dashboard-widget').length===${count}&&${idle}`);
  const levels=['future','local_power'];
  await open();await set(kind,'latest_levels');
  await evaluate(`for(const box of document.querySelectorAll('#dashboard-add .dashboard-add-curve input'))box.checked=${JSON.stringify(levels)}.includes(box.value)`);
  await submit(add+' form');await added(1);
  await wait("document.querySelectorAll('.level-tile').length===2");
  await check('Latest levels shows a tile for each chosen curve, in order, with the backend\'s front price and unit, and the close\'s freshness',
    `(()=>{const tiles=[...document.querySelectorAll('.level-tile')],shapes=new Map(window.__shapeReads.at(-1).shapes.map(s=>[s.curve,s]));return tiles.map(t=>t.dataset.curve).join()===${JSON.stringify(levels.join())}&&tiles.every(t=>t.querySelector('.kpi-figure')?.textContent===shapes.get(t.dataset.curve).front.price)&&/· Latest · Intraday not configured$/.test(document.querySelector('.level-tiles-footer').textContent);})()`,
    "document.querySelector('.level-tiles')?.innerText");
  await open();await set(kind,'price_chart');await set(add+' select[aria-label="Curve"]','local_power');
  await wait("document.querySelector('#dashboard-add select[aria-label=Contract]').options.length>0");
  await submit(add+' form');await added(2);
  await wait("!!document.querySelector('.price-chart .contract-chart canvas')");
  await check('Price chart draws the chosen contract\'s history and states its basis and freshness',
    "(()=>{const text=document.querySelector('.price-chart').innerText;return text.includes('Each close with the prices it was saved with')&&/· Latest$/m.test(text);})()","document.querySelector('.price-chart').innerText");
  await open();await set(kind,'positions');
  await wait("document.querySelector('.dashboard-add-positions select[multiple]').options.length>0");
  await set('.dashboard-add-positions input:not([aria-label])','Synthetic monthly delta');
  await evaluate("for(const book of document.querySelector('.dashboard-add-positions select[multiple]').options)book.selected=true");
  await evaluate("[...document.querySelectorAll('.dashboard-add-positions button')].find(b=>b.textContent==='Read price units').click()");
  await wait("document.querySelectorAll('.dashboard-add-sizes input').length>0");
  await check('a new definition asks for a shock size for each exact price unit, none prefilled',
    "[...document.querySelectorAll('.dashboard-add-sizes input')].every(i=>i.value===''&&i.getAttribute('aria-label').startsWith('Shock size for '))");
  await evaluate("for(const size of document.querySelectorAll('.dashboard-add-sizes input'))size.value='0.5'");
  await submit(add+' form');await added(3);
  await wait("!!document.querySelector('.positions')&&!document.querySelector('.positions').textContent.includes('Reading the latest run')");
  await check('the new definition starts with no completed run, and Run now is offered',
    "document.querySelector('.positions').textContent.includes('No completed run yet')&&!document.querySelector('.positions button.dashboard-edit').hidden","document.querySelector('.positions').innerText");
  const kinds=await evaluate(widgets);
  await check('one widget of each kind was added',JSON.stringify(kinds==='Latest levels,Positions by month,Price chart'));

  await click('#dashboard-morning');
  await check('Morning shows the four sections with the same widgets',
    `document.querySelector('#dashboard-morning').getAttribute('aria-pressed')==='true'&&document.querySelectorAll('#dashboard-widgets .dashboard-section').length===4&&${widgets}===${JSON.stringify(kinds)}`);
  await click('#dashboard-board');
  await check('Board again shows the same widgets on its grid',
    `document.querySelector('#dashboard-board').getAttribute('aria-pressed')==='true'&&!!document.querySelector('#dashboard-widgets .dashboard-board')&&${widgets}===${JSON.stringify(kinds)}`);
  const saved=await evaluate("(async()=>(await(await fetch('/api/dashboard/layout')).json()))()");
  await command('Page.reload');await wait(idle);await click('#nav-dashboard');await added(3);
  await check('after a reload the saved layout is shown: the same widgets, arrangement and version',
    `(async()=>{const now=await(await fetch('/api/dashboard/layout')).json();return now.version===${JSON.stringify(saved.version)}&&now.layout.arrangement==='board'&&JSON.stringify(now.layout.widgets)===${JSON.stringify(JSON.stringify(saved.layout.widgets))}&&${widgets}===${JSON.stringify(kinds)};})()`);

  // Run now queues the definition's run; the risk worker completes it, and the widget's Reload reads it.
  await wait("!!document.querySelector('.positions')&&!document.querySelector('.positions').textContent.includes('Reading the latest run')");
  await evaluate("[...document.querySelectorAll('.positions button')].find(b=>b.textContent==='Run now').click()");
  await wait("/^The run is queued in \\d+ parts?\\./.test(document.querySelector('.positions [role=status]').textContent)");
  for(let attempt=0;attempt<60&&!await evaluate("!!document.querySelector('.positions .positions-table')");attempt++){
    await evaluate("[...document.querySelectorAll('.positions button')].find(b=>b.textContent==='Reload').click()");
    await wait("!document.querySelector('.positions').textContent.includes('Reading the latest run')",30000);
    if(!await evaluate("!!document.querySelector('.positions .positions-table')"))await new Promise(resolve=>setTimeout(resolve,1000));
  }
  await check('after Run now completes, Reload shows the run as a delta ladder by month with its freshness',
    "!!document.querySelector('.positions .positions-table tbody tr')&&/Risk run · .+ · Latest$/m.test(document.querySelector('.positions').innerText)","document.querySelector('.positions').innerText");
  await check('the Dashboard shows no internal identifiers',"!/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(document.querySelector('#dashboard-page').innerText)");
  await evaluate('window.scrollTo(0,0)');await shot('dashboard-board-desktop');

  // A past effective date, applied, is a historical context with no close chosen yet: every widget says so rather than reading another close.
  await click('#date-context>summary');await set('#date-context-on','2020-12-17');await wait(idle);await click('#date-context-latest');await click('#apply-view-context');
  await wait("document.querySelector('#environment-context-note').textContent.startsWith('Historical view')&&"+idle);
  await check('a historical context with no close chosen refuses each widget and asks for a market date',
    "[...document.querySelectorAll('#dashboard-widgets .dashboard-widget')].every(w=>w.querySelector('.dashboard-refusal')?.textContent==='Choose a market date above.')&&document.querySelectorAll('#dashboard-widgets .dashboard-widget').length===3",
    "document.querySelector('#dashboard-widgets').innerText");
  // Its close is chosen with a configured curve left out of the close's read: that curve is refused in its own tile and everything else renders.
  await evaluate("window.__withoutCurve='future'");
  await evaluate(`(()=>{const s=document.querySelector('#snapshot');s.value=[...s.options].filter(o=>o.value).at(-1).value;s.dispatchEvent(new Event('change',{bubbles:true}));})()`);await wait(idle);
  await wait("document.querySelectorAll('.level-tile').length===2&&!!document.querySelector('.price-chart .contract-chart canvas')&&!!document.querySelector('.positions')&&!document.querySelector('.positions').textContent.includes('Reading the latest run')");
  await check('a past context is read only: Run now is not offered and no layout control can be used',
    "document.querySelector('#dashboard-scope').textContent.startsWith('Read only')&&[...document.querySelectorAll('.positions button')].every(b=>b.textContent!=='Run now'||b.hidden)&&[...document.querySelectorAll('#dashboard-page .dashboard-edit')].every(c=>c.disabled)&&document.querySelector('#dashboard-board').disabled&&document.querySelector('#dashboard-morning').disabled",
    "({scope:document.querySelector('#dashboard-scope').textContent,shown:[...document.querySelectorAll('.positions button')].filter(b=>!b.hidden).map(b=>b.textContent),enabled:[...document.querySelectorAll('#dashboard-page .dashboard-edit,#dashboard-board,#dashboard-morning')].filter(c=>!c.disabled).map(c=>c.outerHTML.slice(0,60))})");
  await check('the widgets state the historical freshness of the past context',
    "/· Historical · Intraday not configured$/.test(document.querySelector('.level-tiles-footer').textContent)&&/· Historical$/m.test(document.querySelector('.price-chart').innerText)","document.querySelector('#dashboard-widgets').innerText");
  await check('a curve absent from the close is a refusal tile while the other tile, the chart and Positions render',
    "(()=>{const [absent,shown]=['future','local_power'].map(c=>document.querySelector(`.level-tile[data-curve=${c}]`));return absent.dataset.state==='refused'&&absent.textContent.includes('The curve read did not return this curve.')&&shown.dataset.state!=='refused'&&!!shown.querySelector('.kpi-figure')&&!!document.querySelector('.price-chart canvas')&&!document.querySelector('.positions .dashboard-refusal');})()",
    "document.querySelector('#dashboard-widgets').innerText");
  await evaluate("window.__withoutCurve=undefined");
  await click('#current-view-context');
  await wait("document.querySelector('#dashboard-scope').textContent.startsWith('Your dashboard')&&document.querySelectorAll('.level-tile').length===2");
  await check('returning to working data offers Run now again',"[...document.querySelectorAll('.positions button')].some(b=>b.textContent==='Run now'&&!b.hidden&&!b.disabled)");
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  await check('at 390 pixels the Dashboard does not widen the page',fitsWidth);
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await check('no uncaught JavaScript exceptions',JSON.stringify(errors.length===0));
}
