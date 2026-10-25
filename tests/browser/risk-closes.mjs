// A run's results are drawn only against the read of the close, context and books they were run at (D221). In a historical view every
// close of one date shares the view's market context, so a run made at one of the date's closes, chosen from the job history at another,
// is made elsewhere. A part the job history no longer lists is said to be missing from it, and what it might move is not known.
export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1250,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  // A second close of 2020-12-18, registered by a market data approver from the date's close: the same instant, its own identity.
  await persona('approver');
  const second=await evaluate(`(async()=>{const boot=await(await fetch('/api/bootstrap')).json(),frame=boot.frames.find(f=>f.at.startsWith('2020-12-18'));
    const response=await fetch('/api/market/closes/register',{method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify({requestId:crypto.randomUUID(),snapshotId:frame.id,name:'Synthetic second close'})});
    return response.ok?(await response.json()).at:null;})()`);
  await persona('risk');
  // Every risk read, and the job history read without a part when a check asks for it; installed again after each switch of user.
  await evaluate(`(()=>{const send=window.fetch;window.__riskReads=[];window.__riskJobs=[];window.fetch=async(input,init)=>{const path=String(input);if(path==='/api/portfolio/risk/jobs'&&init?.method==='POST')window.__riskJobs.push(JSON.parse(init.body));
    let response=await send(input,init);if(path==='/api/portfolio/risk/jobs'&&(init?.method??'GET')==='GET'&&window.__dropFromHistory&&response.ok){const jobs=await response.json();response=new Response(JSON.stringify(jobs.filter(j=>j.id!==window.__dropFromHistory)),{status:200,headers:{'Content-Type':'application/json'}});}
    try{window.__riskReads.push({path,data:await response.clone().json()});}catch{}return response;};})()`);
  await set('#date-context-on','2020-12-18');await wait(idle);await click('#date-context-latest');await click('#apply-view-context');
  await wait("document.querySelector('#workspace-mode-label').textContent.startsWith('Historical view')&&"+idle);
  const closes=await evaluate("[...document.querySelector('#snapshot').options].filter(o=>o.value&&o.textContent.startsWith('Dec 18, 2020')).map(o=>o.value)");
  // The job checks may have registered another close of the date already: any two of the date's closes share the view's context.
  await check('a historical view lists the closes of 2020-12-18, the one just registered among them, all at one instant',`${JSON.stringify(closes)}.length>=2&&${JSON.stringify(second)}==='2020-12-18T16:00:00+00:00'`,JSON.stringify([closes,second]));

  const panelRisk="(()=>{const panel=document.querySelector('#side-panel');return !panel.hidden&&panel.dataset.mode==='risk'&&!document.querySelector('#risk-report').hidden;})()";
  const inline="(()=>{const bar=document.querySelector('#desk-run');return !bar.hidden&&!document.querySelector('#desk-lens').hidden&&document.querySelector('#desk-lens [data-lens=risk]').getAttribute('aria-pressed')==='true';})()";
  const elsewhere="(()=>!document.querySelector('#desk-run').hidden&&document.querySelector('#desk-lens').hidden&&[...document.querySelectorAll('#desk-run-notes li')].some(li=>li.textContent.startsWith('This run was made at another close, context or books than the Blotter read on screen'))&&document.querySelector('#desk-tile-risk .desk-risk-state').textContent==='Results made elsewhere'&&!document.querySelector('#risk-export').hidden)()";
  const workspace="window.__riskReads.filter(r=>r.path==='/api/trading/workspace')";
  const blotterAt=async close=>{await set('#snapshot',close);await wait(idle);await click('#nav-futures');await wait("document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&!document.querySelector('#blotter-risk').disabled&&"+idle);};
  const openReport=async()=>{await click('#blotter-risk');await wait(panelRisk+"&&!document.querySelector('#risk-fields').disabled&&"+idle);};
  const fromHistory=async value=>{await set('#risk-job-history',value);await wait("!document.querySelector('#desk-run').hidden&&"+idle,60000);};

  // A run made at the date's first close, queued as one job, is drawn there.
  await blotterAt(closes[0]);await openReport();
  await click('input[name=risk-choice][value=custom]');
  await set('#risk-factor',await evaluate("window.__riskReads.findLast(r=>r.path==='/api/portfolio/risk/catalog').data.scenarioTargets.find(t=>t.kind==='contract').id"));await set('#risk-bump','0.1');
  await click('#risk-queue');await wait(inline+'&&'+idle,60000);
  const job=await evaluate("window.__riskReads.findLast(r=>r.path.startsWith('/api/portfolio/risk/jobs/')&&r.data?.state==='completed').data");
  await check('a run\'s report states the close it was run at: the frame of the read on screen',`${JSON.stringify(job.result.snapshotId)}===${JSON.stringify(closes[0])}&&document.querySelector('#snapshot').value===${JSON.stringify(closes[0])}&&${workspace}.at(-1).data.blotter.context.contextId===${JSON.stringify(job.context.contextId)}`,
    JSON.stringify([job.result.snapshotId,closes]));
  const context=job.context.contextId;

  // At the date's other close the view's context is the same, and the run chosen from the history is not this close's risk.
  await blotterAt(closes[1]);
  await check('the other close of the date is read at the same market context',`${workspace}.at(-1).data.blotter.context.contextId===${JSON.stringify(context)}`);
  await openReport();await fromHistory(job.id);
  await check('a run made at another close of the date, at the same context, is made elsewhere: never drawn as this close\'s risk, and offered for export',elsewhere,
    "[[...document.querySelectorAll('#desk-run-notes li')].map(li=>li.textContent),document.querySelector('#desk-lens').hidden,document.querySelector('#desk-tile-risk').textContent]");
  // Back at its own close, context and books, the same run from the history is drawn.
  await blotterAt(closes[0]);await openReport();await fromHistory(job.id);
  await check('the run from the history at its own close, context and books is drawn inline',`${inline}&&document.querySelector('#desk-tile-risk .desk-risk-count').textContent==='1 shock'`,
    "[[...document.querySelectorAll('#desk-run-notes li')].map(li=>li.textContent),document.querySelector('#desk-tile-risk').textContent]");

  // A run of several parts whose part the job history no longer lists: that part is missing from the history, and a position no listed
  // part moves is not known, never not moved.
  await click('#risk-job-return');await wait(panelRisk+"&&!document.querySelector('#risk-fields').disabled&&"+idle);
  await click('input[name=risk-choice][value=held]');
  for(const index of [...Array(await evaluate("document.querySelectorAll('#risk-sizes input').length")).keys()])await set(`#risk-size-${index}`,'0.05');
  const before=await evaluate('window.__riskJobs.length');
  await click('#risk-queue');await wait(`/completed/.test(document.querySelector('#desk-run-detail').textContent)&&${inline}&&${idle}`,60000);
  const parts=await evaluate(`(()=>{const sent=window.__riskJobs.slice(${before}),done=sent.map(s=>window.__riskReads.findLast(r=>r.path.startsWith('/api/portfolio/risk/jobs/')&&r.data?.requestId===s.requestId&&r.data.state==='completed').data);
    return done.map(j=>({id:j.id,run:j.run,shocks:j.result.factors.length,moved:[...new Set(j.result.factors.flatMap(f=>f.positions.filter(p=>[p.values.upPnl,p.values.downPnl].some(v=>!/^-?0+(?:\\.0+)?(?:\\/\\d+)?$/.test(v))).map(p=>p.tradeReference)))]}));})()`);
  // The part dropped from the history is one whose shocks alone move a position, so that the check can see a position it leaves unknown.
  const dropped=parts.findIndex((part,index)=>part.moved.some(reference=>parts.every((other,o)=>o===index||!other.moved.includes(reference))));
  await check('the queued run has parts, one of which alone moves a position',`${parts.length}>1&&${dropped}>=0&&${JSON.stringify(parts.map(p=>p.run?.part))}.join()===${JSON.stringify(parts.map((_,i)=>i+1))}.join()`,JSON.stringify(parts.map(p=>[p.run,p.moved.length])));
  await evaluate(`window.__dropFromHistory=${JSON.stringify(parts[dropped].id)}`);
  await click('#side-panel-close');await openReport();await fromHistory('run:'+parts[0].run.id);
  const name=`Part ${dropped+1} of ${parts.length}`;
  await check('a part the job history no longer lists is missing from it, in the run bar and in the report\'s list of parts, never not yet queued',
    `[...document.querySelectorAll('#desk-run-notes li')].some(li=>li.textContent===${JSON.stringify(`${name} has no results: it is missing from the job history.`)})&&[...document.querySelectorAll('#risk-job-parts li')].some(li=>li.textContent===${JSON.stringify(`${name} · Missing from the job history`)})&&!document.querySelector('#desk-run').textContent.includes('not yet queued')`,
    "[[...document.querySelectorAll('#desk-run-notes li')].map(li=>li.textContent),[...document.querySelectorAll('#risk-job-parts li')].map(li=>li.textContent)]");
  await click('#side-panel-close');
  // Every position the run values is in each part's report; those no listed part moves are not known.
  const unknown=await evaluate(`(()=>{const listed=${JSON.stringify(parts.filter((_,i)=>i!==dropped).map(p=>p.moved))}.flat(),valued=window.__riskReads.findLast(r=>r.path.startsWith('/api/portfolio/risk/jobs/')&&r.data?.id===${JSON.stringify(parts[0].id)}).data.result.factors[0].positions.map(p=>p.tradeReference);
    return [...new Set(valued)].filter(reference=>!listed.includes(reference));})()`);
  const rowsFound=async text=>{await set('#blotter-filters .table-search',text);await wait(idle);return evaluate("document.querySelector('#blotter-filters .table-row-count').textContent");};
  const notMoved=await rowsFound('Not moved'),notKnown=await rowsFound('Not known');
  await check('a position only that part could move is not known, and no position is said to be not moved',
    `${JSON.stringify(notMoved)}.startsWith('0 of ')&&${JSON.stringify(notKnown)}.startsWith(${JSON.stringify(`${unknown.length} of `)})&&${unknown.length}>0`,JSON.stringify([notMoved,notKnown,unknown]));
  await set('#blotter-filters .table-search',unknown[0]);await wait("[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.getClientRects().length).length===1");
  await check('its row says not known, with the part and why',`document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group) [tabulator-field=c3]').textContent===${JSON.stringify(`Not known${name} is missing from the job history`)}`,
    "document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group) [tabulator-field=c3]').textContent");
  await set('#blotter-filters .table-search','');await wait(idle);
  const known=parts.filter((_,index)=>index!==dropped).reduce((count,part)=>count+part.shocks,0);
  await check('the run states only how many of its shocks are known, never fewer shocks than it ran',
    `document.querySelector('#desk-tile-risk .desk-risk-count').textContent===${JSON.stringify(`${known} shocks known`)}&&document.querySelector('#desk-run-detail').textContent.startsWith(${JSON.stringify(`${known} shocks known · `)})`,
    "[document.querySelector('#desk-tile-risk .desk-risk-count').textContent,document.querySelector('#desk-run-detail').textContent]");
  await check('the change chart and the risk tile say which positions are not known and why',
    `[...document.querySelectorAll('#desk-chart-position .desk-chart-note')].some(p=>p.textContent.startsWith(${JSON.stringify(`Not known, as ${name.toLowerCase()} is missing from the job history: `)}))&&document.querySelector('#desk-tile-risk .desk-tile-note').textContent.includes(${JSON.stringify(`${name} is missing from the job history`)})&&document.querySelector('#desk-tile-risk').textContent.includes('Largest known move: ')`,
    "[[...document.querySelectorAll('#desk-chart-position .desk-chart-note')].map(p=>p.textContent),document.querySelector('#desk-tile-risk').textContent]");
  await evaluate('window.__dropFromHistory=undefined');
}
