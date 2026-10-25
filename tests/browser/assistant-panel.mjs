import {sideSession} from './side-session.mjs';
import {newTrade} from './new-trade.mjs';
import {qualifyProvider,pricedModel} from './model-fixture.mjs';
import {chooseFutures} from './futures-selection.mjs';

// The assistant panel (docs/ai-assistant.md §8, §10.1, §13), after the settings scenarios in the assistant check's
// preview. An administrator's independent session publishes this run's agents; the page asks them as a trader, and the
// fake provider answers exactly as this scenario scripts it through its control listener, holding an answer where a
// check needs a turn in flight. Every answer scripted here is one model call the run must make.
export async function run({command,evaluate,wait,click,set,persona,check,shot,idle,fitsWidth,url,provider,tab}){
  const control=new URL(provider);
  if(control.protocol!=='http:'||control.hostname!=='127.0.0.1'||!control.port||control.pathname!=='/'||control.search)throw Error('The fake provider control must be the owned loopback listener.');
  const script=async(...answers)=>{if(!(await fetch(new URL('/respond',control),{method:'POST',body:JSON.stringify(answers)})).ok)throw Error('The fake provider refused a scripted answer.');};
  const release=async name=>{await fetch(new URL('/release/'+name,control),{method:'POST'});};
  // Each tab records its requests and how many agents each agents read listed, can lose its next ask or price read before it
  // is sent or hold its next ask until released, and keeps every stream it opens, across reloads.
  const recorder='window.__requests=[];window.__streams=[];{const send=window.fetch;window.__fetch=send;window.fetch=async(...args)=>{const path=String(args[0]),'
    +"entry={path,body:args[1]?.body?JSON.parse(args[1].body):null};window.__requests.push(entry);if(path.endsWith('/assistant/turns/ask')){if(window.__drop){window.__drop=false;"
    +"throw TypeError('Synthetic lost connection');}if(window.__held)await window.__held;}if(window.__failEvaluate&&path.endsWith('/evaluate')){window.__failEvaluate=false;"
    +"throw TypeError('Synthetic lost connection');}const response=await send(...args);if(path.endsWith('/assistant/agents')&&response.ok)entry.agents=(await response.clone().json()).agents.length;"
    +'return response;};const Native=window.EventSource;window.EventSource=class extends Native{constructor(...args){super(...args);window.__streams.push(this);}};}';

  // This run's agents: a guide that reads market data and navigates, and a second that reads only the page.
  const stamp=Date.now(),guide='Browser panel guide '+stamp,second='Browser panel second '+stamp,administrator='browser_panel_admin_'+stamp;
  const side=await sideSession(url);
  await side.call('admin/create',{operationId:crypto.randomUUID(),login:administrator,name:'Browser panel administrator',kind:'human',roles:['access_administrator','viewer']});
  await side.call('profile',{profile:administrator});
  await qualifyProvider(side,'sk-synthetic-browser-panel-'+stamp);
  const model=await pricedModel(side,{identifier:'browser-panel-model-'+stamp,displayName:'Browser panel model',efforts:['low']});await model.enable();
  const modelVersion=(await side.call('settings/ai/agents')).models.find(m=>m.id===model.id).version;
  async function publish(agent,tools,allowedRoles=['trader']){
    const saved=await side.call('settings/ai/agents/save',{operationId:crypto.randomUUID(),agentId:agent.id,expectedVersion:agent.version,
      description:'Answers questions about the page it is opened on.',instructions:'Quote only values you read.',modelId:model.id,modelVersion,reasoning:'low',
      caching:false,tools,allowedRoles,limits:{toolSteps:8,inputTokens:200000,outputTokens:2048,elapsedSeconds:120,retries:2},grantDays:28});
    return side.call('settings/ai/agents/publish',{operationId:crypto.randomUUID(),agentId:saved.id,expectedVersion:saved.version});
  }
  const create=name=>side.call('settings/ai/agents/create',{operationId:crypto.randomUUID(),name});
  const guided=await publish(await create(guide),['read_current_page','read_market_data','read_trades','navigate']);
  let seconded=await publish(await create(second),['read_current_page']);

  await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.addScriptToEvaluateOnNewDocument',{source:recorder});
  await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
  const q=selector=>`document.querySelector(${JSON.stringify(selector)})`;
  const shown=selector=>`!${q(selector)}.hidden`;
  const latest="document.querySelector('#assistant-turns > li:last-child')";
  const answer=`(${latest}?.querySelector('.assistant-answer')?.textContent??'')`;
  const finished=`(!!${latest}?.querySelector('.assistant-usage')&&${q('#assistant-send')}.textContent==='Send')`;
  const notice=`${latest}?.querySelector('.assistant-target')`;
  const asks="window.__requests.filter(r=>r.path==='/api/assistant/turns/ask')";
  const looking="['#assistant-looking-page','#assistant-looking-shows','#assistant-looking-context'].map(s=>document.querySelector(s).textContent)";
  const agentsRead="window.__requests.some(r=>r.path==='/api/assistant/agents')";
  const buttons=`[...(${latest}?.querySelector('.assistant-target')?.querySelectorAll('button')??[])].map(b=>b.textContent).join()`;
  const press=label=>evaluate(`[...${latest}.querySelector('.assistant-target').querySelectorAll('button')].find(b=>b.textContent===${JSON.stringify(label)}).click()`);
  // A person opens on the newest close, but these checks begin with no As of date chosen, so it is cleared.
  const clearDate=async()=>{await set('#snapshot','');await wait(idle);};
  // A trader opens on the Blotter, and the checks that follow a reload expect Curves & prices, so it is opened again.
  const reload=async ready=>{const before=await evaluate('performance.timeOrigin');await command('Page.reload');await wait(`performance.timeOrigin!==${before}&&${idle}&&${ready}`);await click('#nav-market');await clearDate();};
  const pause=ms=>evaluate(`new Promise(r=>setTimeout(r,${ms}))`);
  // A check in another tab: its expression is evaluated there, and its outcome recorded here.
  const checkIn=async(page,label,expression)=>check(label,JSON.stringify(!!await page.evaluate(expression)));
  const turns="document.querySelectorAll('#assistant-turns > li').length";
  // Sends a question once the composer takes it, and returns once the panel shows its turn, consent or a refusal.
  async function ask(question){
    const before=await evaluate(turns);
    await set('#assistant-question',question);await wait(`!${q('#assistant-send')}.disabled`);
    await evaluate(`${q('#assistant-composer')}.requestSubmit()`);
    await wait(`${turns}>${before}||${shown('#assistant-consent')}||${shown('#assistant-error')}`);
  }

  // The launcher (§10.1) shows only for a person who uses agents and may use one.
  await persona('viewer');
  await check('the launcher is hidden from a person without UseAgents, who reads no agents',`${q('#assistant-launcher')}.hidden&&!${agentsRead}`);
  await persona('editor');await wait("window.__requests.some(r=>r.path==='/api/assistant/agents'&&r.agents===0)");await pause(50);
  await check('and from a person who uses agents but may use none of them',`${q('#assistant-launcher')}.hidden&&${q('#assistant-agent')}.options.length===1`);
  await persona('trader');await wait(shown('#assistant-launcher'));await click('#nav-market');await clearDate();
  await click('#assistant-launcher');
  const guideChoice=`${guided.id}:1`,secondChoice=`${seconded.id}:1`;
  await wait(`[...${q('#assistant-agent')}.options].some(o=>o.value===${JSON.stringify(guideChoice)})`);
  await check('the panel opens docked beside the narrowed page with nothing chosen: Choose an agent, Send disabled, and each usable version named with its provider and model',
    `${shown('#assistant-panel')}&&${q('#assistant-agent')}.value===''&&${q('#assistant-agent')}.selectedOptions[0].textContent==='Choose an agent'&&${q('#assistant-send')}.disabled`
    +`&&${q('#assistant-composer-note')}.textContent==='Choose an agent to ask.'&&[...${q('#assistant-agent')}.options].find(o=>o.value===${JSON.stringify(guideChoice)}).textContent===${JSON.stringify(guide+' · version 1 · Anthropic Browser panel model')}`
    +`&&getComputedStyle(document.querySelector('main')).marginRight===Math.round(${q('#assistant-panel')}.getBoundingClientRect().width)+'px'`,
    `[${q('#assistant-agent')}.value,getComputedStyle(document.querySelector('main')).marginRight,${q('#assistant-panel')}.getBoundingClientRect().width]`);
  const width=await evaluate(`${q('#assistant-panel')}.getBoundingClientRect().width`);
  await evaluate(`${q('#assistant-resize')}.focus();${q('#assistant-resize')}.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowLeft',bubbles:true}))`);
  await check('its left edge widens it by keyboard, and this browser remembers the width',
    `${q('#assistant-panel')}.getBoundingClientRect().width===${width+16}&&localStorage.getItem('openetrm.assistant.width')==='${width+16}'&&${q('#assistant-resize')}.getAttribute('aria-valuenow')==='${width+16}'`);
  await check('Looking at names the page, what it shows and that working data needs an As of date to be asked about',
    `JSON.stringify(${looking})===${JSON.stringify(JSON.stringify(['Curves & prices','Curve directory','Working data · choose an As of date to ask about it']))}`,looking);
  const [first,later]=await evaluate(`[...${q('#snapshot')}.options].map(o=>o.value).filter(Boolean)`);
  if(!later)throw Error('The standard fixture needs two As of dates for the panel checks.');
  await set('#snapshot',first);await wait(idle);await set('#assistant-agent',guideChoice);
  await check('choosing an agent is this browser\'s explicit choice, and an As of date makes the page askable',
    `localStorage.getItem(Object.keys(localStorage).find(k=>k.startsWith('openetrm.assistant.agent.')))===${JSON.stringify(JSON.stringify({agentId:guided.id,version:1}))}`
    +`&&${q('#assistant-looking-context')}.textContent.startsWith('Working data · As of ')`);

  // Consent (§7.1): an ask without a grant asks for it; Allow grants the version and sends the question again as a new request.
  await ask('What does this page show?');await wait(shown('#assistant-consent'));
  const until=new Date(Date.now()+28*86400000).toISOString().slice(0,10);
  await check('asking without a covering grant shows the consent naming the agent, provider, model, its reads and the date the grant would end',
    `${q('#assistant-consent-text')}.textContent===${JSON.stringify(`Allow ${guide}, which sends your questions and the data it reads to Anthropic (Browser panel model) through the vendor account your administrators configured, whose owner may be able to see them, to read trades and market data on your behalf until ${until}?`)}`
    +`&&${q('#assistant-question')}.value==='What does this page show?'`,`${q('#assistant-consent-text')}.textContent`);
  await script({text:'Reading the page. ',tools:[['toolu_panel_01','read_current_page',{}]]},{tools:[['toolu_panel_02','navigate',{page:'schedules',target:null}]]},
    {text:'The page lists **curves**:\n- Synthetic one\n- Synthetic two\n\nSee `curve` and [the blotter](https://example.invalid/x) <b>here</b>.'});
  await click('#assistant-allow');await wait(`${finished}&&${q('#page-title')}.textContent==='Schedules'&&${idle}&&${buttons}==='Back'`);
  await check('Allow grants that version alone and sends the same question under a new request identity, reporting exactly what Looking at showed',
    `(()=>{const a=${asks},g=window.__requests.filter(r=>r.path==='/api/assistant/grants/grant');return a.length===2&&g.length===1&&Object.keys(g[0].body).sort().join()==='agentId,operationId,version'`
    +`&&g[0].body.agentId===${JSON.stringify(guided.id)}&&g[0].body.version===1&&a[0].body.operationId!==a[1].body.operationId&&a.every(r=>r.body.question==='What does this page show?'&&r.body.conversationId===null`
    +`&&JSON.stringify(r.body.pageReport)===JSON.stringify({page:'curves',selection:{view:'curves',curve:null}})&&JSON.stringify(r.body.knowledge)===JSON.stringify({mode:'current',contextId:null,frameId:${JSON.stringify(first)}}));})()`,asks);
  await check('a live target that keeps the context and leaves nothing unsaved moves the page, with its notice and Back',
    `${notice}.textContent.startsWith('Took you to Schedules')&&${buttons}==='Back'`,`${notice}?.textContent`);
  await check('step lines name each tool in plain words, collapsed until opened',
    `(()=>{const d=${latest}.querySelector('.assistant-steps');return !d.open&&d.querySelector('summary').textContent==='2 steps'&&[...d.querySelectorAll('li')].map(l=>l.textContent).join('|')==='Read the page|Navigate';})()`);
  await check('the answer is drawn from text nodes: emphasis, lists and code, with a link and markup left as literal text',
    `(()=>{const a=${latest}.querySelector('.assistant-answer');return a.querySelector('strong')?.textContent==='curves'&&a.querySelectorAll('ul > li').length===2&&a.querySelector('code')?.textContent==='curve'`
    +`&&!a.querySelector('a,b,img,table,script')&&a.textContent.includes('[the blotter](https://example.invalid/x) <b>here</b>.');})()`,`${latest}.querySelector('.assistant-answer').innerHTML`);
  const conversation=await evaluate(`(async()=>(await (await window.__fetch('/api/assistant/conversations')).json()).conversations[0].id)()`);
  const stored=async()=>evaluate(`(async()=>(await (await window.__fetch('/api/assistant/conversations/${conversation}?after=0')).json()))()`);
  const cost=(await stored()).events.findLast(e=>e.kind==='usage').payload.cost[0];
  await check('the usage line gives tokens by dimension and the exact cost as the turn records it',
    `/^Tokens: [0-9,]+ input · [0-9,]+ cache read · [0-9,]+ cache write · [0-9,]+ output \\([0-9,]+ reasoning\\) · Cost: usd /.test(${latest}.querySelector('.assistant-usage').textContent)`
    +`&&${latest}.querySelector('.assistant-usage').textContent.endsWith(${JSON.stringify(`Cost: usd ${cost.amount}`)})`,`${latest}.querySelector('.assistant-usage').textContent`);
  await press('Back');await wait(`${q('#page-title')}.textContent==='Curves & prices'&&${idle}`);
  await check('Back restores the page, what it showed and its context',
    `JSON.stringify(${looking}.slice(0,2))===${JSON.stringify(JSON.stringify(['Curves & prices','Curve directory']))}&&${q('#snapshot')}.value===${JSON.stringify(first)}`);

  // A target pinned to another As of date than the page shows waits for Go, which selects that date and then moves (§10.1).
  await script({hold:'context',text:'Looking at the curve. ',tools:[['toolu_panel_03','read_market_data',{view:'curve',curve:'future'}]]},
    {tools:[['toolu_panel_04','navigate',{page:null,target:'n1'}]]},{text:'That is the curve.'});
  await ask('Show me the future curve.');await wait(`${answer}.startsWith('Looking at the curve.')`);
  await set('#snapshot',later);await wait(idle);await release('context');await wait(finished);
  await check('a target whose As of date the page no longer shows does not move it: its notice offers Go and says why',
    `${notice}.textContent.includes('Go changes the knowledge or As of date the page shows.')&&${buttons}==='Go'`
    +`&&${q('#page-title')}.textContent==='Curves & prices'&&${q('#snapshot')}.value===${JSON.stringify(later)}&&${q('#assistant-looking-shows')}.textContent==='Curve directory'`,`${notice}?.textContent`);
  await press('Go');
  await wait(`${q('#snapshot')}.value===${JSON.stringify(first)}&&${q('#assistant-looking-shows')}.textContent.startsWith('Prices of ')&&${idle}`);
  await check('Go selects the target\'s As of date, opens what it names, and leaves Back',`${notice}.textContent.startsWith('Took you to ')&&${buttons}==='Back'`);

  // Unsaved edits on the page being left hold a live move (§10.1).
  await click('#nav-futures');await newTrade({click,wait},'futures');
  await evaluate(`${q('#futures-lots')}.focus()`);await command('Input.insertText',{text:'7'});
  await script({tools:[['toolu_panel_05','navigate',{page:'schedules',target:null}]]},{text:'Here are the schedules.'});
  await ask('Take me to the schedules.');await wait(finished);
  await check('a live target that would leave unsaved booking edits does not move the page: its notice says so and offers Go anyway',
    `${q('#page-title')}.textContent==='Blotter'&&${shown('#futures-new-trade')}&&${notice}.textContent.includes('Go would leave unsaved changes on this page.')&&${buttons}==='Go anyway'`
    +`&&JSON.stringify(${asks}.at(-1).body.pageReport)===JSON.stringify({page:'blotter_new_future',selection:{}})`,`${notice}?.textContent`);
  // The page report names the new trade in the panel, and its product: the assistant is told what the page shows, and reads no unsent entry.
  await check('Looking at names the new future being entered, and says the assistant does not read it',`JSON.stringify(${looking}.slice(0,2))===${JSON.stringify(JSON.stringify(['Blotter','A new future is being entered. The assistant does not read a trade being entered.']))}`,looking);
  await press('Go anyway');await wait(`${q('#page-title')}.textContent==='Schedules'&&${idle}`);
  await check('Go anyway leaves the unsaved booking for the target, with Back',`${notice}.textContent.startsWith('Took you to Schedules')&&${buttons}==='Back'`);
  await press('Back');await wait(`${q('#page-title')}.textContent==='Blotter'&&${idle}`);
  // Back says so too before it would leave unsaved edits.
  await newTrade({click,wait},'futures');
  await evaluate(`${q('#futures-lots')}.focus()`);await command('Input.insertText',{text:'5'});
  await press('Back');await wait(`${buttons}==='Back anyway'`);
  await check('Back from a page with unsaved edits first says so, keeping them, and offers Back anyway',
    `${shown('#futures-new-trade')}&&${q('#futures-lots')}.value==='5'&&${notice}.textContent.includes('Back would leave unsaved changes on this page.')`);
  await press('Back anyway');await wait(`${q('#futures-new-trade')}.hidden&&${q('#page-title')}.textContent==='Blotter'&&${idle}`);

  // A trade open in the panel is not a dialog (D219): the assistant stays open beside it and can be asked about it, and Looking at names
  // the trade at its revision.
  // The page assistant's fixture holds no trades, so the one these checks ask about is booked here, from the Blotter's own New trade.
  await click('#nav-futures');await newTrade({click,wait},'futures');
  await chooseFutures({evaluate,set,click});await evaluate("document.querySelector('#futures-book').selectedIndex=1;");
  await set('#futures-lots','4');await set('#futures-execution','2.9');
  await evaluate("for(const id of ['futures-legal-entity','futures-counterparty']){const s=document.getElementById(id);s.selectedIndex=1;s.dispatchEvent(new Event('input',{bubbles:true}));}");
  await evaluate(`${q('#futures-form')}.requestSubmit()`);await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&"+idle);
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');
  await wait("!document.querySelector('#side-panel').hidden");
  const trade=await evaluate("document.querySelector('#side-panel-head .side-panel-reference').textContent");
  const revision=await evaluate("document.querySelector('#side-panel-head .side-panel-meta').textContent.match(/Revision (\\d+)/)[1]");
  await check('Looking at names the trade the panel shows, at its revision, with the assistant open beside the panel',
    `JSON.stringify(${looking}.slice(0,2))===${JSON.stringify(JSON.stringify(['Blotter',`Trade ${trade}, revision ${revision}`]))}&&${shown('#assistant-panel')}&&!${q('#side-panel')}.hidden&&${q('#side-panel')}.dataset.placement==='overlay'`,looking);
  await script({text:'That trade is open in the panel. '});
  await ask('What is this trade?');await wait(finished);
  await check('the assistant is asked while the trade is open, and the ask reports it: its page report names the trade, its kind and its revision',
    `(()=>{const r=${asks}.at(-1).body.pageReport,s=r.selection;return r.page==='blotter'&&s.view==='trade'&&typeof s.tradeId==='string'&&['future','financial_swap'].includes(s.instrumentType)&&s.revision===${revision}&&s.mode===null&&s.groupBy===null;})()`,`JSON.stringify(${asks}.at(-1).body.pageReport)`);
  await click('#side-panel-close');
  await check('closing the panel returns Looking at to the Blotter\'s rows',`${looking}[1].startsWith('Trade rows · ')&&${q('#side-panel')}.hidden`,looking);
  // An amendment under way holds a live move away from the page, like any other unsaved edit, and Back brings the trade back in the panel.
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("!document.querySelector('#side-panel').hidden");
  await click('#side-panel-amend');await wait("!document.querySelector('#trade-editing').hidden");
  await evaluate(`${q('#trade-lots')}.focus();${q('#trade-lots')}.select()`);await command('Input.insertText',{text:'3'});
  await script({tools:[['toolu_panel_05b','navigate',{page:'schedules',target:null}]]},{text:'Here are the schedules.'});
  await ask('Take me to the schedules.');await wait(finished);
  await check('a live target that would leave an amendment with unsaved edits does not move the page: its notice says so and offers Go anyway',
    `${q('#page-title')}.textContent==='Blotter'&&!${q('#side-panel')}.hidden&&!${q('#trade-editing')}.hidden&&${notice}.textContent.includes('Go would leave unsaved changes on this page.')&&${buttons}==='Go anyway'&&${asks}.at(-1).body.pageReport.selection.view==='trade'`,`${notice}?.textContent`);
  await press('Go anyway');await wait(`${q('#page-title')}.textContent==='Schedules'&&${idle}`);
  // The edits were given up on the person's word, so the form holds nothing to return to, and the browser is asked nothing when the page is left.
  await check('once the page has gone on the person\'s word, the form holds nothing and the browser is asked nothing when the page is left',
    `${q('#trade-editing')}.hidden&&!(()=>{const event=new Event('beforeunload',{cancelable:true});window.dispatchEvent(event);return event.defaultPrevented;})()`,`[${q('#trade-editing')}.hidden]`);
  await press('Back');await wait(`${q('#page-title')}.textContent==='Blotter'&&!${q('#side-panel')}.hidden&&${idle}`);
  await check('Back opens the trade again in the panel, on its Terms and without the edits that were given up',
    `${q('#side-panel-head .side-panel-reference')}.textContent===${JSON.stringify(trade)}&&${q('#side-panel-tabs-terms')}.getAttribute('aria-selected')==='true'&&${q('#trade-editing')}.hidden&&JSON.stringify(${looking}.slice(0,2))===${JSON.stringify(JSON.stringify(['Blotter',`Trade ${trade}, revision ${revision}`]))}`,looking);
  await click('#side-panel-close');
  // The report names the revision the form displays: the position's own, or an earlier one that the form's selector shows.
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("!document.querySelector('#side-panel').hidden");
  await click('#side-panel-amend');await wait("!document.querySelector('#trade-editing').hidden&&"+idle);
  await set('#trade-lots','5');await set('#trade-reason','Synthetic report revision');await evaluate(`${q('#trade-amend-form')}.requestSubmit()`);await wait(idle);
  await click('#trade-confirm');await click('#trade-submit');
  await wait(`${q('#side-panel-head .side-panel-meta')}?.textContent.startsWith('Revision 2')&&${idle}`);
  await click('#side-panel-amend');await wait("!document.querySelector('#trade-editing').hidden&&"+idle);
  await set('#trade-revision','1');await wait(idle);
  await check('Looking at names the revision the form displays, though the Blotter holds a later one',
    `JSON.stringify(${looking}.slice(0,2))===${JSON.stringify(JSON.stringify(['Blotter',`Trade ${trade}, revision 1`]))}&&${q('#side-panel-head .side-panel-meta')}.textContent.startsWith('Revision 2')`,looking);
  await script({text:'It shows revision 1. '});
  await ask('Which revision is this?');await wait(finished);
  await check('and so does the page report an ask sends',`${asks}.at(-1).body.pageReport.selection.revision===1&&${asks}.at(-1).body.pageReport.selection.view==='trade'`,`JSON.stringify(${asks}.at(-1).body.pageReport)`);
  await click('#trade-reload');
  await check('which is the Blotter\'s again once the form reloads the latest',`JSON.stringify(${looking}.slice(0,2))===${JSON.stringify(JSON.stringify(['Blotter',`Trade ${trade}, revision 2`]))}`,looking);
  await click('#side-panel-close');
  // A trade the assistant names opens from the Blotter's trade rows. A pivot's rows are totals, so the move is refused in words that name the
  // view it needs: the person's view stays as it is, and nothing says the trade was shown.
  const tradeId=await evaluate(`${asks}.filter(r=>r.body.pageReport.selection?.view==='trade').at(-1).body.pageReport.selection.tradeId`);
  await click('#blotter-pivot>summary');await set('#blotter-mode','pivot');await click('#blotter-dimensions input[value="book"]');await click('#blotter-measures input[value="tradeCount"]');await click('#blotter-apply');
  await wait(`${q('#blotter-table-title')}.textContent==='Pivot summary'&&${idle}`);
  await script({tools:[['toolu_panel_13','read_trades',{view:'trade',mode:null,bookIds:null,portfolioIds:null,groupBy:null,measures:null,tradeId,instrumentType:'future',revision:null}]]},{tools:[['toolu_panel_14','navigate',{page:null,target:'n1'}]]},{text:'That is the trade.'});
  await ask('Open that trade.');await wait(finished);
  await pause(300);
  await check('a trade the assistant names is refused in a pivot, in words that name the view it needs, and the view stays as it was',
    `!${q('#error')}.hidden&&${q('#error')}.textContent.includes('Individual trades')&&${q('#blotter-mode')}.value==='pivot'&&${q('#side-panel')}.hidden&&${q('#page-title')}.textContent==='Blotter'`,`[${q('#error')}.textContent,${q('#blotter-mode')}.value,${q('#side-panel')}.hidden]`);
  await check('and nothing says the trade was shown: its notice still offers Go',`!${notice}.textContent.startsWith('Took you to')&&${buttons}==='Go'`,`[${notice}?.textContent,${buttons}]`);
  // The view is the one the Blotter has applied: a Display choice that has not been applied neither refuses a trade nor allows one, and the
  // control is left as the person set it.
  const trade14={view:'trade',mode:null,bookIds:null,portfolioIds:null,groupBy:null,measures:null,tradeId,instrumentType:'future',revision:null};
  await set('#blotter-mode','trades');
  await script({tools:[['toolu_panel_15','read_trades',trade14]]},{tools:[['toolu_panel_16','navigate',{page:null,target:'n1'}]]},{text:'That is the trade, still.'});
  await ask('Open that trade, though Display shows trade rows that are not applied.');await wait(finished);
  await pause(300);
  await check('a Display choice that has not been applied does not allow a trade: the pivot is the view, the trade is refused, and the control stays as it was set',
    `!${q('#error')}.hidden&&${q('#error')}.textContent.includes('Individual trades')&&${q('#blotter-mode')}.value==='trades'&&${q('#side-panel')}.hidden&&${q('#blotter-table-title')}.textContent==='Pivot summary'`,`[${q('#error')}.textContent,${q('#blotter-mode')}.value,${q('#blotter-table-title')}.textContent]`);
  await click('#blotter-apply');await wait(`${q('#blotter-table-title')}.textContent.startsWith('Positions')&&${idle}`);
  await set('#blotter-mode','pivot');
  await script({tools:[['toolu_panel_17','read_trades',trade14]]},{tools:[['toolu_panel_18','navigate',{page:null,target:'n1'}]]},{text:'That is the trade, again.'});
  await ask('Open that trade, though Display shows a pivot that is not applied.');await wait(finished);
  await pause(300);
  await check('and one that has not been applied does not refuse it: the trade rows are the view, and the trade opens',
    `${q('#error')}.hidden&&!${q('#side-panel')}.hidden&&${q('#side-panel-head .side-panel-reference')}.textContent===${JSON.stringify(trade)}`,`[${q('#error')}.textContent,${q('#side-panel')}.hidden,${q('#side-panel-head .side-panel-reference')}?.textContent]`);
  await click('#side-panel-close');await set('#blotter-mode','trades');

  // A page the assistant does not read (§7.3).
  await click('#nav-delivery');
  await check('Looking at says the assistant does not read Delivery prices',
    `JSON.stringify(${looking}.slice(0,2))===${JSON.stringify(JSON.stringify(['Delivery prices','The assistant does not read this page.']))}`,looking);
  await script({tools:[['toolu_panel_06','read_current_page',{}]]},{text:'I do not read this page.'});
  await ask('What is on this page?');await wait(finished);
  await check('Read current page on it is refused page_not_supported, which its step line says in plain words',
    `[...${latest}.querySelectorAll('.assistant-steps li')].map(l=>l.textContent).join()==='Read the page · the assistant does not read the page you asked from'`
    +`&&JSON.stringify(${asks}.at(-1).body.pageReport)===JSON.stringify({page:'delivery_prices',selection:{}})`);

  // Send becomes Stop during a turn (§10.1).
  await script({hold:'stop',text:'Partial answer. ',tools:[['toolu_panel_07','read_current_page',{}]]});
  await ask('Keep going.');await wait(`${answer}.startsWith('Partial answer.')&&${q('#assistant-send')}.textContent==='Stop'`);
  await check('during a turn Send becomes Stop and the agent and conversation are fixed',
    `!${q('#assistant-send')}.disabled&&${q('#assistant-agent')}.disabled&&${q('#assistant-new')}.disabled`);
  await click('#assistant-send');await wait(finished);await release('stop');
  await check('Stop ends the answer, marked incomplete, saying the person stopped it',
    `${latest}.querySelector('.assistant-ending').textContent==='You stopped this answer.'&&!!${latest}.querySelector('.assistant-incomplete')&&${answer}==='Partial answer. '`);

  // A send whose outcome is unknown keeps the question with Retry, which sends the same request, through a reload (§8, §10.1).
  await evaluate('window.__drop=true');await script({text:'Received once.'});
  await ask('Did this arrive?');await wait(shown('#assistant-retry'));
  await check('a lost send keeps the draft and offers Retry and Discard instead of Send',
    `${q('#assistant-error')}.textContent.startsWith('We could not confirm that your question was received.')&&${q('#assistant-question')}.value==='Did this arrive?'`
    +`&&${shown('#assistant-discard')}&&${q('#assistant-send')}.disabled`);
  const dropped=await evaluate(`${asks}.at(-1).body.operationId`);
  await reload(`${shown('#assistant-panel')}&&${shown('#assistant-retry')}`);
  await check('the kept ask survives a reload, its question and Retry offered again',
    `${q('#assistant-question')}.value==='Did this arrive?'&&${q('#assistant-error')}.textContent.startsWith('We could not confirm that your question was received.')`);
  const lost=await evaluate(turns);await click('#assistant-retry');await wait(`${turns}>${lost}&&${finished}`);
  await check('Retry sends the same request identity and the answer arrives once',
    `${asks}.length===1&&${asks}[0].body.operationId===${JSON.stringify(dropped)}&&${answer}==='Received once.'&&${q('#assistant-question')}.value===''`,
    `[${asks}.map(a=>a.body.operationId),${answer},${q('#assistant-question')}.value]`);

  // While an ask is in flight, History offers no other conversation and New conversation waits (§10.1).
  await set('#snapshot',first);await wait(idle);
  await evaluate('window.__held=new Promise(r=>{window.__send=r;});true');await script({text:'Sent once History closed.'});
  const sent=await evaluate(turns);await set('#assistant-question','Hold this ask.');await wait(`!${q('#assistant-send')}.disabled`);
  await evaluate(`${q('#assistant-composer')}.requestSubmit()`);await wait(`${asks}.length===2`);
  await click('#assistant-history-open');await wait(`document.querySelectorAll('#assistant-history-list button').length>0`);
  await check('while an ask is in flight History offers no conversation to open and New conversation waits',
    `[...document.querySelectorAll('#assistant-history-list button')].every(b=>b.disabled)&&${q('#assistant-new')}.disabled`
    +`&&${q('#assistant-history-status')}.textContent==='Stop or wait for the current answer to open another conversation.'`);
  await evaluate('window.__held=undefined;window.__send()');await click('#assistant-history-close');await wait(`${turns}>${sent}&&${finished}`);

  // Reloading mid-turn reads the turn and resumes it, without duplicates or gaps (§10.1).
  await set('#snapshot',first);await wait(idle);
  await script({hold:'reload',text:'Before the reload. ',tools:[['toolu_panel_08','read_current_page',{}]]},{text:'After the reload.'});
  await ask('Answer across a reload.');await wait(`${answer}==='Before the reload. '`);
  const seen=(await stored()).events.at(-1).sequence;
  await reload(`${shown('#assistant-panel')}&&${answer}==='Before the reload. '&&window.__streams.length===1`);
  await check('after a reload the panel reopens on its conversation and resumes the unfinished turn after the last event it read',
    `(()=>{const s=new URL(window.__streams[0].url);return s.pathname==='/api/assistant/conversations/${conversation}/events'&&s.searchParams.get('after')==='${seen}';})()`,'window.__streams[0]?.url');
  await release('reload');await wait(finished);
  const answered=(await stored()).turns.at(-1).answer;
  await check('the resumed answer is the stored answer exactly, each piece once',`${answer}===${JSON.stringify(answered)}&&${answer}==='Before the reload. After the reload.'`);

  // Closing mid-turn keeps the stream; an answer finishing while closed lights the launcher and stays unread (§10.1).
  await set('#snapshot',first);await wait(idle);
  await script({hold:'closed',text:'Finishing while closed. ',tools:[['toolu_panel_09','read_current_page',{}]]},{text:'Done.'});
  await ask('Finish while the panel is closed.');await wait(`${answer}==='Finishing while closed. '`);
  await click('#assistant-close');
  await check('closing the panel mid-turn keeps the tab\'s stream',`${q('#assistant-panel')}.hidden&&window.__streams.at(-1).readyState===EventSource.OPEN`);
  const marks="window.__requests.filter(r=>r.path==='/api/assistant/conversations/read').length";
  const marked=await evaluate(marks);
  await release('closed');await wait(shown('#assistant-dot'));
  await check('the answer finishing while closed lights the launcher, marking nothing read',`${q('#assistant-launcher')}.getAttribute('aria-label')==='Assistant · a new answer'&&${marks}===${marked}`);
  // A tab that has just navigated may still hold its blank page, which has no launcher and is never idle, so idle comes first.
  const launchable=`${idle}&&${shown('#assistant-launcher')}`;
  const other=await tab();await other.command('Page.addScriptToEvaluateOnNewDocument',{source:recorder});await other.command('Page.navigate',{url:url+'/'});
  await other.wait(launchable);await other.click('#assistant-launcher');await other.click('#assistant-history-open');
  await other.wait(`document.querySelectorAll('#assistant-history-list li').length>0`);
  await checkIn(other,'another tab, holding no stream, learns of the unread answer in History',
    `(()=>{const item=document.querySelector('#assistant-history-list li');return item.classList.contains('unread')&&item.textContent.includes('Unread')&&item.textContent.includes(${JSON.stringify(guide)})&&window.__streams.length===0;})()`);
  await click('#assistant-launcher');await wait(`${q('#assistant-dot')}.hidden&&${marks}===${marked+1}`);
  await other.click('#assistant-history-open');await other.wait(`document.querySelectorAll('#assistant-history-list li').length>0&&${q('#assistant-history-status')}.textContent!=='Reading your conversations…'`);
  await check('opening the panel shows the finished answer, clears the launcher and sends one read mark',`${answer}==='Finishing while closed. Done.'`);
  await checkIn(other,'History no longer marks the conversation unread',`!document.querySelector('#assistant-history-list li').classList.contains('unread')`);

  // One stream per turn: opening the turn in a second tab replaces the first tab's, which offers Resume; a resumed stream's
  // targets wait for Go (§8, D191).
  await set('#snapshot',first);await wait(idle);
  await script({hold:'replaced',text:'One stream. ',tools:[['toolu_panel_10','navigate',{page:'schedules',target:null}]]},{text:'Still one stream.'});
  await ask('Follow me in another tab.');await wait(`${answer}==='One stream. '`);
  await other.evaluate(`${q('#assistant-history-list button')}.click()`);
  await other.wait(`${answer}==='One stream. '&&window.__streams.at(-1)?.readyState===EventSource.OPEN`);
  await wait(`${shown('#assistant-stream')}&&${shown('#assistant-resume')}`);
  await check('the first tab\'s stream is replaced: it closes and offers Resume',
    `window.__streams.at(-1).readyState===EventSource.CLOSED&&${q('#assistant-stream-text')}.textContent==='This answer is continuing in another tab or window.'`);
  await click('#assistant-resume');await wait(`window.__streams.at(-1).readyState===EventSource.OPEN&&${q('#assistant-stream')}.hidden`);
  await other.wait(`${shown('#assistant-stream')}&&${shown('#assistant-resume')}`);
  await release('replaced');await wait(`${answer}==='One stream. Still one stream.'&&${finished}`);
  await check('Resume follows the turn again, and a target it then receives waits for Go instead of moving the page',
    `${q('#page-title')}.textContent==='Curves & prices'&&${buttons}==='Go'&&${notice}.textContent.startsWith('Schedules')`,`[${q('#page-title')}.textContent,${buttons}]`);

  // Six tabs with one unfinished turn: only the asking tab holds a stream; idle panels hold none (§8).
  await set('#snapshot',first);await wait(idle);
  await script({hold:'six',text:'Six tabs. ',tools:[['toolu_panel_11','read_current_page',{}]]},{text:'One stream each turn.'});
  await ask('Count the streams.');await wait(`${answer}==='Six tabs. '`);
  const tabs=[other];
  for(let i=0;i<4;i++){const added=await tab();await added.command('Page.addScriptToEvaluateOnNewDocument',{source:recorder});await added.command('Page.navigate',{url:url+'/'});await added.wait(launchable);tabs.push(added);}
  const open="window.__streams.filter(s=>s.readyState!==EventSource.CLOSED).length";
  const idleTabs=await Promise.all(tabs.map(t=>t.evaluate(open)));
  await check('with six tabs open on one unfinished turn, only the asking tab holds a stream',`${open}===1&&${JSON.stringify(idleTabs)}.every(n=>n===0)`,JSON.stringify(idleTabs));
  await release('six');await wait(`${answer}==='Six tabs. One stream each turn.'`);
  for(const t of tabs)await t.close();

  // A target pinned to a historical view waits for Go from working data; Go selects that view again by its id, and Back
  // returns to working data (§10.1, D191).
  await set('#date-context-on','2020-12-18');await click('#date-context-latest');await click('#apply-view-context');
  await wait(`${q('#workspace-mode-label')}.textContent==='Historical view · read only'&&${idle}`);
  const dated=await evaluate(`[...${q('#snapshot')}.options].map(o=>o.value).find(Boolean)`);await set('#snapshot',dated);await wait(idle);
  await script({hold:'retained',text:'From history. ',tools:[['toolu_panel_12','navigate',{page:'schedules',target:null}]]},{text:'There it is.'});
  await ask('Take me to the schedules as they were.');await wait(`${answer}==='From history. '`);
  const pinned=await evaluate(`${asks}.at(-1).body.knowledge`);
  await click('#return-working');await wait(`${q('#workspace-mode-label')}.textContent.startsWith('Working data')&&${idle}`);
  await release('retained');await wait(finished);
  await check('a target pinned to a historical view waits in working data, saying Go changes the knowledge the page shows',
    `${q('#page-title')}.textContent==='Curves & prices'&&${buttons}==='Go'&&${notice}.textContent.includes('Go changes the knowledge or As of date the page shows.')`
    +`&&${JSON.stringify(pinned.mode)}==='retained'`,`${notice}?.textContent`);
  await press('Go');await wait(`${q('#page-title')}.textContent==='Schedules'&&${idle}`);
  await check('Go selects that historical view again by its id, at its As of date, and opens the page there',
    `${q('#workspace-mode-label')}.textContent==='Historical view · read only'&&${q('#snapshot')}.value===${JSON.stringify(dated)}`
    +`&&window.__requests.some(r=>r.path==='/api/market/context/retained'&&r.body.contextId===${JSON.stringify(pinned.contextId)}&&r.body.selection==='latest_correction')`
    +`&&${q('#assistant-looking-context')}.textContent.startsWith('Historical view · 2020-12-18, as known at ')&&${buttons}==='Back'`,looking);
  await press('Back');await wait(`${q('#page-title')}.textContent==='Curves & prices'&&${idle}`);
  await check('Back returns to working data and the page it left',
    `${q('#workspace-mode-label')}.textContent.startsWith('Working data')&&${q('#assistant-looking-shows')}.textContent==='Curve directory'`);

  // A move that fails once it has changed the page keeps Back (§10.1).
  await reload(`${shown('#assistant-panel')}&&document.querySelectorAll('#assistant-turns > li').length>1`);
  const curveTarget=`document.querySelectorAll('#assistant-turns > li')[1].querySelector('.assistant-target')`;
  await check('after a reload every target of the conversation waits for Go, with no Back',`[...document.querySelectorAll('.assistant-target')].every(t=>[...t.querySelectorAll('button')].map(b=>b.textContent).join()==='Go')`);
  await evaluate('window.__failEvaluate=true');await evaluate(`[...${curveTarget}.querySelectorAll('button')].find(b=>b.textContent==='Go').click()`);
  await wait(`${shown('#error')}&&${idle}&&[...${curveTarget}.querySelectorAll('button')].map(b=>b.textContent).join()==='Go,Back'`);
  await check('a Go whose page read then fails says so on the page and keeps Go and Back',`${q('#snapshot')}.value===${JSON.stringify(first)}&&${q('#page-title')}.textContent==='Curves & prices'`);
  await evaluate(`[...${curveTarget}.querySelectorAll('button')].find(b=>b.textContent==='Back').click()`);await wait(`${q('#snapshot')}.value===''&&${idle}`);
  await check('Back from it restores the As of date and what the page showed',`${q('#assistant-looking-shows')}.textContent==='Curve directory'&&${q('#error')}.hidden`);

  // Another agent starts a new conversation, and the picker preselects the last explicit choice only while it is listed (§10.1).
  await set('#assistant-agent',secondChoice);
  await check('choosing another agent starts a new conversation',`document.querySelectorAll('#assistant-turns > li').length===0&&${shown('#assistant-empty')}`);
  await reload(`${shown('#assistant-panel')}&&${q('#assistant-agent')}.value!==''`);
  await check('after a reload the last explicit choice is preselected while it is listed',`${q('#assistant-agent')}.value===${JSON.stringify(secondChoice)}`);
  seconded=await publish(seconded,['read_current_page']);
  await reload(`${shown('#assistant-panel')}&&[...${q('#assistant-agent')}.options].some(o=>o.value===${JSON.stringify(`${seconded.id}:2`)})`);
  await check('once that version is no longer listed nothing is preselected, and the composer waits for a choice',
    `${q('#assistant-agent')}.value===''&&${q('#assistant-send')}.disabled&&${q('#assistant-composer-note')}.textContent==='Choose an agent to ask.'`);

  // At 390 px the panel is a full-width sheet showing no identifiers (§13).
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:2,mobile:true});
  await click('#assistant-history-open');await wait(`document.querySelectorAll('#assistant-history-list button').length>0`);
  // Every question of this run went to one conversation with the guide.
  await evaluate(`[...document.querySelectorAll('#assistant-history-list button')].find(b=>b.textContent.includes(${JSON.stringify(guide)})).click()`);
  await wait(`document.querySelectorAll('#assistant-turns > li').length===18&&!!${latest}.querySelector('.assistant-usage')`);
  await check('at 390 px the panel covers the width as a sheet that fits it, showing the conversation without identifiers',
    `${q('#assistant-panel')}.getBoundingClientRect().width===document.documentElement.clientWidth&&${fitsWidth}`
    +`&&!/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(${q('#assistant-panel')}.innerText)&&${q('#assistant-agent')}.value===${JSON.stringify(guideChoice)}`,
    `[${q('#assistant-panel')}.getBoundingClientRect().width,document.documentElement.clientWidth,document.documentElement.scrollWidth]`);
  await shot('assistant-panel-phone');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  // A proposal prepared in history opens only on explicit Review, as an unpriced
  // editable draft under the current header. It carries no agent authoring grant.
  const curveAgent=await publish(await create('Browser curve guide '+stamp),['read_custom_curves','prepare_custom_curve','navigate'],['market_data_editor','trader']);
  await persona('editor');await click('#example');await click('#nav-custom-curves');
  await click('#assistant-launcher');await set('#assistant-agent',curveAgent.id+':1');
  await set('#date-context-on','2020-12-18');await click('#date-context-latest');await click('#apply-view-context');
  const historicalFrame=await evaluate(`[...${q('#snapshot')}.options].map(o=>o.value).find(Boolean)`);await set('#snapshot',historicalFrame);await wait(idle);
  const definition={code:'browser_agent_proposal',name:'Synthetic agent proposal',output:'out',nodes:[
   {id:'source',kind:'source',parameters:{curve:'future'},inputs:{}},
   {id:'select',kind:'select',parameters:{mode:'positions',first:'1',count:'1',binding:'context'},inputs:{source:'source'}},
   {id:'dates',kind:'dates',parameters:{mode:'context',sampling:'context_utc_time',corrections:'exact_context_frame_history_latest_visible'},inputs:{}},
   {id:'sample',kind:'sample',parameters:{},inputs:{selection:'select',dates:'dates'}},
   {id:'out',kind:'output',parameters:{},inputs:{values:'sample'}}]};
  const formula=(await side.call('custom-curves/format',{definition})).formula;
  await ask('Prepare this explicitly selected curve.');await wait(shown('#assistant-consent'));
  await script({hold:'curve-proposal',text:'Preparing an explicit draft. ',tools:[['toolu_curve_panel_01','read_custom_curves',{}],
   ['toolu_curve_panel_02','prepare_custom_curve',{code:definition.code,name:definition.name,engine:'custom_curve_recipe_v2',formula}]]},
   {tools:[['toolu_curve_panel_03','navigate',{page:null,target:'n2'}]]},{text:'Review it in the editor.'});
  await click('#assistant-allow');await wait(`${answer}==='Preparing an explicit draft. '`);
  await click('#return-working');await wait(`${q('#workspace-mode-label')}.textContent.startsWith('Working data')&&${idle}`);
  await click('#example');await click('#nav-custom-curves');const workingFrame=await evaluate(`${q('#snapshot')}.value`);
  await release('curve-proposal');await wait(`${finished}&&${buttons}==='Review proposed curve'`);
  await check('a historical proposal waits for explicit review and does not claim it will change the working header',
   `${q('#custom-curves-code')}.value===''&&${q('#snapshot')}.value===${JSON.stringify(workingFrame)}&&!${notice}.textContent.includes('Go changes the knowledge')`);
  await set('#custom-curves-code','unsaved_local_curve');await set('#custom-curves-name','Synthetic local edits');
  await press('Review proposed curve');await wait(`${buttons}==='Go anyway'`);
  await check('review protects existing unsaved curve edits until the explicit second action',`${q('#custom-curves-code')}.value==='unsaved_local_curve'`);
  await press('Go anyway');await wait(`${q('#custom-curves-code')}.value==='browser_agent_proposal'&&${idle}`);
  await check('review opens an unpriced draft with its original context note and keeps the working header editable',
   `${q('#workspace-mode-label')}.textContent.startsWith('Working data')&&${q('#snapshot')}.value===${JSON.stringify(workingFrame)}&&${q('#custom-curves-result')}.hidden`
   +`&&!${q('#custom-curves-save')}.disabled&&${q('#custom-curves-template-help')}.textContent.includes('This is an unpriced draft')`);
  await click('#custom-curves-preview');await check('the reviewed draft prices through the ordinary current-header backend operation',
   `${shown('#custom-curves-result')}&&window.__requests.some(r=>r.path==='/api/custom-curves/preview'&&r.body.snapshotId===${JSON.stringify(workingFrame)})`);
  await click('#custom-curves-save');await check('saving the reviewed draft uses the person’s ordinary authoring operation',`${q('#custom-curves-state')}.textContent.includes('Draft')&&${q('#error')}.hidden`);

}
