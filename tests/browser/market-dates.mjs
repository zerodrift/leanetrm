/** Calendar presentation consumes authorized backend coverage and exact close IDs. */
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url}){
 await command('Runtime.enable');await command('Page.enable');
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1100,deviceScaleFactor:1,mobile:false});
 await command('Page.addScriptToEvaluateOnNewDocument',{source:`
  window.marketDateRequests=[];window.marketDateCalls=[];
  const original=window.fetch;window.fetch=async(...args)=>{
   const path=new URL(typeof args[0]==='string'?args[0]:args[0].url,location.href).pathname;
   window.marketDateCalls.push(path);
   let response;if(path.endsWith('/market/dates')&&window.denyCalendar){window.denyCalendar=false;response=new Response(JSON.stringify({code:'access_denied'}),{status:403,headers:{'Content-Type':'application/json'}});}else response=await original(...args);
   // Before its newest date arrives, the workspace calendar ends one date earlier. Showing that to the app stands in for a
   // close that becomes available during a session; every other read, and every frame, is the backend's own.
   if(path.endsWith('/market/dates')&&window.hideNewestDate&&response.ok){
    const body=JSON.parse(args[1].body),data=await response.json();
    if(body.curve===null&&body.definition===null){data.dates.pop();data.maxDate=data.dates.at(-1)?.date??null;}
    response=new Response(JSON.stringify(data),{status:response.status,headers:{'Content-Type':'application/json'}});
   }
   // A workspace calendar whose newest close has a time that cannot be read, as an inconsistent answer would.
   if(path.endsWith('/market/dates')&&window.unreadableNewestInstant&&response.ok){
    const body=JSON.parse(args[1].body),data=await response.json();
    if(body.curve===null&&body.definition===null)data.dates.at(-1).frames[0].at='Synthetic unreadable instant';
    response=new Response(JSON.stringify(data),{status:response.status,headers:{'Content-Type':'application/json'}});
   }
   if(path.endsWith('/market/dates')&&window.emptyCurveDates&&response.ok){
    const body=JSON.parse(args[1].body),data=await response.json();
    if(body.curve!==null){data.dates=[];data.minDate=null;data.maxDate=null;data.unavailable=[{date:'2020-12-17',reason:'source_expired'},{date:'2020-12-18',reason:'missing_observation'}];}
    response=new Response(JSON.stringify(data),{status:response.status,headers:{'Content-Type':'application/json'}});
   }
   if(path.endsWith('/market/dates')||path.endsWith('/custom-curves/preview'))window.marketDateRequests.push({path,body:JSON.parse(args[1].body),status:response.status,payload:await response.clone().json()});
   return response;
  };
 `});
 await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
 // Reads of the calendar started, and every one of them answered, so a check of the latest answer never races a background check.
 const dateReads="window.marketDateCalls.filter(p=>p.endsWith('/market/dates')).length";
 const settled="window.marketDateRequests.length===window.marketDateCalls.filter(p=>p.endsWith('/market/dates')||p.endsWith('/custom-curves/preview')).length";
 const badge="document.querySelector('#market-date-badge')";
 await check('startup reads the workspace calendar once and opens on its newest close, saying so',`(()=>{const reads=window.marketDateRequests.filter(r=>r.path.endsWith('/market/dates'));if(reads.length!==1||${dateReads}!==1)return false;const [read]=reads,newest=read.payload.dates.find(d=>d.date===read.payload.maxDate);return read.body.curve===null&&read.body.definition===null&&read.body.timeZone===Intl.DateTimeFormat().resolvedOptions().timeZone&&newest.frames.length===1&&document.querySelector('#snapshot').value===newest.frames[0].id&&${badge}.textContent==='Latest close'&&!${badge}.hidden&&!/Choose a date|No available/.test(document.querySelector('#market-date-open').textContent)&&!document.querySelector('#market-date-dialog').open&&!document.querySelector('#market-date-open').disabled&&document.querySelector('#error').hidden;})()`,`JSON.stringify({reads:window.marketDateRequests.map(r=>[r.path,r.body,r.payload.maxDate,r.payload.dates?.at(-1)?.frames.length]),calls:${dateReads},snapshot:document.querySelector('#snapshot').value,badge:${badge}?.textContent,label:document.querySelector('#market-date-open').textContent,dialog:document.querySelector('#market-date-dialog').open,disabled:document.querySelector('#market-date-open').disabled,error:document.querySelector('#error').textContent})`);
 await evaluate("window.newestDate=window.marketDateRequests[0].payload.maxDate;window.newestLabel=new Intl.DateTimeFormat('en',{year:'numeric',month:'short',day:'numeric',timeZone:'UTC'}).format(new Date(window.newestDate+'T00:00:00Z'))");
 // A close that arrives later is offered, never selected: the control says so and only a click on it switches.
 await evaluate('window.hideNewestDate=true');await persona('viewer');
 await check('a user opens on the newest close its calendar offered',`(()=>{const read=window.marketDateRequests.at(-1);return read.payload.maxDate<window.newestDate&&document.querySelector('#snapshot').value===read.payload.dates.at(-1).frames[0].id&&${badge}.textContent==='Latest close';})()`);
 // The calendar's own read of the whole workspace is as new as a check: opening it on a newer close makes the badge offer that close.
 await evaluate(`window.hideNewestDate=false;window.readsBeforeCalendar=${dateReads}`);
 await click('#market-date-open');await wait(settled+`&&${dateReads}===window.readsBeforeCalendar+1`);
 await check('the calendar read over the whole workspace finds the newer close, and the badge offers it',
  `(()=>{const read=window.marketDateRequests.at(-1);return read.body.curve===null&&read.body.definition===null&&read.payload.maxDate===window.newestDate&&document.querySelector('#market-date-dialog').open&&${badge}.textContent==='Newer close available'&&!!${badge}.querySelector('button')&&document.querySelector('#error').hidden;})()`,
  `JSON.stringify({last:window.marketDateRequests.at(-1).body,maxDate:window.marketDateRequests.at(-1).payload.maxDate,badge:${badge}.hidden?'(hidden)':${badge}.textContent})`);
 await click('#market-date-close');await evaluate('window.hideNewestDate=true');await persona('viewer');
 await evaluate(`window.hideNewestDate=false;window.readsBeforeFocus=${dateReads};window.closeBeforeFocus=document.querySelector('#snapshot').value;window.dispatchEvent(new Event('focus'))`);
 await wait(settled+`&&${dateReads}===window.readsBeforeFocus+1`);
 await check('returning to the window offers a newer close without switching to it',`(()=>{const read=window.marketDateRequests.at(-1);return read.body.curve===null&&read.payload.maxDate===window.newestDate&&document.querySelector('#snapshot').value===window.closeBeforeFocus&&${badge}.textContent==='Newer close available'&&${badge}.querySelector('button')&&document.querySelector('#error').hidden;})()`);
 // A display time zone does not move a close in time: another zone reads the calendar again in that zone and keeps the offer.
 const zoneBefore=await evaluate('window.marketDateRequests.at(-1).body.timeZone'),zoneAfter=zoneBefore==='Asia/Tokyo'?'UTC':'Asia/Tokyo';
 const showZone=async zone=>{await evaluate(`window.readsBeforeZone=${dateReads}`);await click('#profile-open');await set('#zone',zone);await submit('#preferences-form');await wait(settled+`&&${dateReads}>window.readsBeforeZone`);};
 await showZone(zoneAfter);
 await check('another display time zone reads the calendar again in that zone and still offers the newer close',
  `(()=>{const read=window.marketDateRequests.at(-1);return read.body.timeZone===${JSON.stringify(zoneAfter)}&&read.body.curve===null&&document.querySelector('#snapshot').value===window.closeBeforeFocus&&${badge}.textContent==='Newer close available'&&!!${badge}.querySelector('button')&&document.querySelector('#error').hidden;})()`,
  `JSON.stringify({last:window.marketDateRequests.at(-1).body,badge:${badge}.hidden?'(hidden)':${badge}.textContent})`);
 await showZone(zoneBefore);
 await click('#market-date-badge button');
 await check('one click on Newer close available selects the newest close',`(()=>{const read=window.marketDateRequests.at(-1);return document.querySelector('#snapshot').value===read.payload.dates.at(-1).frames[0].id&&read.payload.maxDate===window.newestDate&&${badge}.textContent==='Latest close'&&!${badge}.querySelector('button')&&!document.querySelector('#market-date-dialog').open&&document.querySelector('#error').hidden;})()`);
 // A calendar naming a close whose time cannot be read is refused before anything keeps it: the badge stays as the last check left it,
 // and the page goes on working.
 await evaluate(`window.unreadableNewestInstant=true;window.readsBeforeFocus=${dateReads};window.dispatchEvent(new Event('focus'))`);
 await wait(settled+`&&${dateReads}===window.readsBeforeFocus+1`);
 await evaluate('window.unreadableNewestInstant=false');await click('#nav-market');await wait(settled);
 await check('a calendar naming a close whose time cannot be read is refused, the badge stays as it was, and the page still works',
  `${badge}.textContent==='Latest close'&&!document.querySelector('#market-date-open').disabled&&!document.querySelector('#nav-market').disabled&&document.querySelector('#error').hidden`,
  `JSON.stringify({badge:${badge}.hidden?'(hidden)':${badge}.textContent,open:document.querySelector('#market-date-open').disabled,navigation:document.querySelector('#nav-market').disabled,error:document.querySelector('#error').hidden?'(hidden)':document.querySelector('#error').textContent})`);
 // A close a person picked is told about a newer close as well, and is never switched away from.
 await evaluate('window.hideNewestDate=true');await persona('viewer');await click('#market-date-open');
 const earlier=await evaluate("window.marketDateRequests.at(-1).payload.dates.at(-2).date");await click(`#market-date-grid button[data-date="${earlier}"]`);
 await check('a close a person picked, with no newer date since, reads Selected close',`${badge}.textContent==='Selected close'&&!${badge}.querySelector('button')&&document.querySelector('#error').hidden`);
 await evaluate(`window.hideNewestDate=false;window.readsBeforeFocus=${dateReads};window.closeBeforeFocus=document.querySelector('#snapshot').value;window.dispatchEvent(new Event('focus'))`);
 await wait(settled+`&&${dateReads}===window.readsBeforeFocus+1`);
 await check('a newer close arriving after a person\'s choice is offered without switching to it',`(()=>{const read=window.marketDateRequests.at(-1);return read.payload.maxDate===window.newestDate&&document.querySelector('#snapshot').value===window.closeBeforeFocus&&${badge}.textContent==='Newer close available'&&${badge}.querySelector('button')&&document.querySelector('#error').hidden;})()`);
 await click('#market-date-badge button');
 await check('one click on Newer close available selects the newest close from a person\'s choice too',`(()=>{const read=window.marketDateRequests.at(-1);return document.querySelector('#snapshot').value===read.payload.dates.at(-1).frames[0].id&&${badge}.textContent==='Latest close'&&!${badge}.querySelector('button')&&document.querySelector('#error').hidden;})()`);
 await evaluate(`window.readsBeforeSwitch=${dateReads}`);await persona('editor');
 await check('a switched user opens on the newest close as well',`${dateReads}===window.readsBeforeSwitch+1&&document.querySelector('#snapshot').value===window.marketDateRequests.at(-1).payload.dates.at(-1).frames[0].id&&${badge}.textContent==='Latest close'`);
 // Saving a correction selects its corrected close, a new close on the same day. Availability is read again, so the newest
 // close, corrected, is still the latest close; the day now has two closes and needs an explicit choice.
 await evaluate("[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.includes('Synthetic NG monthly futures')).click()");await wait(idle);
 await click('[data-view=inputs]');await click('#inputs tbody tr:first-child button');
 await evaluate("window.closeBeforeCorrection=document.querySelector('#snapshot').value;document.querySelector('#correction-value').value=(Number(document.querySelector('#correction-value').value)+0.001).toFixed(3)");
 await submit('#correction-form');await wait(settled);
 await check('saving a correction on the newest close keeps the corrected close the latest',`document.querySelector('#snapshot').value!==window.closeBeforeCorrection&&${badge}.textContent==='Latest close'&&document.querySelector('#error').hidden&&!document.querySelector('#notice').hidden&&window.marketDateRequests.at(-1).body.curve===null`,`JSON.stringify({before:window.closeBeforeCorrection,now:document.querySelector('#snapshot').value,badge:${badge}.textContent,error:document.querySelector('#error').textContent,notice:document.querySelector('#notice').textContent,last:window.marketDateRequests.at(-1).body})`);
 await evaluate(`(async()=>{const boot=await(await fetch('/api/bootstrap')).json(),workspace=await(await fetch('/api/workspace')).json();let frame=boot.frames.find(f=>f.at.startsWith('2020-12-18T16:00:00')),observation=workspace.observations.find(o=>frame.observationIds.includes(o.id)&&o.role==='daily_settlement');while(workspace.observations.some(o=>o.corrects_id===observation.id))observation=workspace.observations.find(o=>o.corrects_id===observation.id);async function post(path,body){const response=await fetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify(body)}),payload=await response.json();if(!response.ok)throw Error('Synthetic close setup failed: '+path+' '+payload.code);return payload;}if(!frame.observationIds.includes(observation.id))frame=(await post('select-observation',{snapshotId:frame.id,observationId:observation.id})).frame;await post('corrections',{snapshotId:frame.id,observationId:observation.id,value:observation.value});})()`);
 await evaluate(`window.readsBeforeExample=${dateReads}`);await click('#example');
 await check('the As of header retains the selected instant before availability is loaded',`document.querySelector('#snapshot').value&&document.querySelector('#market-date-open').textContent.includes('Dec 18')&&!document.querySelector('#market-date-open').textContent.includes('No available')&&${dateReads}===window.readsBeforeExample`);
 // The sample opens working data, whose time controls show the selected close's effective date at current knowledge (D217).
 const controlsShowClose="(async()=>{const frames=(await(await fetch('/api/workspace')).json()).frames,shown=frames.find(f=>f.id===document.querySelector('#snapshot').value);return !!shown&&document.querySelector('#date-context-on').value===shown.at.slice(0,10)&&document.querySelector('#date-context-view').value!=='';})()";
 const controlsShown="[document.querySelector('#snapshot').value,document.querySelector('#date-context-on').value,document.querySelector('#date-context-view').value]";
 await check('Open sample prices leaves the time controls on the sample close\'s effective date at current knowledge',controlsShowClose,controlsShown);
 await evaluate("[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.includes('Synthetic NG monthly futures')).click()");await wait(idle);
 await click('#market-date-open');
 await check('opening the calendar asks for the selected curve and displays its covered dates',"document.querySelector('#market-date-dialog').open&&window.marketDateRequests.at(-1).body.curve==='ng_monthly'&&!document.querySelector('#market-date-grid button[data-date=\"2020-12-11\"]').disabled");
 await set('#market-date-month','2020-11');
 await check('unavailable calendar days cannot alter the exact selected close',"(()=>{const before=document.querySelector('#snapshot').value,day=document.querySelector('#market-date-grid button[data-date=\"2020-11-01\"]');day.click();return document.querySelector('#snapshot').value===before&&day.disabled&&day.getAttribute('aria-disabled')==='true';})()");
 await set('#market-date-month','2020-12');
 await evaluate("window.calendarBefore=document.querySelector('#snapshot').value");
 await click('#market-date-grid button[data-date="2020-12-18"]');
 await check('a day with multiple versions requires an explicit saved-close choice',"document.querySelector('#market-date-dialog').open&&document.querySelectorAll('.market-close-choice').length>=2&&document.querySelector('#snapshot').value===window.calendarBefore");
 await evaluate("window.chosenCalendarClose=document.querySelectorAll('.market-close-choice')[1].dataset.frameId");await click('.market-close-choice:nth-of-type(2)');
 await check('choosing a close passes its exact snapshot identity through the existing consumer',"!document.querySelector('#market-date-dialog').open&&document.querySelector('#snapshot').value===window.chosenCalendarClose&&document.querySelector('#error').hidden&&document.querySelector('#contracts tbody tr')");
 await evaluate(`window.readsBeforeFocus=${dateReads};window.closeBeforeFocus=document.querySelector('#snapshot').value;window.dispatchEvent(new Event('focus'))`);
 await wait(settled+`&&${dateReads}===window.readsBeforeFocus+1`);
 await check('a close picked from an earlier day stays selected, and stays Selected, when the window regains focus',`document.querySelector('#snapshot').value===window.closeBeforeFocus&&${badge}.textContent==='Selected close'&&!${badge}.querySelector('button')&&window.marketDateRequests.at(-1).body.curve===null&&window.marketDateRequests.at(-1).payload.maxDate===window.newestDate`);
 await click('#market-date-open');
 await click('#market-date-close');await evaluate("window.coverageCount=window.marketDateRequests.filter(r=>r.path.endsWith('/market/dates')).length");await click('#market-date-open');
 await check('reopening unchanged availability reuses its scoped result',"window.marketDateRequests.filter(r=>r.path.endsWith('/market/dates')).length===window.coverageCount");
 await click('#market-date-grid button[data-date="2020-12-11"]');
 await check('a covered date with one close selects that exact backend frame',"(()=>{const group=window.marketDateRequests.find(r=>r.path.endsWith('/market/dates')&&r.body.curve==='ng_monthly').payload.dates.find(d=>d.date==='2020-12-11');return group.frames.length===1&&document.querySelector('#snapshot').value===group.frames[0].id&&!document.querySelector('#market-date-dialog').open&&document.querySelector('#error').hidden;})()");
 await check('choosing an earlier day shows Selected close',`${badge}.textContent==='Selected close'&&!${badge}.querySelector('button')`);
 await evaluate("[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.includes('Futures–forward spread')).click()");await wait(idle);
 await check('switching to an uncovered curve preserves the selected close and explains missing coverage',"!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('Choose an available calendar date')&&document.querySelector('#market-date-open').textContent.includes('Dec 11')&&window.marketDateRequests.filter(r=>r.path.endsWith('/market/dates')).length===window.coverageCount");
 await click('#market-date-open');
 await check('curve changes refresh coverage and disable a date valid only for another curve',"window.marketDateRequests.at(-1).body.curve==='bridge_basis'&&document.querySelector('#market-date-grid button[data-date=\"2020-12-11\"]').disabled&&!document.querySelector('#market-date-grid button[data-date=\"2020-12-17\"]').disabled");
 await click('#market-date-grid button[data-date="2020-12-17"]');
 await evaluate("window.emptyCurveDates=true;window.beforeEmptyClose=document.querySelector('#snapshot').value;[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.includes('Futures–forward spread')).click()");await wait(idle);
 await click('#market-date-open');
 await check('an empty close popup explains expired and missing inputs instead of asking to select absent dates',"document.querySelector('#market-date-dialog').open&&!document.querySelector('#market-date-empty').hidden&&document.querySelector('#market-date-empty').textContent.includes('expired')&&document.querySelector('#market-date-empty').textContent.includes('missing')&&!document.querySelector('#market-date-scope').textContent.includes('Select an available date')&&!document.querySelector('#market-date-workspace').hidden&&document.querySelectorAll('#market-date-grid button').length===0");
 await shot('market-date-empty');
 await click('#market-date-workspace');
 await check('explicit recovery shows workspace closes while preserving the exact selected close',"document.querySelector('#market-date-dialog').open&&document.querySelector('#market-date-empty').hidden&&document.querySelector('#market-date-workspace').hidden&&window.marketDateRequests.at(-1).body.curve===null&&document.querySelector('#snapshot').value===window.beforeEmptyClose&&document.querySelectorAll('#market-date-grid button.available').length>0");
 await evaluate('window.emptyCurveDates=false');await click('#market-date-close');
 await evaluate("[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.includes('Futures–forward spread')).click()");await wait(idle);
 await click('#profile-open');await set('#zone','Asia/Tokyo');await submit('#preferences-form');await wait(settled);
 await check('changing the display time zone reads the workspace calendar again, and the badge stays true to the close chosen',`window.marketDateRequests.at(-1).body.timeZone==='Asia/Tokyo'&&window.marketDateRequests.at(-1).body.curve===null&&window.marketDateRequests.at(-1).body.definition===null&&${badge}.textContent==='Selected close'&&!${badge}.querySelector('button')&&document.querySelector('#error').hidden`,`JSON.stringify({last:window.marketDateRequests.at(-1).body,badge:${badge}.hidden?'(hidden)':${badge}.textContent})`);
 await click('#market-date-open');
 await check('user-zone calendar days preserve the original UTC business date and frame ID',"(()=>{const request=window.marketDateRequests.at(-1),group=request.payload.dates.find(d=>d.date==='2020-12-18');return request.body.timeZone==='Asia/Tokyo'&&group.frames.some(f=>f.id===document.querySelector('#snapshot').value&&f.businessDateUTC==='2020-12-17'&&f.at.startsWith('2020-12-17T16:00:00'))&&document.querySelector('#market-date-grid button[data-date=\"2020-12-18\"]').getAttribute('aria-pressed')==='true';})()");
 await evaluate("document.querySelector('#market-date-grid button[data-date=\"2020-12-18\"]').focus()");
 await command('Input.dispatchKeyEvent',{type:'keyDown',key:'ArrowLeft',code:'ArrowLeft',windowsVirtualKeyCode:37});
 await command('Input.dispatchKeyEvent',{type:'keyUp',key:'ArrowLeft',code:'ArrowLeft',windowsVirtualKeyCode:37});
 await check('keyboard navigation moves to an available day without selecting it',"document.activeElement?.dataset.date==='2020-12-17'&&document.querySelector('#market-date-grid button[aria-pressed=true]').dataset.date==='2020-12-18'");
 await click('#market-date-close');await click('#profile-open');await set('#zone','UTC');await submit('#preferences-form');await wait(settled);
 await persona('editor');
 await check('several closes at the latest instant, a close and its corrected version, open the calendar on their date and select nothing',`document.querySelector('#market-date-dialog').open&&document.querySelector('#snapshot').value===''&&document.querySelector('#market-date-month').value===window.newestDate.slice(0,7)&&document.querySelectorAll('.market-close-choice').length>=2&&document.querySelector('#market-date-closes h3').textContent.includes(window.newestLabel)&&${badge}.textContent==='Choose a close'&&document.querySelector('#error').hidden`);
 await click('#market-date-close');
 await evaluate('window.hideNewestDate=true');await persona('editor');
 await evaluate(`window.hideNewestDate=false;window.readsBeforeFocus=${dateReads};window.closeBeforeFocus=document.querySelector('#snapshot').value;window.dispatchEvent(new Event('focus'))`);
 await wait(settled+`&&${dateReads}===window.readsBeforeFocus+1`);
 await click('#market-date-badge button');
 await check('Newer close available over several closes at the latest instant asks, and keeps the close until one is chosen',`document.querySelector('#market-date-dialog').open&&document.querySelectorAll('.market-close-choice').length>=2&&document.querySelector('#snapshot').value===window.closeBeforeFocus&&${badge}.textContent==='Newer close available'`);
 await click('.market-close-choice:nth-of-type(1)');
 await check('choosing either close at the latest instant shows Latest close',`!document.querySelector('#market-date-dialog').open&&document.querySelector('#snapshot').value!==window.closeBeforeFocus&&${badge}.textContent==='Latest close'&&document.querySelector('#error').hidden`);
 await click('#example');await evaluate(`window.readsBeforeNavigation=${dateReads}`);await click('#nav-custom-curves');await wait(settled);
 await check('opening a page checks the whole workspace for a newer close once, in the background',`${dateReads}===window.readsBeforeNavigation+1&&window.marketDateRequests.at(-1).body.curve===null&&window.marketDateRequests.at(-1).body.definition===null&&document.querySelector('#error').hidden`);
 await evaluate("window.callsBeforeEmpty=window.marketDateCalls.filter(p=>p.endsWith('/market/dates')).length");await click('#market-date-open');
 await check('an empty custom graph explains its scope without falling back to workspace dates',"document.querySelector('#market-date-dialog').open&&document.querySelector('#market-date-scope').textContent.includes('Complete a curve graph')&&window.marketDateCalls.filter(p=>p.endsWith('/market/dates')).length===window.callsBeforeEmpty");
 // Returning to working data asks which of several closes at the workspace's latest instant to open, on the workspace calendar the
 // question comes from, whatever the page's own calendar can show: here, a custom graph with no curve yet.
 await click('#market-date-close');
 await set('#date-context-on','2020-12-17');await wait(idle);await click('#date-context-latest');await click('#apply-view-context');
 await wait("document.querySelector('#workspace-mode-label').textContent==='Historical view · read only'&&"+idle);
 await click('#return-working');await wait(settled);
 await check('returning to working data on a page whose calendar has no scope yet still asks which close at the latest instant',
  `document.querySelector('#market-date-dialog').open&&document.querySelectorAll('.market-close-choice').length>=2&&document.querySelector('#market-date-closes h3')?.textContent.includes(window.newestLabel)&&document.querySelector('#snapshot').value===''&&${badge}.textContent==='Choose a close'&&document.querySelector('#error').hidden`,
  `JSON.stringify({open:document.querySelector('#market-date-dialog').open,choices:document.querySelectorAll('.market-close-choice').length,scope:document.querySelector('#market-date-scope').textContent,snapshot:document.querySelector('#snapshot').value,badge:${badge}.hidden?'(hidden)':${badge}.textContent,error:document.querySelector('#error').hidden?'(hidden)':document.querySelector('#error').textContent})`);
 await click('.market-close-choice:nth-of-type(1)');
 await click('#market-date-close');await set('#custom-curves-template','ng_average');
 // Full-history coverage uses the API's existing 20-second request deadline.
 // Let it complete or refuse before checking coverage, with time for UI rendering.
 try{await click('#market-date-open',30000);}
 catch(error){
  const state=await evaluate("({busy:document.body.getAttribute('aria-busy'),error:document.querySelector('#error').textContent,calendarOpen:document.querySelector('#market-date-dialog').open,pending:window.marketDateCalls.filter(p=>p.endsWith('/market/dates')).length-window.marketDateRequests.filter(r=>r.path.endsWith('/market/dates')).length,last:window.marketDateRequests.at(-1)?.status})");
  throw Error(error.message+' · observed '+JSON.stringify(state),{cause:error});
 }
 await check('custom curve availability validates its full history window in the backend',"(()=>{const request=window.marketDateRequests.at(-1);return request.body.curve===null&&request.body.definition.code==='ng_average'&&!request.payload.dates.some(d=>d.date==='2020-11-18')&&request.payload.dates.some(d=>d.date==='2020-12-17');})()");
 await evaluate("window.calendarGraphNodes=[...document.querySelectorAll('.custom-curve-node')].map(n=>n.dataset.nodeId).sort();window.graphClose=window.marketDateRequests.at(-1).payload.dates.find(d=>d.date==='2020-12-17').frames[0].id");
 await click('#market-date-grid button[data-date="2020-12-17"]');
 await check('changing As of preserves the active custom graph and exact chosen close',"document.querySelector('#snapshot').value===window.graphClose&&JSON.stringify([...document.querySelectorAll('.custom-curve-node')].map(n=>n.dataset.nodeId).sort())===JSON.stringify(window.calendarGraphNodes)&&document.querySelector('#custom-curves-result').hidden&&document.querySelector('#custom-curves-page-prompt').hidden");
 await click('#custom-curves-preview');
 await check('the preserved graph prices through the backend at the selected close',"(()=>{const request=window.marketDateRequests.at(-1);return request.path.endsWith('/custom-curves/preview')&&request.body.snapshotId===window.graphClose&&request.body.definition.code==='ng_average'&&request.status===200&&document.querySelectorAll('#custom-curves-values tbody tr').length===12&&document.querySelector('#error').hidden;})()");
 await click('.custom-curve-node[data-node-id=output] button');await set('#custom-curves-properties select[data-input-port=values]','');await click('#market-date-open');
 await check('an incomplete graph receives no permissive workspace-date fallback',"window.marketDateRequests.at(-1).status===422&&window.marketDateRequests.at(-1).body.definition.nodes.some(n=>n.kind==='output'&&!n.inputs.values)&&document.querySelector('#market-date-dialog').open&&document.querySelector('#market-date-scope').textContent.includes('Complete a valid curve graph')&&document.querySelectorAll('#market-date-grid button').length===0");
 await click('#market-date-close');await set('#custom-curves-template','ng_prompt');await click('#market-date-open');
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
 await check('the calendar remains within a narrow viewport with labeled date controls',fitsWidth+"&&document.querySelector('#market-date-dialog').getBoundingClientRect().width<=document.documentElement.clientWidth&&document.querySelector('#market-date-grid').getAttribute('role')==='grid'&&document.querySelector('#market-date-open').getAttribute('aria-haspopup')==='dialog'");
 await shot('market-calendar-mobile');
 await click('#market-date-close');await click('#custom-curves-preview');
 await evaluate('window.denyCalendar=true');await click('.custom-curve-node[data-node-id=select] button');await set('#custom-curve-property-count','2');await click('#custom-curves-preview');await click('#market-date-open');
 await check('an authorization denial removes custom results and available-date state',"window.marketDateRequests.at(-1).status===403&&document.querySelector('#custom-curves-result').hidden&&document.querySelectorAll('.custom-curve-node').length===0&&document.querySelectorAll('#market-date-grid button').length===0&&!document.querySelector('#error').hidden");
 await click('#nav-market');await wait(settled);await click('#example');await evaluate('window.denyCalendar=true');await click('#market-date-open');
 await check('an authorization denial clears previously displayed market prices',"window.marketDateRequests.at(-1).status===403&&document.querySelector('#contracts').children.length===0&&document.querySelectorAll('#market-date-grid button').length===0&&!document.querySelector('#market-date-dialog').open&&!document.querySelector('#error').hidden");
 await click('#market-date-open');
 await check('availability can be retried explicitly after a denial without restoring old prices',"window.marketDateRequests.at(-1).status===200&&document.querySelector('#market-date-dialog').open&&document.querySelector('#contracts').children.length===0");
 await click('#market-date-close');await click('#nav-power');await wait(settled);await evaluate('window.emptyCurveDates=true');await click('#market-date-open');
 await check('the power workspace calendar follows its selected pricing curve',"window.marketDateRequests.at(-1).body.curve==='local_power'&&!window.marketDateRequests.at(-1).payload.dates.some(d=>d.date==='2020-12-11')");
 await check('an empty power calendar does not offer a curve-browser action on the power page',"!document.querySelector('#market-date-empty').hidden&&document.querySelector('#market-date-workspace').hidden");
 await evaluate('window.emptyCurveDates=false');
 // A historical view the session kept is not working data: starting in it selects no close, reads no workspace calendar
 // and claims no newest close.
 await click('#market-date-close');await set('#date-context-on','2020-12-18');await wait(idle);await click('#date-context-latest');await click('#apply-view-context');
 await wait("document.querySelector('#workspace-mode-label').textContent==='Historical view · read only'&&"+idle);
 const replaced=await evaluate('performance.timeOrigin');await command('Page.reload');
 await wait(`performance.timeOrigin!==${replaced}&&document.querySelector('#profile-label')?.textContent==='Market Data Editor'&&${idle}`);
 await check('starting in a historical view selects no close and claims no newest close',`document.querySelector('#workspace-mode-label').textContent==='Historical view · read only'&&document.querySelector('#snapshot').value===''&&${badge}.hidden&&${dateReads}===0&&document.querySelector('#error').hidden`,`JSON.stringify({mode:document.querySelector('#workspace-mode-label').textContent,snapshot:document.querySelector('#snapshot').value,badge:${badge}?.hidden,reads:${dateReads},error:document.querySelector('#error').textContent,profile:document.querySelector('#profile-label')?.textContent})`);
}
