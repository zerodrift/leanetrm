import {sideSession} from './side-session.mjs';
import {newTrade} from './new-trade.mjs';
import {qualifyProvider,pricedModel} from './model-fixture.mjs';
import {chooseFutures} from './futures-selection.mjs';
import {popOut as openPopOut} from './blotter-window.mjs';

// The pop-out Blotter (blotter-workbench.md, Pop-out Blotter), after the assistant panel in the assistant check's preview: the Blotter opens
// in a window of its own, tied to the main window, which shows where it went and can bring it back. The pop-out follows the main window's
// market selection whichever page that shows, reads every value itself, relays what it shows to the main window's assistant, and stops
// showing values, saying why, when the main window signs out, changes account or closes.
export async function run({command,evaluate,wait,click,set,persona,check,idle,url,provider,tab}){
  const control=new URL(provider);
  if(control.protocol!=='http:'||control.hostname!=='127.0.0.1'||!control.port||control.pathname!=='/'||control.search)throw Error('The fake provider control must be the owned loopback listener.');
  const script=async(...answers)=>{if(!(await fetch(new URL('/respond',control),{method:'POST',body:JSON.stringify(answers)})).ok)throw Error('The fake provider refused a scripted answer.');};
  // A guide that navigates, published for traders through an administrator's own session.
  const stamp=Date.now(),side=await sideSession(url),administrator='browser_popout_admin_'+stamp;
  await side.call('admin/create',{operationId:crypto.randomUUID(),login:administrator,name:'Browser pop-out administrator',kind:'human',roles:['access_administrator','viewer']});
  await side.call('profile',{profile:administrator});
  await qualifyProvider(side,'sk-synthetic-browser-popout-'+stamp);
  const model=await pricedModel(side,{identifier:'browser-popout-model-'+stamp,displayName:'Browser pop-out model',efforts:['low']});await model.enable();
  const modelVersion=(await side.call('settings/ai/agents')).models.find(m=>m.id===model.id).version;
  const agent=await side.call('settings/ai/agents/create',{operationId:crypto.randomUUID(),name:'Browser pop-out guide '+stamp});
  const saved=await side.call('settings/ai/agents/save',{operationId:crypto.randomUUID(),agentId:agent.id,expectedVersion:agent.version,
    description:'Moves the page where it is asked to.',instructions:'Quote only values you read.',modelId:model.id,modelVersion,reasoning:'low',
    caching:false,tools:['read_current_page','read_trades','navigate'],allowedRoles:['trader'],limits:{toolSteps:8,inputTokens:200000,outputTokens:2048,elapsedSeconds:120,readSeconds:15,retries:2,historyTurns:20},grantDays:28});
  const guide=await side.call('settings/ai/agents/publish',{operationId:crypto.randomUUID(),agentId:saved.id,expectedVersion:saved.version});

  // Each window records the API paths and bodies it sends.
  const recorder="window.__reads=[];{const send=window.fetch;window.fetch=async(...args)=>{window.__reads.push({path:String(args[0]),body:args[1]?.body?JSON.parse(args[1].body):null});return send(...args);};}";
  await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.addScriptToEvaluateOnNewDocument',{source:recorder});
  await command('Page.navigate',{url:url+'/'});await wait(idle,30000);await persona('trader');
  const q=selector=>`document.querySelector(${JSON.stringify(selector)})`,shown=selector=>`!${q(selector)}.hidden`;
  const dates=await evaluate(`[...${q('#snapshot')}.options].map(o=>o.value).filter(Boolean)`);
  if(dates.length<2)throw Error('The pop-out checks need two As of dates.');
  await set('#snapshot',dates[0]);await wait(idle,30000);

  // A position of this run's own, booked from the Blotter's New trade.
  await click('#nav-futures',30000);await newTrade({click,wait},'futures');
  await chooseFutures({evaluate,set,click});await evaluate("document.querySelector('#futures-book').selectedIndex=1;");
  await set('#futures-lots','3');await set('#futures-execution','2.7');
  await evaluate("for(const id of ['futures-legal-entity','futures-counterparty']){const s=document.getElementById(id);s.selectedIndex=1;s.dispatchEvent(new Event('input',{bubbles:true}));}");
  await evaluate(`${q('#futures-form')}.requestSubmit()`);await wait("document.querySelector('#position-details')?.dataset.mode==='position'&&"+idle,30000);
  const reference=await evaluate("document.querySelector('#position-details-head .side-panel-reference').textContent");

  const popOut=driver=>openPopOut({command,evaluate,url,tab},driver);
  const away="document.querySelector('#blotter-away')";
  const popup=await popOut();
  await check('Pop out opens the Blotter in its own window, which reads its rows itself, with nothing of the session left in its address',
    JSON.stringify(await popup.evaluate("location.pathname==='/blotter.html'&&location.hash===''&&location.search===''&&window.opener!==null&&!document.querySelector('#futures-page').hidden&&performance.getEntriesByType('resource').some(r=>new URL(r.name).pathname==='/api/trading/workspace')")));
  await check('the main window shows that the Blotter is in its own window, with Bring back and no Blotter controls',
    `${shown('#blotter-away')}&&${away}.textContent.includes('Blotter is in its own window')&&!!${q('#blotter-bring-back')}&&${q('#futures-page')}.hidden`,`[${away}?.textContent,${q('#futures-page')}.hidden]`);
  const asOf="document.querySelector('#header-calculation-time').textContent";
  const firstAsOf=await popup.evaluate(asOf),firstValues=await popup.evaluate("document.querySelector('#blotter-strip').textContent");
  await check('the pop-out says the As of and knowledge cutoff it reads at',JSON.stringify(/^As of .+ · knowledge cutoff .+/.test(firstAsOf)));

  // Edits the pop-out holds are asked about in the main window before a change made there gives them up; a move to another page is not one.
  const prompt=q('#blotter-window-question-prompt');
  const answer=label=>evaluate(`[...${prompt}.querySelectorAll('button')].find(b=>b.textContent===${JSON.stringify(label)}).click()`);
  // The pop-out reports its edits a moment after they are made.
  const reported=()=>evaluate('new Promise(resolve=>setTimeout(resolve,500))');
  // Typed as a person types: only the person's own input is an edit of a form.
  const lots=async(driver,value)=>{
    await newTrade({click:driver.click,wait:driver.wait},'futures');
    await driver.evaluate("(()=>{const n=document.querySelector('#futures-lots');n.focus();n.select();})()");await driver.command('Input.insertText',{text:value});await reported();
  };
  await lots(popup,'2');
  await click('#nav-market',30000);
  await check('moving to another page of the main window asks nothing of the pop-out\'s edits',`${prompt}.hidden&&${q('#page-title')}.textContent==='Curves & prices'`);
  await set('#snapshot',dates[1]);await wait(`!${prompt}.hidden`).catch(async error=>{throw Error(error.message+' · observed '+JSON.stringify({main:await evaluate(`({snapshot:${q('#snapshot')}.value,first:${JSON.stringify(dates[0])},notice:${q('#notice')}.textContent,error:${q('#error')}.textContent})`),
    popup:await popup.evaluate("({mode:document.querySelector('#position-details').dataset.mode,lots:document.querySelector('#futures-lots')?.value,notice:document.querySelector('#notice').textContent,asOf:document.querySelector('#header-calculation-time').textContent})")}));});
  await check('a change of the market date in the main window first asks about the edits the pop-out holds',`${prompt}.textContent.includes('The Blotter’s window holds unsaved edits')`,`${prompt}.textContent`);
  await answer('Keep them');await wait(`${prompt}.hidden&&${idle}`);await reported();
  await check('keeping them leaves the main window on its date and the pop-out on its edits',
    `${q('#snapshot')}.value===${JSON.stringify(dates[0])}&&${JSON.stringify(await popup.evaluate(`document.querySelector('#futures-lots').value==='2'&&${asOf}===${JSON.stringify(firstAsOf)}`))}`);
  // Following live corrections changes what the pop-out reads too, so it asks the same.
  await click('#follow-live');await wait(`!${prompt}.hidden`,10000);
  await check('Follow live corrections asks first about the edits the pop-out holds',`${prompt}.textContent.includes('The Blotter’s window holds unsaved edits')`);
  await answer('Keep them');await wait(`${prompt}.hidden&&${idle}`);
  await check('and keeping them leaves the main window off live corrections and the pop-out on its edits',
    `${q('#live-status')}.hidden&&${q('#workspace-mode-label')}.textContent.startsWith('Working data')&&${JSON.stringify(await popup.evaluate("document.querySelector('#futures-lots').value==='2'"))}`);
  // Giving them up changes the date there, which is applied in the pop-out at once, and the pop-out says its edits went.
  await set('#snapshot',dates[1]);await wait(`!${prompt}.hidden`);await answer('Give them up');await wait(idle,30000);
  await popup.wait(`${idle}&&${asOf}!==${JSON.stringify(firstAsOf)}&&document.querySelector('#futures-page').dataset.pricingState==='complete'`,60000);
  const followed=await popup.evaluate(`(()=>{const reads=performance.getEntriesByType('resource').filter(r=>new URL(r.name).pathname==='/api/trading/workspace');return reads.length>=2;})()`);
  await check('the pop-out follows the market date the main window chose on Curves & prices: its As of and values are read again',
    JSON.stringify(followed&&await popup.evaluate(`${asOf}!==${JSON.stringify(firstAsOf)}&&(document.querySelector('#blotter-strip').textContent)!==${JSON.stringify(firstValues)}`)));
  await check('and says the edits in its details were given up',JSON.stringify(await popup.evaluate("document.querySelector('#notice').textContent.includes('the edits in this window’s details were given up')")));

  // Group the rows in the pop-out: the main window's assistant hears what the pop-out shows, labelled as from it.
  await popup.evaluate("(()=>{const s=document.querySelector('#blotter-row-group');s.value='market';s.dispatchEvent(new Event('change',{bubbles:true}));})()");
  await popup.wait(`${idle}&&document.querySelector('#blotter-row-group').value==='market'`);
  await click('#nav-futures');await wait(`${shown('#blotter-away')}&&${idle}`);
  if(await evaluate(`${q('#assistant-panel')}.hidden`))await click('#assistant-launcher');
  await wait(`${q('#assistant-looking-layout')}.textContent.endsWith('Pop-out window')`).catch(()=>undefined);
  await check('the main window\'s Looking at reports the pop-out\'s Blotter, relayed and labelled as the pop-out window',
    `${q('#assistant-looking-page')}.textContent==='Blotter'&&${shown('#assistant-looking-layout')}&&${q('#assistant-looking-layout')}.textContent.endsWith('Pop-out window')`,
    `[${q('#assistant-looking-page')}.textContent,${q('#assistant-looking-layout')}.textContent]`);

  // Ask the assistant about a position, from the pop-out's row menu, fills the main window's composer and sends nothing.
  await evaluate(`${q('#assistant-question')}.value=''`);
  await popup.evaluate(`(()=>{const s=document.querySelector('#blotter-filters .table-search');s.value=${JSON.stringify(reference)};s.dispatchEvent(new Event('input',{bubbles:true}));})()`);
  const rowAt=`(()=>{const r=[...document.querySelectorAll('#desk-positions .tabulator-row[data-row-key]')].find(r=>r.getClientRects().length&&r.textContent.includes(${JSON.stringify(reference)}));if(!r)return null;const b=r.querySelector('.desk-reference').getBoundingClientRect();return {x:b.left+b.width/2,y:b.top+b.height/2};})()`;
  await popup.wait(`!!${rowAt}`);
  const at=await popup.evaluate(rowAt);
  for(const type of ['mousePressed','mouseReleased'])await popup.command('Input.dispatchMouseEvent',{type,...at,button:'right',buttons:type==='mousePressed'?2:0,clickCount:1});
  await popup.wait("!!document.querySelector('.blotter-row-menu:not([hidden])')");
  const asksBefore=await evaluate("window.__reads.filter(r=>r.path==='/api/assistant/turns/ask').length");
  await popup.evaluate("[...document.querySelector('.blotter-row-menu:not([hidden])').querySelectorAll('[role=menuitem]')].find(i=>i.firstChild.textContent==='Ask the assistant about this position').click()");
  await wait(`${q('#assistant-question')}.value.includes(${JSON.stringify(reference)})`,30000);
  await check('Ask the assistant about this position in the pop-out puts that position in the main window\'s assistant panel, read by the main window, and sends nothing',
    `${shown('#assistant-panel')}&&${q('#assistant-question')}.value.startsWith(${JSON.stringify('Tell me about position '+reference)})&&window.__reads.filter(r=>r.path==='/api/assistant/turns/ask').length===${asksBefore}`
    +`&&window.__reads.some(r=>r.path==='/api/trading/positions')`,`${q('#assistant-question')}.value`);
  await evaluate(`${q('#assistant-question')}.value=''`);

  // An assistant Navigate target to the Blotter brings the pop-out to the front, and the main window draws no second Blotter.
  await click('#nav-market',30000);
  await popup.evaluate("window.__fronted=0;window.focus=()=>{window.__fronted++;};");
  await set('#assistant-agent',guide.id+':1');
  const readsBefore=await evaluate("window.__reads.filter(r=>r.path==='/api/trading/workspace').length");
  await set('#assistant-question','Take me to the Blotter.');await wait(`!${q('#assistant-send')}.disabled`);
  await evaluate(`${q('#assistant-composer')}.requestSubmit()`);await wait(shown('#assistant-consent'),30000);
  await script({tools:[['toolu_popout_01','navigate',{page:'blotter',target:null}]]},{text:'The Blotter is in front.'});
  await click('#assistant-allow');
  await wait(`${q('#page-title')}.textContent==='Blotter'&&${q('#assistant-send')}.textContent==='Send'&&${idle}`,60000).catch(async error=>{
    throw Error(error.message+' · observed '+JSON.stringify(await evaluate(`({title:${q('#page-title')}.textContent,send:${q('#assistant-send')}.textContent,turn:document.querySelector('#assistant-turns > li:last-child')?.textContent,error:${q('#assistant-error')}.textContent,banner:${q('#error')}.textContent,consent:${shown('#assistant-consent')}})`)));});
  await check('a Navigate target to the Blotter brings the pop-out to the front instead of drawing a second Blotter',
    `${shown('#blotter-away')}&&${q('#futures-page')}.hidden&&window.__reads.filter(r=>r.path==='/api/trading/workspace').length===${readsBefore}&&${JSON.stringify(await popup.evaluate('window.__fronted'))}>0`,
    `[${shown('#blotter-away')},window.__reads.filter(r=>r.path==='/api/trading/workspace').length,${JSON.stringify(await popup.evaluate('window.__fronted'))}]`);

  // Bring back asks first about edits the pop-out holds, which keeping leaves where they are.
  await lots(popup,'4');
  // A live target of the assistant does not move this window either while the pop-out holds edits: its notice says so and offers Go anyway.
  const turns="document.querySelectorAll('#assistant-turns > li').length",turnsBefore=await evaluate(turns);
  await script({tools:[['toolu_popout_02','navigate',{page:'schedules',target:null}]]},{text:'Here are the schedules.'});
  await set('#assistant-question','Take me to the schedules.');await wait(`!${q('#assistant-send')}.disabled`);await evaluate(`${q('#assistant-composer')}.requestSubmit()`);
  const notice="document.querySelector('#assistant-turns > li:last-child .assistant-target')";
  await wait(`${turns}>${turnsBefore}&&${q('#assistant-send')}.textContent==='Send'&&!!${notice}&&${idle}`,60000);
  await check('an assistant target does not move the main window while the pop-out holds edits: it says so and offers Go anyway',
    `${q('#page-title')}.textContent==='Blotter'&&${notice}.textContent.includes('Go would leave unsaved changes')&&[...${notice}.querySelectorAll('button')].some(b=>b.textContent==='Go anyway')`,`${notice}?.textContent`);
  await click('#blotter-bring-back');await wait(`!${prompt}.hidden`);await answer('Keep them');await wait(`${prompt}.hidden&&${idle}`);
  await check('Bring back asks about the edits the pop-out holds, and keeping them leaves the Blotter in its window',
    `${shown('#blotter-away')}&&${JSON.stringify((await command('Target.getTargets')).targetInfos.some(t=>t.url.startsWith(url+'/blotter.html')))}&&${JSON.stringify(await popup.evaluate("document.querySelector('#futures-lots').value==='4'"))}`);
  // Bring back hands the pop-out's view, with its unsaved grouping, to the main window, which reads the Blotter itself.
  await click('#blotter-bring-back');await wait(`!${prompt}.hidden`);await answer('Give them up');
  await wait(`${q('#blotter-away')}.hidden&&!${q('#futures-page')}.hidden&&!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`,60000);
  await check('Bring back closes the pop-out and the main Blotter shows the grouping chosen there, unsaved',
    `${q('#blotter-row-group')}.value==='market'&&${q('#blotter-saved')}.value===''&&window.__reads.filter(r=>r.path==='/api/trading/workspace').length>${readsBefore}`
    +`&&!${JSON.stringify((await command('Target.getTargets')).targetInfos.some(t=>t.url.startsWith(url+'/blotter.html')))}`,
    `[${q('#blotter-row-group')}.value,${q('#blotter-saved')}.value]`);
  await popup.close().catch(()=>undefined);

  // The pop-out stops showing values, saying why, when the main window signs out, changes account or closes.
  const endedText="document.querySelector('#blotter-window-ended').textContent";
  const blank=`${shown('#blotter-window-ended')}&&document.querySelector('#futures-page').hidden&&!document.querySelector('#desk-positions .tabulator-row')`;
  const signedOut=await popOut();
  // This preview has no sign-in, so its Sign out button is hidden and its logout refused: the pop-out ends on the main window's word.
  await click('#profile-open');await evaluate(`${q('#sign-out')}.click()`);
  await signedOut.wait(`${endedText}==='Signed out in the main window'`,30000);
  await check('signing out in the main window leaves the pop-out saying so, with no figures',JSON.stringify(await signedOut.evaluate(blank)));
  // The failed logout reads the Blotter again behind the dialog, whose controls wait for it.
  await wait(idle,60000);await evaluate(`${q('#profile-dialog')}.open&&${q('#profile-close')}.click()`);await wait(`!${q('#profile-dialog')}.open&&${idle}`);
  await check('and the main window no longer holds a Blotter in another window',`${q('#blotter-away')}.hidden`);
  await wait(`!${q('#futures-page')}.hidden&&!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`,60000);
  await check('a sign-out that fails shows the main window\'s own Blotter again, read afresh, never an empty page',`!${q('#futures-page')}.hidden&&!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')`);
  await signedOut.close();

  await click('#nav-futures',30000);await wait(`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`,60000);
  const changed=await popOut();
  await persona('editor');
  await changed.wait(`${endedText}==='The account changed in the main window'`,30000);
  await check('changing account in the main window leaves the pop-out saying so, with no figures',JSON.stringify(await changed.evaluate(blank)));
  await changed.close();await persona('trader');

  // A trade request the pop-out sent whose outcome is not known goes back with the Blotter to the main window, which then owns it: its Retry
  // sends the request under the identity it was first sent with.
  await click('#nav-futures',30000);await wait(`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`,60000);
  // Books a future in `driver`'s Blotter whose first send is lost, so its outcome is not known, and gives the identity it was sent under.
  async function holdBooking(driver){
    await newTrade({click:driver.click,wait:driver.wait},'futures');
    await chooseFutures({evaluate:driver.evaluate,set:driver.set,click:driver.click});await driver.evaluate("document.querySelector('#futures-book').selectedIndex=1;");
    await driver.set('#futures-lots','1');await driver.set('#futures-execution','2.6');
    await driver.evaluate("for(const id of ['futures-legal-entity','futures-counterparty']){const s=document.getElementById(id);s.selectedIndex=1;s.dispatchEvent(new Event('input',{bubbles:true}));}");
    await driver.evaluate("(()=>{const send=window.fetch;window.__books=[];window.__lose=true;window.fetch=async(input,init)=>{if(String(input).endsWith('/futures/book')){window.__books.push(JSON.parse(init.body).operationId);if(window.__lose){window.__lose=false;throw new TypeError('Synthetic lost connection');}}return send(input,init);};})()");
    await driver.evaluate("document.querySelector('#futures-form').requestSubmit()");
    await driver.wait(`window.__books.length===1&&!document.querySelector('#futures-pending').hidden&&${idle}`,30000);
    return driver.evaluate('window.__books[0]');
  }
  const holder=await popOut(),held=await holdBooking(holder);
  // A main window that already holds another request is never handed this one: the pop-out stays open on its request and says why.
  const profileKey=await holder.evaluate("Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.futures.request.server_booking_utc_v1.blotter-window.')).replace('blotter-window.','')");
  await evaluate(`sessionStorage.setItem(${JSON.stringify(profileKey)},JSON.stringify({path:'futures/book',operationId:'synthetic-other-request',body:{}}))`);
  await holder.evaluate("setTimeout(()=>{document.querySelector('#blotter-popout').click();setTimeout(()=>document.querySelector('#position-details-prompt:not([hidden]) button:not(.secondary)')?.click(),300);},0);true");
  await holder.wait(`!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('already holds another unconfirmed trade request')&&${idle}`,30000);
  await check('a pop-out whose request the main window cannot take stays open on it, saying why, with the Blotter still there',
    `${shown('#blotter-away')}&&${JSON.stringify(await holder.evaluate("!document.querySelector('#futures-pending').hidden&&!document.querySelector('#futures-page').hidden"))}`);
  await evaluate(`sessionStorage.removeItem(${JSON.stringify(profileKey)})`);
  // Bring back from the pop-out, which asks about what its details still hold.
  await holder.evaluate("setTimeout(()=>{document.querySelector('#blotter-popout').click();setTimeout(()=>document.querySelector('#position-details-prompt:not([hidden]) button:not(.secondary)')?.click(),300);},0);true");
  await wait(`${q('#blotter-away')}.hidden&&!${q('#futures-pending')}.hidden&&${idle}`,60000);
  const stored=`Object.keys(sessionStorage).filter(k=>k.startsWith('openetrm.futures.request.server_booking_utc_v1.')&&!k.includes('blotter-window.')).map(k=>JSON.parse(sessionStorage.getItem(k)).operationId)`;
  await check('closing the pop-out hands its unconfirmed trade request to the main window, which shows it with Check status and Retry under the same identity',
    `JSON.stringify(${stored})===${JSON.stringify(JSON.stringify([held]))}&&!${q('#futures-reconcile')}.hidden&&!${q('#futures-retry')}.hidden`,stored);
  const booksBefore=await evaluate("window.__reads.filter(r=>r.path==='/api/futures/book').length");
  await click('#futures-retry',60000);await wait(`${q('#futures-pending')}.hidden&&${idle}`,60000);
  await check('and its Retry sends that request again under that identity, never a second one',
    `(()=>{const sent=window.__reads.filter(r=>r.path==='/api/futures/book').slice(${booksBefore}).map(r=>r.body.operationId);return sent.length>0&&sent.every(id=>id===${JSON.stringify(held)});})()`,
    `window.__reads.filter(r=>r.path==='/api/futures/book').slice(${booksBefore}).map(r=>r.body.operationId)`);
  await holder.close().catch(()=>undefined);

  // What a pop-out hands over waits in the main window's storage until its next idle look. A risk submission there that the report cannot
  // take is never removed: it stays, and the main window says why.
  const profile=profileKey.slice('openetrm.futures.request.server_booking_utc_v1.'.length),submissionSlot=JSON.stringify('openetrm.risk.handed-submission.'+profile);
  await evaluate(`sessionStorage.setItem(${submissionSlot},JSON.stringify({version:1,queued:true,parts:[]}))`);
  await wait(`!${q('#error')}.hidden&&${q('#error')}.textContent.includes('handed over could not be read')`,10000).catch(()=>undefined);
  await check('a handed risk submission that cannot be read stays in the main window\'s storage, and the main window says why',
    `sessionStorage.getItem(${submissionSlot})!==null&&!${q('#error')}.hidden&&${q('#error')}.textContent.includes('handed over could not be read')`,`[sessionStorage.getItem(${submissionSlot}),${q('#error')}.textContent]`);
  await evaluate(`sessionStorage.removeItem(${submissionSlot})`);
  // A trade request handed over is never written over by a booking the main window makes before it has looked: the booking takes it over
  // first, and is refused while it is outstanding, so its Check status and Retry stay, under its first identity.
  await newTrade({click,wait},'futures');
  await chooseFutures({evaluate,set,click});await evaluate("document.querySelector('#futures-book').selectedIndex=1;");
  await set('#futures-lots','1');await set('#futures-execution','2.6');
  await evaluate("for(const id of ['futures-legal-entity','futures-counterparty']){const s=document.getElementById(id);s.selectedIndex=1;s.dispatchEvent(new Event('input',{bubbles:true}));}");
  const booksSent=await evaluate("window.__reads.filter(r=>r.path==='/api/futures/book').length");
  await evaluate(`(()=>{sessionStorage.setItem(${JSON.stringify(profileKey)},JSON.stringify({path:'futures/book',operationId:'synthetic-handed-request',body:{bookId:'synthetic'},label:'Synthetic handed request'}));document.querySelector('#futures-form').requestSubmit();})()`);
  await wait(`!${q('#futures-pending')}.hidden&&${idle}`,30000).catch(()=>undefined);
  await check('a booking made in the main window while a handed request waits in its storage takes that request over and is refused, sending nothing',
    `JSON.stringify(${stored})===${JSON.stringify(JSON.stringify(['synthetic-handed-request']))}&&!${q('#futures-pending')}.hidden&&!${q('#futures-retry')}.hidden&&window.__reads.filter(r=>r.path==='/api/futures/book').length===${booksSent}&&!${q('#error')}.hidden`,
    `[${stored},${q('#futures-pending')}.hidden,window.__reads.filter(r=>r.path==='/api/futures/book').length,${q('#error')}.textContent]`);
  // The synthetic request is the main window's now: it goes with a reload, after which the Blotter is read afresh.
  await evaluate(`sessionStorage.removeItem(${JSON.stringify(profileKey)})`);
  const before=await evaluate('performance.timeOrigin');await evaluate('setTimeout(()=>location.reload(),0);true');
  for(let n=0;n<100;n++){if(await command('Page.handleJavaScriptDialog',{accept:true}).then(()=>true,()=>false))break;if(await evaluate(`performance.timeOrigin!==${before}`).catch(()=>false))break;await new Promise(r=>setTimeout(r,50));}
  await wait(`performance.timeOrigin!==${before}&&${idle}`,60000);
  await click('#nav-futures',30000);await wait(`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${q('#futures-pending')}.hidden&&${idle}`,60000);

  // A main window that reloads is handed the pop-out's request once it is back, never while it is going.
  const reloading=await popOut(),reloaded=await holdBooking(reloading);await reported();
  // Requests are never given up, so a change made in the main window while the pop-out holds only requests asks nothing, and they stay.
  const shownDate=await evaluate(`${q('#snapshot')}.value`),otherDate=dates.find(d=>d!==shownDate);
  await set('#snapshot',otherDate);await wait(`${q('#snapshot')}.value===${JSON.stringify(otherDate)}&&${idle}`,30000);
  await check('a change in the main window asks nothing while the pop-out holds only unconfirmed requests, which stay with it',
    `${prompt}.hidden&&${JSON.stringify(await reloading.evaluate("!document.querySelector('#futures-pending').hidden"))}`);
  // A main window that reloads may ask, as a browser does; the person goes on.
  const loaded=await evaluate('performance.timeOrigin');await evaluate('setTimeout(()=>location.reload(),0);true');
  for(let n=0;n<100;n++){if(await command('Page.handleJavaScriptDialog',{accept:true}).then(()=>true,()=>false))break;if(await evaluate(`performance.timeOrigin!==${loaded}`).catch(()=>false))break;await new Promise(r=>setTimeout(r,50));}
  await wait(`performance.timeOrigin!==${loaded}&&${idle}`,60000);
  await reloading.wait(`${endedText}.includes('its unconfirmed request went to the main window')`,30000);
  await wait(`!${q('#futures-pending')}.hidden&&${idle}`,30000);
  await check('a main window that reloaded shows the pop-out\'s request as soon as it is handed over, under the same identity',`JSON.stringify(${stored})===${JSON.stringify(JSON.stringify([reloaded]))}`,stored);
  await click('#futures-retry',60000);await wait(`${q('#futures-pending')}.hidden&&${idle}`,60000);
  await reloading.close().catch(()=>undefined);

  // A pop-out whose session no longer holds the main window's account stops showing values, and hands the Blotter back.
  const mismatched=await popOut();
  await mismatched.evaluate("(async()=>{const boot=await(await fetch('/api/bootstrap?includeFrames=false')).json();await fetch('/api/profile',{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify({profile:'editor'})});document.querySelector('#futures-refresh').click();})()");
  await mismatched.wait(`${endedText}.startsWith('This window’s account is not the main window’s')`,30000);
  await check('a pop-out that finds another account than the main window\'s shows no figures',JSON.stringify(await mismatched.evaluate(blank)));
  await wait(`${q('#blotter-away')}.hidden`,30000);
  await check('and hands the Blotter back to the main window',`${q('#blotter-away')}.hidden`);
  await mismatched.close();await persona('trader');

  // A main window of its own, closed outright: with its word, and without one, as a crashed window gives none.
  async function mainWindow(){
    const other=await tab();await other.command('Page.enable');await other.command('Runtime.enable');
    await other.command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
    await other.command('Page.navigate',{url:url+'/'});await other.wait(idle,30000);
    if(!await other.evaluate(`!!${q('#snapshot')}.value`)){await other.evaluate(`(()=>{const s=${q('#snapshot')};s.value=${JSON.stringify(dates[0])};s.dispatchEvent(new Event('change',{bubbles:true}));})()`);await other.wait(idle,30000);}
    await other.evaluate(`${q('#nav-futures')}.click()`);await other.wait(`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`,60000);
    return other;
  }
  const second=await mainWindow(),orphan=await popOut(second);
  await second.close();
  await orphan.wait(`${endedText}==='The main window was closed'`,30000);
  await check('closing the main window leaves the pop-out saying so, with no figures',JSON.stringify(await orphan.evaluate(blank)));
  await orphan.close();
  // With the main window gone, a request whose outcome is not known stays in the pop-out alone, with Check status and Retry, and no figures.
  const other=await mainWindow(),keeper=await popOut(other),kept=await holdBooking(keeper);
  await other.close();
  await keeper.wait(`${endedText}.startsWith('The main window was closed')&&!document.querySelector('#futures-pending').hidden`,30000);
  await check('a pop-out whose main window closed keeps only its unconfirmed request, with Check status and Retry, and shows no figures',
    JSON.stringify(await keeper.evaluate("!document.querySelector('#futures-retry').disabled&&!document.querySelector('#futures-reconcile').disabled&&!document.querySelector('#desk-positions .tabulator-row')&&getComputedStyle(document.querySelector('#blotter-workspace')).display==='none'")),
    '('+JSON.stringify(await keeper.evaluate("({retry:document.querySelector('#futures-retry').disabled,reconcile:document.querySelector('#futures-reconcile').disabled,rows:document.querySelectorAll('#desk-positions .tabulator-row').length,workspace:getComputedStyle(document.querySelector('#blotter-workspace')).display,page:document.querySelector('#futures-page').hidden,only:document.querySelector('#futures-page').dataset.retainedOnly,ended:document.querySelector('#blotter-window-ended').textContent})"))+')');
  await keeper.evaluate("document.querySelector('#futures-retry').click()");
  await keeper.wait(`document.querySelector('#futures-pending').hidden&&${idle}`,30000);
  await check('and its Retry sends the request under its first identity',JSON.stringify(await keeper.evaluate(`window.__books.length===2&&window.__books.every(id=>id===${JSON.stringify(kept)})`)));
  await keeper.close();
  // A main window that moves to another site cannot be read: the pop-out treats it as gone, keeps its request, and asks before it closes.
  const elsewhere=await mainWindow(),stray=await popOut(elsewhere);await holdBooking(stray);
  await elsewhere.command('Page.navigate',{url:'data:text/html,<p>Elsewhere</p>'});
  await stray.wait(`${endedText}.startsWith('The main window was closed')&&!document.querySelector('#futures-pending').hidden`,30000);
  await stray.evaluate('new Promise(resolve=>setTimeout(resolve,2500))');
  await check('a main window gone to another site leaves the pop-out keeping its request, with no figures, and asking before it closes',
    JSON.stringify(await stray.evaluate("(()=>{const event=new Event('beforeunload',{cancelable:true});window.dispatchEvent(event);return event.defaultPrevented&&!document.querySelector('#futures-retry').disabled&&!document.querySelector('#desk-positions .tabulator-row');})()")));
  await stray.close();await elsewhere.close();
  const third=await mainWindow(),silent=await popOut(third);
  await third.evaluate("window.addEventListener('pagehide',event=>event.stopImmediatePropagation(),true);true");
  await third.close();
  await silent.wait(`${endedText}==='The main window was closed'`,30000);
  await check('a main window gone without a word is noticed by the pop-out itself, which then shows no figures',JSON.stringify(await silent.evaluate(blank)));
  await silent.close();
}
