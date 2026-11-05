// Risk owns its setup; both entry points show the same exact report widget. All data is the owned synthetic preview's.
import {runJointChecks} from './joint-risk-workspace.mjs';
export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,fitsWidth,url}){
 await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1150,deviceScaleFactor:1,mobile:false});
 const hook=`(()=>{const send=window.fetch;window.__workspaceRiskReads=[];window.__workspaceRiskPosts=[];window.fetch=async(input,init)=>{
  const path=String(input),post=path==='/api/portfolio/risk/jobs'&&init?.method==='POST';
  if(post)window.__workspaceRiskPosts.push(JSON.parse(init.body));
  let response=await send(input,init);
  if(path==='/api/portfolio/risk/jobs'&&(init?.method??'GET')==='GET'&&response.ok&&(window.__hideWorkspaceHistory||window.__omitWorkspaceJob)){
   const jobs=await response.json();response=new Response(JSON.stringify(window.__hideWorkspaceHistory?[]:jobs.filter(j=>j.id!==window.__omitWorkspaceJob)),{status:200,headers:{'Content-Type':'application/json'}});
  }
  if(path.includes('/portfolio/risk')&&response.ok){let data=await response.clone().json();
   if(post&&data.run?.part===1&&window.__completeWorkspaceFirst){
    for(let n=0;n<100;n++){const r=await send('/api/portfolio/risk/jobs/'+data.id);const j=await r.json();if(j.state==='completed'){data=j;response=new Response(JSON.stringify(j),{status:200,headers:{'Content-Type':'application/json'}});window.__workspaceFirstCompleted=true;break;}await new Promise(r=>setTimeout(r,100));}
   }
   window.__workspaceRiskReads.push({path,data});if(post&&window.__holdWorkspaceReply&&(!window.__holdWorkspacePart||data.run?.part===window.__holdWorkspacePart)){window.__heldWorkspaceJob=data;await new Promise(()=>{});}}
  return response;
 };})();`;
 await command('Page.addScriptToEvaluateOnNewDocument',{source:hook});
 await command('Page.navigate',{url});await wait(idle);await persona('risk');
 const frame=await evaluate("(async()=>{const b=await(await fetch('/api/workspace')).json();return b.frames.find(f=>f.at.startsWith('2020-12-18')&&b.initialFrameIds.includes(f.id)).id;})()");
 await set('#snapshot',frame);await wait(idle);
 await evaluate('window.__hideWorkspaceHistory=true');
 await click('#nav-risk');await wait("!document.querySelector('#risk-page').hidden&&"+idle);
 await check('Risk opens on report availability and owns Run risk, with worker details collapsed',
  "document.querySelector('#risk-workspace-results').textContent.includes('No report selected')&&document.querySelector('#risk-workspace-note').textContent.includes('No completed recent report')&&!document.querySelector('#risk-jobs-new').disabled&&!document.querySelector('.risk-report-library').open&&!document.querySelector('#snapshot').closest('.date-control').hidden",
  "({results:document.querySelector('#risk-workspace-results').innerText,note:document.querySelector('#risk-workspace-note').innerText,disabled:document.querySelector('#risk-jobs-new').disabled,error:document.querySelector('#error').innerText,dateHidden:document.querySelector('#snapshot').closest('.date-control').hidden})");
 await click('#risk-jobs-new');await wait("!document.querySelector('#workspace-risk-fields').disabled&&"+idle);
 await check('the shared run editor opens inside Risk without loading or opening the Blotter',
  "document.querySelector('#risk-workspace-editor').open&&document.querySelector('#risk-workspace-editor-body').contains(document.querySelector('#workspace-risk-report'))&&document.querySelector('#futures-page').hidden&&new Set([...document.querySelectorAll('[id]')].map(n=>n.id)).size===document.querySelectorAll('[id]').length");
 const catalog="window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk/catalog').data";
 // A held contract's target, so the shock moves a held measure and the report draws its chart.
 const target=await evaluate(`${catalog}.heldContractTargets[0]`);
 await click('#workspace-risk-form input[value=custom]');await set('#workspace-risk-factor',target);await set('#workspace-risk-bump','0.05');
 await click('#risk-jobs-new');
 await check('Run risk on an open, edited setup brings it into view and keeps its choices without asking',
  `document.querySelector('#risk-workspace-editor').open&&document.querySelector('#workspace-risk-form input[value=custom]').checked&&document.querySelector('#workspace-risk-factor').value===${JSON.stringify(target)}&&document.querySelector('#workspace-risk-bump').value==='0.05'&&document.querySelector('#risk-page-prompt').hidden`);
 await click('#workspace-risk-run');
 await wait("document.querySelector('#risk-workspace-results canvas')&&"+idle,60000);
 const baseline=await evaluate("window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk').data");
 await check('Risk shows each moved total\'s delta at display precision with its exact amount beneath it, and its native unit',
  `(()=>{const f=${JSON.stringify(baseline.factors[0])},root=document.querySelector('#risk-workspace-results');const moved=f.totals.filter(t=>[t.values.delta,t.values.gamma,t.values.upPnl,t.values.downPnl].some(v=>!/^-?0+(?:\\.0+)?(?:\\/\\d+)?$/.test(v)));return moved.length>0&&moved.every(t=>[...root.querySelectorAll('td span[title]')].some(n=>n.title===t.values.delta)&&root.textContent.includes(t.values.deltaUnit))&&root.querySelector('[data-chart-kind=bar]')&&root.querySelector('.risk-coverage').dataset.state===${JSON.stringify(baseline.excludedPositions.length?'incomplete':'complete')};})()`,
  `({titles:[...document.querySelectorAll('#risk-workspace-results td span[title]')].map(n=>n.title).slice(0,6),deltas:${JSON.stringify(baseline.factors[0].totals.map(t=>[t.values.display.delta,t.values.delta,t.values.deltaUnit]))},coverage:document.querySelector('#risk-workspace-results .risk-coverage')?.dataset.state,bar:!!document.querySelector('#risk-workspace-results [data-chart-kind=bar]')})`);
 await click('#risk-workspace-results [data-risk-metric=gamma]');
 await check('gamma shows each moved total\'s exact backend gamma beneath its display text, with the squared factor unit',
  `(()=>{const root=document.querySelector('#risk-workspace-results');return ${JSON.stringify(baseline.factors[0].totals)}.filter(t=>[t.values.delta,t.values.gamma,t.values.upPnl,t.values.downPnl].some(v=>!/^-?0+(?:\\.0+)?(?:\\/\\d+)?$/.test(v))).every(t=>[...root.querySelectorAll('td span[title]')].some(n=>n.title===t.values.gamma)&&root.textContent.includes(t.values.gammaUnit));})()`);
 await set('#risk-workspace-results select[aria-label="Risk graphic"]','heatmap');
 await check('the same report has a graphical heatmap with an accessible table and factor drill-through',
  "!!document.querySelector('#risk-workspace-results [data-chart-kind=heatmap] canvas')&&!!document.querySelector('#risk-workspace-results .risk-result-table table')&&!!document.querySelector('#risk-workspace-results [data-risk-factor]')");
 await click('#risk-workspace-results [data-risk-factor]');
 await check('a factor exposes book totals and the positions its shock moves',"document.querySelector('#risk-workspace-results .risk-factor-detail').textContent.includes('Positions this shock moves')&&document.querySelector('#risk-workspace-results .risk-factor-detail table')");
 await shot('risk-workspace-heatmap');

 await click('#nav-futures');await wait("!document.querySelector('#blotter-risk').disabled&&"+idle,60000);await click('#blotter-risk');await wait("!document.querySelector('#risk-fields').disabled&&"+idle);
 await click('#risk-form input[value=custom]');await set('#risk-factor',target);await set('#risk-bump','0.05');await click('#risk-run');await wait("!document.querySelector('#desk-run').hidden&&"+idle,60000);
 await evaluate("document.querySelector('#desk-risk-full').open=true");await wait("document.querySelector('#desk-risk-full-results canvas')");
 await check('the Blotter renders the same exact financial values through the shared full-report widget',
  `(()=>{const report=window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk').data,base=${JSON.stringify(baseline)};return JSON.stringify(report.factors.map(f=>f.totals))===JSON.stringify(base.factors.map(f=>f.totals))&&base.factors[0].totals.filter(t=>[t.values.delta,t.values.gamma,t.values.upPnl,t.values.downPnl].some(v=>!/^-?0+(?:\\.0+)?(?:\\/\\d+)?$/.test(v))).every(t=>[...document.querySelectorAll('#desk-risk-full-results td span[title]')].some(n=>n.title===t.values.delta));})()`);

 await tradeSelection({command,evaluate,wait,click,set,check,shot,idle,url,frame});
 await runJointChecks({command,evaluate,wait,click,set,check,shot,idle,url,frame});
 await click('#nav-risk');await wait(idle);await click('#risk-jobs-new');await wait("!document.querySelector('#workspace-risk-fields').disabled&&"+idle);
 await click('#workspace-risk-form input[value=custom]');await set('#workspace-risk-factor',target);await set('#workspace-risk-bump','0.05');
 await evaluate('window.__hideWorkspaceHistory=false;window.__holdWorkspaceReply=true;document.querySelector("#workspace-risk-queue").click();true');
 await wait("!!window.__heldWorkspaceJob",60000);
 const original=await evaluate('window.__workspaceRiskPosts.at(-1).requestId');
 await check('an in-flight queued write is retained before its response, with the exact original request identity',
  `(()=>{const key=Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.risk.workspace.submission.'));if(!key)return false;const retained=JSON.parse(sessionStorage.getItem(key)),command=retained.parts[0].command;return command.operationId===${JSON.stringify(original)}&&command.path==='portfolio/risk/jobs'&&(()=>{const e=new Event('beforeunload',{cancelable:true});window.dispatchEvent(e);return e.defaultPrevented;})();})()`);
 const before=await evaluate('performance.timeOrigin');await evaluate('setTimeout(()=>location.reload(),0);true');
 for(let n=0;n<100;n++){if(await command('Page.handleJavaScriptDialog',{accept:true}).then(()=>true,()=>false))break;if(await evaluate(`performance.timeOrigin!==${before}`).catch(()=>false))break;await new Promise(r=>setTimeout(r,50));}
 await wait(`performance.timeOrigin!==${before}&&${idle}`,60000);await click('#nav-risk');
 await wait("document.querySelector('#risk-workspace-results canvas')&&"+idle,60000);
 await check('reload reconciles the committed request under its original identity without another queued write',
  `window.__workspaceRiskPosts.length===0&&window.__workspaceRiskReads.some(r=>r.data?.requestId===${JSON.stringify(original)})&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.risk.workspace.submission.'))`);
 const link=await evaluate("document.querySelector('#risk-workspace-results .risk-report-link').href");
 await command('Page.navigate',{url:link});await wait("document.querySelector('#risk-workspace-results canvas')&&"+idle,60000);
 await check('a retained report URL opens directly after reload with its captured context and completed values',
  "location.hash.startsWith('#risk/job/')&&document.querySelector('#risk-workspace-context-note').textContent.startsWith('Retained report:')&&!document.querySelector('#risk-workspace-export').disabled");
 await shot('risk-workspace-retained-report');
 await click('#profile-open');await set('#zone','America/Chicago');await evaluate("document.querySelector('#preferences-form').requestSubmit();true");await wait(idle);
 await check('retained report timestamps follow a changed display zone without repricing',"document.querySelector('#risk-workspace-results .risk-result-heading .field-help').textContent.includes('10:00 AM')");

 // Retained reads work without a header close. A new calculation still requires an explicit selection.
 await set('#snapshot',frame);await wait(idle);

 // Profile's modal must close before asking about Risk edits. Keeping them also restores a declined report address.
 await click('#risk-jobs-new');await wait("!document.querySelector('#workspace-risk-fields').disabled&&"+idle);
 await click('#workspace-risk-form input[value=custom]');await set('#workspace-risk-factor',target);
 await evaluate("const n=document.querySelector('#workspace-risk-bump');n.focus();n.select();");await command('Input.insertText',{text:'0.07'});
 await evaluate(`location.hash=${JSON.stringify(new URL(link).hash)}`);await wait("!document.querySelector('#risk-page-prompt').hidden");await click('#risk-page-prompt button.secondary');
 await check('keeping Risk edits restores the displayed address instead of retaining a declined report link',"!location.hash&&document.querySelector('#workspace-risk-bump').value==='0.07'");
 await click('#profile-open');await set('#profile','trader');await click('#open');await wait("!document.querySelector('#risk-page-prompt').hidden");
 await check('the Risk leave question is reachable outside the Profile modal',"!document.querySelector('#profile-dialog').open&&document.querySelector('#profile-label').textContent==='Risk Analyst'");
 await click('#risk-page-prompt button.secondary');
 await check('keeping Risk edits cancels the account change',"document.querySelector('#profile-label').textContent==='Risk Analyst'&&document.querySelector('#workspace-risk-bump').value==='0.07'");
 await click('#profile-open');await set('#profile','trader');await click('#open');await wait("!document.querySelector('#risk-page-prompt').hidden");await click('#risk-page-prompt button:not(.secondary)');await wait("document.querySelector('#profile-label').textContent==='Trader'&&"+idle);
 await check('discarding setup edits permits account change and clears previous report content',"document.querySelector('#risk-workspace-results').textContent===''&&document.querySelector('#nav-risk').hidden");
 await persona('risk');await set('#snapshot',frame);await wait(idle);await click('#nav-risk');await wait(idle);
 await check('Risk automatically opens the recent completed report matching the selected scope and exact close',"!!document.querySelector('#risk-workspace-results canvas')&&location.hash.startsWith('#risk/job/')&&document.querySelector('#risk-workspace-context-note').textContent.startsWith('Retained report:')");
 const book=await evaluate("document.querySelector('#risk-workspace-books option').value");await set('#risk-workspace-books',book);await wait(idle);
 await click('#workspace-risk-form input[value=custom]');await set('#workspace-risk-factor',await evaluate(`${catalog}.scenarioTargets.find(t=>t.kind==='contract').id`));await set('#workspace-risk-bump','0.05');await click('#workspace-risk-run');await wait(idle);
 await check('a Risk-owned book selection reaches the backend and shows only the selected holdings',`(()=>{const report=window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk').data;return report.factors.some(f=>f.positions.length>0)&&report.factors.every(f=>f.positions.every(p=>p.bookId===${JSON.stringify(book)}));})()`);
 await set('#risk-workspace-books','');await wait(idle);

 // Reload during ordinal 2, after ordinal 1 completed. Future unsent identities and completed metadata must both recover.
 await click('#risk-jobs-new');await wait("!document.querySelector('#workspace-risk-fields').disabled&&"+idle);await click('#workspace-risk-form input[value=held]');
 await evaluate("for(const input of document.querySelectorAll('#workspace-risk-sizes input')){input.value='0.05';input.dispatchEvent(new Event('input',{bubbles:true}));}");
 await evaluate("window.__completeWorkspaceFirst=true;window.__holdWorkspaceReply=true;window.__holdWorkspacePart=2;document.querySelector('#workspace-risk-queue').click();true");await wait("window.__heldWorkspaceJob?.run?.part===2",60000);
 const intended=await evaluate("(()=>{const key=Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.risk.workspace.submission.')),r=JSON.parse(sessionStorage.getItem(key));return {id:r.id,parts:r.parts.map((p,i)=>({part:i+1,requestId:p.command?.operationId??p.job?.requestId})),firstCompleted:window.__workspaceFirstCompleted,keptFirstResult:r.parts[0].job?.result!==undefined};})()");
 await check('every multipart identity is retained before dispatch, including completed and unsent ordinals',`(()=>{const r=${JSON.stringify(intended)};return r.parts.length>2&&r.parts.every(p=>!!p.requestId)&&r.firstCompleted&&!r.keptFirstResult;})()`);
 const prior=await evaluate('performance.timeOrigin');await evaluate('setTimeout(()=>location.reload(),0);true');
 for(let n=0;n<100;n++){if(await command('Page.handleJavaScriptDialog',{accept:true}).then(()=>true,()=>false))break;if(await evaluate(`performance.timeOrigin!==${prior}`).catch(()=>false))break;await new Promise(r=>setTimeout(r,50));}
 await wait(`performance.timeOrigin!==${prior}&&${idle}`,60000);await click('#nav-risk');await wait(idle);
 if(await evaluate("!document.querySelector('#workspace-risk-job-retry').hidden"))await click('#workspace-risk-job-retry');
 await wait("document.querySelector('#risk-workspace-results canvas')&&document.querySelector('#workspace-risk-job-parts').textContent.includes('Completed')&&"+idle,60000);
 const parts=await evaluate(`(async()=>{const jobs=await(await fetch('/api/portfolio/risk/jobs')).json();return jobs.filter(j=>j.run?.id===${JSON.stringify(intended.id)}).sort((a,b)=>a.run.part-b.run.part);})()`);
 await check('reload hydrates earlier completed reports and resumes each remaining original ordinal once',
  `(()=>{const intended=${JSON.stringify(intended)},jobs=${JSON.stringify(parts)};return jobs.length===intended.parts.length&&jobs.every(j=>j.state==='completed'&&j.requestId===intended.parts.find(p=>p.part===j.run.part).requestId)&&window.__workspaceRiskPosts.length===intended.parts.length-2&&window.__workspaceRiskPosts.every(p=>p.run.part>2&&p.requestId===intended.parts.find(i=>i.part===p.run.part).requestId)&&!document.querySelector('#risk-workspace-results').textContent.includes('not yet queued')&&!document.querySelector('#risk-workspace-export').disabled;})()`);
 const single=new URL(link).hash.split('/').at(-1);
 await set('#workspace-risk-job-history',single);await wait(idle);
 await check('the embedded retained history updates the report URL to the actual run shown',`location.hash===${JSON.stringify('#risk/job/'+single)}&&document.querySelector('#risk-workspace-results .risk-report-link').hash===location.hash`);
 await evaluate(`location.hash=${JSON.stringify('#risk/job/'+parts[0].id)}`);await wait(idle);
 await evaluate(`window.__omitWorkspaceJob=${JSON.stringify(parts[1].id)};document.querySelector('.risk-report-library').open=true`);
 await click('#risk-jobs-refresh');await wait(idle);
 await click(`[data-risk-job="${parts[0].id}"]`);
 await wait("document.querySelector('#risk-workspace-results .risk-coverage')?.dataset.state==='incomplete'&&"+idle,60000);
 await check('an unlisted real sibling is incomplete and prevents complete-report export',
  "document.querySelector('#risk-workspace-results').textContent.includes('missing from the job history')&&document.querySelector('#risk-workspace-export').disabled");
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await evaluate("document.querySelector('#risk-workspace-results').scrollIntoView({block:'start'});");
 await evaluate('new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
 await shot('risk-workspace-phone');
 await check('Risk controls and graphical widgets fit a phone viewport',fitsWidth,"({width:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,wide:[...document.querySelectorAll('#risk-page *')].filter(n=>n.getClientRects().length&&n.getBoundingClientRect().right>document.documentElement.clientWidth+1&&!n.closest('.table-wrap')).slice(0,12).map(n=>({id:n.id,class:n.className,right:n.getBoundingClientRect().right,width:n.getBoundingClientRect().width}))})");
}

async function tradeSelection({command,evaluate,wait,click,set,check,shot,idle,url,frame}){
 await click('#nav-risk');await wait(idle);
 const scopePage="window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk/scope').data";
 const book=await evaluate(`${scopePage}.trades.find(t=>t.available).bookId`);
 const portfolio=await evaluate(`${scopePage}.books.find(b=>b.id===${JSON.stringify(book)}).portfolio_id`);
 await set('#risk-workspace-portfolios',portfolio);await wait(idle);
 await set('#risk-workspace-books',book);await wait(idle);
 await check('selecting a portfolio and book lists that book’s trades',
  `(()=>{const page=${scopePage};return page.trades.length>0&&page.trades.every(t=>t.bookId===${JSON.stringify(book)})&&document.querySelector('#risk-workspace-trades').options.length>0&&[...document.querySelectorAll('#risk-workspace-books-picker .scope-option input')].every(o=>page.books.find(b=>b.id===o.value)?.portfolio_id===${JSON.stringify(portfolio)});})()`);
 await set('#risk-workspace-trade-mode','selected');await wait(idle);
 await check('an empty individual selection cannot run whole-book risk',"document.querySelector('#risk-jobs-new').disabled&&document.querySelector('#risk-workspace-trade-help').textContent.includes('Select at least one trade')");
 const trade=await evaluate("[...document.querySelector('#risk-workspace-trades').options].find(o=>!o.disabled).value");
 await set('#risk-workspace-trades',trade);await wait(idle);
 await click('#risk-jobs-new');await wait("!document.querySelector('#workspace-risk-fields').disabled&&"+idle);
 const target=await evaluate("window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk/catalog').data.scenarioTargets.find(t=>t.kind==='contract').id");
 await click('#workspace-risk-form input[value=custom]');await set('#workspace-risk-factor',target);await set('#workspace-risk-bump','0.05');await click('#workspace-risk-run');
 // A shock that moves none of the selected trade's measures draws no chart; the report's coverage line shows it is drawn.
 await wait("document.querySelector('#risk-workspace-results .risk-coverage')&&"+idle,60000);
 await check('an individual trade report includes only that trade and labels its captured subset',
  `(()=>{const r=window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk').data;return JSON.stringify(r.tradeIds)===${JSON.stringify(JSON.stringify([trade]))}&&r.inputs.positions.length>0&&r.inputs.positions.every(p=>p.tradeId===${JSON.stringify(trade)})&&document.querySelector('#risk-workspace-results').textContent.includes('1 selected trade');})()`);
 await shot('risk-workspace-selected-trade');
 await evaluate('window.__hideWorkspaceHistory=false;true');await click('#workspace-risk-queue');
 await wait("window.__workspaceRiskReads.some(r=>r.path==='/api/portfolio/risk/jobs'&&r.data?.id)&&"+idle,60000);
 const job=await evaluate("window.__workspaceRiskReads.findLast(r=>r.path==='/api/portfolio/risk/jobs'&&r.data?.id).data.id");
 await wait(`window.__workspaceRiskReads.some(r=>r.data?.id===${JSON.stringify(job)}&&r.data?.state==='completed'&&r.data?.result)&&${idle}`,60000);
 await evaluate(`location.hash=${JSON.stringify('#risk/job/'+job)};true`);
 await wait("document.querySelector('#risk-workspace-results .risk-coverage')&&document.querySelector('#risk-workspace-context-note').textContent.startsWith('Retained report:')&&"+idle,60000);
 const origin=await evaluate('performance.timeOrigin');await command('Page.reload');
 try{await wait(`performance.timeOrigin!==${origin}&&document.querySelector('#risk-workspace-results .risk-coverage')&&${idle}`,60000);}
 catch(error){console.error('TRADE_RELOAD_STATE',JSON.stringify(await evaluate(`({origin:performance.timeOrigin,before:${origin},hash:location.hash,busy:document.body.getAttribute('aria-busy'),error:document.querySelector('#error')?.textContent,note:document.querySelector('#risk-workspace-note')?.textContent,result:document.querySelector('#risk-workspace-results')?.textContent})`)));await shot('risk-joint-trade-reload-failure');throw error;}
 await check('a queued trade subset survives a retained link and reload without widening its captured selection',
  `(()=>{const j=window.__workspaceRiskReads.findLast(r=>r.data?.id===${JSON.stringify(job)}&&r.data?.result).data;return JSON.stringify(j.scope.tradeIds)===${JSON.stringify(JSON.stringify([trade]))}&&j.result.inputs.positions.every(p=>p.tradeId===${JSON.stringify(trade)})&&document.querySelector('#risk-workspace-results').textContent.includes('1 selected trade');})()`);
 await set('#snapshot',frame);await wait(idle);
 await set('#risk-workspace-portfolios',portfolio);await wait(idle);await set('#risk-workspace-books',book);await wait(idle);
 await click('#risk-workspace-refresh');await wait(idle);
 await check('a retained subset is never automatically opened as the whole-book report at the same close',
  "document.querySelector('#risk-workspace-trade-mode').value==='all'&&document.querySelector('#risk-workspace-note').textContent.includes('No completed recent report')&&document.querySelector('#risk-workspace-results').textContent.includes('No report selected')",
  "({mode:document.querySelector('#risk-workspace-trade-mode').value,note:document.querySelector('#risk-workspace-note').textContent,results:document.querySelector('#risk-workspace-results').textContent,hash:location.hash,scope:window.__workspaceRiskReads.filter(r=>r.path==='/api/portfolio/risk/scope').map(r=>({count:r.data.total,ctx:r.data.contextId}))})");
 await set('#risk-workspace-books','');await wait(idle);await set('#risk-workspace-portfolios','');await wait(idle);
}
