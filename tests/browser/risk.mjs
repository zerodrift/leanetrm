import {setupTradingReplies} from './trading-responses.mjs';
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,errors}){
  const selectFuturesScope=async()=>{
  if(await evaluate("document.querySelector('#blotter-risk').hidden")){
    const book=await evaluate("(()=>{const rows=window.__tradingReports(window.__riskReads).at(-1).data.holdings;return rows.find(row=>row.instrumentType==='future'&&!rows.some(other=>other.bookId===row.bookId&&other.instrumentType!=='future'))?.bookId;})()");
    if(!book)throw Error('The risk browser fixture requires a futures-only book.');
    if(!await evaluate("document.querySelector('#blotter-scope').open"))await click('#blotter-scope>summary');
    await evaluate(`(()=>{for(const option of document.querySelector('#blotter-books').options)option.selected=option.value===${JSON.stringify(book)};})()`);await click('#blotter-scope-apply');
  }
  };
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1250,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  await setupTradingReplies(evaluate);
  await evaluate(`(()=>{const send=window.fetch;window.__riskReads=[];window.__riskRequests=[];window.__riskJobs=[];window.fetch=async(input,init)=>{const path=String(input);if(path==='/api/futures/risk'){window.__riskRequests.push(JSON.parse(init.body));if(window.__denyRisk){window.__denyRisk=false;return new Response(JSON.stringify({code:'access_denied'}),{status:403,headers:{'Content-Type':'application/json'}});}}if(path==='/api/futures/risk/jobs'&&init?.method==='POST'){window.__riskJobs.push(JSON.parse(init.body));if(window.__rejectRiskSubmission){const code=window.__rejectRiskSubmission;window.__rejectRiskSubmission=false;return new Response(JSON.stringify({code}),{status:code==='access_denied'?403:400,headers:{'Content-Type':'application/json'}});}}const response=await send(input,init);try{window.__riskReads.push({path,data:await response.clone().json()});}catch{}if(path==='/api/futures/risk/jobs'&&init?.method==='POST'&&window.__loseRiskSubmission){const outcome=window.__loseRiskSubmission;window.__loseRiskSubmission=false;if(outcome==='server')return new Response(JSON.stringify({code:'risk_job_outcome_unknown'}),{status:503,headers:{'Content-Type':'application/json'}});throw new TypeError('Synthetic lost submission response');}return response;};})();`);
  await persona('risk');
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");
  await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait("document.querySelector('#futures-holdings .tabulator-row')&&!document.querySelector('#blotter-risk').disabled");
  await selectFuturesScope();
  await check('interactive futures risk is available for a complete futures-only book selection',"!document.querySelector('#blotter-risk').hidden&&window.__tradingReports(window.__riskReads).at(-1).data.holdings.every(row=>row.instrumentType==='future')");
  await check('Risk Analyst can inspect holdings and run scenarios without booking authority',"document.querySelector('#profile-label').textContent==='Risk Analyst'&&!document.querySelector('#futures-new').disabled&&document.querySelector('#futures-book-submit').disabled&&!document.querySelector('#blotter-risk').disabled");
  await click('#blotter-risk');
  await check('shock entry requires explicit factor and bump choices',"!document.querySelector('#risk-card').hidden&&document.querySelector('#risk-factor').value===''&&document.querySelector('#risk-bump').value===''&&!document.querySelector('#risk-form').checkValidity()");
  await check('risk opens with an explicit unchanged market and an empty nested stress stack',"document.querySelector('#risk-stress-stack')?.children.length===0&&document.querySelector('#risk-stress-parent')?.disabled&&document.querySelector('#risk-stress-clear')?.disabled&&document.querySelector('#risk-stress-factor')?.value===''&&document.querySelector('#risk-stress-size')?.value===''");
  const factor=await evaluate("window.__riskReads.find(r=>r.path==='/api/futures/risk/catalog').data.factors.find(f=>f.curves.includes('future')&&f.deliveryFrom==='2021-01-01').seriesId");
  await set('#risk-factor',factor);await set('#risk-bump','0.1');await submit('#risk-form');await wait("document.querySelector('#risk-results .tabulator-row')");
  await check('risk screen displays exact shared-factor results with explicit derivative units',"document.querySelector('#error').hidden&&document.querySelector('#risk-results').textContent.includes('Delta')&&document.querySelector('#risk-results').textContent.includes('Gamma')&&document.querySelector('#risk-results').textContent.includes('USD / (1 USD / MMBtu (IT))')");
  await check('risk calculation uses the blotter context and reuses a single held unit',"(()=>{const r=window.__riskReads.filter(r=>r.path==='/api/futures/risk').at(-1).data;const h=window.__tradingReports(window.__riskReads).at(-1).data;return r.context.contextId===h.context.contextId&&r.work.unitPricerCalls===3&&r.heldContracts===1&&r.work.positionCount===h.holdings.filter(row=>row.instrumentType==='future').length&&r.inputs.positions.every(p=>h.holdings.some(row=>row.instrumentType==='future'&&row.tradeId===p.tradeId&&row.bindingId===p.bindingId));})()");
  await check('shared bar chart shows both signed shock directions by book',"document.querySelector('#risk-plots canvas')&&document.querySelector('#risk-plots .chart').dataset.chartAxis==='category'&&document.querySelector('#risk-work').textContent.includes('3 pricer calls')");
  await click('#risk-results details>summary');await wait("document.querySelectorAll('#risk-results .standard-data-grid').length===2");
  await check('individual results remain available beneath book totals',"document.querySelector('#risk-results details').textContent.includes('Individual holding results')&&document.querySelector('#risk-results details .tabulator-row')");
  await click('#risk-stress>summary');await set('#risk-stress-factor',factor);await set('#risk-stress-size','0.3');await click('#risk-stress-add');
  await set('#risk-stress-size','-0.1');await click('#risk-stress-add');await submit('#risk-form');
  await check('nested stresses retain ordered signed child shifts beneath the same financial context',"(()=>{const r=window.__riskRequests.at(-1),result=window.__riskReads.filter(r=>r.path==='/api/futures/risk').at(-1).data;return r.stress.length===2&&r.stress[0].size==='0.3'&&r.stress[1].size==='-0.1'&&r.stress.every(s=>s.convention==='absolute_quote_input_parent_additive_rebuild_v1')&&result.scenario.depth===2&&result.context.contextId===r.context.contextId&&document.querySelectorAll('#risk-stress-stack li').length===2&&document.querySelector('#risk-scenario').textContent.includes('2 nested stresses');})()");
  await click('#risk-stress-parent');await submit('#risk-form');
  await check('returning to parent removes only the inner stress and reprices that parent',"(()=>{const request=window.__riskRequests.at(-1),report=window.__riskReads.filter(r=>r.path==='/api/futures/risk').at(-1).data;return request.stress.length===1&&request.stress[0].size==='0.3'&&report.scenario.depth===1&&document.querySelectorAll('#risk-stress-stack li').length===1;})()");
  await click('#risk-stress-clear');await submit('#risk-form');
  await check('leaving the outer stress restores the original unchanged-market result',"(()=>{const reports=window.__riskReads.filter(r=>r.path==='/api/futures/risk').map(r=>r.data);return window.__riskRequests.at(-1).stress.length===0&&reports.at(-1).scenario.isBase&&JSON.stringify(reports[0].factors[0].totals)===JSON.stringify(reports.at(-1).factors[0].totals)&&document.querySelector('#risk-stress-parent').disabled;})()");
  await evaluate("document.querySelector('#risk-card').scrollIntoView({block:'start'})");await shot('risk-desktop');
  await set('#risk-bump','0');
  await check('editing a shock clears its prior result and export immediately',"document.querySelector('#risk-results').children.length===0&&document.querySelector('#risk-plots').children.length===0&&document.querySelector('#risk-export').hidden");
  await submit('#risk-form');
  await check('zero bump produces a visible structured failure without cached results',"!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('positive absolute bump')&&document.querySelector('#risk-results').children.length===0");
  await set('#risk-bump','0.1');await submit('#risk-form');
  await evaluate(`(()=>{window.__riskBlob=undefined;const create=URL.createObjectURL;URL.createObjectURL=b=>{window.__riskBlob=b;return create(b);};const click=HTMLAnchorElement.prototype.click;HTMLAnchorElement.prototype.click=function(){if(this.download==='leanetrm-risk.json')return;return click.call(this);};})();`);
  await click('#risk-export');
  await check('risk download reauthorizes the exact pinned request and includes its inputs',"(async()=>{const r=JSON.parse(await window.__riskBlob.text());const requests=window.__riskRequests;return r.inputs.positions.length===r.work.positionCount&&r.inputs.observations.length>0&&r.factors[0].factor.priceUnit.multiplier==='1'&&JSON.stringify(requests.at(-1))===JSON.stringify(requests.at(-2));})()");
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  await check('shock controls and charts avoid page-wide mobile overflow',fitsWidth);await evaluate("document.querySelector('#risk-card').scrollIntoView({block:'start'})");await shot('risk-mobile');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1250,deviceScaleFactor:1,mobile:false});
  await evaluate('window.__previousRiskBlob=window.__riskBlob;window.__denyRisk=true');await click('#risk-export');
  await check('denied risk export creates no download and clears risk and holdings caches',"window.__riskBlob===window.__previousRiskBlob&&document.querySelector('#risk-card').hidden&&document.querySelector('#risk-results').children.length===0&&document.querySelector('#risk-plots').children.length===0&&document.querySelector('#futures-holdings').children.length===0");
  await click('#futures-refresh');await click('#blotter-risk');await set('#risk-factor',factor);await set('#risk-bump','0.1');
  for(const outcome of ['network','server']){
    if(await evaluate("!document.querySelector('#risk-job-return').hidden"))await click('#risk-job-return');
    await set('#risk-factor',factor);await set('#risk-bump','0.1');await evaluate('window.__loseRiskSubmission='+JSON.stringify(outcome));await click('#risk-queue');
    await check(`${outcome} unknown queue outcomes retain their exact request for safe reconciliation`,"!document.querySelector('#risk-job-retry').hidden&&document.querySelector('#risk-fields').disabled&&document.querySelector('#risk-job-status').textContent.includes('unknown')&&window.__riskJobs.at(-1).request.context.knowledgeMode==='retained'");
    await evaluate('window.__riskCatalogCount=window.__riskReads.filter(r=>r.path==="/api/futures/risk/catalog").length;window.__rejectRiskSubmission='+JSON.stringify(outcome==='server'?'access_denied':'request_format'));await click('#risk-job-retry');
    if(outcome==='server')await click('#futures-refresh');else await click('#risk-close');
    await click('#blotter-risk');
    await check(`${outcome} uncertainty survives a rejected retry and recovers its receipt without selecting a new financial context`,"document.querySelector('#risk-form').hidden&&document.querySelector('#risk-fields').disabled&&document.querySelector('#risk-job-context').textContent.includes('2020-12-18')&&window.__riskReads.filter(r=>r.path==='/api/futures/risk/catalog').length===window.__riskCatalogCount");
    await wait("document.querySelector('#risk-job-status').textContent.includes('Completed')");
    await check(`${outcome} queued risk retries use one identity and publish the captured context`,"(()=>{const jobs=window.__riskJobs,reads=window.__riskReads.filter(r=>r.path.startsWith('/api/futures/risk/jobs'));const done=reads.findLast(r=>r.data.state==='completed').data;return JSON.stringify(jobs.at(-1))===JSON.stringify(jobs.at(-2))&&done.result.context.contextId===jobs.at(-1).request.context.contextId&&done.result.work.unitPricerCalls===3&&!document.querySelector('#risk-export').hidden;})()");
  }
  await click('#risk-export');
  await check('completed job download reads its retained result with current authorization',"(async()=>{const report=JSON.parse(await window.__riskBlob.text()),last=window.__riskReads.at(-1);return last.path.startsWith('/api/futures/risk/jobs/')&&last.data.state==='completed'&&report.context.contextId===window.__riskJobs.at(-1).request.context.contextId;})()");
  const retainedJob=await evaluate("window.__riskReads.findLast(r=>r.path.startsWith('/api/futures/risk/jobs')&&r.data.state==='completed').data.id");
  const earlier=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-17')).id;})()");
  await set('#snapshot',earlier);await wait(idle);
  await check('changing the financial context discards risk results and factor choices',"document.querySelector('#risk-card').hidden&&document.querySelector('#risk-factor').value===''&&document.querySelector('#risk-results').children.length===0");
  await click('#blotter-risk');await set('#risk-job-history',retainedJob);await wait(idle);await wait("document.querySelector('#risk-results .tabulator-row')");
  await check('recent jobs remain discoverable after context changes and identify their original dates',"document.querySelector('#risk-job-context').textContent.includes('2020-12-18')&&document.querySelector('#risk-scope').textContent.includes('captured financial context')&&document.querySelector('#risk-form').hidden&&document.querySelector('#risk-job-scenario').textContent.includes('original market')&&document.querySelector('#risk-fields').disabled&&!document.querySelector('#risk-job-return').hidden&&document.querySelector('#risk-results .tabulator-row')");
  await click('#risk-job-return');
  await check('returning from a retained job clears results before enabling the selected context',"!document.querySelector('#risk-form').hidden&&!document.querySelector('#risk-fields').disabled&&document.querySelector('#risk-results').children.length===0&&document.querySelector('#risk-job').hidden");
  await persona('trader');await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
  await check('Trader permissions do not enable the scenario action',"document.querySelector('#blotter-risk').disabled&&document.querySelector('#risk-card').hidden");
  await persona('risk');await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait("document.querySelector('#futures-holdings .tabulator-row')&&!document.querySelector('#blotter-risk').disabled");
  await selectFuturesScope();await click('#blotter-risk');
  // Controlled transport ordering exercises presentation races independently of
  // the database suite's worker/cancellation transaction races.
  await evaluate(`(async()=>{
    const catalog=window.__riskReads.find(r=>r.path==='/api/futures/risk/catalog').data;
    const completed=window.__riskReads.findLast(r=>r.path.startsWith('/api/futures/risk/jobs')&&r.data.state==='completed').data;
    window.__riskRaceJob=completed;window.__riskRaceResponses=[];
    const api=async(path,body)=>{
      if(path==='futures/risk/catalog')return catalog;
      if(path==='futures/risk/jobs')return body?{...completed,state:'running',result:undefined}:[];
      if(path.endsWith('/cancel'))return {...completed,state:'cancellation_requested',result:undefined};
      return new Promise(resolve=>window.__riskRaceResponses.push(resolve));
    };
    // The independently imported module has its own metadata map beside the
    // bundled application; initialize it from the actual authorized directory.
    (await import('/market-view.js')).setCurveNames(await(await fetch('/api/curve-names')).json());
    window.__riskHarness=(await import('/risk-view.js')).setupRisk({api,act:work=>work(),busy:()=>false,canRun:()=>true,profile:()=> 'synthetic-risk-races',scope:()=>window.__riskJobs.at(-1).request,onDenied:()=>{}});
    await window.__riskHarness.open();
  })()`);
  await set('#risk-factor',factor);await set('#risk-bump','0.1');await click('#risk-queue');await wait('window.__riskRaceResponses.length===1');
  await click('#risk-job-cancel');
  await check('cancellation requested remains distinct from confirmed termination',"document.querySelector('#risk-job-status').textContent.includes('Cancellation requested')&&document.querySelector('#risk-job-status').textContent.includes('Waiting for the worker to stop')&&document.querySelector('#risk-job-cancel').hidden");
  await evaluate("(async()=>{window.__riskRaceResponses.shift()({...window.__riskRaceJob,state:'running',result:undefined});await Promise.resolve();})()");
  await check('a delayed earlier status cannot overwrite the accepted cancellation response',"document.querySelector('#risk-job-status').textContent.includes('Cancellation requested')&&document.querySelector('#risk-job-cancel').hidden");
  await evaluate('window.__riskHarness.reset();window.__riskHarness.open()');await set('#risk-factor',factor);await set('#risk-bump','0.1');await click('#risk-queue');await wait('window.__riskRaceResponses.length===1');
  await evaluate("(async()=>{window.__riskHarness.reset();window.__riskRaceResponses.shift()(window.__riskRaceJob);await Promise.resolve();})()");
  await check('late completed status after a financial-context reset cannot repopulate the old results',"document.querySelector('#risk-card').hidden&&document.querySelector('#risk-results').children.length===0&&document.querySelector('#risk-export').hidden");
  await evaluate('window.__riskHarness.open()');await set('#risk-factor',factor);await set('#risk-bump','0.1');await click('#risk-queue');await wait('window.__riskRaceResponses.length===1');
  await evaluate("(async()=>{window.__riskRaceResponses.shift()(window.__riskRaceJob);await Promise.resolve();})()");await wait("!document.querySelector('#risk-export').hidden");
  await evaluate('window.__beforeLateRiskExport=window.__riskBlob');await click('#risk-export');await wait('window.__riskRaceResponses.length===1');
  await evaluate("(async()=>{window.__riskHarness.reset();window.__riskRaceResponses.shift()(window.__riskRaceJob);await Promise.resolve();})()");
  await check('late export after a financial-context reset creates no download',"window.__riskBlob===window.__beforeLateRiskExport&&document.querySelector('#risk-card').hidden");
  await check('risk workflow raises no uncaught browser exceptions',JSON.stringify(errors.length===0));
}
