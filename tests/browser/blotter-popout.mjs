import {sideSession} from './side-session.mjs';
import {newTrade} from './new-trade.mjs';
import {qualifyProvider,pricedModel} from './model-fixture.mjs';
import {chooseFutures} from './futures-selection.mjs';

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

  // Presses a control with the pointer, as a person does: a window opens only on a person's gesture.
  async function press(driver,selector){
    const {x,y}=await driver.evaluate(`(()=>{const r=${q(selector)}.getBoundingClientRect();return {x:r.left+r.width/2,y:r.top+r.height/2};})()`);
    for(const type of ['mousePressed','mouseReleased'])await driver.command('Input.dispatchMouseEvent',{type,x,y,button:'left',buttons:type==='mousePressed'?1:0,clickCount:1});
  }
  // Pops the Blotter out of `driver`'s window and attaches to the window that opens, once it has drawn its rows.
  async function popOut(driver){
    const before=(await command('Target.getTargets')).targetInfos.map(t=>t.targetId);
    await press(driver,'#blotter-popout');
    let target;for(let n=0;n<200&&!target;n++){target=(await command('Target.getTargets')).targetInfos.find(t=>t.type==='page'&&!before.includes(t.targetId)&&t.url.startsWith(url+'/blotter.html'));if(!target)await new Promise(r=>setTimeout(r,50));}
    if(!target)throw Error('Pop out opened no Blotter window · '+JSON.stringify(await driver.evaluate(`({error:${q('#error')}.textContent,notice:${q('#notice')}.textContent})`)));
    const popup=await tab(target.targetId);
    // A browser opens the window at the size Pop out asks for; this headless one is given it.
    await popup.command('Emulation.setDeviceMetricsOverride',{width:1440,height:900,deviceScaleFactor:1,mobile:false});
    await popup.wait(`${idle}&&!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&document.querySelector('#futures-page').dataset.pricingState==='complete'`,60000).catch(async error=>{
      throw Error(error.message+' · popup '+JSON.stringify(await popup.evaluate("({url:location.href,busy:document.body.getAttribute('aria-busy'),pricing:document.querySelector('#futures-page').dataset.pricingState,rows:document.querySelectorAll('#desk-positions .tabulator-row').length,keyed:document.querySelectorAll('#desk-positions .tabulator-row[data-row-key]').length,grid:document.querySelector('#blotter-pane-grid').getBoundingClientRect().height,window:[innerWidth,innerHeight],text:document.body.innerText.slice(0,1500)})")));});
    return popup;
  }
  const away="document.querySelector('#blotter-away')";
  const popup=await popOut({command,evaluate});
  await check('Pop out opens the Blotter in its own window, which reads its rows itself, with nothing of the session left in its address',
    JSON.stringify(await popup.evaluate("location.pathname==='/blotter.html'&&location.hash===''&&location.search===''&&window.opener!==null&&!document.querySelector('#futures-page').hidden&&performance.getEntriesByType('resource').some(r=>new URL(r.name).pathname==='/api/trading/workspace')")));
  await check('the main window shows that the Blotter is in its own window, with Bring back and no Blotter controls',
    `${shown('#blotter-away')}&&${away}.textContent.includes('Blotter is in its own window')&&!!${q('#blotter-bring-back')}&&${q('#futures-page')}.hidden`,`[${away}?.textContent,${q('#futures-page')}.hidden]`);
  const asOf="document.querySelector('#header-calculation-time').textContent";
  const firstAsOf=await popup.evaluate(asOf),firstValues=await popup.evaluate("document.querySelector('#blotter-strip').textContent");
  await check('the pop-out says the As of and knowledge cutoff it reads at',JSON.stringify(/^As of .+ · knowledge cutoff .+/.test(firstAsOf)));

  // A change of the market date in the main window, made on another page, is applied in the pop-out at once.
  await click('#nav-market',30000);await set('#snapshot',dates[1]);await wait(idle,30000);
  await popup.wait(`${idle}&&${asOf}!==${JSON.stringify(firstAsOf)}&&document.querySelector('#futures-page').dataset.pricingState==='complete'`,60000);
  const followed=await popup.evaluate(`(()=>{const reads=performance.getEntriesByType('resource').filter(r=>new URL(r.name).pathname==='/api/trading/workspace');return reads.length>=2;})()`);
  await check('the pop-out follows the market date the main window chose on Curves & prices: its As of and values are read again',
    JSON.stringify(followed&&await popup.evaluate(`${asOf}!==${JSON.stringify(firstAsOf)}&&(document.querySelector('#blotter-strip').textContent)!==${JSON.stringify(firstValues)}`)));

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

  // Bring back hands the pop-out's view, with its unsaved grouping, to the main window, which reads the Blotter itself.
  await click('#blotter-bring-back',60000);
  await wait(`${q('#blotter-away')}.hidden&&!${q('#futures-page')}.hidden&&!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`,60000);
  await check('Bring back closes the pop-out and the main Blotter shows the grouping chosen there, unsaved',
    `${q('#blotter-row-group')}.value==='market'&&${q('#blotter-saved')}.value===''&&window.__reads.filter(r=>r.path==='/api/trading/workspace').length>${readsBefore}`
    +`&&!${JSON.stringify((await command('Target.getTargets')).targetInfos.some(t=>t.url.startsWith(url+'/blotter.html')))}`,
    `[${q('#blotter-row-group')}.value,${q('#blotter-saved')}.value]`);
  await popup.close().catch(()=>undefined);

  // The pop-out stops showing values, saying why, when the main window signs out, changes account or closes.
  const endedText="document.querySelector('#blotter-window-ended').textContent";
  const blank=`${shown('#blotter-window-ended')}&&document.querySelector('#futures-page').hidden&&!document.querySelector('#desk-positions .tabulator-row')`;
  const signedOut=await popOut({command,evaluate});
  // This preview has no sign-in, so its Sign out button is hidden and its logout refused: the pop-out ends on the main window's word.
  await click('#profile-open');await evaluate(`${q('#sign-out')}.click()`);
  await signedOut.wait(`${endedText}==='Signed out in the main window'`,30000);
  await check('signing out in the main window leaves the pop-out saying so, with no figures',JSON.stringify(await signedOut.evaluate(blank)));
  await evaluate(`${q('#profile-dialog')}.open&&${q('#profile-close')}.click()`);await wait(idle);
  await check('and the main window no longer holds a Blotter in another window',`${q('#blotter-away')}.hidden`);
  await signedOut.close();

  await click('#nav-futures',30000);await wait(`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`,60000);
  const changed=await popOut({command,evaluate});
  await persona('editor');
  await changed.wait(`${endedText}==='The account changed in the main window'`,30000);
  await check('changing account in the main window leaves the pop-out saying so, with no figures',JSON.stringify(await changed.evaluate(blank)));
  await changed.close();await persona('trader');

  // A main window of its own, closed outright.
  const second=await tab();await second.command('Page.enable');await second.command('Runtime.enable');
  await second.command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await second.command('Page.navigate',{url:url+'/'});await second.wait(idle,30000);
  if(!await second.evaluate(`!!${q('#snapshot')}.value`)){await second.evaluate(`(()=>{const s=${q('#snapshot')};s.value=${JSON.stringify(dates[0])};s.dispatchEvent(new Event('change',{bubbles:true}));})()`);await second.wait(idle,30000);}
  await second.evaluate(`${q('#nav-futures')}.click()`);await second.wait(`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&${idle}`,60000);
  const orphan=await popOut(second);
  await second.close();
  await orphan.wait(`${endedText}==='The main window was closed'`,30000);
  await check('closing the main window leaves the pop-out saying so, with no figures',JSON.stringify(await orphan.evaluate(blank)));
  await orphan.close();
}
