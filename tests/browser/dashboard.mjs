// The Dashboard (docs/market-dashboard.md): one widget of each kind added through Add widget, the same widgets under Board and Morning,
// the layout kept across a reload, a Positions definition run now until the widget reads its ladder by itself, a Board widget moved and
// resized from the keyboard, a curve the catalog does not have refused by the backend in its own tile, close footers that follow a newer
// close, a newer run stated at an earlier close, tied runs chosen explicitly, and a past context read only with no Run now.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,errors,url}){
  await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  // The curve-shapes reads the page makes, and the workspace calendar ending a date early while a check asks: showing that to the app stands
  // in for a close that becomes available during a session, as the market-dates check does. Every other reply is the backend's own.
  await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.__shapeReads=[];const send=window.fetch;window.fetch=async(input,init)=>{
    const path=String(input);
    // A layout read the check asks to fail, as a lost connection would.
    if(path==='/api/dashboard/layout'&&!init?.body&&window.__failLayoutRead)return new Response(JSON.stringify({code:'request_failed',message:'Synthetic failed layout read.'}),{status:503,headers:{'Content-Type':'application/json'}});
    let response=await send(input,init);
    if(path==='/api/market/curve-shapes'&&response.ok)window.__shapeReads.push({curves:JSON.parse(init.body).curves,shapes:(await response.clone().json()).curves});
    if(path==='/api/market/dates'&&sessionStorage.getItem('hideNewestDate')&&response.ok){
      const body=JSON.parse(init.body),data=await response.json();
      if(body.curve===null&&body.definition===null){data.dates.pop();data.maxDate=data.dates.at(-1)?.date??null;}
      response=new Response(JSON.stringify(data),{status:response.status,headers:{'Content-Type':'application/json'}});
    }
    return response;};
    window.__api=async(path,body)=>{const boot=await(await send('/api/bootstrap')).json();const r=await send('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify(body)});return {status:r.status,data:await r.json()};};`});
  await command('Page.navigate',{url:url+'/'});await wait(idle);
  await persona('risk');
  await click('#nav-dashboard');
  await check('the Dashboard opens with no widgets, says what it reads and offers the three kinds',
    "!document.querySelector('#dashboard-page').hidden&&document.querySelector('#status').textContent.startsWith('Dashboard · ')&&document.querySelector('#dashboard-widgets').textContent.includes('No widgets')&&[...document.querySelector('#dashboard-add select[aria-label=\"Widget kind\"]').options].map(o=>o.value).join()==='latest_levels,price_chart,positions'&&document.querySelector('#error').hidden");
  const add='#dashboard-add',kind=add+' select[aria-label="Widget kind"]',open=()=>evaluate("document.querySelector('#dashboard-add details').open=true");
  const widgets="[...document.querySelectorAll('#dashboard-widgets .dashboard-widget h3')].map(h=>h.textContent).sort().join()";
  const added=count=>wait(`document.querySelectorAll('#dashboard-widgets .dashboard-widget').length===${count}&&${idle}`);
  const positions="document.querySelector('.positions')";
  const read=`!!${positions}&&!${positions}.textContent.includes('Reading the latest run')`;
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
  await wait(read);
  await check('the new definition starts with no completed run, and Run now is offered',
    `${positions}.textContent.includes('No completed run yet')&&!${positions}.querySelector('button.dashboard-edit').hidden`,`${positions}.innerText`);
  const kinds=await evaluate(widgets);
  await check('one widget of each kind was added',JSON.stringify(kinds==='Latest levels,Positions by month,Price chart'));

  await evaluate('window.__readsBeforeArranging=window.__shapeReads.length');
  await click('#dashboard-morning');
  await check('Morning shows the four sections with the same widgets',
    `document.querySelector('#dashboard-morning').getAttribute('aria-pressed')==='true'&&document.querySelectorAll('#dashboard-widgets .dashboard-section').length===4&&${widgets}===${JSON.stringify(kinds)}`);
  await click('#dashboard-board');
  await check('Board again shows the same widgets on its grid',
    `document.querySelector('#dashboard-board').getAttribute('aria-pressed')==='true'&&!!document.querySelector('#dashboard-widgets .dashboard-board')&&${widgets}===${JSON.stringify(kinds)}`);
  await check('switching the arrangement keeps the drawn widgets and reads no curve again',
    "window.__shapeReads.length===window.__readsBeforeArranging&&document.querySelectorAll('.level-tile').length===2&&!!document.querySelector('.price-chart .contract-chart canvas')");
  const saved=await evaluate("(async()=>(await(await fetch('/api/dashboard/layout')).json()))()");
  await command('Page.reload');await wait(idle);await evaluate('window.__readsBeforeOpening=window.__shapeReads.length');await click('#nav-dashboard');await added(3);
  await wait("document.querySelectorAll('.level-tile').length===2");
  await check('after a reload the saved layout is shown: the same widgets, arrangement and version',
    `(async()=>{const now=await(await fetch('/api/dashboard/layout')).json();return now.version===${JSON.stringify(saved.version)}&&now.layout.arrangement==='board'&&JSON.stringify(now.layout.widgets)===${JSON.stringify(JSON.stringify(saved.layout.widgets))}&&${widgets}===${JSON.stringify(kinds)};})()`);
  await check('opening the Dashboard reads the curves of every Latest levels and Price chart widget in one curve-shapes read',
    // The market page behind it reads its own selected curve, so only the reads naming the chart's curve are the dashboard's.
    "window.__shapeReads.slice(window.__readsBeforeOpening).filter(r=>r.curves.includes('local_power')).map(r=>r.curves.join()).join('|')==='future,local_power'","window.__shapeReads.map(r=>r.curves)");

  // Run now queues the definition's run; the widget follows its parts and reads the run once the risk worker has finished them.
  const runNow=async()=>{await wait(read);await evaluate(`[...${positions}.querySelectorAll('button')].find(b=>b.textContent==='Run now').click()`);
    await wait(`/^The run is queued in \\d+ parts?\\./.test(${positions}.querySelector('[role=status]').textContent)`);
    await wait(`${positions}.querySelector('[role=status]').textContent===''&&${read}`,60000);};
  await runNow();
  await check('after Run now the widget reads the finished run by itself: a delta ladder by month that is the latest',
    `!!${positions}.querySelector('.positions-table tbody tr')&&/Risk run · .+ · Latest$/m.test(${positions}.innerText)&&!${positions}.innerText.includes('queued')`,`${positions}.innerText`);
  await check('the Dashboard shows no internal identifiers',"!/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(document.querySelector('#dashboard-page').innerText)");
  await evaluate('window.scrollTo(0,0)');await shot('dashboard-board-desktop');

  // The Board's Arrange controls move and size a widget from the keyboard; the save keeps the focus and redraws no widget.
  const levelsCard="[...document.querySelectorAll('#dashboard-widgets .dashboard-widget')].find(c=>c.querySelector('h3').textContent==='Latest levels')";
  const nudge=name=>`${levelsCard}.querySelector('button[data-nudge=${name}]')`;
  const box="(async()=>(await(await fetch('/api/dashboard/layout')).json()).layout.widgets.find(w=>w.kind==='latest_levels').board)()";
  const before=await evaluate(box);
  await evaluate(`${levelsCard}.querySelector('.dashboard-arrange').open=true;window.__readsBeforeArranging=window.__shapeReads.length`);
  await check('at the grid\'s top left a widget offers no move left or up',`${nudge('left')}.disabled&&${nudge('up')}.disabled&&!${nudge('down')}.disabled&&!${nudge('wider')}.disabled`);
  const press=async name=>{await evaluate(`${nudge(name)}.focus()`);
    for(const type of ['keyDown','keyUp'])await command('Input.dispatchKeyEvent',{type,key:'Enter',code:'Enter',windowsVirtualKeyCode:13,...(type==='keyDown'?{text:'\r'}:{})});
    await wait(`document.activeElement?.dataset.nudge===${JSON.stringify(name)}&&!document.activeElement.disabled&&${idle}`);};
  await press('down');await press('narrower');
  await check('Move down and Narrower, pressed from the keyboard, save the widget\'s box and keep the focus on the control',
    `(async()=>{const now=await ${box};return now.x===${before.x}&&now.y===${before.y+1}&&now.w===${before.w-1}&&now.h===${before.h}&&${levelsCard}.style.gridRow.startsWith('${before.y+2} ')&&${levelsCard}.style.gridColumn.endsWith('span ${before.w-1}');})()`,
    `(async()=>({box:await ${box},row:${levelsCard}.style.gridRow,column:${levelsCard}.style.gridColumn,focus:document.activeElement?.outerHTML.slice(0,80)}))()`);
  await check('moving a widget redraws none of them and reads no curve again',
    "window.__shapeReads.length===window.__readsBeforeArranging&&document.querySelectorAll('.level-tile').length===2&&!!document.querySelector('.price-chart .contract-chart canvas')");
  await command('Page.reload');await wait(idle);await click('#nav-dashboard');await added(3);
  await check('after a reload the widget keeps its new place and size',
    `${levelsCard}.style.gridRow.startsWith('${before.y+2} ')&&${levelsCard}.style.gridColumn.endsWith('span ${before.w-1}')`,`[${levelsCard}.style.gridRow,${levelsCard}.style.gridColumn]`);

  // A curve the catalog does not have, saved in the layout: the backend refuses it in the curve read, and only its own tile and chart refuse.
  const layout=await evaluate("(async()=>await(await fetch('/api/dashboard/layout')).json())()");
  const absent=[{id:'00000000-0000-4000-8000-00000000a001',kind:'latest_levels',config:{curves:['no_such_curve','local_power']},board:{x:0,y:40,w:6,h:4},morning:{section:'missed',order:0}},
    {id:'00000000-0000-4000-8000-00000000a002',kind:'price_chart',config:{curve:'no_such_curve',contractId:'00000000-0000-4000-8000-00000000a0c1'},board:{x:6,y:40,w:6,h:4},morning:{section:'missed',order:1}}];
  const saveLayout=async(widgets,expected)=>(await evaluate(`window.__api('dashboard/layout',${JSON.stringify({requestId:crypto.randomUUID(),expectedVersion:expected,layout:{...layout.layout,widgets}})})`)).data.version;
  const withAbsent=await saveLayout([...layout.layout.widgets,...absent],layout.version);
  await click('#dashboard-reload');await added(5);
  const absentCard=index=>`document.querySelectorAll('#dashboard-widgets .dashboard-widget')[${3+index}]`;
  await wait(`[0,1].every(i=>!/Reading the/.test(document.querySelectorAll('#dashboard-widgets .dashboard-widget')[3+i]?.textContent??'Reading the'))&&!!document.querySelector('.price-chart canvas')`,20000);
  await check('a curve the catalog does not have is the backend\'s refusal in its own tile and in the chart, while every other widget renders',
    `(()=>{const shape=window.__shapeReads.at(-1).shapes.find(s=>s.curve==='no_such_curve'),[absent,shown]=${absentCard(0)}.querySelectorAll('.level-tile');return !!shape&&shape.front==null&&absent.dataset.state==='refused'&&!absent.textContent.includes('did not return')&&absent.querySelector('.level-tile-reason').textContent.length>0&&shown.dataset.state!=='refused'&&${absentCard(1)}.querySelector('.dashboard-refusal').textContent.length>0&&document.querySelectorAll('.level-tile:not([data-state=refused])').length===3&&!!document.querySelector('.price-chart canvas')&&!!${positions}.querySelector('.positions-table');})()`,
    `({shape:window.__shapeReads.at(-1).shapes.find(s=>s.curve==='no_such_curve'),widgets:document.querySelector('#dashboard-widgets').innerText})`);
  await saveLayout(layout.layout.widgets,withAbsent);await click('#dashboard-reload');await added(3);await wait(read);

  // The calendar first ends a date early, so the page opens on the close before the newest, which it calls the latest. A newer close
  // found when the window is focused again turns every close footer to Newer available without reading any widget again.
  await evaluate("sessionStorage.setItem('hideNewestDate','1')");await command('Page.reload');await wait(idle);await click('#nav-dashboard');await added(3);
  await wait(`document.querySelectorAll('.level-tile').length===2&&!!document.querySelector('.price-chart .contract-chart canvas')&&${read}`);
  await check('at the close before the newest, which the calendar calls the latest, the close footers say Latest',
    "document.querySelector('#market-date-badge').textContent==='Latest close'&&/· Latest · Intraday not configured$/.test(document.querySelector('.level-tiles-footer').textContent)&&/· Latest$/m.test(document.querySelector('.price-chart').innerText)");
  await evaluate('window.__readsBeforeFocus=window.__shapeReads.length;sessionStorage.removeItem("hideNewestDate");window.dispatchEvent(new Event("focus"))');
  await wait("document.querySelector('#market-date-badge').textContent==='Newer close available'");
  await check('a newer close found after the widgets were drawn turns every close footer to Newer available, with no read',
    "/· Newer available · Intraday not configured$/.test(document.querySelector('.level-tiles-footer').textContent)&&/· Newer available$/m.test(document.querySelector('.price-chart').innerText)&&window.__shapeReads.length===window.__readsBeforeFocus",
    "[document.querySelector('.level-tiles-footer').textContent,document.querySelector('.price-chart').innerText]");
  // At this earlier close in working data, the run valued at the newest close is a newer completed run than any shown here.
  await check('in working data at an earlier close, the newer completed run is stated though no run is shown yet',
    `${positions}.textContent.includes('No completed run at or before this close')&&${positions}.textContent.includes('A newer completed run is available')`,`${positions}.innerText`);
  await runNow();
  await check('a run at the earlier close is shown as Newer available, with the newer run stated',
    `!!${positions}.querySelector('.positions-table tbody tr')&&/Risk run · .+ · Newer available$/m.test(${positions}.innerText)&&${positions}.textContent.includes('A newer completed run is available')`,`${positions}.innerText`);
  // Run again at the same close: two completed runs share the latest valuation time, and the person chooses which to show.
  await runNow();
  await check('two completed runs at one valuation time are listed, each with its version and known time, and none is shown',
    `(()=>{const items=[...${positions}.querySelectorAll('.positions-tie li')].map(li=>li.firstChild.textContent),labels=[...${positions}.querySelectorAll('.positions-tie button')].map(b=>b.getAttribute('aria-label'));
      return ${positions}.textContent.includes('2 completed runs share the latest valuation time')&&items.length===2&&items.every((text,i)=>text.startsWith('Run '+(i+1)+' of 2 · version ')&&/ known .+:\\d\\d:\\d\\d/.test(text))
        &&new Set(items).size===2&&new Set(labels).size===2&&!${positions}.querySelector('.positions-table');})()`,`${positions}.innerText`);
  await evaluate(`${positions}.querySelector('.positions-tie li:last-child button').click()`);await wait(read);
  await check('the run chosen from the tie is shown as the chosen run, never as Latest',
    `!!${positions}.querySelector('.positions-table tbody tr')&&/Risk run · .+ · Chosen run$/m.test(${positions}.innerText)&&!/· Latest$/m.test(${positions}.innerText)&&${positions}.textContent.includes('You chose this run')`,`${positions}.innerText`);

  // Newer close available selects the newest close. When the dashboard's read then fails, no figure read at the earlier close is left
  // under the newest close's label; Reload reads every widget at it.
  await evaluate('window.__failLayoutRead=true');await click('#market-date-badge button');
  await wait("document.querySelector('#market-date-badge').textContent==='Latest close'");
  await check('a failed read after a close change leaves no figures of the earlier close under the new close\'s label',
    "!document.querySelector('#error').hidden&&document.querySelectorAll('#dashboard-widgets .level-tile,#dashboard-widgets .price-chart,#dashboard-widgets .positions,#dashboard-widgets .fine-print').length===0",
    "({error:document.querySelector('#error').textContent,widgets:document.querySelector('#dashboard-widgets').innerText})");
  await evaluate('window.__failLayoutRead=false');await click('#dashboard-reload');
  await wait(`document.querySelector('#market-date-badge').textContent==='Latest close'&&document.querySelectorAll('.level-tile').length===2&&${read}`);
  await check('selecting the newer close reads every widget at it, and the run valued there is the latest again',
    `/· Latest · Intraday not configured$/.test(document.querySelector('.level-tiles-footer').textContent)&&/Risk run · .+ · Latest$/m.test(${positions}.innerText)`,`${positions}.innerText`);
  // A past effective date, applied, is a historical context with no close chosen yet: every widget says so rather than reading another close.
  await click('#date-context>summary');await set('#date-context-on','2020-12-17');await wait(idle);await click('#date-context-latest');await click('#apply-view-context');
  await wait("document.querySelector('#environment-context-note').textContent.startsWith('Historical view')&&"+idle);
  await check('a historical context with no close chosen refuses each widget and asks for a market date',
    "[...document.querySelectorAll('#dashboard-widgets .dashboard-widget')].every(w=>w.querySelector('.dashboard-refusal')?.textContent==='Choose a market date above.')&&document.querySelectorAll('#dashboard-widgets .dashboard-widget').length===3&&document.querySelector('#error').hidden",
    "document.querySelector('#dashboard-widgets').innerText");
  await evaluate(`(()=>{const s=document.querySelector('#snapshot');s.value=[...s.options].filter(o=>o.value).at(-1).value;s.dispatchEvent(new Event('change',{bubbles:true}));})()`);await wait(idle);
  await wait(`document.querySelectorAll('.level-tile').length===2&&!!document.querySelector('.price-chart .contract-chart canvas')&&${read}`);
  await check('a past context is read only: Run now is not offered and no layout control can be used',
    `document.querySelector('#dashboard-scope').textContent.startsWith('Read only')&&[...${positions}.querySelectorAll('button')].every(b=>b.textContent!=='Run now'||b.hidden)&&[...document.querySelectorAll('#dashboard-page .dashboard-edit')].every(c=>c.disabled)&&document.querySelector('#dashboard-board').disabled&&document.querySelector('#dashboard-morning').disabled`,
    `({scope:document.querySelector('#dashboard-scope').textContent,shown:[...${positions}.querySelectorAll('button')].filter(b=>!b.hidden).map(b=>b.textContent),enabled:[...document.querySelectorAll('#dashboard-page .dashboard-edit,#dashboard-board,#dashboard-morning')].filter(c=>!c.disabled).map(c=>c.outerHTML.slice(0,60))})`);
  await check('the widgets state the historical freshness of the past context',
    "/· Historical · Intraday not configured$/.test(document.querySelector('.level-tiles-footer').textContent)&&/· Historical$/m.test(document.querySelector('.price-chart').innerText)","document.querySelector('#dashboard-widgets').innerText");
  await click('#current-view-context');
  await wait("document.querySelector('#dashboard-scope').textContent.startsWith('Your dashboard')&&document.querySelectorAll('.level-tile').length===2");
  await check('returning to working data offers Run now again',`[...${positions}.querySelectorAll('button')].some(b=>b.textContent==='Run now'&&!b.hidden&&!b.disabled)`);
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  await check('at 390 pixels the Dashboard does not widen the page',fitsWidth);
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await check('no uncaught JavaScript exceptions',JSON.stringify(errors.length===0));
}
