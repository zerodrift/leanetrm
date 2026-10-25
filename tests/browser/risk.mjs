import {setupTradingReplies} from './trading-responses.mjs';
// The risk report in the side panel's Risk report mode (blotter-desk.md, Side panel: Risk report): its presets, the custom scenario lab,
// runs made now and queued as jobs, split into parts of at most sixteen shocks, the retained job history and the results.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,errors}){
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1250,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  await setupTradingReplies(evaluate);
  await evaluate(`(()=>{const send=window.fetch;window.__riskReads=[];window.__riskRequests=[];window.__riskJobs=[];window.fetch=async(input,init)=>{const path=String(input);if(path==='/api/portfolio/risk'){window.__riskRequests.push(JSON.parse(init.body));if(window.__denyRisk){window.__denyRisk=false;return new Response(JSON.stringify({code:'access_denied'}),{status:403,headers:{'Content-Type':'application/json'}});}if(window.__failRiskPart!==undefined&&window.__failRiskPart--===0){window.__failRiskPart=undefined;return new Response(JSON.stringify({code:'missing_observation'}),{status:422,headers:{'Content-Type':'application/json'}});}}if(path==='/api/portfolio/risk/jobs'&&init?.method==='POST'){window.__riskJobs.push(JSON.parse(init.body));if(window.__rejectRiskSubmission){const code=window.__rejectRiskSubmission;window.__rejectRiskSubmission=false;return new Response(JSON.stringify({code}),{status:code==='access_denied'?403:400,headers:{'Content-Type':'application/json'}});}}const response=await send(input,init);try{window.__riskReads.push({path,data:await response.clone().json()});}catch{}if(path==='/api/portfolio/risk/jobs'&&init?.method==='POST'&&window.__loseRiskSubmission){const outcome=window.__loseRiskSubmission;window.__loseRiskSubmission=false;if(outcome==='server')return new Response(JSON.stringify({code:'risk_job_outcome_unknown'}),{status:503,headers:{'Content-Type':'application/json'}});throw new TypeError('Synthetic lost submission response');}return response;};})();`);
  // A person's own typing, which the setup holds as an edit, unlike a value a script sets.
  const typeInto=async(selector,text)=>{await evaluate(`(()=>{const n=document.querySelector(${JSON.stringify(selector)});n.focus();n.select();})()`);await command('Input.insertText',{text});};
  const catalog="window.__riskReads.findLast(r=>r.path==='/api/portfolio/risk/catalog').data";
  const panelRisk="(()=>{const panel=document.querySelector('#side-panel');return !panel.hidden&&panel.dataset.mode==='risk'&&document.querySelector('#side-panel-view').contains(document.querySelector('#risk-report'))&&!document.querySelector('#risk-report').hidden;})()";
  const tileSays=state=>`document.querySelector('#desk-tile-risk .desk-risk-state').textContent===${JSON.stringify(state)}`;
  const choose=async report=>{await click(`input[name=risk-choice][value=${report}]`);};
  const openReport=async()=>{await click('#blotter-risk');await wait(panelRisk+"&&!document.querySelector('#risk-fields').disabled");};
  await persona('risk');
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");
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
  await submit('#risk-form',60000);await wait("document.querySelector('#risk-results .risk-factor .tabulator-row')",60000);
  await check('the run shocks every held contract target once by its size, in parts of at most sixteen made one after another, all at the catalog\'s one retained context',
    `(()=>{const c=${catalog},targets=${heldTargets},parts=window.__riskRequests.slice(${requestsBefore});return parts.length===Math.ceil(targets.length/16)&&parts.every(r=>r.bumps.length>=1&&r.bumps.length<=16&&r.stress.length===0&&r.context.knowledgeMode==='retained'&&r.context.contextId===c.context.contextId&&r.convention===c.convention&&JSON.stringify(r.pricingPolicy)===JSON.stringify(c.pricingPolicy))&&JSON.stringify(parts.flatMap(r=>r.bumps.map(b=>b.factor)))===JSON.stringify(targets.map(t=>t.factor))&&parts.every(r=>r.bumps.every(b=>b.size==='0.05'&&b.convention===c.bumpConvention));})()`);
  await check('the run completes and its results show in the panel: a section for each held contract, the first one open, and nothing added across them',
    `(()=>{const sections=[...document.querySelectorAll('#risk-results .risk-factor')],parts=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').slice(-Math.ceil(${heldTargets}.length/16));return ${panelRisk}&&sections.length===${heldTargets}.length&&sections[0].open&&sections.slice(1).every(s=>!s.open)&&sections.map(s=>s.querySelector('summary strong').textContent).join('|')===parts.flatMap(r=>r.data.factors.map(f=>f.factor.label)).join('|')&&document.querySelector('#error').hidden&&!document.querySelector('#risk-export').hidden;})()`);
  await check('the panel lies over the desk at its wider width while it shows results, and the risk tile says where they are',`document.querySelector('#side-panel').dataset.wide!==undefined&&${tileSays('Results in the panel')}`);
  await shot('risk-held-results');
  // Results stay while a position is looked at, and the tile is the way back to them.
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("document.querySelector('#side-panel').dataset.mode==='position'&&"+idle);
  await check('opening a position takes the report out of the panel, which gives the panel its own width again, and keeps its results',`document.querySelector('#editor-shelf').contains(document.querySelector('#risk-report'))&&document.querySelector('#side-panel').dataset.wide===undefined&&${tileSays('Results in the panel')}&&document.querySelectorAll('#risk-results .risk-factor').length===${heldTargets}.length`);
  await click('#desk-risk-run');await wait(panelRisk+"&&document.querySelector('#risk-results .risk-factor .tabulator-row')");
  await check('the tile opens the report again with the same results and the sizes the run used',`document.querySelectorAll('#risk-results .risk-factor').length===${heldTargets}.length&&document.querySelector('input[name=risk-choice][value=held]').checked&&[...document.querySelectorAll('#risk-sizes input')].every(i=>i.value==='0.05')`);

  // A new read of the Blotter clears the report; the sizes last used are prefilled, visibly, the next time.
  await click('#futures-refresh');await wait("document.querySelector('#side-panel').hidden&&"+idle);
  await check('a new read of the Blotter clears the report and closes the panel, and the tile says it has not run',`document.querySelector('#risk-results').children.length===0&&${tileSays('Not run at this close')}`);
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
  await submit('#risk-form',60000);await wait("document.querySelector('#risk-results .risk-factor .tabulator-row')",60000);
  await check('it shifts each held curve as a whole, by its unit\'s size, in one part',
    `(()=>{const r=window.__riskRequests.at(-1);return JSON.stringify(r.bumps.map(b=>b.factor))===JSON.stringify(${curveTargets}.map(t=>t.factor))&&r.bumps.every(b=>b.size==='0.05')&&document.querySelectorAll('#risk-results .risk-factor').length===${curveTargets}.length;})()`);

  // Queued: one job for each part, shown as one run.
  await choose('held');
  const jobsBefore=await evaluate('window.__riskJobs.length');
  await click('#risk-queue');await wait("document.querySelectorAll('#risk-results .risk-factor').length>0",60000);
  await check('a queued run submits one job for each part, each with its own request identity and the catalog\'s one retained context',
    `(()=>{const c=${catalog},jobs=window.__riskJobs.slice(${jobsBefore});return jobs.length===Math.ceil(${heldTargets}.length/16)&&new Set(jobs.map(j=>j.requestId)).size===jobs.length&&jobs.every(j=>j.request.context.contextId===c.context.contextId&&j.request.bumps.length<=16)&&JSON.stringify(jobs.flatMap(j=>j.request.bumps.map(b=>b.factor)))===JSON.stringify(${heldTargets}.map(t=>t.factor));})()`);
  await check('the queued parts complete as one run whose results show in the panel',
    `(()=>{const parts=${heldTargets}.length>16;return document.querySelector('#risk-job-status').textContent.includes(parts?'parts, each queued as its own job':'Risk job: Completed')&&(!parts||[...document.querySelectorAll('#risk-job-parts li')].every(li=>li.textContent.endsWith('Completed')))&&document.querySelectorAll('#risk-results .risk-factor').length===${heldTargets}.length&&${tileSays('Results in the panel')};})()`);

  // A part that fails is said to have failed, and nothing stands in for it: the other parts still run and show their own results.
  const parts=`Math.ceil(${heldTargets}.length/16)`;
  const requestsBeforeFailure=await evaluate('window.__riskRequests.length');
  await evaluate('window.__failRiskPart=1');await submit('#risk-form',60000);
  await check('a part of a run made now that fails is said to have failed, and the parts after it still run and show their own results',
    `(()=>{const failed=[...document.querySelectorAll('#risk-results .risk-part-failed')];return ${heldTargets}.length>16&&window.__riskRequests.length-${requestsBeforeFailure}===${parts}&&failed.length===1&&failed[0].getAttribute('role')==='alert'&&failed[0].textContent.startsWith('Part 2 of '+${parts}+' failed: ')&&document.querySelectorAll('#risk-results .risk-factor').length===${heldTargets}.length-Math.min(16,${heldTargets}.length-16)&&!document.querySelector('#error').hidden;})()`,
    "[[...document.querySelectorAll('#risk-results .risk-part-failed')].map(p=>p.textContent),document.querySelectorAll('#risk-results .risk-factor').length,document.querySelector('#error').textContent]");
  await check('the download of a run that lacks a part is refused, and says why',"document.querySelector('#risk-export').disabled&&!document.querySelector('#risk-export-note').hidden&&document.querySelector('#risk-export-note').textContent==='The download needs the results of every part of the run.'");
  // A queued part whose submission is refused is not queued, and says why; the other parts are queued and complete as the run.
  const jobsBeforeRefusal=await evaluate('window.__riskJobs.length');
  await evaluate("window.__rejectRiskSubmission='risk_convention'");await click('#risk-queue');
  await wait("document.querySelectorAll('#risk-results .risk-factor').length>0",60000);
  await check('a queued part that is refused says it was not queued and why, and the other parts complete and show their results',
    `(()=>{const items=[...document.querySelectorAll('#risk-job-parts li')],failed=[...document.querySelectorAll('#risk-results .risk-part-failed')];return window.__riskJobs.length-${jobsBeforeRefusal}===${parts}&&items.length===${parts}&&items[0].textContent.startsWith('Part 1 of '+${parts}+' · Not queued · ')&&items.slice(1).every(li=>li.textContent.endsWith('Completed'))&&failed.length===1&&failed[0].textContent.startsWith('Part 1 of '+${parts}+' failed: ')&&document.querySelectorAll('#risk-results .risk-factor').length===${heldTargets}.length-16;})()`,
    "[[...document.querySelectorAll('#risk-job-parts li')].map(li=>li.textContent),document.querySelectorAll('#risk-results .risk-factor').length]");
  // A queued part whose submission has no known outcome keeps the run retained: the parts that were queued complete and show their
  // results, the part says it has none, and Reconcile sends its same request again, which returns its job with its result.
  const jobsBeforeLoss=await evaluate('window.__riskJobs.length');
  await evaluate("window.__loseRiskSubmission='network'");await click('#risk-queue');
  await wait("document.querySelectorAll('#risk-results .risk-factor').length>0",60000);
  await check('a queued part whose submission has no known outcome keeps the run to reconcile, and the parts that were queued show their results',
    `(()=>{const items=[...document.querySelectorAll('#risk-job-parts li')],missing=[...document.querySelectorAll('#risk-results .risk-part-missing')];return window.__riskJobs.length-${jobsBeforeLoss}===${parts}&&items[0].textContent==='Part 1 of '+${parts}+' · Submission outcome unknown'&&items.slice(1).every(li=>li.textContent.endsWith('Completed'))&&!document.querySelector('#risk-job-retry').hidden&&document.querySelector('#risk-form').hidden&&missing.length===1&&missing[0].textContent==='Part 1 of '+${parts}+' has no results: its submission has no known outcome.'&&document.querySelectorAll('#risk-results .risk-factor').length===${heldTargets}.length-16&&document.querySelector('#risk-export').disabled;})()`,
    "[[...document.querySelectorAll('#risk-job-parts li')].map(li=>li.textContent),[...document.querySelectorAll('#risk-results>.field-help')].map(p=>p.textContent).slice(0,3)]");
  // The lost reply's job was made all the same. Once it has completed, the same request returns it, result and all.
  const lost=await evaluate(`window.__riskJobs[${jobsBeforeLoss}].requestId`);
  await wait(`(async()=>(await(await fetch('/api/portfolio/risk/jobs')).json()).some(j=>j.requestId===${JSON.stringify(lost)}&&j.state==='completed'))()`,60000);
  await click('#risk-job-retry',60000);await wait("!document.querySelector('#risk-form').hidden&&"+idle,60000);
  await check('Reconcile sends the same request again, which returns its completed job, and the run shows every part',
    `(()=>{const jobs=window.__riskJobs.slice(${jobsBeforeLoss});return jobs.length===${parts}+1&&jobs.at(-1).requestId===${JSON.stringify(lost)}&&JSON.stringify(jobs.at(-1).request)===JSON.stringify(jobs[0].request)&&[...document.querySelectorAll('#risk-job-parts li')].every(li=>li.textContent.endsWith('Completed'))&&document.querySelectorAll('#risk-results .risk-factor').length===${heldTargets}.length&&!document.querySelector('#risk-results .risk-part-missing')&&!document.querySelector('#risk-export').disabled;})()`,
    "[document.querySelectorAll('#risk-results .risk-factor').length,[...document.querySelectorAll('#risk-job-parts li')].map(li=>li.textContent),document.querySelector('#risk-job-status').textContent]");

  // The custom scenario lab: an explicit target and bump, nested stresses and the targets the catalog offers.
  await choose('custom');
  await check('shock entry requires explicit factor and bump choices',"!document.querySelector('#risk-custom').hidden&&document.querySelector('#risk-preset').hidden&&document.querySelector('#risk-factor').value===''&&document.querySelector('#risk-bump').value===''&&!document.querySelector('#risk-form').checkValidity()");
  await check('risk opens with an explicit unchanged market and an empty nested stress stack',"document.querySelector('#risk-stress-stack')?.children.length===0&&document.querySelector('#risk-stress-parent')?.disabled&&document.querySelector('#risk-stress-clear')?.disabled&&document.querySelector('#risk-stress-factor')?.value===''&&document.querySelector('#risk-stress-size')?.value===''");
  const factor=await evaluate(`(()=>{const c=${catalog},source=c.factors.find(f=>f.curves.includes('future')&&f.deliveryFrom==='2021-01-01');return c.scenarioTargets.find(t=>t.id===source.id&&JSON.stringify(t.factor)===JSON.stringify(source.factor)).id;})()`);
  await set('#risk-factor',factor);await set('#risk-bump','0.1');await submit('#risk-form');await wait("document.querySelector('#risk-results .tabulator-row')");
  await check('risk screen displays exact shared-factor results with explicit derivative units',"document.querySelector('#error').hidden&&document.querySelector('#risk-results').textContent.includes('Delta')&&document.querySelector('#risk-results').textContent.includes('Gamma')&&document.querySelector('#risk-results').textContent.includes('USD / (1 USD / MMBtu (IT))')&&document.querySelectorAll('#risk-results .risk-factor').length===1");
  await check('risk uses the blotter context and every selected futures and swap holding',"(()=>{const r=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').at(-1).data;const h=window.__tradingReports(window.__riskReads).at(-1).data;return r.context.contextId===h.context.contextId&&r.heldInstruments>1&&r.work.positionCount===h.holdings.length&&r.inputs.positions.length===h.holdings.length&&h.holdings.every(row=>r.inputs.positions.some(p=>row.tradeId===p.tradeId&&row.bindingId===p.bindingId))&&new Set(r.factors[0].totals.map(t=>t.measure)).size===2;})()");
  await check('risk submits the complete pricing policy offered by its catalog without inventing inputs',`(()=>{const c=${catalog},r=window.__riskRequests.at(-1);const source=c.factors.find(f=>f.curves.includes('future')&&f.deliveryFrom==='2021-01-01');return r.context.convention==='trading_workspace_v1'&&r.convention==='portfolio_instrument_repricing_v1'&&r.numericalPolicy==='exact_rational_portfolio_repricing_v1'&&JSON.stringify(r.pricingPolicy)===JSON.stringify(c.pricingPolicy)&&JSON.stringify(r.bumps[0].factor)===JSON.stringify(source.factor);})()`);
  await check('different valuation measures remain separately labeled in same-currency results',"document.querySelector('#risk-results').textContent.includes('Futures execution P&L')&&document.querySelector('#risk-results').textContent.includes('Present value')&&document.querySelectorAll('#risk-results .risk-factor .chart').length>=2");
  await check('shared bar chart shows both signed shock directions by book',"document.querySelector('#risk-results canvas')&&document.querySelector('#risk-results .risk-factor .chart').dataset.chartAxis==='category'&&document.querySelector('#risk-work').textContent.includes('held instruments')");
  await check('work evidence distinguishes materialization from each backend connected graph receipt',"(()=>{const r=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').at(-1).data,rows=[...document.querySelectorAll('#risk-work li')];return rows.length===r.connectedScenarios.length+1&&rows[0].textContent.includes('Materialization')&&r.connectedScenarios.every((m,i)=>rows[i+1].textContent.includes(m.definitions.revisionId)&&rows[i+1].textContent.includes(m.work.evaluatedNodes+' graph nodes evaluated')&&rows[i+1].textContent.includes(m.preparation.sourceCompilations+' source compilations'));})()");
  await click('#risk-results .risk-factor details>summary');await wait("document.querySelector('#risk-results .risk-factor details .tabulator-row')");
  await check('individual results remain available beneath book totals',"document.querySelector('#risk-results .risk-factor details').textContent.includes('Individual holding results')&&document.querySelector('#risk-results .risk-factor details .tabulator-row')");
  // Closing the panel is not clearing the report: the tile is the way back to its results.
  await click('#side-panel-close');
  await check('closing the panel keeps the results, and the risk tile says where they are',`document.querySelector('#side-panel').hidden&&${tileSays('Results in the panel')}&&document.querySelector('#desk-risk-run').textContent==='Show the results'`);
  await click('#desk-risk-run');await wait(panelRisk+"&&document.querySelector('#risk-results .tabulator-row')");
  await click('#risk-stress>summary');await set('#risk-stress-factor',factor);await set('#risk-stress-size','0.3');await click('#risk-stress-add');
  await set('#risk-stress-size','-0.1');await click('#risk-stress-add');await submit('#risk-form');
  await check('nested stresses retain ordered signed child shifts beneath the same financial context',`(()=>{const r=window.__riskRequests.at(-1),result=window.__riskReads.filter(r=>r.path==='/api/portfolio/risk').at(-1).data;return r.stress.length===2&&r.stress[0].size==='0.3'&&r.stress[1].size==='-0.1'&&r.stress.every(s=>s.convention===${catalog}.stressConvention)&&result.scenario.depth===2&&result.context.contextId===r.context.contextId&&document.querySelectorAll('#risk-stress-stack li').length===2&&document.querySelector('#risk-scenario').textContent.includes('2 nested stresses');})()`);
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
  await shot('risk-desktop');
  await set('#risk-bump','0');
  await check('editing a shock clears its prior result and export immediately',"document.querySelector('#risk-results').children.length===0&&document.querySelector('#risk-export').hidden&&document.querySelector('#side-panel').dataset.wide===undefined");
  await submit('#risk-form');
  await check('zero bump produces a visible structured failure without cached results',"!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('positive absolute bump')&&document.querySelector('#risk-results').children.length===0");
  await set('#risk-bump','0.1');await submit('#risk-form');
  await evaluate(`(()=>{window.__riskBlob=undefined;const create=URL.createObjectURL;URL.createObjectURL=b=>{window.__riskBlob=b;return create(b);};const click=HTMLAnchorElement.prototype.click;HTMLAnchorElement.prototype.click=function(){if(this.download==='leanetrm-risk.json')return;return click.call(this);};})();`);
  await click('#risk-export');
  await check('risk download reauthorizes the exact pinned request and includes its inputs',"(async()=>{const {parts}=JSON.parse(await window.__riskBlob.text()),r=parts[0];const requests=window.__riskRequests;return parts.length===1&&r.inputs.positions.length===r.work.positionCount&&r.inputs.markets.length>1&&r.inputs.markets.every(m=>m.observations.length>0)&&r.factors[0].factor.coordinate==='market_quote'&&!!r.factors[0].factor.unit&&r.displayPolicy&&r.factors[0].totals.every(t=>t.values.display&&t.measure&&t.measureLabel)&&JSON.stringify(requests.at(-1))===JSON.stringify(requests.at(-2));})()");
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await wait("document.querySelector('#side-panel').dataset.placement==='overlay'");
  await check('the report fills the phone sheet without page-wide overflow',fitsWidth+"&&document.querySelector('#side-panel').dataset.wide===undefined");await shot('risk-mobile');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1250,deviceScaleFactor:1,mobile:false});
  await evaluate('window.__previousRiskBlob=window.__riskBlob;window.__denyRisk=true');await click('#risk-export');
  await check('denied risk export creates no download and clears risk and holdings caches',"window.__riskBlob===window.__previousRiskBlob&&document.querySelector('#side-panel').hidden&&document.querySelector('#risk-results').children.length===0&&document.querySelector('#desk-positions').children.length===0");
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
    // The job finishes on the report's own timer, outside the page's actions: the tile still follows it.
    await check(`${outcome} queued risk that completes by polling moves the risk tile to its results`,tileSays('Results in the panel'));
    await check(`${outcome} queued risk retries use one identity and publish the captured context`,"(()=>{const jobs=window.__riskJobs,reads=window.__riskReads.filter(r=>r.path.startsWith('/api/portfolio/risk/jobs'));const done=reads.findLast(r=>r.data.state==='completed').data;return JSON.stringify(jobs.at(-1))===JSON.stringify(jobs.at(-2))&&done.result.context.contextId===jobs.at(-1).request.context.contextId&&done.result.work.positionCount===window.__tradingReports(window.__riskReads).at(-1).data.holdings.length&&!document.querySelector('#risk-export').hidden;})()");
  }
  await click('#risk-export');
  await check('completed job download reads its retained result with current authorization',"(async()=>{const {parts}=JSON.parse(await window.__riskBlob.text()),last=window.__riskReads.at(-1);return parts.length===1&&last.path.startsWith('/api/portfolio/risk/jobs/')&&last.data.state==='completed'&&parts[0].context.contextId===window.__riskJobs.at(-1).request.context.contextId;})()");
  const retainedJob=await evaluate("window.__riskReads.findLast(r=>r.path.startsWith('/api/portfolio/risk/jobs')&&r.data.state==='completed').data.id");
  const earlier=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-17')).id;})()");
  await set('#snapshot',earlier);await wait(idle);
  await check('changing the financial context discards risk results and factor choices',"document.querySelector('#side-panel').hidden&&document.querySelector('#risk-factor').value===''&&document.querySelector('#risk-results').children.length===0&&[...document.querySelectorAll('input[name=risk-choice]')].every(c=>!c.checked)");
  await click('#blotter-risk');await wait(panelRisk);await set('#risk-job-history',retainedJob);await wait(idle);await wait("document.querySelector('#risk-results .tabulator-row')");
  await check('recent jobs remain discoverable after context changes and identify their original dates',"document.querySelector('#risk-job-context').textContent.includes('2020-12-18')&&document.querySelector('#risk-scope').textContent.includes('captured financial context')&&document.querySelector('#risk-form').hidden&&document.querySelector('#risk-job-scenario').textContent.includes('original market')&&document.querySelector('#risk-fields').disabled&&!document.querySelector('#risk-job-return').hidden&&document.querySelector('#risk-results .tabulator-row')");
  await click('#risk-job-return');
  await check('returning from a retained job clears results before enabling the selected context',"!document.querySelector('#risk-form').hidden&&!document.querySelector('#risk-fields').disabled&&document.querySelector('#risk-results').children.length===0&&document.querySelector('#risk-job').hidden");
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
  await check('late completed status after a financial-context reset cannot repopulate the old results',"document.querySelector('#risk-results').children.length===0&&document.querySelector('#risk-export').hidden&&document.querySelector('#risk-job').hidden");
  await evaluate('(async()=>{await window.__riskHarness.open();})()');await choose('custom');await set('#risk-factor',factor);await set('#risk-bump','0.1');await click('#risk-queue');await wait('window.__riskRaceResponses.length===1');
  await evaluate("(async()=>{window.__riskRaceResponses.shift()(window.__riskRaceJob);await Promise.resolve();})()");await wait("!document.querySelector('#risk-export').hidden");
  await evaluate('window.__beforeLateRiskExport=window.__riskBlob');await click('#risk-export');await wait('window.__riskRaceResponses.length===1');
  await evaluate("(async()=>{window.__riskHarness.reset();window.__riskRaceResponses.shift()(window.__riskRaceJob);await Promise.resolve();})()");
  await check('late export after a financial-context reset creates no download',"window.__riskBlob===window.__beforeLateRiskExport&&document.querySelector('#risk-results').children.length===0");
  // This response exercises generic presentation only. Numerical nonlinear
  // repricing is qualified separately by the managed engine checks.
  await evaluate(`(async()=>{
    const receipt=structuredClone(window.__riskRaceJob),report=receipt.result,first=report.factors[0];
    const values={...first.totals[0].values,base:'1/3',up:'103/300',down:'103/300',upPnl:'1/100',downPnl:'1/100',delta:'0',gamma:'2',display:{base:'0.333333',up:'0.343333',down:'0.343333',upPnl:'0.010000',downPnl:'0.010000',delta:'0.000000',gamma:'2.000000'}};
    first.totals=[{...first.totals[0],measure:'synthetic_nonlinear_pv',measureLabel:'Synthetic nonlinear PV',values}];
    first.positions=[{...first.positions[0],measure:'synthetic_nonlinear_pv',measureLabel:'Synthetic nonlinear PV',instrumentType:'Synthetic nonlinear instrument',values}];
    report.factors=[first];window.__riskRaceJob=receipt;
    await window.__riskHarness.open();
  })()`);
  await choose('custom');await set('#risk-factor',factor);await set('#risk-bump','0.1');await click('#risk-queue');await wait('window.__riskRaceResponses.length===1');
  await evaluate("(async()=>{window.__riskRaceResponses.shift()(window.__riskRaceJob);await Promise.resolve();})()");await wait("document.querySelector('#risk-results .tabulator-row')");
  await check('generic nonlinear responses retain zero delta, nonzero gamma and exact rational hover values',"document.querySelector('#risk-results h4').textContent.includes('Synthetic nonlinear PV')&&[...document.querySelectorAll('#risk-results .tabulator-cell span')].some(c=>c.title==='1/3'&&c.textContent==='0.333333')&&[...document.querySelectorAll('#risk-results .tabulator-cell span')].some(c=>c.title==='0'&&c.textContent==='0.000000')&&[...document.querySelectorAll('#risk-results .tabulator-cell span')].some(c=>c.title==='2'&&c.textContent==='2.000000')&&document.querySelector('#risk-results .risk-factor .chart').textContent.includes('Synthetic nonlinear PV change by book')");
  await click('#risk-results .risk-factor details>summary');await wait("document.querySelector('#risk-results .risk-factor details .tabulator-row')");
  await check('generic holding details use returned instrument and measure labels without assuming a futures curve',"document.querySelector('#risk-results .risk-factor details').textContent.includes('Synthetic nonlinear instrument')&&document.querySelector('#risk-results .risk-factor details').textContent.includes('Synthetic nonlinear PV')");
  await evaluate('window.__riskHarness.reset()');
  await check('risk workflow raises no uncaught browser exceptions',JSON.stringify(errors.length===0));
}
