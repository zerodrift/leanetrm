import {setupTradingReplies} from './trading-responses.mjs';
// The risk report (blotter-desk.md, Risk report inline): its setup in the side panel's Risk report mode, with its presets, the custom
// scenario lab, runs made now and queued as jobs, split into parts of at most sixteen shocks, and the retained job history; and its
// results inline in the desk (D220): the run bar, the Values | Risk lens of the positions, the risk tile, the change chart and the
// Risk tab of a position's panel.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,errors}){
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1250,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  await setupTradingReplies(evaluate);
  // Every risk read and request, and the replies a check asks to be refused, lost or edited; installed again after a reload.
  const hook=`(()=>{const send=window.fetch;window.__riskReads=[];window.__riskRequests=[];window.__riskJobs=[];window.fetch=async(input,init)=>{const path=String(input);if(path==='/api/portfolio/risk'){window.__riskRequests.push(JSON.parse(init.body));if(window.__denyRisk){window.__denyRisk=false;return new Response(JSON.stringify({code:'access_denied'}),{status:403,headers:{'Content-Type':'application/json'}});}if(window.__failRiskPart!==undefined&&window.__failRiskPart--===0){window.__failRiskPart=undefined;return new Response(JSON.stringify({code:'missing_observation'}),{status:422,headers:{'Content-Type':'application/json'}});}if(window.__refuseRisk)return new Response(JSON.stringify({code:'missing_observation'}),{status:422,headers:{'Content-Type':'application/json'}});}if(path==='/api/portfolio/risk/jobs'&&init?.method==='POST'){window.__riskJobs.push(JSON.parse(init.body));if(window.__rejectRiskSubmission){const code=window.__rejectRiskSubmission;window.__rejectRiskSubmission=false;return new Response(JSON.stringify({code}),{status:code==='access_denied'?403:400,headers:{'Content-Type':'application/json'}});}}let response=await send(input,init);const edit=path==='/api/portfolio/risk/catalog'?window.__catalogEdit:path==='/api/portfolio/risk'?window.__reportEdit:undefined;if(edit&&response.ok)response=new Response(JSON.stringify(edit(await response.json())),{status:200,headers:{'Content-Type':'application/json'}});if(window.__holdRiskJobs&&(init?.method??'GET')==='GET'&&path.startsWith('/api/portfolio/risk/jobs/')&&response.ok){const {result,...job}=await response.json();window.__heldPolls=(window.__heldPolls??0)+1;response=new Response(JSON.stringify({...job,state:'running'}),{status:200,headers:{'Content-Type':'application/json'}});}try{window.__riskReads.push({path,data:await response.clone().json()});}catch{}if(path==='/api/portfolio/risk/jobs'&&init?.method==='POST'&&window.__loseRiskSubmission){const outcome=window.__loseRiskSubmission;window.__loseRiskSubmission=false;if(outcome==='server')return new Response(JSON.stringify({code:'risk_job_outcome_unknown'}),{status:503,headers:{'Content-Type':'application/json'}});throw new TypeError('Synthetic lost submission response');}return response;};})();`;
  await evaluate(hook);
  // A person's own typing, which the setup holds as an edit, unlike a value a script sets.
  const typeInto=async(selector,text)=>{await evaluate(`(()=>{const n=document.querySelector(${JSON.stringify(selector)});n.focus();n.select();})()`);await command('Input.insertText',{text});};
  const catalog="window.__riskReads.findLast(r=>r.path==='/api/portfolio/risk/catalog').data";
  const panelRisk="(()=>{const panel=document.querySelector('#side-panel');return !panel.hidden&&panel.dataset.mode==='risk'&&document.querySelector('#side-panel-view').contains(document.querySelector('#risk-report'))&&!document.querySelector('#risk-report').hidden;})()";
  const tileSays=state=>`document.querySelector('#desk-tile-risk .desk-risk-state').textContent===${JSON.stringify(state)}`;
  // A run's results are inline: the run bar says what ran, and the lens shows the positions under Risk.
  const inline="(()=>{const bar=document.querySelector('#desk-run');return !bar.hidden&&document.querySelector('#desk-lens [data-lens=risk]')?.getAttribute('aria-pressed')==='true'&&!document.querySelector('#desk-lens').hidden;})()";
  const noRun="document.querySelector('#desk-run').hidden&&document.querySelector('#desk-lens').hidden";
  const shocksShown="Number(document.querySelector('#desk-tile-risk .desk-risk-count').textContent.split(' ')[0])";
  // Changing the close, the context or the book scope clears a run in the design's words; any other new read of the Blotter says it was read.
  const cleared='Risk results cleared · the close or books changed',readAgain='Risk results cleared · the Blotter was read again';
  const notice="document.querySelector('#notice').hidden?'':document.querySelector('#notice').textContent";
  // How many shocks of a run's reports move a position, by its trade reference: a factor moves it when its up or down change is not zero.
  const movedBy=`((reference,reports)=>reports.flatMap(r=>r.factors).filter(f=>f.positions.some(p=>p.tradeReference===reference&&[p.values.upPnl,p.values.downPnl].some(v=>!/^-?0+(?:\\.0+)?(?:\\/\\d+)?$/.test(v)))).length)`;
  const runReports=count=>`window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').slice(-${count}).map(r=>r.data)`;
  const lensCell=(row,field)=>`${row}.querySelector('[tabulator-field=${field}]')`;
  const choose=async report=>{await click(`input[name=risk-choice][value=${report}]`);};
  const openReport=async()=>{await click('#blotter-risk');await wait(panelRisk+"&&!document.querySelector('#risk-fields').disabled");};
  await persona('risk');
  // The date's own close, never a close another check registered from it at the same instant.
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')&&!f.originCloseId).id;})()");
  await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait("document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&!document.querySelector('#blotter-risk').disabled");
  await check('portfolio risk includes a mixed book selection without hiding swap holdings',"(()=>{const holdings=window.__tradingReports(window.__riskReads).at(-1).data.holdings;return !document.querySelector('#blotter-risk').hidden&&document.querySelector('#blotter-risk').textContent==='Risk report'&&holdings.some(row=>row.instrumentType==='future')&&holdings.some(row=>row.instrumentType==='financial_swap');})()");
  await check('Risk Analyst can inspect holdings and run scenarios without booking authority, and is told so rather than offered New trade',"document.querySelector('#profile-label').textContent==='Risk Analyst'&&document.querySelector('#futures-new').disabled&&document.querySelector('#new-trade-availability').textContent.includes('Book Trade permission')&&document.querySelector('#futures-book-submit').disabled&&!document.querySelector('#blotter-risk').disabled");
  await check('the risk tile says no report has run at this close and offers to run one',tileSays('Not run at this close')+"&&!document.querySelector('#desk-risk-run').disabled");

  // The tile opens the risk report in the side panel, which reads the risk catalog of the Blotter read on screen.
  await click('#desk-risk-run');await wait(panelRisk+"&&!document.querySelector('#risk-fields').disabled");
  await check('the risk tile opens the side panel in its Risk report mode, named so for assistive technology, with the catalog of the read on screen',
    `${panelRisk}&&document.querySelector('#side-panel').getAttribute('aria-label')==='Risk report'&&document.querySelector('#side-panel-title').textContent==='Risk report'&&${catalog}.context.contextId===window.__tradingReports(window.__riskReads).at(-1).data.context.contextId&&${tileSays('Set up in the panel')}`);
  await check('no report is chosen for the person: the form asks for one before it can run',"[...document.querySelectorAll('input[name=risk-choice]')].every(c=>!c.checked)&&!document.querySelector('#risk-form').checkValidity()&&document.querySelector('#risk-preset').hidden&&document.querySelector('#risk-custom').hidden");
  await check('each preset says what it shocks at this read, from the held targets the backend names',
    `(()=>{const c=${catalog};return c.heldContractTargets.length>0&&c.heldCurveTargets.length>0&&document.querySelector('#risk-choice-held-detail').textContent.startsWith(c.heldContractTargets.length+' held contract')&&document.querySelector('#risk-choice-parallel-detail').textContent.startsWith(c.heldCurveTargets.length+' held curve');})()`);

  // Shock every held contract: one size for each price unit the held contracts are quoted in, none of them built in.
  await choose('held');
  const heldTargets=`${catalog}.scenarioTargets.filter(t=>${catalog}.heldContractTargets.includes(t.id))`;
  await check('Shock every held contract asks for a size for each price unit of the held contract targets, with none built in',
    `(()=>{const units=[...new Set(${heldTargets}.map(t=>t.unit))],inputs=[...document.querySelectorAll('#risk-sizes input')];return !document.querySelector('#risk-preset').hidden&&document.querySelector('#risk-custom').hidden&&units.length>0&&inputs.map(i=>i.dataset.unit).join('|')===units.join('|')&&inputs.every(i=>i.value===''&&i.required)&&[...document.querySelectorAll('.risk-prefilled')].every(n=>n.hidden)&&!document.querySelector('#risk-form').checkValidity();})()`);
  const sizeCount=await evaluate("document.querySelectorAll('#risk-sizes input').length");
  for(let index=0;index<sizeCount;index++)await set(`#risk-size-${index}`,'0.05');
  await shot('risk-held-desktop');
  const requestsBefore=await evaluate('window.__riskRequests.length');
  await submit('#risk-form',60000);await wait(inline+'&&'+idle,60000);
  await check('the run shocks every held contract target once by its size, in parts of at most sixteen made one after another, all at the catalog\'s one retained context',
    `(()=>{const c=${catalog},targets=${heldTargets},parts=window.__riskRequests.slice(${requestsBefore});return parts.length===Math.ceil(targets.length/16)&&parts.every(r=>r.bumps.length>=1&&r.bumps.length<=16&&r.stress.length===0&&r.context.knowledgeMode==='retained'&&r.context.contextId===c.context.contextId&&r.convention===c.convention&&JSON.stringify(r.pricingPolicy)===JSON.stringify(c.pricingPolicy))&&JSON.stringify(parts.flatMap(r=>r.bumps.map(b=>b.factor)))===JSON.stringify(targets.map(t=>t.factor))&&parts.every(r=>r.bumps.every(b=>b.size==='0.05'&&b.convention===c.bumpConvention));})()`);
  await check('the run comes back inline: the run bar says what ran, at which close and knowledge, and the panel keeps the setup and shows no results',
    `${panelRisk}&&!document.querySelector('#risk-results')&&document.querySelector('#desk-run-title').textContent.startsWith('Risk report · Shock every held contract · ')&&document.querySelector('#desk-run-detail').textContent.startsWith(${heldTargets}.length+' held contracts · Dec 18, 2020')&&document.querySelector('#desk-run-detail').textContent.includes(' close · current knowledge · ran ')&&document.querySelector('#error').hidden&&!document.querySelector('#risk-export').disabled&&document.querySelector('#side-panel').dataset.wide===undefined`,
    "[document.querySelector('#desk-run-title').textContent,document.querySelector('#desk-run-detail').textContent]");
  await check('the results say which report was run, with its size for each price unit, and which held prices it did not shock, if any',
    `(()=>{const units=[...new Set(${heldTargets}.map(t=>t.unit))],left=${catalog}.heldContractsWithoutTarget;return document.querySelector('#risk-run-setup')?.textContent==='Shock every held contract · '+units.map(unit=>'±0.05 '+unit).join(', ')+' · '+(left.length?'not shocked, as no whole-contract target covers '+(left.length===1?'it':'them')+': '+left.join('; '):'every held price shocked');})()`,
    "document.querySelector('#risk-run-setup')?.textContent");
  await check('the Risk lens shows each position\'s shock, up and down changes and delta, signed, and how many shocks move one that several move, never a sum',
    `(()=>{const reports=${runReports(`Math.ceil(${heldTargets}.length/16)`)},rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')],titles=[...document.querySelectorAll('#desk-positions .tabulator-col-title')].map(t=>t.textContent.trim());
      if(!['Trade','Market','Position','Shock','Up','Down','Delta'].every(t=>titles.includes(t))||titles.includes('Value')||titles.includes('Curve')||!rows.length)return false;
      let several=0,single=0;
      const fine=rows.every(row=>{const reference=row.querySelector('.desk-reference').textContent,moved=${movedBy}(reference,reports),up=${lensCell('row','c4')}.querySelector('.desk-figure'),delta=${lensCell('row','c6')}.querySelector('.desk-figure');
        if(moved>1){several++;return ${lensCell('row','c3')}.querySelector('.desk-figure').textContent===moved+' shocks'&&up.textContent===moved+' shocks'&&delta.textContent===moved+' shocks'&&!up.title;}
        if(moved!==1)return true;single++;
        const position=reports.flatMap(r=>r.factors).flatMap(f=>f.positions).find(p=>p.tradeReference===reference&&!/^-?0+(?:\\.0+)?$/.test(p.values.upPnl));
        return /^[+−]/.test(up.textContent)&&up.title===position.values.upPnl&&up.dataset.sign===(position.values.upPnl.startsWith('-')?'negative':'positive')&&/^[+−0]/.test(delta.textContent)&&${lensCell('row','c6')}.textContent.includes(position.values.deltaUnit)&&${lensCell('row','c3')}.querySelector('.desk-figure').textContent==='±0.05';});
      return fine&&several>0&&single>0;})()`);
  await check('the risk tile shows the largest move and the number of shocks, its label and count reading apart, and the chart becomes the change if each contract moves up',
    `(()=>{const tile=document.querySelector('#desk-tile-risk'),figure=tile.querySelector('.kpi-value'),panels=[...document.querySelectorAll('#desk-chart-position .chart')];return ${shocksShown}===${heldTargets}.length&&!!figure&&figure.title.endsWith(' USD')&&/^[+−]/.test(figure.querySelector('.kpi-figure').textContent)&&tile.textContent.includes('Largest move: ')&&document.querySelector('#desk-chart-position-title').textContent==='Change if each contract moves up'&&panels.length>0&&panels.some(p=>/Not drawn: .* · \\d+ shocks/.test(p.textContent))&&document.querySelector('#desk-tile-risk .desk-risk-state').hidden&&(label=>label.children.length===2&&label.textContent===label.children[0].textContent+' '+label.children[1].textContent)(tile.querySelector('.desk-tile-label'));})()`,
    "[document.querySelector('#desk-tile-risk').textContent,document.querySelector('#desk-chart-position').textContent]");
  await shot('risk-held-results');
  // Results stay while a position is looked at; its panel's Risk tab lists each shock that moves it: a position the run's own reports say a
  // shock moves, found by its trade reference among the positions read, whatever the order of the rows.
  const heldReports=runReports(`Math.ceil(${heldTargets}.length/16)`);
  const movedReference=await evaluate(`(()=>{const reports=${heldReports};return window.__tradingReports(window.__riskReads).at(-1).data.holdings.map(h=>h.tradeReference).find(reference=>${movedBy}(reference,reports)>0);})()`);
  await set('#blotter-filters .table-search',movedReference);await wait("[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.getClientRects().length).length===1");
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("document.querySelector('#side-panel').dataset.mode==='position'&&"+idle);
  await check('opening a position takes the report out of the panel and keeps the run inline',`document.querySelector('#editor-shelf').contains(document.querySelector('#risk-report'))&&document.querySelector('#side-panel').dataset.wide===undefined&&${inline}`);
  await click('#side-panel-tabs-risk');await wait("document.querySelector('#side-panel-tabs-risk').getAttribute('aria-selected')==='true'&&"+idle);
  await check('the position\'s Risk tab says its results come from the risk run on the desk, and lists each shock that moves it, with its base, up and down values, changes, delta and gamma',
    `(()=>{const reference=document.querySelector('#side-panel-head .side-panel-reference').textContent,reports=${heldReports},moved=${movedBy}(reference,reports),sections=[...document.querySelectorAll('#side-panel-view .side-risk-shock')];
      return reference===${JSON.stringify(movedReference)}&&moved>0&&sections.length===moved&&sections.every(s=>[...s.querySelectorAll('dt')].map(t=>t.textContent).join('|')==='Base|Up|Down|Up change|Down change|Delta|Gamma'&&s.querySelector('.field-help').textContent.startsWith('Shocked up and down by ±0.05 '))&&document.querySelector('#side-panel-view .side-panel-status').textContent.startsWith('From the risk run on the desk: ');})()`,
    `[document.querySelector('#side-panel-head .side-panel-reference').textContent,${JSON.stringify(movedReference)},${movedBy}(document.querySelector('#side-panel-head .side-panel-reference').textContent,${heldReports}),document.querySelector('#side-panel-view').textContent]`);
  await shot('risk-position-tab');
  await set('#blotter-filters .table-search','');
  await click('#desk-run-edit');await wait(panelRisk+"&&"+idle);
  await check('Edit shocks opens the risk report with the choice and the sizes the run used',`document.querySelector('input[name=risk-choice][value=held]').checked&&[...document.querySelectorAll('#risk-sizes input')].every(i=>i.value==='0.05')&&${inline}`);

  // A new read of the Blotter clears the run and says so, each read at current knowledge being made at a context of its own; the sizes last
  // used are prefilled, visibly, the next time.
  await click('#futures-refresh');await wait("document.querySelector('#side-panel').hidden&&"+idle);
  await check('Refresh reads the Blotter again, which clears the run: the page says it was read again, not that the close or books changed, and the tile says it has not run',
    `${noRun}&&(${notice}).includes(${JSON.stringify(readAgain)})&&!(${notice}).includes(${JSON.stringify(cleared)})&&${tileSays('Not run at this close')}&&document.querySelector('#desk-chart-position-title').textContent==='Long and short by market'`,
    `[${notice},document.querySelector('#desk-tile-risk').textContent]`);
  await openReport();await choose('held');
  await check('the sizes the last run used are prefilled, each marked as prefilled for the person and for assistive technology',
    "[...document.querySelectorAll('#risk-sizes input')].every(i=>i.value==='0.05'&&i.getAttribute('aria-describedby')===i.id+'-note'&&!document.getElementById(i.id+'-note').hidden&&document.getElementById(i.id+'-note').textContent==='Prefilled · the size you last used')");
  // The setup's own edits are what the panel asks about before they are left.
  await typeInto('#risk-size-0','0.07');
  await check('a size the person types is theirs: it is no longer marked as prefilled',"document.querySelector('#risk-size-0').value==='0.07'&&document.querySelector('#risk-size-0-note').hidden&&!document.querySelector('#risk-size-0').hasAttribute('aria-describedby')");
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("!document.querySelector('#side-panel-prompt').hidden");
  await check('opening a position while the setup holds edits asks first, in the panel',"document.querySelector('#side-panel-prompt').textContent.includes('unsaved changes')&&document.querySelector('#side-panel').dataset.mode==='risk'");
  await click('#side-panel-prompt button.secondary');
  await check('Keep editing keeps the setup as the person left it',`${panelRisk}&&document.querySelector('#risk-size-0').value==='0.07'&&document.querySelector('input[name=risk-choice][value=held]').checked`);
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("!document.querySelector('#side-panel-prompt').hidden");
  await click('#side-panel-prompt button:not(.secondary)');await wait("document.querySelector('#side-panel').dataset.mode==='position'&&"+idle);
  await openReport();
  await check('Discard changes leaves the setup, which starts afresh the next time',"[...document.querySelectorAll('input[name=risk-choice]')].every(c=>!c.checked)&&document.querySelector('#risk-preset').hidden");

  // Parallel shift: one whole-curve target for each held curve, in one part.
  await choose('parallel');
  const curveTargets=`${catalog}.scenarioTargets.filter(t=>${catalog}.heldCurveTargets.includes(t.id))`;
  await check('Parallel shift asks for the sizes of its curves\' price units, prefilled with the last used',
    `(()=>{const units=[...new Set(${curveTargets}.map(t=>t.unit))],inputs=[...document.querySelectorAll('#risk-sizes input')];return inputs.map(i=>i.dataset.unit).join('|')===units.join('|')&&inputs.every(i=>i.value==='0.05');})()`);
  await shot('risk-parallel-desktop');
  await submit('#risk-form',60000);await wait(inline+'&&'+idle,60000);
  await check('it shifts each held curve as a whole, by its unit\'s size, in one part',
    `(()=>{const r=window.__riskRequests.at(-1);return JSON.stringify(r.bumps.map(b=>b.factor))===JSON.stringify(${curveTargets}.map(t=>t.factor))&&r.bumps.every(b=>b.size==='0.05')&&document.querySelector('#desk-run-detail').textContent.startsWith(${curveTargets}.length+' held curve')&&${shocksShown}===${curveTargets}.length;})()`,
    "document.querySelector('#desk-run-detail').textContent");
  // Values shows the positions' values again, and the chart their long and short sizes; Risk shows the run.
  await click('#desk-lens [data-lens=values]');await wait(idle);
  await check('the Values lens shows the positions\' values and the long and short chart, and the run stays',`document.querySelector('#desk-lens [data-lens=values]').getAttribute('aria-pressed')==='true'&&[...document.querySelectorAll('#desk-positions .tabulator-col-title')].some(t=>t.textContent.trim()==='Value')&&document.querySelector('#desk-chart-position-title').textContent==='Long and short by market'&&!document.querySelector('#desk-run').hidden`);
  await click('#desk-lens [data-lens=risk]');await wait(inline+'&&'+idle);
  // Clear takes the run off the desk, which the person asked for, so nothing is said about it.
  await evaluate("document.querySelector('#notice').hidden=true");await click('#desk-run-clear');await wait(idle);
  await check('Clear takes the run off the desk and keeps the setup',`${noRun}&&document.querySelector('#notice').hidden&&document.querySelector('input[name=risk-choice][value=parallel]').checked&&${tileSays('Set up in the panel')}`);

  // Queued: one job for each part, each stating the run it is a part of, and the run shown as one.
  await choose('held');
  const jobsBefore=await evaluate('window.__riskJobs.length');
  // Each part's job is read as under way for a few polls, so that the panel can be seen not to be placed again on each poll.
  await evaluate('window.__holdRiskJobs=true;window.__heldPolls=0');await click('#risk-queue');
  await evaluate("window.__placements=0;window.__placementObserver=new MutationObserver(records=>{window.__placements+=records.length;});window.__placementObserver.observe(document.querySelector('#side-panel'),{attributes:true,attributeFilter:['data-placement']});");
  await wait(`window.__heldPolls>=3*Math.ceil(${heldTargets}.length/16)`,30000);
  await check('polling the jobs of a queued run under way does not place the panel again, so the grid beside it is not fitted again on each poll','window.__placements===0','window.__placements');
  await evaluate('window.__holdRiskJobs=false');await wait(inline+'&&'+idle,60000);
  await check('its results come back inline without the panel taking another width',"document.querySelector('#side-panel').dataset.wide===undefined",'window.__placements');
  await evaluate('window.__placementObserver.disconnect()');
  await check('a queued run submits one job for each part, each with its own request identity and the catalog\'s one retained context',
    `(()=>{const c=${catalog},jobs=window.__riskJobs.slice(${jobsBefore});return jobs.length===Math.ceil(${heldTargets}.length/16)&&new Set(jobs.map(j=>j.requestId)).size===jobs.length&&jobs.every(j=>j.request.context.contextId===c.context.contextId&&j.request.bumps.length<=16)&&JSON.stringify(jobs.flatMap(j=>j.request.bumps.map(b=>b.factor)))===JSON.stringify(${heldTargets}.map(t=>t.factor));})()`);
  await check('each part states the run it is a part of: one identity for the run, its own part and the run\'s part count',
    `(()=>{const jobs=window.__riskJobs.slice(${jobsBefore});return jobs.length>1&&jobs.every((j,i)=>j.run&&j.run.id===jobs[0].run.id&&j.run.part===i+1&&j.run.parts===jobs.length);})()`,
    `window.__riskJobs.slice(${jobsBefore}).map(j=>j.run)`);
  await check('the queued parts complete as one run whose results come back inline',
    `(()=>{const parts=${heldTargets}.length>16;return document.querySelector('#risk-job-status').textContent.includes(parts?'parts, each queued as its own job':'Risk job: Completed')&&(!parts||[...document.querySelectorAll('#risk-job-parts li')].every(li=>li.textContent.endsWith('Completed')))&&${shocksShown}===${heldTargets}.length&&document.querySelector('#desk-run-detail').textContent.includes(' · queued as '+Math.ceil(${heldTargets}.length/16)+' jobs, completed ');})()`,
    "document.querySelector('#desk-run-detail').textContent");
  const splitRun=await evaluate(`window.__riskJobs[${jobsBefore}].run.id`);
  await check('the job history lists the run as one, not each of its parts',
    `(()=>{const options=[...document.querySelectorAll('#risk-job-history option')],ids=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk/jobs'&&r.data?.run?.id===${JSON.stringify(splitRun)}).map(r=>r.data.id);return options.filter(o=>o.value==='run:'+${JSON.stringify(splitRun)}).length===1&&options.find(o=>o.value==='run:'+${JSON.stringify(splitRun)}).textContent.includes(' · run of '+Math.ceil(${heldTargets}.length/16)+' parts · ')&&ids.length>0&&!options.some(o=>ids.includes(o.value));})()`,
    "[...document.querySelectorAll('#risk-job-history option')].map(o=>[o.value,o.textContent])");

  // A part that fails is said to have failed, and nothing stands in for it: the other parts still run and show their own results, and a
  // position only it could move is not known, never not moved. The part failed is one whose shocks alone move a position in the queued run.
  const parts=`Math.ceil(${heldTargets}.length/16)`;
  const failing=await evaluate(`(()=>{const reports=window.__riskJobs.slice(${jobsBefore},${jobsBefore}+${parts}).map(sent=>window.__riskReads.findLast(r=>r.path.startsWith('/api/portfolio/risk/jobs/')&&r.data?.requestId===sent.requestId&&r.data.state==='completed').data.result);
    const moved=reports.map(r=>new Set(r.factors.flatMap(f=>f.positions.filter(p=>[p.values.upPnl,p.values.downPnl].some(v=>!/^-?0+(?:\\.0+)?(?:\\/\\d+)?$/.test(v))).map(p=>p.tradeReference))));
    return moved.findIndex((set,index)=>[...set].some(reference=>moved.every((other,o)=>o===index||!other.has(reference))));})()`);
  const requestsBeforeFailure=await evaluate('window.__riskRequests.length'),reportsBeforeFailure=await evaluate("window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').length");
  await evaluate(`window.__failRiskPart=${failing}`);await submit('#risk-form',60000);
  await check('a part of a run made now that fails is said to have failed, and the parts after it still run and show their own results',
    `(()=>{const failed=[...document.querySelectorAll('#desk-run-notes .risk-part-failed')];return ${heldTargets}.length>16&&${failing}>=0&&window.__riskRequests.length-${requestsBeforeFailure}===${parts}&&failed.length===1&&failed[0].getAttribute('role')==='alert'&&failed[0].textContent.startsWith('Part ${failing+1} of '+${parts}+' failed: ')&&${shocksShown}===${heldTargets}.length-Math.min(16,${heldTargets}.length-16*${failing})&&!document.querySelector('#error').hidden;})()`,
    "[[...document.querySelectorAll('#desk-run-notes li')].map(p=>p.textContent),document.querySelector('#desk-tile-risk').textContent,document.querySelector('#error').textContent]");
  const failedPart=(await evaluate("document.querySelector('#desk-run-notes .risk-part-failed').textContent")).replace(/\.$/,'');
  const unknownAfterFailure=await evaluate(`(()=>{const reports=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').slice(${reportsBeforeFailure}).map(r=>r.data).filter(r=>Array.isArray(r.factors)),moved=new Set(reports.flatMap(r=>r.factors.flatMap(f=>f.positions.filter(p=>[p.values.upPnl,p.values.downPnl].some(v=>!/^-?0+(?:\\.0+)?(?:\\/\\d+)?$/.test(v))).map(p=>p.tradeReference))));
    return [...new Set(reports[0].factors[0].positions.map(p=>p.tradeReference))].filter(reference=>!moved.has(reference));})()`);
  const rowsFound=async text=>{await set('#blotter-filters .table-search',text);await wait(idle);return evaluate("document.querySelector('#blotter-filters .table-row-count').textContent");};
  const notMoved=await rowsFound('Not moved'),notKnown=await rowsFound('Not known');
  await set('#blotter-filters .table-search',unknownAfterFailure[0]??'');await wait(idle);
  await check('a position only the failed part could move is not known, with the part and why, in the lens, the change chart and the tile; none is said to be not moved',
    `${unknownAfterFailure.length}>0&&${JSON.stringify(notMoved)}.startsWith('0 of ')&&${JSON.stringify(notKnown)}.startsWith('${unknownAfterFailure.length} of ')`
    +`&&[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.getClientRects().length).every(r=>r.querySelector('[tabulator-field=c3]').textContent===${JSON.stringify('Not known'+failedPart)})`
    +`&&[...document.querySelectorAll('#desk-chart-position .desk-chart-note')].some(p=>p.textContent.startsWith(${JSON.stringify('Not known, as '+failedPart.charAt(0).toLowerCase()+failedPart.slice(1)+': ')}))`
    +`&&document.querySelector('#desk-tile-risk .desk-tile-note').textContent.includes(${JSON.stringify(`Not known for ${unknownAfterFailure.length} position${unknownAfterFailure.length===1?'':'s'} · ${failedPart}`)})&&document.querySelector('#desk-tile-risk').textContent.includes('Largest known move: ')`,
    `[${JSON.stringify([notMoved,notKnown,unknownAfterFailure,failedPart])},[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].map(r=>r.textContent),[...document.querySelectorAll('#desk-chart-position .desk-chart-note')].map(p=>p.textContent),document.querySelector('#desk-tile-risk').textContent]`);
  await set('#blotter-filters .table-search','');await wait(idle);
  // When the parts with results move nothing while a part failed, nothing is known to move: the change chart and the tile say so alike.
  await evaluate(`window.__reportEdit=report=>({...report,factors:report.factors.map(f=>({...f,positions:f.positions.map(p=>({...p,values:{...p.values,up:p.values.base,down:p.values.base,upPnl:'0',downPnl:'0',delta:'0',gamma:'0',
    display:{...p.values.display,up:p.values.display.base,down:p.values.display.base,upPnl:'0',downPnl:'0',delta:'0',gamma:'0'}}}))}))});window.__failRiskPart=${failing}`);
  await submit('#risk-form',60000);await evaluate('window.__reportEdit=undefined');await wait(inline+'&&'+idle,60000);
  await check('a run whose parts with results move nothing while a part failed says no known move, in the change chart as in the tile',
    `document.querySelector('#desk-chart-position .desk-chart-empty')?.textContent===${JSON.stringify('No known move: '+failedPart)}&&document.querySelector('#desk-tile-risk .desk-risk-moves').textContent===${JSON.stringify('No known move: '+failedPart)}`,
    "[document.querySelector('#desk-chart-position').textContent,document.querySelector('#desk-tile-risk').textContent]");
  await check('the download of a run that lacks a part is refused, and says why',"document.querySelector('#risk-export').disabled&&!document.querySelector('#risk-export-note').hidden&&document.querySelector('#risk-export-note').textContent==='The download needs the results of every part of the run.'");
  // A queued part whose submission is refused is not queued, and says why; the other parts are queued and complete as the run.
  const jobsBeforeRefusal=await evaluate('window.__riskJobs.length');
  await evaluate("window.__rejectRiskSubmission='risk_convention'");await click('#risk-queue');
  await wait(inline+'&&'+idle,60000);
  await check('a queued part that is refused says it was not queued and why, and the other parts complete and show their results',
    `(()=>{const items=[...document.querySelectorAll('#risk-job-parts li')],failed=[...document.querySelectorAll('#desk-run-notes .risk-part-failed')];return window.__riskJobs.length-${jobsBeforeRefusal}===${parts}&&items.length===${parts}&&items[0].textContent.startsWith('Part 1 of '+${parts}+' · Not queued · ')&&items.slice(1).every(li=>li.textContent.endsWith('Completed'))&&failed.length===1&&failed[0].textContent.startsWith('Part 1 of '+${parts}+' failed: ')&&${shocksShown}===${heldTargets}.length-16;})()`,
    "[[...document.querySelectorAll('#risk-job-parts li')].map(li=>li.textContent),[...document.querySelectorAll('#desk-run-notes li')].map(li=>li.textContent)]");
  // A queued part whose submission has no known outcome keeps the run retained: the parts that were queued complete and show their
  // results, the part says it has none, and Reconcile sends its same request again, which returns its job with its result.
  const jobsBeforeLoss=await evaluate('window.__riskJobs.length');
  await evaluate("window.__loseRiskSubmission='network'");await click('#risk-queue');
  await wait(inline+'&&'+idle,60000);
  await check('a queued part whose submission has no known outcome keeps the run to reconcile, and the parts that were queued show their results',
    `(()=>{const items=[...document.querySelectorAll('#risk-job-parts li')],missing=[...document.querySelectorAll('#desk-run-notes .risk-part-missing')];return window.__riskJobs.length-${jobsBeforeLoss}===${parts}&&items[0].textContent==='Part 1 of '+${parts}+' · Submission outcome unknown'&&items.slice(1).every(li=>li.textContent.endsWith('Completed'))&&!document.querySelector('#risk-job-retry').hidden&&document.querySelector('#risk-form').hidden&&missing.length===1&&missing[0].textContent==='Part 1 of '+${parts}+' has no results: its submission has no known outcome.'&&${shocksShown}===${heldTargets}.length-16&&document.querySelector('#risk-export').disabled;})()`,
    "[[...document.querySelectorAll('#risk-job-parts li')].map(li=>li.textContent),[...document.querySelectorAll('#desk-run-notes li')].map(li=>li.textContent)]");
  // The lost reply's job was made all the same. Once it has completed, the same request returns it, result and all.
  const lost=await evaluate(`window.__riskJobs[${jobsBeforeLoss}].requestId`);
  await wait(`(async()=>(await(await fetch('/api/portfolio/risk/jobs')).json()).some(j=>j.requestId===${JSON.stringify(lost)}&&j.state==='completed'))()`,60000);
  await click('#risk-job-retry',60000);await wait("!document.querySelector('#risk-form').hidden&&"+idle,60000);
  await check('Reconcile sends the same request again, which returns its completed job, and the run shows every part',
    `(()=>{const jobs=window.__riskJobs.slice(${jobsBeforeLoss});return jobs.length===${parts}+1&&jobs.at(-1).requestId===${JSON.stringify(lost)}&&JSON.stringify(jobs.at(-1))===JSON.stringify(jobs[0])&&[...document.querySelectorAll('#risk-job-parts li')].every(li=>li.textContent.endsWith('Completed'))&&${shocksShown}===${heldTargets}.length&&!document.querySelector('#desk-run-notes .risk-part-missing')&&!document.querySelector('#risk-export').disabled;})()`,
    "[document.querySelector('#desk-tile-risk').textContent,[...document.querySelectorAll('#risk-job-parts li')].map(li=>li.textContent),document.querySelector('#risk-job-status').textContent]");

  // Sizes are kept for the next setup only once the backend has taken a run of them.
  await typeInto('#risk-size-0','0.09');
  await evaluate('window.__refuseRisk=true');await submit('#risk-form',60000);await evaluate('window.__refuseRisk=false');
  await check('the sizes of a run the backend refused are not kept for the next setup',
    `(()=>{const kept=Object.keys(sessionStorage).filter(k=>k.startsWith('openetrm.desk.shockSizes.'));return kept.length===1&&!sessionStorage.getItem(kept[0]).includes('0.09')&&sessionStorage.getItem(kept[0]).includes('0.05')&&document.querySelectorAll('#desk-run-notes .risk-part-failed').length===${parts};})()`,
    "Object.entries(sessionStorage).filter(([key])=>key.startsWith('openetrm.desk.shockSizes.'))");
  const noResults=`('Parts '+[...Array(${parts}-1).keys()].map(i=>i+1).join(', ')+' and '+${parts}+' of '+${parts}+' have no results')`;
  await check('a run no part of which has results says so in the risk tile, which names its parts, never as not run',
    `document.querySelector('#desk-tile-risk .desk-risk-state').textContent==='No results'&&document.querySelector('#desk-tile-risk .desk-tile-hint').textContent==='Parts '+[...Array(${parts}-1).keys()].map(i=>i+1).join(', ')+' and '+${parts}+' of '+${parts}+' have no results'&&!document.querySelector('#desk-run').hidden`,
    "document.querySelector('#desk-tile-risk').textContent");
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("document.querySelector('#side-panel').dataset.mode==='position'&&"+idle);
  await click('#side-panel-tabs-risk');await wait("document.querySelector('#side-panel-tabs-risk').getAttribute('aria-selected')==='true'&&"+idle);
  await check('a position\'s Risk tab says the run on the desk has no results, and why, never that there is no run',
    `document.querySelector('#side-panel-view .side-panel-status').textContent==='The risk run on the desk has no results. '+${noResults}+'.'`,"document.querySelector('#side-panel-view').textContent");
  // What a preset leaves out is said where it is chosen, in its results and in its download, so that a run that left prices out can be told
  // from a complete one. The catalog read here names held prices and curves without a whole target: synthetic labels in its own lists.
  await click('#futures-refresh');await wait("document.querySelector('#side-panel').hidden&&"+idle);
  await check('a new read clears a run that has no results, and says so too',`${noRun}&&(${notice}).includes(${JSON.stringify(readAgain)})`,notice);
  await evaluate("window.__catalogEdit=c=>({...c,heldContractsWithoutTarget:['Synthetic output · 2021-01-01 to 2021-02-01'],heldCurvesWithoutTarget:['Synthetic output curve']})");
  await openReport();await choose('parallel');
  await check('Parallel shift names the held curves it does not shift, as no whole-curve target covers them',
    "!document.querySelector('#risk-preset-gaps').hidden&&document.querySelector('#risk-preset-gaps').textContent==='No whole-curve scenario target covers these held curves, so this preset does not shift them: Synthetic output curve.'",
    "document.querySelector('#risk-preset-gaps').textContent");
  await choose('held');
  await check('Shock every held contract names the held prices it does not shock, as no whole-contract target covers them',
    "!document.querySelector('#risk-preset-gaps').hidden&&document.querySelector('#risk-preset-gaps').textContent==='No whole-contract scenario target covers these held prices, so this preset does not shock them: Synthetic output · 2021-01-01 to 2021-02-01.'",
    "document.querySelector('#risk-preset-gaps').textContent");
  await submit('#risk-form',60000);await wait(inline+'&&'+idle,60000);
  await check('its results say which report was run, with its sizes, and name the held price it did not shock',
    `(()=>{const units=[...new Set(${heldTargets}.map(t=>t.unit))];return document.querySelector('#risk-run-setup')?.textContent==='Shock every held contract · '+units.map(unit=>'±0.05 '+unit).join(', ')+' · not shocked, as no whole-contract target covers it: Synthetic output · 2021-01-01 to 2021-02-01';})()`,
    "document.querySelector('#risk-run-setup')?.textContent");
  await evaluate(`(()=>{window.__riskBlob=undefined;const create=URL.createObjectURL;URL.createObjectURL=b=>{window.__riskBlob=b;return create(b);};const click=HTMLAnchorElement.prototype.click;HTMLAnchorElement.prototype.click=function(){if(this.download==='leanetrm-risk.json')return;return click.call(this);};})();`);
  await click('#risk-export');await wait('!!window.__riskBlob');
  await check('and so does its download, beside the report of each part',
    `(async()=>{const units=[...new Set(${heldTargets}.map(t=>t.unit))],{setup,parts:reports}=JSON.parse(await window.__riskBlob.text());return setup?.report==='held'&&setup.label==='Shock every held contract'&&JSON.stringify(setup.sizes)===JSON.stringify(Object.fromEntries(units.map(unit=>[unit,'0.05'])))&&JSON.stringify(setup.withoutTarget)===JSON.stringify(['Synthetic output · 2021-01-01 to 2021-02-01'])&&reports.length===${parts};})()`,
    "window.__riskBlob.text()");
  // Another book scope is a new read for other books, which clears the run in the design's words; the books it had are read again after.
  const scope=async()=>{if(!await evaluate("document.querySelector('#blotter-scope').open"))await click('#blotter-scope>summary');};
  const booksChosen=await evaluate("[...document.querySelector('#blotter-books').options].map(o=>o.selected)");
  await scope();await evaluate("(()=>{const options=[...document.querySelector('#blotter-books').options];options.forEach((o,i)=>{o.selected=i===0;});})()");await click('#blotter-scope-apply');
  await wait(noRun+'&&'+idle);
  await check('changing the book scope clears the run, and the page says the close or books changed',
    `${noRun}&&(${notice}).includes(${JSON.stringify(cleared)})&&!(${notice}).includes(${JSON.stringify(readAgain)})&&${tileSays('Not run at this close')}`,notice);
  await scope();await evaluate(`(()=>{const chosen=${JSON.stringify(booksChosen)};[...document.querySelector('#blotter-books').options].forEach((o,i)=>{o.selected=chosen[i];});})()`);await click('#blotter-scope-apply');await wait(idle);
  // A catalog that does not say what a preset leaves out, or which positions a run leaves out, is refused: never read as saying none.
  await click('#futures-refresh');await wait("document.querySelector('#side-panel').hidden&&"+idle);
  await evaluate("window.__catalogEdit=({heldContractsWithoutTarget,...rest})=>rest");
  await openReport();await choose('held');
  const requestsBeforeRefusal=await evaluate('window.__riskRequests.length');
  await submit('#risk-form');
  await check('a catalog without the list of held prices a preset does not shock cannot run that preset, and says it is inconsistent',
    `document.querySelector('#risk-choice-held-detail').textContent==='The risk catalog is inconsistent. Reload it.'&&document.querySelector('#risk-preset-summary').textContent==='The risk catalog is inconsistent. Reload it.'&&document.querySelector('#error').textContent.includes('The risk catalog is inconsistent. Reload it.')&&window.__riskRequests.length===${requestsBeforeRefusal}`,
    "[document.querySelector('#risk-choice-held-detail').textContent,document.querySelector('#risk-preset-summary').textContent,document.querySelector('#error').textContent]");
  // The refused setup still holds the person's choice, which a new read of the Blotter asks about before it is given up.
  await click('#futures-refresh');await wait("!document.querySelector('#side-panel-prompt').hidden");
  await click('#side-panel-prompt button:not(.secondary)');await wait("document.querySelector('#side-panel').hidden&&"+idle);
  await evaluate("window.__catalogEdit=({excludedPositions,...rest})=>rest");
  await click('#desk-risk-run');await wait("document.querySelector('#side-panel').dataset.mode==='risk'&&"+idle);
  await check('a catalog that does not say which positions a run leaves out is refused, and the report offers nothing to run',
    "document.querySelector('#error').textContent.includes('The risk catalog is inconsistent. Reload it.')&&document.querySelector('#risk-fields').disabled&&!document.querySelector('#risk-status').hidden&&!document.querySelector('#risk-reload').hidden",
    "[document.querySelector('#error').textContent,document.querySelector('#risk-status').textContent]");
  await evaluate('window.__catalogEdit=undefined');await click('#risk-reload');await wait("!document.querySelector('#risk-fields').disabled&&"+idle);
  await choose('custom');await set('#risk-factor',await evaluate(`${catalog}.scenarioTargets.find(t=>t.kind==='contract').id`));await set('#risk-bump','0.1');
  await evaluate("window.__reportEdit=({excludedPositions,...rest})=>rest");await submit('#risk-form');await evaluate('window.__reportEdit=undefined');
  await check('a result that does not say which positions it leaves out is not shown, and the risk tile says so with the reason',
    "document.querySelector('#desk-tile-risk .desk-risk-state').textContent==='Results not shown'&&document.querySelector('#desk-tile-risk .desk-tile-hint').textContent==='A result of this run does not say which positions it leaves out, so its results are not shown. Run it again.'&&[...document.querySelectorAll('#desk-run-notes [role=alert]')].some(p=>p.textContent==='A result of this run does not say which positions it leaves out, so its results are not shown. Run it again.')&&document.querySelector('#desk-lens').hidden&&document.querySelector('#risk-export').hidden&&!document.querySelector('#desk-tile-risk .kpi-value')",
    "[...document.querySelectorAll('#desk-run-notes li')].map(p=>p.textContent)");
  await click('#futures-refresh');await wait("document.querySelector('#side-panel').hidden&&"+idle);await openReport();

  // The custom scenario lab: an explicit target and bump, nested stresses and the targets the catalog offers.
  await choose('custom');
  await check('shock entry requires explicit factor and bump choices',"!document.querySelector('#risk-custom').hidden&&document.querySelector('#risk-preset').hidden&&document.querySelector('#risk-factor').value===''&&document.querySelector('#risk-bump').value===''&&!document.querySelector('#risk-form').checkValidity()");
  await check('risk opens with an explicit unchanged market and an empty nested stress stack',"document.querySelector('#risk-stress-stack')?.children.length===0&&document.querySelector('#risk-stress-parent')?.disabled&&document.querySelector('#risk-stress-clear')?.disabled&&document.querySelector('#risk-stress-factor')?.value===''&&document.querySelector('#risk-stress-size')?.value===''");
  const factor=await evaluate(`(()=>{const c=${catalog},source=c.factors.find(f=>f.curves.includes('future')&&f.deliveryFrom==='2021-01-01');return c.scenarioTargets.find(t=>t.id===source.id&&JSON.stringify(t.factor)===JSON.stringify(source.factor)).id;})()`);
  await set('#risk-factor',factor);await set('#risk-bump','0.1');await submit('#risk-form');await wait(inline+'&&'+idle);
  const customMoved=`(()=>{const r=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').at(-1).data;return [...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(row=>${movedBy}(row.querySelector('.desk-reference').textContent,[r])===1);})()`;
  await check('the lens states each moved position\'s delta in its explicit derivative unit, and a position the shock does not move says so',
    `(()=>{const moved=${customMoved},rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')];return document.querySelector('#error').hidden&&moved.length>0&&moved.every(row=>${lensCell('row','c6')}.textContent.includes('USD / (1 USD / MMBtu (IT))'))&&rows.some(row=>${lensCell('row','c3')}.textContent.startsWith('Not moved'))&&${shocksShown}===1;})()`);
  await check('risk uses the blotter context and every selected futures and swap holding',"(()=>{const r=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').at(-1).data;const h=window.__tradingReports(window.__riskReads).at(-1).data;return r.context.contextId===h.context.contextId&&r.heldInstruments>1&&r.work.positionCount===h.holdings.length&&r.inputs.positions.length===h.holdings.length&&h.holdings.every(row=>r.inputs.positions.some(p=>row.tradeId===p.tradeId&&row.bindingId===p.bindingId))&&new Set(r.factors[0].totals.map(t=>t.measure)).size===2;})()");
  await check('risk submits the complete pricing policy offered by its catalog without inventing inputs',`(()=>{const c=${catalog},r=window.__riskRequests.at(-1);const source=c.factors.find(f=>f.curves.includes('future')&&f.deliveryFrom==='2021-01-01');return r.context.convention==='trading_workspace_v1'&&r.convention==='portfolio_instrument_repricing_v1'&&r.numericalPolicy==='exact_rational_portfolio_repricing_v1'&&JSON.stringify(r.pricingPolicy)===JSON.stringify(c.pricingPolicy)&&JSON.stringify(r.bumps[0].factor)===JSON.stringify(source.factor);})()`);
  await check('each moved position\'s change names its own measure and currency, and states its exact amount beneath the display text',
    `(()=>{const r=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').at(-1).data,moved=${customMoved};return moved.length>0&&moved.every(row=>{const position=r.factors[0].positions.find(p=>p.tradeReference===row.querySelector('.desk-reference').textContent),up=${lensCell('row','c4')};return up.querySelector('.desk-figure').title===position.values.upPnl&&up.querySelector('.desk-sub').textContent===(position.measure==='futures_execution_pnl'?'P&L':'PV')+' · '+position.values.currency;});})()`);
  await check('the change chart draws a signed bar for each position the shock moves, and names those it does not move',
    `(()=>{const moved=${customMoved},items=[...document.querySelectorAll('#desk-chart-position .chart ul.sr-only li')];return document.querySelector('#desk-chart-position-title').textContent==='Change if each contract moves up'&&items.length===moved.length&&items.every(li=>/: [+−]/.test(li.textContent))&&document.querySelector('#desk-chart-position .desk-chart-note')?.textContent.startsWith('Not moved by any shock: ');})()`,
    "document.querySelector('#desk-chart-position').textContent");
  await check('work evidence distinguishes materialization from each backend connected graph receipt',"(()=>{const r=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').at(-1).data,rows=[...document.querySelectorAll('#risk-work li')];return rows.length===r.connectedScenarios.length+1&&rows[0].textContent.includes('Materialization')&&r.connectedScenarios.every((m,i)=>rows[i+1].textContent.includes(m.definitions.revisionId)&&rows[i+1].textContent.includes(m.work.evaluatedNodes+' graph nodes evaluated')&&rows[i+1].textContent.includes(m.preparation.sourceCompilations+' source compilations'));})()");
  // Closing the panel is not clearing the run: it stays inline, and Edit shocks opens the report again.
  await click('#side-panel-close');
  await check('closing the panel keeps the run inline, and the tile shows its largest move',`document.querySelector('#side-panel').hidden&&${inline}&&!!document.querySelector('#desk-tile-risk .kpi-value')&&document.querySelector('#desk-risk-run').hidden`);
  await click('#desk-run-edit');await wait(panelRisk+"&&"+idle);
  await click('#risk-stress>summary');await set('#risk-stress-factor',factor);await set('#risk-stress-size','0.3');await click('#risk-stress-add');
  await set('#risk-stress-size','-0.1');await click('#risk-stress-add');await submit('#risk-form');
  await check('nested stresses retain ordered signed child shifts beneath the same financial context',`(()=>{const r=window.__riskRequests.at(-1),result=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').at(-1).data;return r.stress.length===2&&r.stress[0].size==='0.3'&&r.stress[1].size==='-0.1'&&r.stress.every(s=>s.convention===${catalog}.stressConvention)&&result.scenario.depth===2&&result.context.contextId===r.context.contextId&&document.querySelectorAll('#risk-stress-stack li').length===2&&document.querySelector('#risk-scenario').textContent.includes('2 nested stresses')&&[...document.querySelectorAll('#desk-run-notes li')].some(li=>li.textContent==='Around 2 nested stresses: the base, up and down values all include them.');})()`);
  await click('#risk-stress-parent');await submit('#risk-form');
  await check('returning to parent removes only the inner stress and reprices that parent',"(()=>{const request=window.__riskRequests.at(-1),report=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').at(-1).data;return request.stress.length===1&&request.stress[0].size==='0.3'&&report.scenario.depth===1&&document.querySelectorAll('#risk-stress-stack li').length===1;})()");
  await click('#risk-stress-clear');await submit('#risk-form');
  await check('leaving the outer stress restores the original unchanged-market result',"(()=>{const reports=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').map(r=>r.data).filter(r=>r.factors.length===1&&r.factors[0].factor.id===document.querySelector('#risk-factor').value);return window.__riskRequests.at(-1).stress.length===0&&reports.at(-1).scenario.isBase&&JSON.stringify(reports[0].factors[0].totals)===JSON.stringify(reports.at(-1).factors[0].totals)&&document.querySelector('#risk-stress-parent').disabled;})()");
  await set('#risk-coordinate','computed_output');await set('#risk-target-kind','parallel');
  await check('computed-output filters show only backend-offered whole-curve targets',`(()=>{const c=${catalog},choices=[...document.querySelector('#risk-factor').options].filter(o=>o.value);return choices.length>0&&choices.every(o=>c.scenarioTargets.some(t=>t.id===o.value&&t.coordinate==='computed_output'&&t.kind==='parallel'));})()`);
  const output=await evaluate("document.querySelector('#risk-factor').options[1].value");await set('#risk-factor',output);await submit('#risk-form');
  await check('computed output repricing transports its exact offered coordinate without rewriting primitive inputs',`(()=>{const request=window.__riskRequests.at(-1),c=${catalog},target=c.scenarioTargets.find(t=>t.id===document.querySelector('#risk-factor').value),report=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').at(-1).data;return JSON.stringify(request.bumps[0].factor)===JSON.stringify(target.factor)&&request.bumps[0].convention===c.bumpConvention&&report.factors[0].factor.coordinate==='computed_output'&&document.querySelector('#risk-unit').textContent.includes(target.unit);})()`);
  await set('#risk-coordinate','market_quote');await set('#risk-target-kind','delivery_bucket');
  const bucket=await evaluate(`(()=>{const c=${catalog};return c.scenarioTargets.find(t=>t.coordinate==='market_quote'&&t.kind==='delivery_bucket'&&c.availableFactors.some(f=>f.deliveryFrom==='2021-01-01'&&f.factor.target.curve.code===t.factor.target.curve.code&&f.factor.target.curve.revision.revisionId===t.factor.target.curve.revision.revisionId)).id;})()`);await set('#risk-factor',bucket);
  await check('delivery bucket requires an explicit window instead of inferring dates from the header',"!document.querySelector('#risk-bucket').hidden&&document.querySelector('#risk-from').value===''&&document.querySelector('#risk-to').value===''");
  await set('#risk-from','2021-01-01');await set('#risk-to','2021-02-01');await submit('#risk-form');
  await check('bucket repricing preserves the offered containment rule and explicitly entered half-open dates',`(()=>{const b=window.__riskRequests.at(-1).bumps[0],c=${catalog},t=c.scenarioTargets.find(t=>t.id===document.querySelector('#risk-factor').value);return b.factor.target.from==='2021-01-01'&&b.factor.target.to==='2021-02-01'&&b.factor.target.convention===t.factor.target.convention&&b.factor.coordinate===t.factor.coordinate&&document.querySelector('#error').hidden;})()`);
  await set('#risk-coordinate','all');await set('#risk-target-kind','all');await set('#risk-factor',factor);await submit('#risk-form');
  // A rational change reads as the backend's display text, with its exact amount beside it: a generic nonlinear response, edited into the
  // run's own report, keeps zero delta, nonzero gamma and its own measure label. Nonlinear repricing is qualified by the managed engine checks.
  await evaluate(`window.__reportEdit=report=>{const first=report.factors[0],held=first.positions.findIndex(p=>!/^-?0+(?:\\.0+)?$/.test(p.values.upPnl));first.positions[held]={...first.positions[held],measureLabel:'Synthetic nonlinear PV',values:{...first.positions[held].values,base:'1/3',up:'103/300',down:'103/300',upPnl:'1/100',downPnl:'1/100',delta:'0',gamma:'2'}};window.__nonlinear=first.positions[held].tradeReference;return report;}`);
  await submit('#risk-form');await evaluate('window.__reportEdit=undefined');await wait(inline+'&&'+idle);
  const nonlinear=await evaluate('window.__nonlinear');
  await set('#blotter-filters .table-search',nonlinear);await wait("[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(r=>r.getClientRects().length).length===1");
  await check('a rational change reads as the backend\'s display text with its exact amount, and a zero delta stays zero',
    `(()=>{const row=document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)'),up=${lensCell('row','c4')}.querySelector('.desk-figure'),delta=${lensCell('row','c6')}.querySelector('.desk-figure');return row.querySelector('.desk-reference').textContent===${JSON.stringify(nonlinear)}&&up.title==='1/100'&&up.textContent.startsWith('+')&&!up.textContent.includes('/')&&delta.title==='0'&&delta.textContent==='0';})()`,
    "document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)').textContent");
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("document.querySelector('#side-panel').dataset.mode==='position'&&"+idle);
  await click('#side-panel-tabs-risk');await wait("document.querySelector('#side-panel-tabs-risk').getAttribute('aria-selected')==='true'&&"+idle);
  await check('its Risk tab keeps the response\'s own measure label, a nonzero gamma and a zero delta, each with its exact amount',
    "(()=>{const facts=Object.fromEntries([...document.querySelectorAll('#side-panel-view .side-risk-shock dt')].map(dt=>[dt.textContent,dt.nextElementSibling.querySelector('b')]));return document.querySelector('#side-panel-view .side-risk-shock .field-help').textContent.includes('Synthetic nonlinear PV')&&facts.Gamma.title==='2'&&facts.Gamma.textContent==='+2'&&facts.Delta.title==='0'&&facts.Base.title==='1/3';})()",
    "document.querySelector('#side-panel-view').textContent");
  await set('#blotter-filters .table-search','');
  // A run whose shocks move no position says so in the risk tile: its report, edited, states no change for any position.
  await click('#blotter-risk');await wait(panelRisk+'&&'+idle);await choose('custom');await set('#risk-factor',factor);await set('#risk-bump','0.1');
  await evaluate(`window.__reportEdit=report=>({...report,factors:report.factors.map(f=>({...f,positions:f.positions.map(p=>({...p,values:{...p.values,up:p.values.base,down:p.values.base,upPnl:'0',downPnl:'0',delta:'0',gamma:'0',
    display:{...p.values.display,up:p.values.display.base,down:p.values.display.base,upPnl:'0',downPnl:'0',delta:'0',gamma:'0'}}}))}))})`);
  await submit('#risk-form');await evaluate('window.__reportEdit=undefined');await wait(inline+'&&'+idle);
  await check('a run that moves no position says so in the risk tile',
    "document.querySelector('#desk-tile-risk .desk-risk-moves').textContent===\"No position moves by this run's shocks.\"&&!document.querySelector('#desk-tile-risk .kpi-value')",
    "document.querySelector('#desk-tile-risk').textContent");
  // Results prepared late, for a run no longer on screen, are never drawn: a first run's display text is held while a second run is made,
  // and the first's, released while the second's is still being prepared, draws nothing; the second's then draws its own. A size not run
  // before has amounts not yet prepared for display, so each run's preparation is read, and held here.
  await evaluate(`(()=>{const send=window.fetch;window.__heldDisplay=[];window.fetch=async(input,init)=>{if(String(input)==='/api/amounts/display'&&window.__holdDisplay)await new Promise(resolve=>window.__heldDisplay.push(resolve));return send(input,init);};})()`);
  await set('#risk-bump','0.37');await evaluate('window.__holdDisplay=true');await submit('#risk-form');await wait("window.__heldDisplay.length===1&&!document.querySelector('#desk-run').hidden&&"+idle);
  await set('#risk-bump','0.41');await submit('#risk-form');await wait("window.__heldDisplay.length===2&&"+idle);
  await evaluate('window.__heldDisplay[0]()');await new Promise(resolve=>setTimeout(resolve,500));
  await check('results prepared late for a run no longer on screen are not drawn while the run on screen is being prepared',
    "document.querySelector('#desk-lens').hidden&&!document.querySelector('#desk-tile-risk .kpi-value')&&!document.querySelector('#desk-run').hidden",
    "[document.querySelector('#desk-lens').hidden,document.querySelector('#desk-tile-risk').textContent]");
  await evaluate('window.__holdDisplay=false;for(const release of window.__heldDisplay.slice(1))release()');await wait(inline+'&&'+idle);
  await check('and the run on screen draws its own results once they are prepared',
    "[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group) [tabulator-field=c3]')].some(cell=>cell.textContent.startsWith('±0.41'))&&![...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group) [tabulator-field=c3]')].some(cell=>cell.textContent.startsWith('±0.37'))",
    "[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group) [tabulator-field=c3]')].map(cell=>cell.textContent)");
  // A pivot shows totals, which have no lens: the run bar says the results are by position and points to Export and to a run made on trade rows.
  await click('#blotter-pivot>summary');await set('#blotter-mode','pivot');await click('#blotter-dimensions input[value="book"]');await click('#blotter-measures input[value="tradeCount"]');await click('#blotter-apply');
  await wait("document.querySelector('#blotter-table-title').textContent==='Pivot summary'&&"+idle);
  await click('#blotter-risk');await wait(panelRisk+'&&'+idle);await choose('custom');await set('#risk-factor',factor);await set('#risk-bump','0.1');await submit('#risk-form');
  await wait("!document.querySelector('#desk-run').hidden&&"+idle);
  await check('a pivot view has no lens: the run bar says where a position\'s results are, and the tile still shows the run',
    "document.querySelector('#desk-lens').hidden&&[...document.querySelectorAll('#desk-run-notes li')].some(li=>li.textContent==='Risk results are by position, which totals do not show. Export downloads them for every factor; a run made with Display set to Individual trades shows each position\\'s shocks in its row and in the Risk tab of its panel.')&&!!document.querySelector('#desk-tile-risk .kpi-value')",
    "[...document.querySelectorAll('#desk-run-notes li')].map(li=>li.textContent)");
  await click('#blotter-pivot>summary');await set('#blotter-mode','trades');await click('#blotter-apply');
  await wait("document.querySelector('#blotter-table-title').textContent.startsWith('Positions')&&"+idle);
  await click('#blotter-risk');await wait(panelRisk+'&&'+idle);await choose('custom');await set('#risk-factor',factor);await set('#risk-bump','0.1');await submit('#risk-form');await wait(inline+'&&'+idle);
  await shot('risk-desktop');
  await set('#risk-bump','0');
  await check('editing a shock clears its prior result and export immediately',`${noRun}&&document.querySelector('#side-panel').dataset.wide===undefined`);
  await submit('#risk-form');
  await check('zero bump produces a visible structured failure without cached results',`!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('positive absolute bump')&&${noRun}`);
  await set('#risk-bump','0.1');await submit('#risk-form');
  await evaluate(`(()=>{window.__riskBlob=undefined;const create=URL.createObjectURL;URL.createObjectURL=b=>{window.__riskBlob=b;return create(b);};const click=HTMLAnchorElement.prototype.click;HTMLAnchorElement.prototype.click=function(){if(this.download==='leanetrm-risk.json')return;return click.call(this);};})();`);
  await click('#risk-export');
  await check('risk download reauthorizes the exact pinned request and includes its inputs',"(async()=>{const {setup,parts}=JSON.parse(await window.__riskBlob.text()),r=parts[0];if(setup?.report!=='custom'||setup.label!=='Custom scenario')return false;const requests=window.__riskRequests;return parts.length===1&&r.inputs.positions.length===r.work.positionCount&&r.inputs.markets.length>1&&r.inputs.markets.every(m=>m.observations.length>0)&&r.factors[0].factor.coordinate==='market_quote'&&!!r.factors[0].factor.unit&&r.displayPolicy&&r.factors[0].totals.every(t=>t.values.display&&t.measure&&t.measureLabel)&&JSON.stringify(requests.at(-1))===JSON.stringify(requests.at(-2));})()");
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await wait("document.querySelector('#side-panel').dataset.placement==='overlay'");
  await check('the report fills the phone sheet without page-wide overflow',fitsWidth+"&&document.querySelector('#side-panel').dataset.wide===undefined");await shot('risk-mobile');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1250,deviceScaleFactor:1,mobile:false});
  await evaluate('window.__previousRiskBlob=window.__riskBlob;window.__denyRisk=true');await click('#risk-export');
  await check('denied risk export creates no download and clears risk and holdings caches',`window.__riskBlob===window.__previousRiskBlob&&document.querySelector('#side-panel').hidden&&${noRun}&&document.querySelector('#desk-positions').children.length===0`);
  await click('#futures-refresh');await openReport();await choose('custom');await set('#risk-factor',factor);await set('#risk-bump','0.1');
  for(const outcome of ['network','server']){
    if(await evaluate("!document.querySelector('#risk-job-return').hidden"))await click('#risk-job-return');
    await choose('custom');await set('#risk-factor',factor);await set('#risk-bump','0.1');await evaluate('window.__loseRiskSubmission='+JSON.stringify(outcome));await click('#risk-queue');
    await check(`${outcome} unknown queue outcomes retain their exact request for safe reconciliation`,"!document.querySelector('#risk-job-retry').hidden&&document.querySelector('#risk-fields').disabled&&document.querySelector('#risk-form').hidden&&document.querySelector('#risk-job-status').textContent.includes('unknown')&&window.__riskJobs.at(-1).request.context.knowledgeMode==='retained'");
    await evaluate('window.__riskCatalogCount=window.__riskReads.filter(r=>r.path==="/api/portfolio/risk/catalog").length;window.__rejectRiskSubmission='+JSON.stringify(outcome==='server'?'access_denied':'request_format'));await click('#risk-job-retry');
    if(outcome==='server')await click('#futures-refresh');else await click('#side-panel-close');
    await click('#blotter-risk');await wait(panelRisk);
    await check(`${outcome} uncertainty survives a rejected retry and recovers its receipt without selecting a new financial context`,"document.querySelector('#risk-form').hidden&&document.querySelector('#risk-fields').disabled&&document.querySelector('#risk-job-context').textContent.includes('2020-12-18')&&window.__riskReads.filter(r=>r.path==='/api/portfolio/risk/catalog').length===window.__riskCatalogCount",
      "[document.querySelector('#risk-form').hidden,document.querySelector('#risk-fields').disabled,document.querySelector('#risk-job-context').textContent,window.__riskReads.filter(r=>r.path==='/api/portfolio/risk/catalog').length,window.__riskCatalogCount,document.querySelector('#side-panel').dataset.mode,document.querySelector('#error').textContent]");
    await wait("/Completed|Failed/.test(document.querySelector('#risk-job-status').textContent)");
    await check('queued risk completes rather than ending with a hidden worker failure',"document.querySelector('#risk-job-status').textContent.includes('Completed')","window.__riskReads.filter(r=>r.path.startsWith('/api/portfolio/risk/jobs')).at(-1)?.data");
    // The job finishes on the report's own timer, outside the page's actions: the desk still follows it. A run kept through a new read of the
    // Blotter was made at the context it was captured at, which is no longer the one on screen, so its results are not shown inline.
    await check(`${outcome} queued risk that completes by polling moves the run bar to its results`,outcome==='server'
      ?"!document.querySelector('#desk-run').hidden&&document.querySelector('#desk-lens').hidden&&[...document.querySelectorAll('#desk-run-notes li')].some(li=>li.textContent.startsWith('This run was made at another close, context or books than the Blotter read on screen'))&&document.querySelector('#desk-tile-risk .desk-risk-state').textContent==='Results made elsewhere'"
      :`${inline}&&!!document.querySelector('#desk-tile-risk .kpi-value')`,"[document.querySelector('#desk-run').textContent,document.querySelector('#desk-tile-risk').textContent]");
    await check(`${outcome} queued risk retries use one identity and publish the captured context`,"(()=>{const jobs=window.__riskJobs,reads=window.__riskReads.filter(r=>r.path.startsWith('/api/portfolio/risk/jobs'));const done=reads.findLast(r=>r.data.state==='completed').data;return JSON.stringify(jobs.at(-1))===JSON.stringify(jobs.at(-2))&&done.result.context.contextId===jobs.at(-1).request.context.contextId&&done.result.work.positionCount===window.__tradingReports(window.__riskReads).at(-1).data.holdings.length&&!document.querySelector('#risk-export').hidden;})()");
  }
  await click('#risk-export');
  await check('completed job download reads its retained result with current authorization',"(async()=>{const {parts}=JSON.parse(await window.__riskBlob.text()),last=window.__riskReads.at(-1);return parts.length===1&&last.path.startsWith('/api/portfolio/risk/jobs/')&&last.data.state==='completed'&&parts[0].context.contextId===window.__riskJobs.at(-1).request.context.contextId;})()");
  const retainedJob=await evaluate("window.__riskReads.findLast(r=>r.path.startsWith('/api/portfolio/risk/jobs')&&r.data.state==='completed').data.id");
  const earlier=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-17')).id;})()");
  await set('#snapshot',earlier);await wait(idle);
  await check('changing the close clears the run and its choices, and the page says so',`document.querySelector('#side-panel').hidden&&document.querySelector('#risk-factor').value===''&&${noRun}&&[...document.querySelectorAll('input[name=risk-choice]')].every(c=>!c.checked)&&document.querySelector('#notice').textContent.includes(${JSON.stringify(cleared)})`,
    "document.querySelector('#notice').textContent");
  await click('#blotter-risk');await wait(panelRisk);await set('#risk-job-history',retainedJob);await wait(idle);await wait("!document.querySelector('#desk-run').hidden");
  await check('recent jobs remain discoverable after context changes and identify their original dates, and their results are not shown against another close',"document.querySelector('#risk-job-context').textContent.includes('2020-12-18')&&document.querySelector('#risk-scope').textContent.includes('captured financial context')&&document.querySelector('#risk-form').hidden&&document.querySelector('#risk-job-scenario').textContent.includes('original market')&&document.querySelector('#risk-fields').disabled&&!document.querySelector('#risk-job-return').hidden&&document.querySelector('#desk-lens').hidden&&[...document.querySelectorAll('#desk-run-notes li')].some(li=>li.textContent.startsWith('This run was made at another close, context or books'))&&!document.querySelector('#risk-export').hidden");
  await click('#risk-job-return');
  await check('returning from a retained job clears results before enabling the selected context',`!document.querySelector('#risk-form').hidden&&!document.querySelector('#risk-fields').disabled&&${noRun}&&document.querySelector('#risk-job').hidden`);
  await click('#profile-open');await set('#zone','America/Chicago');await submit('#preferences-form');await click('#blotter-risk');await wait(panelRisk);
  await set('#risk-job-history',retainedJob);await wait(idle);
  await check('retained job timestamp labels follow the selected display zone',"document.querySelector('label[for=risk-job-history]').textContent.includes('America/Chicago')&&document.querySelector('#risk-job-history').selectedOptions[0].textContent.includes('10:00 AM')");
  await persona('trader');await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
  await check('Trader permissions do not enable the risk report, and the risk tile says why',`document.querySelector('#blotter-risk').disabled&&document.querySelector('#blotter-risk').title.includes('Risk Analyst role')&&document.querySelector('#desk-risk-run').disabled&&document.querySelector('#desk-tile-risk').textContent.includes('Risk Analyst role')&&document.querySelector('#side-panel').dataset.mode!=='risk'`);
  await persona('risk');await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait("document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&!document.querySelector('#blotter-risk').disabled");
  await openReport();
  // Controlled transport ordering exercises presentation races independently of
  // the database suite's worker/cancellation transaction races.
  await evaluate(`(async()=>{
    const catalog=window.__riskReads.find(r=>r.path==='/api/portfolio/risk/catalog').data;
    const completed=window.__riskReads.findLast(r=>r.path.startsWith('/api/portfolio/risk/jobs')&&r.data.state==='completed').data;
    window.__riskRaceJob=completed;window.__riskRaceResponses=[];
    const api=async(path,body)=>{
      if(path==='portfolio/risk/catalog')return catalog;
      if(path==='portfolio/risk/jobs')return body?{...completed,state:'running',result:undefined}:[];
      if(path.endsWith('/cancel'))return {...completed,state:'cancellation_requested',result:undefined};
      return new Promise(resolve=>window.__riskRaceResponses.push(resolve));
    };
    // The independently imported module has its own metadata map beside the
    // bundled application; initialize it from the actual authorized directory.
    (await import('/market-view.js')).setCurveNames(await(await fetch('/api/curve-names')).json());
    window.__riskHarness=(await import('/risk-view.js')).setupRisk({api,act:work=>work(),busy:()=>false,zone:()=> 'America/Chicago',canRun:()=>true,profile:()=> 'synthetic-risk-races',scope:()=>window.__riskJobs.at(-1).request,onDenied:()=>{},onChange:()=>{}});
    await window.__riskHarness.open();
  })()`);
  await choose('custom');await set('#risk-factor',factor);await set('#risk-bump','0.1');await click('#risk-queue');await wait('window.__riskRaceResponses.length===1');
  await click('#risk-job-cancel');
  await check('cancellation requested remains distinct from confirmed termination',"document.querySelector('#risk-job-status').textContent.includes('Cancellation requested')&&document.querySelector('#risk-job-status').textContent.includes('Waiting for the worker to stop')&&document.querySelector('#risk-job-cancel').hidden");
  await evaluate("(async()=>{window.__riskRaceResponses.shift()({...window.__riskRaceJob,state:'running',result:undefined});await Promise.resolve();})()");
  await check('a delayed earlier status cannot overwrite the accepted cancellation response',"document.querySelector('#risk-job-status').textContent.includes('Cancellation requested')&&document.querySelector('#risk-job-cancel').hidden");
  await evaluate('(async()=>{window.__riskHarness.reset();await window.__riskHarness.open();})()');await choose('custom');await set('#risk-factor',factor);await set('#risk-bump','0.1');await click('#risk-queue');await wait('window.__riskRaceResponses.length===1');
  await evaluate("(async()=>{window.__riskHarness.reset();window.__riskRaceResponses.shift()(window.__riskRaceJob);await Promise.resolve();})()");
  await check('late completed status after a financial-context reset cannot repopulate the old results',"window.__riskHarness.current()===undefined&&document.querySelector('#risk-job').hidden");
  await evaluate('(async()=>{await window.__riskHarness.open();})()');await choose('custom');await set('#risk-factor',factor);await set('#risk-bump','0.1');await click('#risk-queue');await wait('window.__riskRaceResponses.length===1');
  await evaluate("(async()=>{window.__riskRaceResponses.shift()(window.__riskRaceJob);await Promise.resolve();})()");await wait("window.__riskHarness.current()?.parts.every(p=>p.report)===true");
  await evaluate('window.__beforeLateRiskExport=window.__riskBlob;window.__lateRiskExport=window.__riskHarness.download().catch(()=>undefined);0');await wait('window.__riskRaceResponses.length===1');
  await evaluate("(async()=>{window.__riskHarness.reset();window.__riskRaceResponses.shift()(window.__riskRaceJob);await window.__lateRiskExport;})()");
  await check('late export after a financial-context reset creates no download',"window.__riskBlob===window.__beforeLateRiskExport&&window.__riskHarness.current()===undefined");
  await evaluate('window.__riskHarness.reset()');
  // After a reload the job history still lists the queued split run as one run, and the run bar shows its parts as one, made at the
  // context it was captured at.
  // The setup the races left edited is given up first, so that the reload asks nothing.
  await click('#nav-market');if(await evaluate("!document.querySelector('#side-panel-prompt').hidden"))await click('#side-panel-prompt button:not(.secondary)');await wait(idle);
  const replaced=await evaluate('performance.timeOrigin');await command('Page.reload');await wait(`performance.timeOrigin!==${replaced}&&document.querySelector('#profile-label')?.textContent==='Risk Analyst'&&${idle}`);
  await evaluate(hook);await setupTradingReplies(evaluate);
  await set('#snapshot',frame);await wait(idle);await click('#nav-futures');await wait("document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&!document.querySelector('#blotter-risk').disabled");
  await click('#blotter-risk');await wait(panelRisk+'&&'+idle);
  await check('after a reload the job history lists the split run as one run, not each of its parts',
    `(async()=>{const jobs=(await(await fetch('/api/portfolio/risk/jobs')).json()).filter(j=>j.run?.id===${JSON.stringify(splitRun)}).map(j=>j.id),values=[...document.querySelectorAll('#risk-job-history option')].map(o=>o.value);return jobs.length===${parts}&&values.filter(v=>v==='run:'+${JSON.stringify(splitRun)}).length===1&&!jobs.some(id=>values.includes(id));})()`,
    "[...document.querySelectorAll('#risk-job-history option')].map(o=>[o.value,o.textContent])");
  await set('#risk-job-history','run:'+splitRun);await wait("!document.querySelector('#desk-run').hidden&&"+idle);
  await check('and the run bar shows its parts as one run, made at the context it was captured at, which is not the one on screen',
    `document.querySelector('#desk-run-detail').textContent.includes(' · queued as '+${parts}+' jobs, completed ')&&document.querySelectorAll('#risk-job-parts li').length===${parts}&&document.querySelector('#desk-lens').hidden&&!document.querySelector('#risk-export').disabled`,
    "[document.querySelector('#desk-run').textContent,document.querySelectorAll('#risk-job-parts li').length]");
  await check('risk workflow raises no uncaught browser exceptions',JSON.stringify(errors.length===0));
}
