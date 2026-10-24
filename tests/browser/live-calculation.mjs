import {sideSession} from './side-session.mjs';

export async function run({command,evaluate,wait,click,persona,set,check,shot,idle,url}){
 await command('Runtime.enable');await command('Page.enable');await command('Network.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.liveResponses=[];window.liveRequests=[];window.liveTasks=[];new PerformanceObserver(list=>window.liveTasks.push(...list.getEntries().map(e=>e.duration))).observe({type:'longtask',buffered:true});{const fetch=window.fetch;window.fetch=async(path,init)=>{window.liveRequests.push(String(path));if((window.failLivePreview&&String(path)==='/api/custom-curves/preview')||(window.failLiveEditor&&String(path)==='/api/swaps/editor')){window.failLivePreview=false;window.failLiveEditor=false;return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}const response=await fetch(path,init);if(response.headers.get('content-type')?.includes('application/json'))window.liveResponses.push({path:String(path),data:await response.clone().json()});return response;};}`});
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
 const reference=await evaluate("window.liveResponses.filter(r=>r.path==='/api/trading/workspace').at(-1).data.blotter.holdings.find(h=>h.instrumentType==='financial_swap').tradeReference");
 await set('#desk-positions .table-search',reference);await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("document.querySelector('#swap-editor').open");
 await evaluate('window.failLiveEditor=true');await correct('base','3.4');
 await wait("document.querySelector('#live-status').dataset.state==='stale'&&"+idle);await click('#live-retry');
 await wait("document.querySelector('#swap-editor').open&&document.querySelector('#live-status').dataset.state==='live'&&"+idle);
 await check('failed booked-side refresh retries the requested editor before reporting Live',"document.querySelector('#swap-schedules').children.length>0&&document.querySelector('#swap-error').hidden");
 await click('#swap-close');
 console.log('LIVE browser synthetic workload:',JSON.stringify(await evaluate("({requests:window.liveRequests.length,longTasks:window.liveTasks.length,maxLongTaskMilliseconds:Math.max(0,...window.liveTasks),heapBytes:performance.memory?.usedJSHeapSize})")));
}
