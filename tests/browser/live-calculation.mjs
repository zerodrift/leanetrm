import {sideSession} from './side-session.mjs';

export async function run({command,evaluate,wait,click,persona,set,submit,check,shot,idle,url}){
 await command('Runtime.enable');await command('Page.enable');await command('Network.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.liveResponses=[];window.liveRequests=[];window.liveTasks=[];new PerformanceObserver(list=>window.liveTasks.push(...list.getEntries().map(e=>e.duration))).observe({type:'longtask',buffered:true});{const fetch=window.fetch;window.fetch=async(path,init)=>{window.liveRequests.push(String(path));if(window.holdShapes&&String(path)==='/api/market/curve-shapes'){window.holdShapes=false;await new Promise(resolve=>window.releaseShapes=resolve);}if(window.failLiveWorkspace&&String(path)==='/api/trading/workspace'){window.failLiveWorkspace=false;return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}if((window.failLivePreview&&String(path)==='/api/custom-curves/preview')||(window.failLiveEditor&&['/api/swaps/editor','/api/futures/editor'].includes(String(path)))){window.failLivePreview=false;window.failLiveEditor=false;return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}let response=await fetch(path,init);if(window.refuseDesk&&String(path)==='/api/trading/workspace'&&response.ok){window.refuseDesk=false;const data=await response.json(),market=data.summaries[3],swap=market.groups.find(g=>g.instrumentType==='financial_swap');market.groups.push({...swap,quantitySpecification:{...swap.quantitySpecification,code:'other_quantity'},unit:'other unit'});for(const group of data.summaries[0].groups)if(group.instrumentType==='future')group.executionPnl='4242.5';for(const holding of data.blotter.holdings)if(holding.instrumentType==='future')holding.executionPrice='9.995';response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}if(response.headers.get('content-type')?.includes('application/json'))window.liveResponses.push({path:String(path),data:await response.clone().json()});return response;};}`});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 // A risk analyst opens on the Blotter, and a live correction refreshes the page that is showing, so start on Curves & prices.
 await persona('risk');await click('#nav-market');await click('#example');await click('#contracts tbody tr:first-child button');
 await click('#follow-live');await wait("document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 await check('live mode preserves the shared header and open price history',"document.querySelector('#workspace-mode-label').textContent.includes('Live corrections')&&!document.querySelector('#history-card').hidden&&document.querySelectorAll('#history-values tbody tr').length===window.liveResponses.filter(r=>r.path==='/api/history').at(-1).data.points.length&&document.querySelectorAll('#history-values tbody tr').length>=2&&document.querySelector('#error').hidden");
 const actor=await sideSession(url,'editor');
 async function correct(series,value){
  const data=await actor.call('workspace');let frame=data.frames.find(f=>f.at.startsWith('2020-12-18'));
  const seriesId=series==='base'?'60000000-0000-0000-0000-000000000001':data.definitions.series.find(s=>s.code===series).id;
  let fact=data.observations.find(o=>o.series_id===seriesId&&frame.observationIds.includes(o.id));
  if(!fact&&series==='base')fact=data.observations.find(o=>o.id==='70000000-0000-0000-0000-000000000001');
  while(data.observations.some(o=>o.corrects_id===fact.id))fact=data.observations.find(o=>o.corrects_id===fact.id);
  if(!frame.observationIds.includes(fact.id))frame=(await actor.call('select-observation',{snapshotId:frame.id,observationId:fact.id})).frame;
  return actor.call('corrections',{snapshotId:frame.id,observationId:fact.id,value});
 }
 const started=performance.now();await correct('base','3.3');
 await wait("window.liveResponses.filter(r=>r.path==='/api/evaluate').at(-1)?.data.points?.[0].value==='31.2'&&document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 console.log('LIVE synthetic correction to rendered dependent curve milliseconds:',Math.round(performance.now()-started));
 await check('a correction updates dependent prices and history without browser polling',"document.querySelector('#error').hidden&&document.querySelector('#contracts').textContent.includes('31.2')&&window.liveResponses.filter(r=>r.path==='/api/history').at(-1).data.points.at(-1).value==='31.2'&&document.querySelector('#live-status').textContent.includes('last refreshed')");
 await click('#nav-custom-curves');await click('#nav-market');
 await wait("document.querySelector('#live-status').dataset.state==='live'&&document.querySelector('#contracts').textContent.includes('31.2')&&"+idle);
 await check('returning to a live market screen refreshes its displayed generation',"document.querySelector('#error').hidden&&document.querySelector('#contracts').textContent.includes('31.2')");
 await click('#return-working');await persona('editor');await click('#example');await click('#nav-custom-curves');await set('#custom-curves-template','ng_prompt');await click('#custom-curves-preview');
 const nodes=await evaluate("document.querySelectorAll('.custom-curve-node').length");
 await click('#follow-live');await wait("document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 await check('entering live preserves the selected unsaved custom graph and its preview',`document.querySelectorAll('.custom-curve-node').length===${nodes}&&!document.querySelector('#custom-curves-result').hidden&&document.querySelector('#error').hidden`);
 await correct('custom_ng_monthly_2','3.18');
 await wait("window.liveResponses.filter(r=>r.path==='/api/custom-curves/preview').at(-1)?.data.rows?.[0]?.[1]==='3.18'&&document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 await evaluate('window.failLivePreview=true');await correct('custom_ng_monthly_2','3.28');
 await wait("document.querySelector('#live-status').dataset.state==='stale'&&"+idle);
 await check('a failed update stays visibly stale with its last successful value and time',"document.querySelector('#live-status').textContent.includes('last refreshed')&&!document.querySelector('#live-retry').hidden&&document.querySelector('#custom-curves-values').textContent.includes('3.18')");
 await shot('live-calculation-stale');await click('#live-retry');
 await wait("window.liveResponses.filter(r=>r.path==='/api/custom-curves/preview').at(-1)?.data.rows?.[0]?.[1]==='3.28'&&document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 await check('Retry runs the failed backend preview before marking it live',"document.querySelector('#custom-curves-values').textContent.includes('3.28')");
 const before=await evaluate("window.liveResponses.filter(r=>r.path==='/api/workspace/navigation').at(-1).data.context.knownAt");
 await correct('custom_ng_monthly_2','3.38');await click('#nav-schedules');
 await check('pausing pins the displayed cutoff in both server and header before other screens read',`window.liveResponses.filter(r=>r.path==='/api/workspace/navigation').at(-1).data.context.knownAt===${JSON.stringify(before)}&&window.liveResponses.filter(r=>r.path==='/api/workspace/navigation').at(-1).data.live===null&&document.querySelector('#live-status').textContent.includes('Paused')&&document.querySelector('#error').hidden`);
 await persona('risk');await click('#example');await click('#nav-futures');await click('#follow-live');
 await wait("document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 // A refresh that fails while a curve read is still waiting must not leave the kept desk loading for ever.
 const shapesLoading="document.querySelectorAll('#desk-positions .sparkline-loading').length";
 await evaluate('window.holdShapes=true');await correct('base','3.35');
 await wait(`typeof window.releaseShapes==='function'&&${shapesLoading}>0&&document.querySelector('#live-status').dataset.state==='live'&&`+idle);
 await evaluate('window.failLiveWorkspace=true');await correct('base','3.36');
 await wait("document.querySelector('#live-status').dataset.state==='stale'&&"+idle);
 await check('a refresh that fails while the curves are being read leaves each unavailable with the reason, not loading',"(()=>{const lines=[...document.querySelectorAll('#desk-positions svg.sparkline')],rows=document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)');return rows.length>0&&lines.length===rows.length&&document.querySelectorAll('#desk-positions .sparkline-loading').length===0&&lines.every(s=>s.classList.contains('sparkline-unavailable')&&s.querySelector('title').textContent.includes('refresh'));})()","[...document.querySelectorAll('#desk-positions .desk-cell:has(.sparkline), #desk-positions .sparkline-loading')].map(n=>n.className+' '+n.textContent).slice(0,3)");
 await evaluate('window.releaseShapes()');
 await click('#live-retry');await wait(`document.querySelector('#live-status').dataset.state==='live'&&${shapesLoading}===0&&`+idle);
 await check('Retry reads the curves again',"document.querySelectorAll('#desk-positions svg.sparkline:not(.sparkline-unavailable)').length>0");
 // A read the desk refuses leaves the desk it had: its new totals and rows do not appear beside its old charts.
 // The injected read has new futures totals and prices, and a swap market held in a second quantity specification, which the charts refuse.
 const deskNow="JSON.stringify({tiles:[...document.querySelectorAll('.desk-tile')].map(t=>t.textContent),prices:[...document.querySelectorAll('#desk-positions [tabulator-field=c3] .desk-figure')].map(n=>n.textContent),charts:document.querySelectorAll('#desk-chart-pv canvas,#desk-chart-position canvas').length,curves:document.querySelectorAll('#desk-positions svg.sparkline:not(.sparkline-unavailable)').length})";
 const deskBefore=await evaluate(deskNow);
 await evaluate('window.refuseDesk=true');await correct('base','3.37');
 await wait("document.querySelector('#live-status').dataset.state==='stale'&&"+idle);
 await check('a read the desk refuses leaves the desk it had whole, with no new totals or rows beside its old charts',`${deskNow}===${JSON.stringify(deskBefore)}&&!document.querySelector('#desk').textContent.includes('4,242.500')&&!document.querySelector('#desk-positions').textContent.includes('9.995')`,deskNow);
 await click('#live-retry');await wait("document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 // A swap's Terms are read in the live context too, with its valuation and schedules, as a form of its amendment would be read in working data.
 const swapReference=await evaluate("window.liveResponses.filter(r=>r.path==='/api/trading/workspace').at(-1).data.blotter.holdings.find(h=>h.instrumentType==='financial_swap').tradeReference");
 await set('#blotter-filters .table-search',swapReference);await click('#desk-positions .tabulator-row:not(.tabulator-group)');
 await click('#side-panel-tabs-terms');await wait("!!document.querySelector('#side-panel-view .side-panel-section')&&"+idle);
 await check('a swap\'s Terms are read in a live context: its structure, each side, and its valuation and schedules, with no error',
  `(()=>{const titles=[...document.querySelectorAll('#side-panel-view .side-panel-section>h3')].map(n=>n.textContent);return titles[0]==='Trade'&&titles[1]==='Structure'&&titles.includes('Valuation & schedules')&&document.querySelectorAll('#side-panel-view .swap-schedule').length>=1&&!document.querySelector('#side-panel-view .side-panel-error')&&document.querySelector('#error').hidden;})()`,
  "[document.querySelector('#side-panel-view').innerText.slice(0,300),document.querySelector('#error').textContent]");
 await click('#side-panel-close');await set('#blotter-filters .table-search','');
 // A position in a market Curves & prices lists, so that its Open curve is available to hold focus below.
 const reference=await evaluate("(async()=>{const listed=new Set((await(await fetch('/api/workspace/navigation')).json()).curves.map(c=>c.code));return window.liveResponses.filter(r=>r.path==='/api/trading/workspace').at(-1).data.blotter.holdings.find(h=>listed.has(h.curve)).tradeReference;})()");
 await set('#blotter-filters .table-search',reference);await click('#desk-positions .tabulator-row:not(.tabulator-group)');
 // A risk analyst cannot amend: the panel offers no Amend and says why, in words, where Amend would be.
 await check('an account that cannot amend is not offered Amend, and the panel says in words why',
  "(()=>{const amend=document.querySelector('#side-panel-amend'),note=document.querySelector('#side-panel-amend-note');return amend.hidden&&amend.getClientRects().length===0&&!note.hidden&&note.getClientRects().length>0&&note.textContent==='This account cannot amend trades in this market context.';})()",
  "document.querySelector('#side-panel-head').innerText");
 // The panel's Terms are read once, in the live context, and a live correction reads them again with the rest of the desk.
 await click('#side-panel-tabs-terms');await wait("!!document.querySelector('#side-panel-view .side-panel-section')&&"+idle);
 const editorReads="window.liveRequests.filter(p=>['/api/swaps/editor','/api/futures/editor'].includes(p)).length",readsAtOpen=await evaluate("window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length"),termsRead=await evaluate(editorReads);
 await correct('base','3.39');
 await wait(`window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length>${readsAtOpen}&&document.querySelector('#live-status').dataset.state==='live'&&`+idle);
 await check('a live correction reads the panel\'s terms again, with the desk, since the person has no edits in it',`${editorReads}===${termsRead}+1&&!!document.querySelector('#side-panel-view .side-panel-section')&&!document.querySelector('#side-panel-view .side-panel-error')&&document.querySelector('#notice').hidden`,`[${editorReads},${termsRead},document.querySelector('#notice').textContent]`);
 await evaluate('window.failLiveEditor=true');await correct('base','3.4');
 await wait("document.querySelector('#live-status').dataset.state==='stale'&&"+idle);
 await check('a refresh whose read of the panel\'s terms fails leaves the page stale, and the panel says what failed, with Retry',"!!document.querySelector('#side-panel-view .side-panel-error')&&!!document.querySelector('#side-panel-retry')&&!document.querySelector('#live-retry').hidden");
 await click('#live-retry');
 await wait("document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 await check('Retry reads the panel\'s terms again before reporting Live',`${editorReads}===${termsRead}+3&&!!document.querySelector('#side-panel-view .side-panel-section')&&!document.querySelector('#side-panel-view .side-panel-error')`,`[${editorReads},${termsRead}]`);
 // The side panel follows the live refresh: it shows its position from the newest read of the desk.
 await check('the side panel is drawn again from the newest read when a live correction refreshes the desk',`(()=>{const reads=window.liveResponses.filter(r=>r.path==='/api/trading/workspace'),h=reads.at(-1).data.blotter.holdings.find(h=>h.tradeReference===${JSON.stringify(reference)});return reads.length>${readsAtOpen}&&!document.querySelector('#side-panel').hidden&&document.querySelector('#side-panel-head .side-panel-reference').textContent===${JSON.stringify(reference)}&&document.querySelector('.side-panel-value .kpi-value').title===(h.instrumentType==='financial_swap'?h.presentValue:h.executionPnl)+' '+h.currency;})()`);
 // A live refresh holds the page busy, which disables the panel's controls while it is drawn again: focus on one comes back with it.
 // The profile dialog of the persona switches may still be open, and it is modal: the panel can take focus once it is closed.
 if(await evaluate("document.querySelector('#profile-dialog').open"))await click('#profile-close');
 // Each correction below is one more refresh of the desk, waited for from the reads there were before it.
 const workspaceReads="window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length";
 const refreshedBy=async value=>{const before=await evaluate(workspaceReads);await correct('base',value);await wait(`${workspaceReads}>${before}&&document.querySelector('#live-status').dataset.state==='live'&&`+idle);};
 await evaluate("document.querySelector('#side-panel-tabs-terms').focus()");
 await refreshedBy('3.41');
 await check('keyboard focus on a tab of the panel survives a live refresh, which disables it while the page is busy and draws the panel again',
  "document.activeElement.id==='side-panel-tabs-terms'",
  "[document.activeElement.id,document.activeElement.tagName]");
 // Open curve is the head's own button, enabled for this market, and keeps focus the same way.
 await check('Open curve is available for the position, so that focus can be held on a button of the panel\'s head',"!document.querySelector('#side-panel-open-curve').disabled","document.querySelector('#side-panel-head').innerText");
 await evaluate("document.querySelector('#side-panel-open-curve').focus()");
 await refreshedBy('3.415');
 await check('and so does keyboard focus on a button of the panel\'s head',"document.activeElement.id==='side-panel-open-curve'&&!document.querySelector('#side-panel-open-curve').disabled","[document.activeElement.id,document.activeElement.tagName]");
 // Focus the person has moved off the panel is not taken back by a refresh.
 await evaluate("(()=>{const title=document.querySelector('#page-title');title.tabIndex=-1;title.focus();})()");
 await refreshedBy('3.43');
 await check('while focus that is elsewhere stays there: a refresh does not take it back to the panel',
  "document.activeElement.id==='page-title'",
  "[document.activeElement.id,document.activeElement.tagName]");
 // A risk run belongs to the inputs it was made on: a live correction clears it and says so, and a refresh that then fails keeps the note.
 const riskNote='Risk results cleared · live corrections changed the inputs';
 const runRisk=async()=>{
  await click('#desk-risk-run');
  const factor=await evaluate("(()=>{const c=window.liveResponses.filter(r=>r.path==='/api/portfolio/risk/catalog').at(-1).data,source=c.factors.find(f=>f.curves.includes('future')&&f.deliveryFrom==='2021-01-01');return c.scenarioTargets.find(t=>t.id===source.id&&JSON.stringify(t.factor)===JSON.stringify(source.factor)).id;})()");
  await set('#risk-factor',factor);await set('#risk-bump','0.1');await submit('#risk-form');await wait("document.querySelector('#risk-results .tabulator-row')");
 };
 await runRisk();
 await correct('base','3.371');
 await wait("document.querySelector('#live-status').dataset.state==='live'&&!document.querySelector('#risk-results .tabulator-row')&&"+idle);
 await check('a live correction clears a risk run, whose inputs it changed, and the page says so',`!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes(${JSON.stringify(riskNote)})&&document.querySelector('#desk-tile-risk .desk-risk-state').textContent==='Not run at this close'`,"[document.querySelector('#notice').textContent,document.querySelector('#desk-tile-risk').textContent]");
 await runRisk();
 await evaluate('window.failLiveWorkspace=true');await correct('base','3.372');
 await wait("document.querySelector('#live-status').dataset.state==='stale'&&"+idle);
 await check('a refresh that fails after it cleared a risk run keeps the note that says so',`!document.querySelector('#risk-results .tabulator-row')&&!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes(${JSON.stringify(riskNote)})`,"[document.querySelector('#notice').hidden,document.querySelector('#notice').textContent,!!document.querySelector('#risk-results .tabulator-row')]");
 await click('#live-retry');await wait("document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 console.log('LIVE browser synthetic workload:',JSON.stringify(await evaluate("({requests:window.liveRequests.length,longTasks:window.liveTasks.length,maxLongTaskMilliseconds:Math.max(0,...window.liveTasks),heapBytes:performance.memory?.usedJSHeapSize})")));
}
