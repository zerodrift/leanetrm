import {sideSession} from './side-session.mjs';

export async function run({command,evaluate,wait,click,persona,set,submit,check,shot,idle,url}){
 await command('Runtime.enable');await command('Page.enable');await command('Network.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.liveResponses=[];window.liveRequests=[];window.liveTasks=[];new PerformanceObserver(list=>window.liveTasks.push(...list.getEntries().map(e=>e.duration))).observe({type:'longtask',buffered:true});{const fetch=window.fetch;window.fetch=async(path,init)=>{window.liveRequests.push(String(path));if(window.holdShapes&&String(path)==='/api/market/curve-shapes'){window.holdShapes=false;await new Promise(resolve=>window.releaseShapes=resolve);}if(window.failLiveWorkspace&&String(path)==='/api/trading/workspace'){window.failLiveWorkspace=false;return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}if((window.failLivePreview&&String(path)==='/api/custom-curves/preview')||(window.failLiveEditor&&['/api/swaps/editor','/api/futures/editor'].includes(String(path)))){window.failLivePreview=false;window.failLiveEditor=false;return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}let response=await fetch(path,init);if(window.loseRiskJob&&String(path)==='/api/portfolio/risk/jobs'&&init?.method==='POST'&&response.ok){window.loseRiskJob=false;throw new TypeError('Synthetic lost risk job reply');}if(window.dropRiskTarget&&String(path)==='/api/portfolio/risk/catalog'&&response.ok){const data=await response.json(),id=window.dropRiskTarget;window.dropRiskTarget=undefined;data.scenarioTargets=data.scenarioTargets.filter(t=>t.id!==id);response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}if(window.refuseDesk&&String(path)==='/api/trading/workspace'&&response.ok){window.refuseDesk=false;const data=await response.json(),market=data.summaries[3],swap=market.groups.find(g=>g.instrumentType==='financial_swap');market.groups.push({...swap,quantitySpecification:{...swap.quantitySpecification,code:'other_quantity'},unit:'other unit'});for(const group of data.summaries[0].groups)if(group.instrumentType==='future')group.executionPnl='4242.5';for(const holding of data.blotter.holdings)if(holding.instrumentType==='future')holding.executionPrice='9.995';response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}if(response.headers.get('content-type')?.includes('application/json'))window.liveResponses.push({path:String(path),data:await response.clone().json()});return response;};}`});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 // A risk analyst opens on the Blotter, and a live correction refreshes the page that is showing, so start on Curves & prices.
 await persona('risk');await click('#nav-market');await click('#example');await click('#contracts tbody tr:first-child');
 await click('#follow-live');await wait("document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 await check('live mode preserves the shared header and open price history',"document.querySelector('#workspace-mode-label').textContent.includes('Live corrections')&&!document.querySelector('#contract-panel').hidden&&document.querySelectorAll('#history-values tbody tr').length===window.liveResponses.filter(r=>r.path==='/api/history').at(-1).data.points.length&&document.querySelectorAll('#history-values tbody tr').length>=2&&document.querySelector('#error').hidden");
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
 await check('a correction updates dependent prices and history without browser polling',"document.querySelector('#error').hidden&&document.querySelector('#contracts').textContent.includes('31.2')&&window.liveResponses.filter(r=>r.path==='/api/history').at(-1).data.points.at(-1).value==='31.2'&&!document.querySelector('#contract-panel').hidden&&document.querySelector('#history-values tbody tr:last-child td:nth-child(2)').textContent.startsWith('31.2')&&document.querySelector('#live-status').textContent.includes('last refreshed')");
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
 // The template's graph is an unsaved edit, which the question asks about before the page is left.
 await click('#custom-curves-panel-prompt button:not(.secondary)');
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
 // The curves are read after the rows are drawn, so wait for them before saying Retry read them.
 await wait(`document.querySelectorAll('#desk-positions svg.sparkline:not(.sparkline-unavailable)').length>0&&${shapesLoading}===0`);
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
 await click('#position-details-tabs-terms');await wait("!!document.querySelector('#position-details-view .side-panel-section')&&"+idle);
 await check('a swap\'s Terms are read in a live context: its structure, each side, and its valuation and schedules, with no error',
  `(()=>{const titles=[...document.querySelectorAll('#position-details-view .side-panel-section>h3')].map(n=>n.textContent);return titles[0]==='Trade'&&titles[1]==='Structure'&&titles.includes('Valuation & schedules')&&document.querySelectorAll('#position-details-view .swap-schedule').length>=1&&!document.querySelector('#position-details-view .side-panel-error')&&document.querySelector('#error').hidden;})()`,
  "[document.querySelector('#position-details-view').innerText.slice(0,300),document.querySelector('#error').textContent]");
 await click('#position-details .position-close');await set('#blotter-filters .table-search','');
 // A position in a market Curves & prices lists, so that its Open curve is available to hold focus below.
 const reference=await evaluate("(async()=>{const listed=new Set((await(await fetch('/api/workspace/navigation')).json()).curves.map(c=>c.code));return window.liveResponses.filter(r=>r.path==='/api/trading/workspace').at(-1).data.blotter.holdings.find(h=>listed.has(h.curve)).tradeReference;})()");
 await set('#blotter-filters .table-search',reference);await click('#desk-positions .tabulator-row:not(.tabulator-group)');
 // A risk analyst cannot amend: the panel offers no Amend and says why, in words, where Amend would be.
 await check('an account that cannot amend is not offered Amend, and the panel says in words why',
  "(()=>{const amend=document.querySelector('#side-panel-amend'),note=document.querySelector('#side-panel-amend-note');return amend.hidden&&amend.getClientRects().length===0&&!note.hidden&&note.getClientRects().length>0&&note.textContent==='This account cannot amend trades in this market context.';})()",
  "document.querySelector('#position-details-head').innerText");
 // The panel's Terms are read once, in the live context, and a live correction reads them again with the rest of the desk.
 await click('#position-details-tabs-terms');await wait("!!document.querySelector('#position-details-view .side-panel-section')&&"+idle);
 const editorReads="window.liveRequests.filter(p=>['/api/swaps/editor','/api/futures/editor'].includes(p)).length",readsAtOpen=await evaluate("window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length"),termsRead=await evaluate(editorReads);
 await correct('base','3.39');
 await wait(`window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length>${readsAtOpen}&&document.querySelector('#live-status').dataset.state==='live'&&`+idle);
 await check('a live correction reads the panel\'s terms again, with the desk, since the person has no edits in it',`${editorReads}===${termsRead}+1&&!!document.querySelector('#position-details-view .side-panel-section')&&!document.querySelector('#position-details-view .side-panel-error')&&document.querySelector('#notice').hidden`,`[${editorReads},${termsRead},document.querySelector('#notice').textContent]`);
 await evaluate('window.failLiveEditor=true');await correct('base','3.4');
 await wait("document.querySelector('#live-status').dataset.state==='stale'&&"+idle);
 await check('a refresh whose read of the panel\'s terms fails leaves the page stale, and the panel says what failed, with Retry',"!!document.querySelector('#position-details-view .side-panel-error')&&!!document.querySelector('#side-panel-retry')&&!document.querySelector('#live-retry').hidden");
 await click('#live-retry');
 await wait("document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 await check('Retry reads the panel\'s terms again before reporting Live',`${editorReads}===${termsRead}+3&&!!document.querySelector('#position-details-view .side-panel-section')&&!document.querySelector('#position-details-view .side-panel-error')`,`[${editorReads},${termsRead}]`);
 // The side panel follows the live refresh: it shows its position from the newest read of the desk.
 await check('the side panel is drawn again from the newest read when a live correction refreshes the desk',`(()=>{const reads=window.liveResponses.filter(r=>r.path==='/api/trading/workspace'),h=reads.at(-1).data.blotter.holdings.find(h=>h.tradeReference===${JSON.stringify(reference)});return reads.length>${readsAtOpen}&&document.querySelector('#position-details')?.hidden===false&&document.querySelector('#position-details-head .side-panel-reference').textContent===${JSON.stringify(reference)}&&document.querySelector('#position-details .side-panel-value .kpi-value').title===(h.instrumentType==='financial_swap'?h.presentValue:h.executionPnl)+' '+h.currency;})()`);
 // A live refresh holds the page busy, which disables the panel's controls while it is drawn again: focus on one comes back with it.
 // The profile dialog of the persona switches may still be open, and it is modal: the panel can take focus once it is closed.
 if(await evaluate("document.querySelector('#profile-dialog').open"))await click('#profile-close');
 // Each correction below is one more refresh of the desk, waited for from the reads there were before it.
 const workspaceReads="window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length";
 const refreshedBy=async value=>{const before=await evaluate(workspaceReads);await correct('base',value);await wait(`${workspaceReads}>${before}&&document.querySelector('#live-status').dataset.state==='live'&&`+idle);};
 await evaluate("document.querySelector('#position-details-tabs-terms').focus()");
 await refreshedBy('3.41');
 await check('keyboard focus on a tab of the panel survives a live refresh, which disables it while the page is busy and draws the panel again',
  "document.activeElement.id==='position-details-tabs-terms'",
  "[document.activeElement.id,document.activeElement.tagName]");
 // Open curve is the head's own button, enabled for this market, and keeps focus the same way.
 await check('Open curve is available for the position, so that focus can be held on a button of the panel\'s head',"!document.querySelector('#side-panel-open-curve').disabled","document.querySelector('#position-details-head').innerText");
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
 // The report is in the panel's Risk report mode, whose setup a correction reads again, as the catalog it offers may change.
 const riskNote='Risk results cleared · live corrections changed the inputs';
 const catalogs="window.liveResponses.filter(r=>r.path==='/api/portfolio/risk/catalog')";
 const reportOpen="document.querySelector('#side-panel').dataset.mode==='risk'&&!document.querySelector('#risk-fields').disabled";
 const runRisk=async()=>{
  await click('#desk-risk-run');await wait(reportOpen+"&&"+idle);await click('input[name=risk-choice][value=custom]');
  const factor=await evaluate(`(()=>{const c=${catalogs}.at(-1).data,source=c.factors.find(f=>f.curves.includes('future')&&f.deliveryFrom==='2021-01-01');return c.scenarioTargets.find(t=>t.id===source.id&&JSON.stringify(t.factor)===JSON.stringify(source.factor)).id;})()`);
  await set('#risk-factor',factor);await set('#risk-bump','0.1');await submit('#risk-form');await wait("!document.querySelector('#desk-run').hidden&&"+idle);
 };
 await runRisk();
 const catalogsBefore=await evaluate(catalogs+'.length');
 await correct('base','3.371');
 await wait("document.querySelector('#live-status').dataset.state==='live'&&document.querySelector('#desk-run').hidden&&"+reportOpen+"&&"+idle);
 await check('a live correction clears a risk run, whose inputs it changed, and the page says so',`!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes(${JSON.stringify(riskNote)})&&document.querySelector('#desk-tile-risk .desk-risk-state').textContent==='Set up in the panel'`,"[document.querySelector('#notice').textContent,document.querySelector('#desk-tile-risk').textContent]");
 await check('and reads the risk setup open in the panel again, for the context the correction made',
  `${catalogs}.length===${catalogsBefore}+1&&${catalogs}.at(-1).data.context.contextId!==${catalogs}.at(-2).data.context.contextId&&${catalogs}.at(-1).data.context.contextId===window.liveResponses.filter(r=>r.path==='/api/trading/workspace').at(-1).data.blotter.context.contextId&&[...document.querySelectorAll('input[name=risk-choice]')].every(c=>!c.checked)`,
  `[${catalogs}.length,${catalogsBefore}]`);
 // A setup that holds the person's own edits is read again at the corrected inputs, with the person's choice and sizes, and a run of it is
 // made at the context on screen, never at the inputs it was first read at (D188).
 const workspaceContext="window.liveResponses.filter(r=>r.path==='/api/trading/workspace').at(-1).data.blotter.context.contextId";
 await click('input[name=risk-choice][value=held]');
 await evaluate("(()=>{const n=document.querySelector('#risk-size-0');n.focus();n.select();})()");await command('Input.insertText',{text:'0.25'});
 const unit=await evaluate("document.querySelector('#risk-size-0').dataset.unit");
 const catalogsKept=await evaluate(catalogs+'.length'),readsKept=await evaluate("window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length");
 await correct('base','3.373');
 await wait(`window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length>${readsKept}&&document.querySelector('#live-status').dataset.state==='live'&&`+idle);
 await check('a live correction reads a setup that holds the person\'s edits again at the corrected inputs, with the person\'s choice and sizes',
  `${catalogs}.length>${catalogsKept}&&${catalogs}.at(-1).data.context.contextId===${workspaceContext}&&document.querySelector('#side-panel').dataset.mode==='risk'&&document.querySelector('input[name=risk-choice][value=held]').checked&&document.querySelector('#risk-size-0').value==='0.25'&&document.querySelector('#risk-size-0').dataset.unit===${JSON.stringify(unit)}&&document.querySelector('#risk-size-0-note').hidden&&!document.querySelector('#risk-carried').hidden&&document.querySelector('#risk-carried').textContent.startsWith('The inputs on screen changed, so this setup was read again for them with your choices.')&&${catalogs}.at(-1).data.context.contextId!==${catalogs}.at(${catalogsKept}-1).data.context.contextId`,
  "[document.querySelector('input[name=risk-choice][value=held]').checked,document.querySelector('#risk-size-0')?.value,document.querySelector('#risk-carried').textContent]");
 const reportsBefore=await evaluate("window.liveResponses.filter(r=>r.path==='/api/portfolio/risk').length");
 const sizes=await evaluate("document.querySelectorAll('#risk-sizes input').length");
 for(let index=1;index<sizes;index++)if(!await evaluate(`document.querySelector('#risk-size-${index}').value`))await set(`#risk-size-${index}`,'0.05');
 await submit('#risk-form',60000);await wait("!document.querySelector('#desk-run').hidden&&"+idle,60000);
 await check('a run of it is made at the context on screen, which the correction made, with the size the person typed',
  `(()=>{const reports=window.liveResponses.filter(r=>r.path==='/api/portfolio/risk').slice(${reportsBefore}).map(r=>r.data),typed=reports.flatMap(r=>r.factors).filter(f=>f.factor.unit===${JSON.stringify(unit)});return reports.length>0&&reports.every(r=>r.context.contextId===${workspaceContext}&&r.context.contextId===${catalogs}.at(-1).data.context.contextId)&&typed.length>0&&typed.every(f=>Number(f.bump)===0.25);})()`,
  `window.liveResponses.filter(r=>r.path==='/api/portfolio/risk').slice(${reportsBefore}).map(r=>[r.data.context.contextId,r.data.factors.map(f=>f.bump)])`);
 // A carried choice that the catalog read at the corrected inputs no longer offers is named and left out; the person's other entries stay.
 // The catalog read after this correction is given without one scenario target, which the custom scenario has chosen.
 await click('input[name=risk-choice][value=custom]');
 const dropped=await evaluate(`(()=>{const c=${catalogs}.at(-1).data,t=c.scenarioTargets.find(t=>t.kind!=='delivery_bucket'&&!c.heldContractTargets.includes(t.id)&&!c.heldCurveTargets.includes(t.id));return {id:t.id,label:t.label};})()`);
 await set('#risk-factor',dropped.id);
 await evaluate("(()=>{const n=document.querySelector('#risk-bump');n.focus();n.select();})()");await command('Input.insertText',{text:'0.2'});
 const readsDropped=await evaluate("window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length");
 await evaluate(`window.dropRiskTarget=${JSON.stringify(dropped.id)}`);await correct('base','3.375');
 await wait(`window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length>${readsDropped}&&document.querySelector('#live-status').dataset.state==='live'&&`+idle);
 await check('a carried choice the catalog read at the corrected inputs no longer offers is named and left out, and the person\'s other entries stay',
  `document.querySelector('input[name=risk-choice][value=custom]').checked&&document.querySelector('#risk-factor').value===''&&document.querySelector('#risk-bump').value==='0.2'&&document.querySelector('#risk-carried').textContent.endsWith('No longer offered, so left out: '+${JSON.stringify(dropped.label)}+'.')`,
  "[document.querySelector('#risk-factor').value,document.querySelector('#risk-bump').value,document.querySelector('#risk-carried').textContent]");
 await click('input[name=risk-choice][value=held]');
 // An unconfirmed queued submission is shown again after a correction, under the inputs it was captured at, and the page says so rather than
 // that its results were cleared.
 await evaluate('window.loseRiskJob=true');await click('#risk-queue',60000);await wait("!document.querySelector('#risk-job-retry').hidden&&"+idle);
 const readsBeforeRetained=await evaluate("window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length");
 await correct('base','3.374');
 await wait(`window.liveResponses.filter(r=>r.path==='/api/trading/workspace').length>${readsBeforeRetained}&&document.querySelector('#live-status').dataset.state==='live'&&!document.querySelector('#risk-job').hidden&&`+idle);
 await check('a correction says an unconfirmed risk submission keeps the inputs it was captured at, under which the panel shows it again, and not that it was cleared',
  "document.querySelector('#notice').textContent.includes('Live corrections changed the inputs · the unconfirmed risk submission keeps the inputs it was captured at')&&!document.querySelector('#notice').textContent.includes('Risk results cleared')&&document.querySelector('#side-panel').dataset.mode==='risk'&&!document.querySelector('#risk-job').hidden&&document.querySelector('#risk-job-context').textContent.startsWith('Captured market time')",
  "[document.querySelector('#notice').textContent,document.querySelector('#risk-job-context').textContent]");
 // Reopened, the report found the lost reply's job in the history; either way it returns to a setup at the inputs on screen.
 await click(await evaluate("document.querySelector('#risk-job-discard').hidden")?'#risk-job-return':'#risk-job-discard');
 await wait("!document.querySelector('#risk-fields').disabled&&"+idle);
 await runRisk();
 await evaluate('window.failLiveWorkspace=true');await correct('base','3.372');
 await wait("document.querySelector('#live-status').dataset.state==='stale'&&"+idle);
 await check('a refresh that fails after it cleared a risk run keeps the note that says so',`document.querySelector('#desk-run').hidden&&!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes(${JSON.stringify(riskNote)})`,"[document.querySelector('#notice').hidden,document.querySelector('#notice').textContent,document.querySelector('#desk-run').hidden]");
 await click('#live-retry');await wait("document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 await click('#side-panel-close');
 // New trade follows the backend: a live view cannot book, as a historical one cannot, so a trader there is not offered it and is told why, in the
 // backend's words, which say that the view is live. No form of a new trade is open there, and none is held while corrections arrive.
 await persona('trader');await click('#example');await click('#nav-futures');
 if(await evaluate("!document.querySelector('#follow-live').hidden"))await click('#follow-live');
 await wait("document.querySelector('#live-status').dataset.state==='live'&&!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&"+idle);
 await check('a trader in a live view is not offered New trade, and is told in words under the toolbar that the view is live',"document.querySelector('#workspace-mode-label').textContent.includes('Live corrections')&&document.querySelector('#futures-new').disabled&&!document.querySelector('#new-trade-availability').hidden&&document.querySelector('#new-trade-availability').getClientRects().length>0&&document.querySelector('#new-trade-availability').textContent==='Live view is read-only. Return to working data, then select an As of date.'&&document.querySelector('#futures-new').getAttribute('aria-describedby')==='new-trade-availability'&&!document.querySelector('#futures-new').title&&document.querySelector('#side-panel').hidden&&document.querySelector('#futures-new-trade').hidden&&document.querySelector('#swap-editing').hidden","[document.querySelector('#workspace-mode-label').textContent,document.querySelector('#new-trade-availability').textContent,document.querySelector('#futures-new').disabled]");
 // Refresh reads the cheap directory and facts before valuation. A failed valuation keeps that backend's live refusal,
 // rather than substituting the fallback used before any catalog has been read.
 const directoryReads="window.liveResponses.filter(r=>r.path==='/api/trading/directory')";
 const factReads="window.liveResponses.filter(r=>r.path==='/api/trading/positions')";
 const directoriesBefore=await evaluate(directoryReads+'.length');
 const factsBefore=await evaluate(factReads+'.length');
 const liveBookingReason=await evaluate("window.liveResponses.filter(r=>r.path==='/api/trading/workspace').at(-1).data.catalog.bookingUnavailableReason");
 await evaluate('window.failLiveWorkspace=true');await click('#futures-refresh');
 await wait(`${directoryReads}.length>${directoriesBefore}&&${factReads}.length>${factsBefore}&&document.querySelector('#futures-page').dataset.pricingState==='failed'&&`+idle);
 await check('a failed valuation keeps same-context live facts, the backend booking refusal, and unavailable totals without an edit form',
  `(()=>{const directory=${directoryReads}.at(-1).data,facts=${factReads}.at(-1).data,note=document.querySelector('#new-trade-availability'),rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')];return directory.kind==='directory'&&directory.canBook===false&&directory.canAmendTrades===false&&directory.bookingUnavailableReason===${JSON.stringify(liveBookingReason)}&&note.textContent===directory.bookingUnavailableReason&&!note.hidden&&facts.context.contextId===directory.context.contextId&&facts.context.knownAt===directory.context.knownAt&&facts.asOf===directory.asOf&&facts.positions.length>0&&facts.positions.every(position=>rows.some(row=>row.querySelector('[tabulator-field="c0"]')?.textContent===position.tradeReference))&&document.querySelector('#futures-page').dataset.pricingContext===directory.context.contextId&&!document.querySelector('#error').hidden&&document.querySelector('#futures-new').disabled&&document.querySelector('#futures-new').getAttribute('aria-describedby')==='new-trade-availability'&&document.querySelector('#desk-tile-swapPv').textContent.includes('Unavailable')&&document.querySelector('#side-panel').hidden&&document.querySelector('#futures-new-trade').hidden&&document.querySelector('#swap-editing').hidden;})()`,
  `[${directoryReads}.at(-1).data,${factReads}.at(-1).data,document.querySelector('#error').textContent,document.querySelector('#new-trade-availability').textContent,document.querySelector('#futures-page').dataset.pricingContext]`);
 await click('#futures-refresh');await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&"+idle);
 console.log('LIVE browser synthetic workload:',JSON.stringify(await evaluate("({requests:window.liveRequests.length,longTasks:window.liveTasks.length,maxLongTaskMilliseconds:Math.max(0,...window.liveTasks),heapBytes:performance.memory?.usedJSHeapSize})")));
}
