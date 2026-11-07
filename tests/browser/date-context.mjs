export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelector('#market-date-open')&&!document.querySelector('#date-context').hidden&&"+idle);
 await check('the header groups session and close date separately from knowledge',"(()=>{const bar=document.querySelector('header #context-toolbar'),close=bar.querySelector('#market-close-controls'),knowledge=document.querySelector('#knowledge-context');return close?.contains(document.querySelector('#close-session'))&&close.contains(document.querySelector('#market-date-open'))&&bar.contains(knowledge)&&!knowledge.open&&getComputedStyle(bar).position==='static'&&document.querySelector('#close-session').labels[0].checkVisibility()&&!document.querySelector('#date-context-view').checkVisibility();})()");
 await click('#context-help>summary');
 await check('context help explains the session, close and knowledge without changing them',"document.querySelector('#context-help').open&&document.querySelector('#close-session-help').checkVisibility()&&document.querySelector('#knowledge-context-help').textContent.includes('cutoff')&&document.querySelector('#context-help').textContent.includes('recorded close')&&document.querySelector('#error').hidden");
 await click('#context-help>summary');
 await check('market pages show only one date selector',"document.querySelector('#date-context-on').type==='hidden'&&document.querySelector('#market-date-open').getClientRects().length>0");
 const chooseDay=async day=>{
  await click('#market-date-open');
  if(await evaluate("document.querySelector('#market-date-month').value")!==day.slice(0,7))await set('#market-date-month',day.slice(0,7));
  await click('#market-date-grid button[data-date="'+day+'"]');
  if(await evaluate("document.querySelector('#market-date-dialog').open&&document.querySelectorAll('.market-close-choice').length>0"))await click('.market-close-choice');
 };
 await click('#nav-schedules');
 await check('date-library pages replace the market selector with their business date',"document.querySelector('#date-context-on').type==='date'&&document.querySelector('#date-context-on').getClientRects().length>0&&document.querySelector('#market-date-calendar').closest('.date-control').hidden");
 await set('#date-expression','2021-01-01');await submit('#date-expression-form');
 await check('a date lookup has a visible result before changing the shared date',"!document.querySelector('#date-expression-result').hidden");
 await click('#nav-market');await chooseDay('2020-12-17');
 await check('the one market-date selector also sets the business date used by knowledge selection',"document.querySelector('#date-context-on').value==='2020-12-17'&&document.querySelector('#workspace-mode-label').textContent.includes('current knowledge')");
 await check('changing the shared date clears results resolved in the previous context',"document.querySelector('#date-expression-result').hidden&&document.querySelector('#curve-dates-result').hidden");
 await evaluate(`window.contextCaptureOriginal=window.fetch;window.refuseContextCapture=true;window.fetch=(input,init)=>{
  if(String(input).endsWith('/date-schedules/context')&&init?.body&&JSON.parse(init.body).mode==='current'&&window.refuseContextCapture){window.refuseContextCapture=false;return Promise.resolve(new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}}));}
  return window.contextCaptureOriginal(input,init);
 };`);
 await chooseDay('2020-12-18');
 await check('a failed replacement capture cannot retain a usable context for the old day',"!document.querySelector('#error').hidden&&document.querySelector('#date-context-on').value==='2020-12-18'&&document.querySelector('#date-context-view').value===''");
 await evaluate("window.fetch=window.contextCaptureOriginal");
 await chooseDay('2020-12-18');
 await check('the same selected date can retry its refused capture explicitly',"document.querySelector('#error').hidden&&document.querySelector('#date-context-on').value==='2020-12-18'&&document.querySelector('#date-context-view').value!==''");
 await set('#snapshot','');await wait(idle);
 await check('clearing the date also clears its business date and cutoff',"document.querySelector('#date-context-on').value===''&&document.querySelector('#date-context-view').value===''");
 await chooseDay('2020-12-17');
 await shot('date-context-header');
 await evaluate('window.scrollTo(0,document.body.scrollHeight)');
 await check('the date controls scroll with the header instead of covering the page',"document.querySelector('#context-toolbar').getBoundingClientRect().bottom<0");
 await evaluate('window.scrollTo(0,0)');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await check('the header controls fit a narrow screen',fitsWidth);
 await check('knowledge stays collapsed and reachable on a narrow screen',"!document.querySelector('#knowledge-context').open&&document.querySelector('#knowledge-context>summary').getClientRects().length>0");
 await check('the grouped context stays compact on a narrow screen',"document.querySelector('#context-toolbar').getBoundingClientRect().height<=180","document.querySelector('#context-toolbar').getBoundingClientRect().toJSON()");
 await shot('date-context-mobile');
 await click('#knowledge-context>summary');
 await check('expanded knowledge fits a narrow screen without overlay positioning',fitsWidth+"&&getComputedStyle(document.querySelector('#knowledge-context .knowledge-panel')).position==='static'&&document.querySelector('#date-context-view').checkVisibility()");
 await shot('date-context-mobile-expanded');await click('#knowledge-context>summary');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 await click('#knowledge-context>summary');await click('#date-context-latest');await click('#apply-view-context');
 await check('the selector identifies the retained business date before an exact historical close is chosen',"document.querySelector('#market-date-open').textContent.includes('Dec 17, 2020')&&document.querySelector('#workspace-mode-label').textContent.includes('Historical view · read only')");
 await click('#current-view-context');
 await check('returning to working data restores the selected close and current knowledge',"(async()=>{const frames=(await(await fetch('/api/workspace')).json()).frames,selected=frames.find(frame=>frame.id===document.querySelector('#snapshot').value);return !!selected&&document.querySelector('#date-context-on').value===selected.at.slice(0,10)&&document.querySelector('#date-context-view').value!==''&&document.querySelector('#workspace-mode-label').textContent.includes('current knowledge');})()");
 await click('#nav-reference');await set('#date-context-on','2021-01-15');await wait(idle);
 await check('native date edits visibly retain their unapplied status',"document.querySelector('#workspace-mode-label').textContent.includes('Unapplied')&&document.querySelector('#date-context-on').value==='2021-01-15'&&document.querySelector('#market-date-calendar').closest('.date-control').hidden");
 await click('#nav-jobs');
 await check('current Jobs navigation leaves financial date edits unapplied',"!document.querySelector('#jobs-page').hidden&&document.querySelector('#workspace-mode-label').textContent.includes('Unapplied')&&document.querySelector('#error').hidden");
 await click('#current-view-context');await click('#nav-market');
 // Supply read-only report-action metadata; no synthetic valuation result is returned.
 await evaluate(`window.jobsContextReads=[];window.jobsReportReads=0;window.jobsContextFetch=window.fetch;window.fetch=async(input,init)=>{
  const url=new URL(typeof input==='string'?input:input.url,location.href);
  if(url.pathname==='/api/jobs/pilot-close/results'){window.jobsReportReads++;return new Response(JSON.stringify({code:'request_failed'}),{status:503,headers:{'Content-Type':'application/json'}});}
  const response=await window.jobsContextFetch(input,init);
  if(url.pathname==='/api/jobs'&&response.ok){
   const data=await response.json();window.jobsContextReads.push({url:url.href,data:structuredClone(data)});
   data.jobs.push({id:'00000000-0000-4000-8000-000000000071',name:'Synthetic report context check',owner:'Synthetic reader',operation:'pilot_close',operationName:'Synthetic close',state:'completed',version:1,dueAt:'2020-12-18T00:00:00Z',expiresAt:'2020-12-19T00:00:00Z',canEdit:false,editReason:null,mine:true,actions:[],runs:[],pilotReport:{runId:'00000000-0000-4000-8000-000000000072',state:'report_published'},intervalSeconds:null,timeZone:'UTC',misfire:'run_once',overlap:'forbid',timeoutSeconds:30,maximumAttempts:1,profileId:null,profileVersion:null,profileName:null});data.total++;
   return new Response(JSON.stringify(data),{status:response.status,headers:response.headers});
  }return response;
 };`);
 await click('#nav-jobs');
 await check('Jobs and Infrastructure exposes its report context without another market-date selector',"!document.querySelector('#context-toolbar').hidden&&!document.querySelector('#date-context').hidden&&document.querySelector('#date-context-on').type==='date'&&document.querySelector('#date-context-on').getClientRects().length>0&&document.querySelector('#market-date-calendar').closest('.date-control').hidden&&!document.querySelector('#knowledge-context').open");
 await check('Jobs explains which dates affect reports and which status stays current',"document.querySelector('#jobs-scope').textContent.includes('current')&&document.querySelector('#jobs-scope').textContent.includes('saved valuation reports')");
 await set('#date-context-on','2020-12-17');await wait(idle);
 await evaluate("[...document.querySelectorAll('#jobs-table button')].find(button=>button.textContent==='View close report').click()");await wait(idle);
 await check('saved reports require Apply before reading under a changed header context',"window.jobsReportReads===0&&document.querySelector('#pilot-report').hidden&&document.querySelector('#error').textContent.includes('Apply the changed date context')&&document.querySelector('#workspace-mode-label').textContent.includes('Unapplied')");
 await click('#knowledge-context>summary');
 await check('the expanded knowledge control explains which corrections are included',"document.querySelector('#context-date-help').checkVisibility()&&document.querySelector('#context-date-help').textContent.includes('an earlier cutoff excludes later corrections')");
 await click('#date-context-latest');await click('#apply-view-context');
 await evaluate("document.querySelector('button[aria-label=\"View close report for Synthetic report context check\"]').click()");await wait(idle);
 await check('applying the selected context enables the saved report read',"window.jobsReportReads===1&&document.querySelector('#pilot-report').hidden");
 await click('#jobs-refresh');
 await check('report date selection preserves the current operational directory contract',"(()=>{const reads=window.jobsContextReads,last=reads.at(-1);return reads.length>=2&&new URL(last.url).searchParams.get('knownAt')===null&&!Object.hasOwn(last.data,'context')&&JSON.stringify(last.data.jobs.map(j=>[j.id,j.state,j.version]))===JSON.stringify(reads[0].data.jobs.map(j=>[j.id,j.state,j.version]))&&document.querySelector('#workspace-mode-label').textContent.includes('Historical view')&&document.querySelector('#error').hidden;})()");
 await click('#current-view-context');await click('#nav-market');await evaluate("window.fetch=window.jobsContextFetch");
 await command('Page.reload');await wait("document.querySelector('#knowledge-context')&&!document.querySelector('#date-context').hidden&&document.querySelectorAll('.curve-button').length>0&&"+idle);
 await check('ordinary reload keeps the matching script and stylesheet usable',"document.querySelector('script[src^=\"/app.js?v=\"]')&&document.querySelector('link[href^=\"/app.css?v=\"]')&&document.querySelector('#error').hidden");
 // Exercise the inline controls against a real synthetic amendment, independently of desk layout benchmarks.
 await persona('trader');await click('#nav-futures');
 await wait("document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group):has(.desk-pill-fut)')&&"+idle);
 await click('#desk-positions .tabulator-row:not(.tabulator-group):has(.desk-pill-fut)');await click('#side-panel-amend');
 await wait("!document.querySelector('#trade-editing').hidden&&"+idle);
 const edited=await evaluate("document.querySelector('#trade-lots').value==='5'?'6':'5'");
 await evaluate("document.querySelector('#trade-lots').focus();document.querySelector('#trade-lots').select()");await command('Input.insertText',{text:edited});
 await click('#knowledge-context>summary');
 const coordinates=await evaluate("JSON.stringify([document.querySelector('#date-context-on').value,document.querySelector('#date-context-view').value])"),prompt="document.querySelector('#position-details-prompt')";
 await evaluate("document.querySelector('#date-context-view').focus()");
 await check('focusing inline time controls retains an edited amendment without a question',`document.querySelector('#trade-lots').value===${JSON.stringify(edited)}&&!document.querySelector('#trade-editing').hidden&&${prompt}.hidden`);
 await set('#date-context-view','2020-12-16T12:00:01');await wait(`!${prompt}.hidden`);
 await check('editing the inline cutoff asks before discarding an amendment',`${prompt}.querySelectorAll('button').length===2&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value===${JSON.stringify(edited)}`);
 await evaluate(`${prompt}.querySelectorAll('button')[0].click()`);await wait(`${prompt}.hidden`);
 await check('keeping the amendment restores the exact date and cutoff',`JSON.stringify([document.querySelector('#date-context-on').value,document.querySelector('#date-context-view').value])===${JSON.stringify(coordinates)}&&!document.querySelector('#trade-editing').hidden`);
 for(const selector of ['#apply-view-context','#current-view-context']){
  await click(selector);await wait(`!${prompt}.hidden`);await evaluate(`${prompt}.querySelectorAll('button')[0].click()`);await wait(`${prompt}.hidden`);
  await check(selector+' preserves the amendment when the user keeps editing',`!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value===${JSON.stringify(edited)}`);
 }
 await set('#date-context-view','2020-12-16T12:00:01');await wait(`!${prompt}.hidden`);await evaluate(`${prompt}.querySelectorAll('button')[1].click()`);await wait(idle);
 await check('discarding the amendment applies only the requested cutoff edit',"document.querySelector('#trade-editing').hidden&&document.querySelector('#date-context-view').value==='2020-12-16T12:00:01'&&document.querySelector('#workspace-mode-label').textContent.includes('Unapplied')","({editingHidden:document.querySelector('#trade-editing').hidden,cutoff:document.querySelector('#date-context-view').value,mode:document.querySelector('#workspace-mode-label').textContent,note:document.querySelector('#environment-context-note').textContent})");
 await click('#current-view-context');await persona('admin');await click('#nav-jobs');
 await check('operational-only access keeps Jobs available without unauthorized financial controls',"!document.querySelector('#jobs-page').hidden&&document.querySelector('#context-toolbar').hidden&&document.querySelector('#error').hidden");
 await persona('viewer');await click('#nav-market');
}
