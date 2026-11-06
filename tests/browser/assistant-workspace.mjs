import {sideSession} from './side-session.mjs';
import {press} from './blotter-window.mjs';
import {openPhysical} from './physical-workspace.mjs';

// Real page selection -> admitted report -> pinned digest, in the owned synthetic workspace fixture.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,url,provider}){
  const sendQuestion=async()=>{await wait("!document.querySelector('#assistant-send').disabled&&document.querySelector('#assistant-send').textContent==='Send'&&"+idle);await submit('#assistant-composer');};
  const control=new URL(provider);
  if(control.protocol!=='http:'||control.hostname!=='127.0.0.1'||!control.port)throw Error('Use the owned synthetic provider.');
  const script=async answers=>{if(!(await fetch(new URL('/respond',control),{method:'POST',body:JSON.stringify(answers)})).ok)throw Error('Synthetic provider refused the script.');};
  const side=await sideSession(url);
  const reportPages=(await side.call('settings/ai/page-prompts')).pages.map(page=>page.id).sort();
  const agent=(await side.call('settings/ai/agents')).agents.find(a=>a.name.includes('workspace operations'));
  if(!agent)throw Error('The workspace fixture must publish its explicitly enabled guide.');
  await side.call('profile',{profile:'trader'});
  const navigation=await side.call('workspace/navigation');
  const frame=navigation.frames.find(f=>navigation.initialFrameIds.includes(f.id)&&f.at.startsWith('2020-12-18'));
  const context={snapshotId:frame.id,convention:'contract_lot_execution_pnl_v2',knowledgeMode:'current'};
  const catalog=await side.call('futures/catalog',context),contract=catalog.contracts.find(c=>c.curve==='future');
  await side.call('futures/book',{...context,operationId:crypto.randomUUID(),bookId:catalog.books[0].id,curve:'future',contractId:contract.id,lots:'3',executionPrice:'3.3',
    legalEntityId:'d4000000-0000-0000-0000-000000000001',legalEntityRevision:1,counterpartyId:'d5000000-0000-0000-0000-000000000001',counterpartyRevision:1,memo:'Synthetic selection browser fixture'});
  await command('Page.enable');await command('Runtime.enable');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.addScriptToEvaluateOnNewDocument',{source:`
    window.__selectionRequests=[];window.__selectionChangedHeads=window.__selectionWaits=0;
    {const send=window.fetch;window.fetch=async(path,options)=>{
      __selectionRequests.push({path:String(path),body:options?.body?JSON.parse(options.body):null});
      if(path==='/api/trading/changes')__selectionWaits++;
      const response=await send(path,options);
      if(path==='/api/swaps/catalog'&&window.__denySwapOpen)return new Response(JSON.stringify({...await response.json(),canBook:false,bookingUnavailableReason:'Synthetic read-only ticket'}),{status:200,headers:{'content-type':'application/json'}});
      if(path==='/api/trading/changes'){__selectionWaits--;if(response.ok&&(await response.clone().json()).version!==JSON.parse(options.body).version)__selectionChangedHeads++;}
      if(path==='/api/assistant/grants/grant'&&window.__holdConsentGrant)await new Promise(resolve=>window.__releaseConsentGrant=resolve);
      if(path==='/api/assistant/agents'&&window.__holdConsentAgents)await new Promise(resolve=>window.__releaseConsentAgents=resolve);
      if(path==='/api/trading/workspace'&&window.__holdConsentPricing)await new Promise(resolve=>window.__releaseConsentPricing=resolve);
      if(path==='/api/swaps/catalog'&&window.__holdSwapOpen)await new Promise(resolve=>window.__releaseSwapOpen=resolve);
      return response;
    };}`});
  await command('Page.navigate',{url});await wait(idle);await persona('admin');await click('#nav-ai');await click('#ai-tab-agents');
  await check('native page reports, labels and server prompt admission have the same catalog',`(()=>{return import('/assistant-pages.js').then(({pageReport,reportedPages})=>{
    const base={page:'futures',risk:false,trade:null,entry:null,lens:'values',view:{mode:'trades',bookIds:[],portfolioIds:[],groupBy:[],measures:[]},pane:{details:false,charts:false},poppedOut:false};
    const states=[{page:'market',curve:null,tab:'prices'},{page:'power',frame:true},{page:'configuration'},{page:'schedules'},
      {page:'reference',table:'units',search:''},{page:'parties',kind:'counterparty'},{page:'jobs',job:null},{page:'ppa',trade:null,book:null},{page:'custom-curves',curve:null,unsaved:false},
      base,{...base,risk:true},{...base,lens:'risk'},...[null,'futures','options','swaps','ppa','gas','crude','lng'].map(product=>({...base,entry:{product}})),
      ...['facilities','risk','dashboard','crude-setup','gas-setup','gas','crude','delivery','interval-series','interval-curves','vol-surfaces','admin','ai'].map(page=>({page}))];
    const actual=[...new Set(states.map(state=>pageReport(state).page))].sort();return JSON.stringify(actual)===${JSON.stringify(JSON.stringify(reportPages))}&&JSON.stringify(actual)===JSON.stringify([...reportedPages].sort());});})()`);
  await check('an administrator can explicitly configure both new workspace tools',"['read_page_digest','present_walkthrough'].every(code=>[...document.querySelectorAll('#ai-agent-tools input')].some(i=>i.value===code))&&document.querySelector('#error').hidden");
  await persona('trader');await click('#nav-futures');await wait(idle);
  await wait("document.querySelectorAll('#desk-positions .tabulator-row[data-row-key]').length>=3");
  await evaluate(`(()=>{
    const rows=[...document.querySelectorAll('#desk-positions .tabulator-row[data-row-key]')].slice(0,3);
    window.__selectedTradeIds=rows.map(row=>row.dataset.rowKey.split(':')[0]);
    rows[0].querySelector('.tabulator-cell').textContent='Synthetic cell evidence 918273';
    for(const row of rows)row.dispatchEvent(new MouseEvent('click',{bubbles:true,ctrlKey:true}));
    rows[0].dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,button:2,clientX:300,clientY:300}));
  })()`);
  await wait("document.querySelectorAll('#desk-positions .tabulator-row[aria-selected=true]').length===3");
  await check('three rows are visibly selected and offer an in-place ask',"document.querySelectorAll('#desk-positions .tabulator-row[aria-selected=true]').length===3&&[...document.querySelectorAll('.blotter-row-menu button')].some(b=>b.textContent==='Ask about these positions')");
  await evaluate("[...document.querySelectorAll('.blotter-row-menu button')].find(b=>b.textContent==='Ask about these positions').click()");
  await wait("!document.querySelector('#assistant-panel').hidden");
  await wait(`[...document.querySelector('#assistant-agent').options].some(o=>o.value===${JSON.stringify(agent.id+':'+agent.publishedVersion)})`);
  await set('#assistant-agent',agent.id+':'+agent.publishedVersion);
  await check('Looking at states the selection and the server digest',"document.querySelector('#assistant-looking-shows').textContent.includes('3 selected positions')&&document.querySelector('#assistant-looking-shows').textContent.includes('server will read a page digest')");
  await check('the prefilled ask remains editable and unsent',"document.querySelector('#assistant-question').value.includes('selected positions')&&!__selectionRequests.some(r=>r.path.endsWith('/assistant/turns/ask'))");
  await sendQuestion();await wait("!document.querySelector('#assistant-consent').hidden");
  const answers=[{tools:[['toolu_selection_digest','read_page_digest',{mode:'overview'}]]},{text:'Synthetic selected positions were read at the question pin.'}];
  if(!(await fetch(new URL('/respond',control),{method:'POST',body:JSON.stringify(answers)})).ok)throw Error('Synthetic provider refused the script.');
  await wait('__selectionWaits>0');
  const consentPrices=await evaluate("__selectionRequests.filter(r=>r.path==='/api/trading/workspace').length"),changedHeads=await evaluate('__selectionChangedHeads');
  await evaluate("window.__holdConsentGrant=window.__holdConsentPricing=true;document.querySelector('#assistant-allow').click()");
  await wait("typeof window.__releaseConsentGrant==='function'&&__selectionChangedHeads>"+changedHeads);
  await evaluate('new Promise(resolve=>setTimeout(resolve,600))');
  await check('a grant publication waits behind the admitted assistant consent action',`__selectionRequests.filter(r=>r.path==='/api/trading/workspace').length===${consentPrices}&&document.body.getAttribute('aria-busy')==='false'`);
  await evaluate('window.__holdConsentAgents=true;window.__releaseConsentGrant()');
  await wait("typeof window.__releaseConsentAgents==='function'");await evaluate('new Promise(resolve=>setTimeout(resolve,600))');
  await check('consent keeps its admission gate through the agent directory refresh',`__selectionRequests.filter(r=>r.path==='/api/trading/workspace').length===${consentPrices}&&document.body.getAttribute('aria-busy')==='false'`);
  await evaluate('window.__holdConsentGrant=window.__holdConsentAgents=window.__holdConsentPricing=false;window.__releaseConsentAgents();window.__releaseConsentPricing?.()');
  await wait("document.querySelector('#assistant-turns .assistant-answer')?.textContent.includes('Synthetic selected positions')&&document.querySelector('#assistant-send').textContent==='Send'");
  await check('the admitted ask sends exactly the three trade ids and no browser cell evidence',"(()=>{const request=__selectionRequests.filter(r=>r.path.endsWith('/assistant/turns/ask')).at(-1).body;return JSON.stringify(request.pageReport.focus.tradeIds)===JSON.stringify(__selectedTradeIds)&&request.pageReport.focus.pivotCell===null&&!JSON.stringify(request).includes('918273');})()");
  await check('the server digest completes without a selection refusal',"document.querySelector('#assistant-turns .assistant-steps').textContent.includes('Read page digest')&&!document.querySelector('#assistant-turns .assistant-steps').textContent.includes('refused')");
  await shot('assistant-selection');
  const selected=await evaluate('__selectedTradeIds');
  const swapPlan=[{kind:'go',target:'n4'},{kind:'pause',page:'blotter_new_swap'}];
  const openSwap=async(walkthrough,tag)=>{
    const fast=walkthrough&&tag!=='unsaved_walkthrough';
    if(await evaluate("!!document.querySelector('#swap-form')?.getBoundingClientRect().height&&!!document.querySelector('#details-collapse')?.getClientRects().length")){await click('#details-collapse');await wait("!document.querySelector('#swap-form')?.getBoundingClientRect().height");}
    await script([...(tag==='navigation'?[{tools:[['toolu_full_catalog','discover_page_operations',{page:'all',operation:null}]]}]:[]),
      {tools:[[ 'toolu_'+tag+'_read',fast?'read_page_digest':'read_workspace_page',fast?{mode:'controls'}:{page:'blotter_new_swap'}]]},
      {tools:[[ 'toolu_'+tag+'_open',walkthrough?'present_walkthrough':'navigate',walkthrough?{planJson:JSON.stringify(fast?swapPlan:[{kind:'go',target:'n1'},{kind:'pause',page:'blotter_new_swap'}])}:{page:'blotter_new_swap',target:null}]]},
      {text:'Synthetic swap ticket '+tag+'.'}]);
    await evaluate('window.__entryRequestStart=__selectionRequests.length');await set('#assistant-question','Help me open a swap ticket.');await sendQuestion();
  };
  await openSwap(false,'navigation');
  await wait("document.querySelector('#swap-form')?.getBoundingClientRect().height>0");
  await check('the assistant New swap navigation target opens the native swap ticket',"document.querySelector('#assistant-looking-shows').textContent.includes('new swap')&&document.querySelector('#swap-quantity').value===''&&document.querySelector('#swap-price').value===''");
  await wait("document.querySelector('#assistant-turns').textContent.includes('Synthetic swap ticket navigation.')&&document.querySelector('#assistant-send').textContent==='Send'");
  await check('full operation discovery completes before native navigation without a timeout',"document.querySelector('#assistant-error').hidden&&[...document.querySelectorAll('#assistant-turns .assistant-steps')].at(-1).textContent.includes('Inspect available operations')");
  await wait("document.querySelector('#assistant-send').textContent==='Send'&&"+idle);await click('#nav-futures');await wait(idle);
  await evaluate('window.__denySwapOpen=true');await openSwap(false,'readonly');
  await wait("!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('Synthetic read-only ticket')");
  await check('native booking refusal leaves swap fields closed without choosing a different context',"!document.querySelector('#swap-form')?.getBoundingClientRect().height&&__selectionRequests.filter(r=>r.path.endsWith('/assistant/turns/ask')).at(-1).body.knowledge.mode==='current'");
  await evaluate('window.__denySwapOpen=false');await wait("document.querySelector('#assistant-send').textContent==='Send'&&"+idle);await click('#nav-futures');await wait(idle);
  await openSwap(true,'walkthrough');
  await wait("document.querySelector('#swap-form')?.getBoundingClientRect().height>0&&!document.querySelector('.assistant-walkthrough-continue').hidden");
  await check('the swap guide points inside a visible native ticket control',`(()=>{const c=document.querySelector('.assistant-walkthrough-cursor'),r=c.getBoundingClientRect(),target=document.elementFromPoint(r.left,r.top);return !c.hidden&&!!target?.closest('#swap-form')&&target.matches('input,select,button,textarea');})()`);
  await check('native swap entry needs no portfolio valuation or broad operation discovery',"!__selectionRequests.slice(__entryRequestStart).some(r=>r.path.endsWith('/trading/workspace')||r.path.includes('/trading/workspace/'))&&![...document.querySelectorAll('#assistant-turns .assistant-steps')].at(-1).textContent.includes('Inspect available operations')");
  await check('an issued New swap go opens the ticket and pauses without pricing or booking',"!__selectionRequests.some(r=>['/swaps/price','/swaps/preview','/swaps/draft-risk','/swaps/book','/assistant/actions/authorize'].some(path=>r.path.endsWith(path)))");
  await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
  await wait("document.querySelector('#assistant-send').textContent==='Send'&&"+idle);await click('#nav-futures');await wait(idle);
  await check('Escape stops the walkthrough without closing its inline assistant panel',"!document.querySelector('#assistant-panel').hidden");
  await evaluate('window.__holdSwapOpen=true');await openSwap(true,'stopped');
  await wait("typeof window.__releaseSwapOpen==='function'").catch(async error=>{throw Error(error.message+' · '+JSON.stringify(await evaluate("({status:document.querySelector('.assistant-walkthrough-status').textContent,report:__selectionRequests.filter(r=>r.path.endsWith('/assistant/turns/ask')).at(-1).body.pageReport,steps:[...document.querySelectorAll('#assistant-turns .assistant-steps')].at(-1).textContent})")));});
  await check('walkthrough Stop stays enabled during a native page read',"!document.querySelector('.assistant-walkthrough button:last-child').disabled");
  await press({command,evaluate},'.assistant-walkthrough button:last-child');
  await evaluate('window.__holdSwapOpen=false;window.__releaseSwapOpen()');await wait(idle);
  await check('Stop prevents a delayed swap catalog from opening a ticket afterward',"!document.querySelector('#swap-form')?.getBoundingClientRect().height&&document.querySelector('.assistant-walkthrough-status').textContent.startsWith('Stopped')&&document.querySelector('#error').hidden");
  await wait(idle);
  // A presentation refresh can fail before the requested work starts. It must not retain
  // the global busy flag, which would leave every subsequent native action disabled.
  await evaluate(`(()=>{const control=document.querySelector('#profile-open'),disabled=Object.getOwnPropertyDescriptor(HTMLButtonElement.prototype,'disabled');
    Object.defineProperty(control,'disabled',{configurable:true,get(){return disabled.get.call(this);},set(value){if(!value){disabled.set.call(this,value);return;}delete this.disabled;window.__controlRefreshThrown=true;throw Error('Synthetic control refresh failure');}});})()`);
  await evaluate("document.querySelector('#nav-dashboard').click();new Promise(resolve=>setTimeout(resolve,100))");
  await check('a control refresh failure releases busy state and reports its reason',"window.__controlRefreshThrown===true&&document.body.getAttribute('aria-busy')==='false'&&!document.querySelector('#nav-dashboard').disabled&&!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.includes('Synthetic control refresh failure')","({thrown:window.__controlRefreshThrown,busy:document.body.getAttribute('aria-busy'),disabled:document.querySelector('#nav-dashboard').disabled,error:document.querySelector('#error').textContent,prompt:document.querySelector('#position-details-prompt').textContent})");
  await click('#nav-dashboard');await wait(idle);
  await check('native navigation remains usable after a failed control refresh',"!document.querySelector('#dashboard-page').hidden&&document.querySelector('#error').hidden");
  await click('#nav-futures');await wait(idle);
  await evaluate(`(()=>{for(const row of document.querySelectorAll('#desk-positions .tabulator-row[data-row-key]')){
    const wanted=__selectedTradeIds.includes(row.dataset.rowKey.split(':')[0]);if(wanted!==(row.getAttribute('aria-selected')==='true'))row.dispatchEvent(new MouseEvent('click',{bubbles:true,ctrlKey:true}));}})()`);
  await evaluate("document.querySelector('#blotter-reset-layout').click()");await wait(idle);
  await check('the default layout collapses Details before ordinary page guidance',"document.querySelector('#position-details').getBoundingClientRect().height===0");
  await script([{tools:[['toolu_collapsed_digest','read_page_digest',{mode:'overview'}],['toolu_collapsed_plan','present_walkthrough',{planJson:JSON.stringify([{kind:'go',target:'n1'},{kind:'pause',page:'blotter'}])}]]},{text:'Synthetic ordinary page guidance.'}]);
  await set('#assistant-question','Show this page and pause.');await sendQuestion();await wait("!document.querySelector('.assistant-walkthrough-continue').hidden");
  await check('ordinary Blotter guidance points at a visible page anchor with Details collapsed',"!document.querySelector('.assistant-walkthrough-cursor').hidden&&['desk-positions','page-title'].includes(document.querySelector('.assistant-walkthrough-target')?.id)&&document.querySelector('#position-details').getBoundingClientRect().height===0","({cursor:document.querySelector('.assistant-walkthrough-cursor').outerHTML,target:document.querySelector('.assistant-walkthrough-target')?.id,details:document.querySelector('#position-details').getBoundingClientRect().toJSON(),status:document.querySelector('.assistant-walkthrough-status').textContent,looking:document.querySelector('#assistant-looking-shows').textContent})");
  await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});await wait("document.querySelector('#assistant-send').textContent==='Send'");
  await click('#blotter-dock-grid');await wait("document.querySelector('#desk-positions').getBoundingClientRect().height>0&&"+idle);
  const stopPlan=[{kind:'pause',page:'blotter'}];
  const stopAnswers=[{tools:[['toolu_stop_digest','read_page_digest',{mode:'overview'}],['toolu_stop_plan','present_walkthrough',{planJson:JSON.stringify(stopPlan)}]]},
    {hold:'walkthrough_later_plan',tools:[['toolu_later_plan','present_walkthrough',{planJson:JSON.stringify(stopPlan)}]]},{text:'Synthetic stopped walkthrough.'}];
  if(!(await fetch(new URL('/respond',control),{method:'POST',body:JSON.stringify(stopAnswers)})).ok)throw Error('Synthetic provider refused the stop script.');
  await set('#assistant-question','Show this selection and pause.');await sendQuestion();
  await wait("!document.querySelector('.assistant-walkthrough-continue').hidden").catch(async error=>{throw Error(error.message+' · '+JSON.stringify(await evaluate("({turns:document.querySelector('#assistant-turns').textContent,error:document.querySelector('#assistant-error').textContent,busy:document.body.getAttribute('aria-busy')})")));});
  await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
  await fetch(new URL('/release/walkthrough_later_plan',control),{method:'POST'});
  await wait("document.querySelector('#assistant-turns').textContent.includes('Synthetic stopped walkthrough.')&&document.querySelector('#assistant-send').textContent==='Send'");
  await check('Stop prevents a later plan in the same ask from automatically restarting',"document.querySelector('.assistant-walkthrough-continue').hidden&&document.querySelector('.assistant-walkthrough-status').textContent.startsWith('Stopped')");
  const plan=[{kind:'go',target:'n1'},
    {kind:'sort',page:'blotter',column:'presentValue',direction:'desc'},
    {kind:'filter',page:'blotter',field:'query',value:''},
    {kind:'select',page:'blotter',focus:{tradeIds:selected,pivotCell:null}},
    {kind:'open',page:'blotter',pane:'details',target:'n2',proposalStep:null},
    {kind:'scroll_to',page:'blotter',widget:'details'},
    {kind:'pause',page:'blotter'},
    {kind:'open',page:'blotter',pane:'new_future',target:null,proposalStep:null},
    {kind:'fill',page:'blotter',field:'ticket.lots',value:'2'},
    {kind:'fill',page:'blotter',field:'ticket.executionPrice',value:'3.4'},
    {kind:'say',page:'blotter',message:'fill'},{kind:'pause',page:'blotter'}];
  await set('#assistant-question','Fill unsaved futures ticket\nSet ticket.lots = "2"\nSet ticket.executionPrice = "3.4"');
  await script([{tools:[['toolu_walk_digest','read_page_digest',{mode:'overview'}],['toolu_walk_plan','present_walkthrough',{planJson:JSON.stringify(plan)}]]},{text:'Synthetic walkthrough presented.'}]);
  await sendQuestion();
  try{await wait("!document.querySelector('.assistant-walkthrough-continue').hidden");}catch(error){throw Error(error.message+' · '+JSON.stringify(await evaluate("({walk:document.querySelector('.assistant-walkthrough-status').textContent,turns:document.querySelector('#assistant-turns').textContent,status:document.querySelector('#assistant-status')?.textContent})")));}
  await check('the live walkthrough visibly changes only presentation and pauses',"document.querySelector('.assistant-walkthrough-label').textContent.includes('workspace operations')&&document.querySelector('#assistant-looking-shows').textContent.includes('3 selected positions')&&!__selectionRequests.some(r=>r.path.endsWith('/futures/book'))","({label:document.querySelector('.assistant-walkthrough-label').textContent,looking:document.querySelector('#assistant-looking-shows').textContent,rows:document.querySelectorAll('#desk-positions .tabulator-row[aria-selected=true]').length,walk:document.querySelector('.assistant-walkthrough-status').textContent})");
  await click('.assistant-walkthrough-continue');
  await wait("document.querySelector('#futures-lots').value==='2'&&document.querySelector('#futures-execution').value==='3.4'");
  await wait("!document.querySelector('.assistant-walkthrough-continue').hidden");
  await check('ticket fills remain unsaved and choose no unstated book or contract',"document.querySelector('#futures-book').value===''&&document.querySelector('#futures-contract').value===''&&!__selectionRequests.some(r=>r.path.endsWith('/futures/book')||r.path.endsWith('/assistant/actions/authorize'))");
  await check('the narration remains visible while paused',"document.querySelector('.assistant-walkthrough-status').textContent.includes('These terms remain unsaved')");
  await check('the filled control and labelled assistant cursor are visible',"(()=>{const field=document.querySelector('#futures-execution').getBoundingClientRect(),cursor=document.querySelector('.assistant-walkthrough-cursor');return field.top>=0&&field.bottom<=innerHeight&&!cursor.hidden&&cursor.getBoundingClientRect().top<innerHeight;})()");
  await shot('assistant-walkthrough');
  await command('Input.dispatchKeyEvent',{type:'keyDown',key:'Escape',code:'Escape',windowsVirtualKeyCode:27});
  await check('Escape stops at the current step without submitting',"document.querySelector('.assistant-walkthrough-status').textContent.startsWith('Stopped')&&!__selectionRequests.some(r=>r.path.endsWith('/futures/book'))");
  await shot('assistant-walkthrough-stopped');
  await openSwap(true,'unsaved_walkthrough');
  await wait("document.querySelector('#assistant-turns').textContent.includes('Synthetic swap ticket unsaved_walkthrough.')&&document.querySelector('#assistant-send').textContent==='Send'");
  await evaluate("[...document.querySelectorAll('#assistant-turns [data-assistant-walkthrough-control]')].at(-1).click()");
  await check('a walkthrough cannot discard the unsaved ticket while confirmed navigation remains available',"document.querySelector('#assistant-status').textContent.includes('Finish your unsaved changes')&&document.querySelector('#futures-lots').value==='2'&&document.querySelector('#futures-execution').value==='3.4'&&!document.querySelector('#swap-form')?.getBoundingClientRect().height&&document.querySelector('.assistant-walkthrough-continue').hidden");
  await openSwap(false,'confirmed_navigation');
  await wait("[...document.querySelectorAll('#assistant-turns .assistant-target button')].some(button=>button.textContent==='Go anyway')");
  await check('an entry navigation target keeps unsaved terms until the person confirms',"document.querySelector('#futures-lots').value==='2'&&document.querySelector('#futures-execution').value==='3.4'&&document.querySelector('#futures-new-trade').getBoundingClientRect().height>0");
  await evaluate("[...document.querySelectorAll('#assistant-turns .assistant-target button')].filter(button=>button.textContent==='Go anyway').at(-1).click()");await wait(idle);
  await check('confirmed Go opens the native swap ticket with no carried financial terms',"document.querySelector('#swap-form')?.getBoundingClientRect().height>0&&document.querySelector('#swap-quantity').value===''&&document.querySelector('#swap-price').value===''&&document.querySelector('#error').hidden");
  for(const [navigation,page] of [['dashboard','dashboard'],['crude-setup','crude_setup'],['vol-surfaces','vol_surfaces']]){
    if(navigation==='crude-setup')await openPhysical({click,evaluate,wait},navigation);else await click('#nav-'+navigation);await wait(idle);
    await evaluate(`(()=>{const value=document.createElement('p');value.textContent='Synthetic widget evidence 918273';document.querySelector('#'+${JSON.stringify(navigation)}+'-page').append(value);})()`);
    await script([{tools:[['toolu_identity_'+page,'read_page_digest',{mode:'overview'}],['toolu_unsupported_'+page,'read_current_page',{}]]},{text:'Synthetic page identity '+page+'.'}]);
    await set('#assistant-question','Identify this page.');await sendQuestion();
    await wait(`document.querySelector('#assistant-turns').textContent.includes(${JSON.stringify('Synthetic page identity '+page+'.')})&&document.querySelector('#assistant-send').textContent==='Send'`);
    await check(page+' admits its native empty report without browser widget values',`(()=>{const request=__selectionRequests.filter(r=>r.path.endsWith('/assistant/turns/ask')).at(-1).body;return JSON.stringify(request.pageReport)===JSON.stringify({page:${JSON.stringify(page)},selection:{}})&&!JSON.stringify(request).includes('918273')&&document.querySelector('#assistant-error').hidden;})()`);
  }
  await wait("document.querySelector('#assistant-send').textContent==='Send'&&"+idle);
  // Reuse the native ordinary entry navigation, then present explicitly stated unsaved swap terms.
  await openSwap(false,'swap_fill_entry');await wait("document.querySelector('#swap-form')?.getBoundingClientRect().height>0&&document.querySelector('#assistant-send').textContent==='Send'");
  const swapTerms=[['swap.structure','financial_fixed_floating'],['swap.priceMode','agreed'],['swap.quantity','1000'],['swap.fixedPrice','3.4'],['swap.fixedDirection','-1'],['swap.memo','Synthetic unsaved terms']];
  const fillSwapPlan=swapTerms.map(([field,value])=>({kind:'fill',page:'blotter_new_swap',field,value}));fillSwapPlan.push({kind:'pause',page:'blotter_new_swap'});
  await script([{tools:[['toolu_swap_fill_controls','read_page_digest',{mode:'controls'}],['toolu_swap_fill_plan','present_walkthrough',{planJson:JSON.stringify(fillSwapPlan)}]]},{text:'Synthetic unsaved swap terms presented.'}]);
  await set('#assistant-question','Fill unsaved swap ticket'+swapTerms.map(([field,value])=>'\nSet '+field+' = '+JSON.stringify(value)).join(''));
  await evaluate('window.__swapFillStart=__selectionRequests.length');await sendQuestion();
  await wait("document.querySelector('#swap-memo').value==='Synthetic unsaved terms'&&!document.querySelector('.assistant-walkthrough-continue').hidden").catch(async error=>{throw Error(error.message+' · '+JSON.stringify(await evaluate("({status:document.querySelector('.assistant-walkthrough-status').textContent,announce:document.querySelector('#assistant-status').textContent,error:document.querySelector('#assistant-error').textContent,steps:[...document.querySelectorAll('#assistant-turns .assistant-steps')].at(-1).textContent,report:__selectionRequests.filter(r=>r.path.endsWith('/assistant/turns/ask')).at(-1).body.pageReport,fields:['swap-type','swap-quantity','swap-price-mode','swap-price','swap-memo'].map(id=>{const c=document.getElementById(id);return {id,value:c.value,disabled:c.disabled,box:c.getBoundingClientRect().toJSON(),hit:(()=>{const b=c.getBoundingClientRect(),h=document.elementFromPoint(b.left+16,b.top+16);return h?{id:h.id,tag:h.tagName,classes:h.className}:null;})()};})})")));});
  await check('typed swap fills operate native controls and leave every unstated term unselected',"document.querySelector('#swap-type').value==='financial_fixed_floating'&&document.querySelector('#swap-quantity').value==='1000'&&document.querySelector('#swap-price').value==='3.4'&&document.querySelector('#swap-direction').value==='-1'&&document.querySelector('#swap-book').value===''&&document.querySelector('#swap-commodity').value===''&&document.querySelector('#swap-discounting').value===''");
  await check('native swap fills never preview, book, save or authorize',"!__selectionRequests.slice(__swapFillStart).some(r=>['/swaps/price','/swaps/book','/assistant/actions/authorize'].some(path=>r.path.endsWith(path)))&&document.querySelector('#swap-submit').disabled&&document.querySelectorAll('aside.assistant-walkthrough').length===0");
  await shot('assistant-swap-fill');
  await click('#assistant-close');
  await check('closing the assistant stops its cursor and leaves the unsaved swap for the person',"document.querySelector('.assistant-walkthrough-cursor').hidden&&document.querySelector('.assistant-walkthrough-continue').hidden&&document.querySelector('#swap-price').value==='3.4'");

}
