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
 const newestAt=await evaluate("window.newestDate=window.marketDateRequests[0].payload.maxDate;window.newestLabel=new Intl.DateTimeFormat('en',{year:'numeric',month:'short',day:'numeric',timeZone:'UTC'}).format(new Date(window.newestDate+'T00:00:00Z'));window.marketDateRequests[0].payload.dates.at(-1).frames[0].at.slice(0,19)");
 // A close that arrives later is offered, never selected: the control says so and only a click on it switches.
 await evaluate('window.hideNewestDate=true');await persona('viewer');
 await check('a user opens on the newest close its calendar offered',`(()=>{const read=window.marketDateRequests.at(-1);return read.payload.maxDate<window.newestDate&&document.querySelector('#snapshot').value===read.payload.dates.at(-1).frames[0].id&&${badge}.textContent==='Latest close';})()`);
 await evaluate(`window.hideNewestDate=false;window.readsBeforeFocus=${dateReads};window.closeBeforeFocus=document.querySelector('#snapshot').value;window.dispatchEvent(new Event('focus'))`);
 await wait(settled+`&&${dateReads}===window.readsBeforeFocus+1`);
 await check('returning to the window offers a newer close without switching to it',`(()=>{const read=window.marketDateRequests.at(-1);return read.body.curve===null&&read.payload.maxDate===window.newestDate&&document.querySelector('#snapshot').value===window.closeBeforeFocus&&${badge}.textContent==='Newer close available'&&${badge}.querySelector('button')&&document.querySelector('#error').hidden;})()`);
 await click('#market-date-badge button');
 await check('one click on Newer close available selects the newest close',`(()=>{const read=window.marketDateRequests.at(-1);return document.querySelector('#snapshot').value===read.payload.dates.at(-1).frames[0].id&&read.payload.maxDate===window.newestDate&&${badge}.textContent==='Latest close'&&!${badge}.querySelector('button')&&!document.querySelector('#market-date-dialog').open&&document.querySelector('#error').hidden;})()`);
 await persona('editor');
 await check('a switched user opens on the newest close as well',`${dateReads}===5&&document.querySelector('#snapshot').value===window.marketDateRequests.at(-1).payload.dates.at(-1).frames[0].id&&${badge}.textContent==='Latest close'`);
 // One more saved version of the close at an instant: a second close that day, so the day needs an explicit choice.
 const addVersion=at=>evaluate(`(async()=>{const boot=await(await fetch('/api/bootstrap')).json(),workspace=await(await fetch('/api/workspace')).json();let frame=boot.frames.find(f=>f.at.startsWith(${JSON.stringify(at)})),observation=workspace.observations.find(o=>frame.observationIds.includes(o.id)&&o.role==='daily_settlement');while(workspace.observations.some(o=>o.corrects_id===observation.id))observation=workspace.observations.find(o=>o.corrects_id===observation.id);async function post(path,body){const response=await fetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify(body)}),payload=await response.json();if(!response.ok)throw Error('Synthetic close setup failed: '+path+' '+payload.code);return payload;}if(!frame.observationIds.includes(observation.id))frame=(await post('select-observation',{snapshotId:frame.id,observationId:observation.id})).frame;await post('corrections',{snapshotId:frame.id,observationId:observation.id,value:observation.value});})()`);
 await addVersion('2020-12-18T16:00:00');await addVersion(newestAt);
 await evaluate(`window.readsBeforeExample=${dateReads}`);await click('#example');
 await check('the As of header retains the selected instant before availability is loaded',`document.querySelector('#snapshot').value&&document.querySelector('#market-date-open').textContent.includes('Dec 18')&&!document.querySelector('#market-date-open').textContent.includes('No available')&&${dateReads}===window.readsBeforeExample`);
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
 await click('#profile-open');await set('#zone','Asia/Tokyo');await submit('#preferences-form');
 await click('#market-date-open');
 await check('user-zone calendar days preserve the original UTC business date and frame ID',"(()=>{const request=window.marketDateRequests.at(-1),group=request.payload.dates.find(d=>d.date==='2020-12-18');return request.body.timeZone==='Asia/Tokyo'&&group.frames.some(f=>f.id===document.querySelector('#snapshot').value&&f.businessDateUTC==='2020-12-17'&&f.at.startsWith('2020-12-17T16:00:00'))&&document.querySelector('#market-date-grid button[data-date=\"2020-12-18\"]').getAttribute('aria-pressed')==='true';})()");
 await evaluate("document.querySelector('#market-date-grid button[data-date=\"2020-12-18\"]').focus()");
 await command('Input.dispatchKeyEvent',{type:'keyDown',key:'ArrowLeft',code:'ArrowLeft',windowsVirtualKeyCode:37});
 await command('Input.dispatchKeyEvent',{type:'keyUp',key:'ArrowLeft',code:'ArrowLeft',windowsVirtualKeyCode:37});
 await check('keyboard navigation moves to an available day without selecting it',"document.activeElement?.dataset.date==='2020-12-17'&&document.querySelector('#market-date-grid button[aria-pressed=true]').dataset.date==='2020-12-18'");
 await click('#market-date-close');await click('#profile-open');await set('#zone','UTC');await submit('#preferences-form');
 await persona('editor');
 await check('a newest date with several closes opens the calendar on it and selects nothing',`document.querySelector('#market-date-dialog').open&&document.querySelector('#snapshot').value===''&&document.querySelector('#market-date-month').value===window.newestDate.slice(0,7)&&document.querySelectorAll('.market-close-choice').length>=2&&document.querySelector('#market-date-closes h3').textContent.includes(window.newestLabel)&&${badge}.textContent==='Choose a close'&&document.querySelector('#error').hidden`);
 await click('#market-date-close');
 await evaluate('window.hideNewestDate=true');await persona('editor');
 await evaluate(`window.hideNewestDate=false;window.readsBeforeFocus=${dateReads};window.closeBeforeFocus=document.querySelector('#snapshot').value;window.dispatchEvent(new Event('focus'))`);
 await wait(settled+`&&${dateReads}===window.readsBeforeFocus+1`);
 await click('#market-date-badge button');
 await check('Newer close available over a date with several closes asks, and keeps the close until one is chosen',`document.querySelector('#market-date-dialog').open&&document.querySelectorAll('.market-close-choice').length>=2&&document.querySelector('#snapshot').value===window.closeBeforeFocus&&${badge}.textContent==='Newer close available'`);
 await click('.market-close-choice:nth-of-type(1)');
 await check('choosing a close of the newest date shows Latest close',`!document.querySelector('#market-date-dialog').open&&document.querySelector('#snapshot').value!==window.closeBeforeFocus&&${badge}.textContent==='Latest close'&&document.querySelector('#error').hidden`);
 await click('#example');await evaluate(`window.readsBeforeNavigation=${dateReads}`);await click('#nav-custom-curves');await wait(settled);
 await check('opening a page checks the whole workspace for a newer close once, in the background',`${dateReads}===window.readsBeforeNavigation+1&&window.marketDateRequests.at(-1).body.curve===null&&window.marketDateRequests.at(-1).body.definition===null&&document.querySelector('#error').hidden`);
 await evaluate("window.callsBeforeEmpty=window.marketDateCalls.filter(p=>p.endsWith('/market/dates')).length");await click('#market-date-open');
 await check('an empty custom graph explains its scope without falling back to workspace dates',"document.querySelector('#market-date-dialog').open&&document.querySelector('#market-date-scope').textContent.includes('Complete a curve graph')&&window.marketDateCalls.filter(p=>p.endsWith('/market/dates')).length===window.callsBeforeEmpty");
 await click('#market-date-close');await set('#custom-curves-template','ng_average');await click('#market-date-open');
 await check('custom curve availability validates its full history window in the backend',"(()=>{const request=window.marketDateRequests.at(-1);return request.body.curve===null&&request.body.definition.code==='ng_average'&&!request.payload.dates.some(d=>d.date==='2020-11-18')&&request.payload.dates.some(d=>d.date==='2020-12-17');})()");
 await evaluate("window.calendarGraphNodes=[...document.querySelectorAll('.custom-curve-node')].map(n=>n.dataset.nodeId).sort();window.graphClose=window.marketDateRequests.at(-1).payload.dates.find(d=>d.date==='2020-12-17').frames[0].id");
 await click('#market-date-grid button[data-date="2020-12-17"]');
 await check('changing As of preserves the active custom graph and exact chosen close',"document.querySelector('#snapshot').value===window.graphClose&&JSON.stringify([...document.querySelectorAll('.custom-curve-node')].map(n=>n.dataset.nodeId).sort())===JSON.stringify(window.calendarGraphNodes)&&document.querySelector('#custom-curves-result').hidden");
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
 await click('#market-date-close');await click('#nav-power');await wait(settled);await click('#market-date-open');
 await check('the power workspace calendar follows its selected pricing curve',"window.marketDateRequests.at(-1).body.curve==='local_power'&&!window.marketDateRequests.at(-1).payload.dates.some(d=>d.date==='2020-12-11')");
 // A historical view the session kept is not working data: starting in it selects no close, reads no workspace calendar
 // and claims no newest close.
 await click('#market-date-close');await set('#date-context-on','2020-12-18');await click('#date-context-latest');await click('#apply-view-context');
 await wait("document.querySelector('#workspace-mode-label').textContent==='Historical view · read only'&&"+idle);
 const replaced=await evaluate('performance.timeOrigin');await command('Page.reload');
 await wait(`performance.timeOrigin!==${replaced}&&document.querySelector('#profile-label')?.textContent==='Market Data Editor'&&${idle}`);
 await check('starting in a historical view selects no close and claims no newest close',`document.querySelector('#workspace-mode-label').textContent==='Historical view · read only'&&document.querySelector('#snapshot').value===''&&${badge}.hidden&&${dateReads}===0&&document.querySelector('#error').hidden`,`JSON.stringify({mode:document.querySelector('#workspace-mode-label').textContent,snapshot:document.querySelector('#snapshot').value,badge:${badge}?.hidden,reads:${dateReads},error:document.querySelector('#error').textContent,profile:document.querySelector('#profile-label')?.textContent})`);
}
