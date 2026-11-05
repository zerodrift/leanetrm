import {sideSession} from './side-session.mjs';

// Real page selection -> admitted report -> pinned digest, in the owned synthetic workspace fixture.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,url,provider}){
  const control=new URL(provider);
  if(control.protocol!=='http:'||control.hostname!=='127.0.0.1'||!control.port)throw Error('Use the owned synthetic provider.');
  const side=await sideSession(url);
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
  await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.__selectionRequests=[];{const send=window.fetch;window.fetch=(path,options)=>{__selectionRequests.push({path:String(path),body:options?.body?JSON.parse(options.body):null});return send(path,options);};}`});
  await command('Page.navigate',{url});await wait(idle);await persona('admin');await click('#nav-ai');await click('#ai-tab-agents');
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
  await submit('#assistant-composer');await wait("!document.querySelector('#assistant-consent').hidden");
  const answers=[{tools:[['toolu_selection_digest','read_page_digest',{}]]},{text:'Synthetic selected positions were read at the question pin.'}];
  if(!(await fetch(new URL('/respond',control),{method:'POST',body:JSON.stringify(answers)})).ok)throw Error('Synthetic provider refused the script.');
  await click('#assistant-allow');
  await wait("document.querySelector('#assistant-turns .assistant-answer')?.textContent.includes('Synthetic selected positions')&&document.querySelector('#assistant-send').textContent==='Send'");
  await check('the admitted ask sends exactly the three trade ids and no browser cell evidence',"(()=>{const request=__selectionRequests.filter(r=>r.path.endsWith('/assistant/turns/ask')).at(-1).body;return JSON.stringify(request.pageReport.focus.tradeIds)===JSON.stringify(__selectedTradeIds)&&request.pageReport.focus.pivotCell===null&&!JSON.stringify(request).includes('918273');})()");
  await check('the server digest completes without a selection refusal',"document.querySelector('#assistant-turns .assistant-steps').textContent.includes('Read page digest')&&!document.querySelector('#assistant-turns .assistant-steps').textContent.includes('refused')");
  await shot('assistant-selection');
  const selected=await evaluate('__selectedTradeIds');
  const stopPlan=[{kind:'pause',page:'blotter'}];
  const stopAnswers=[{tools:[['toolu_stop_digest','read_page_digest',{}],['toolu_stop_plan','present_walkthrough',{planJson:JSON.stringify(stopPlan)}]]},
    {hold:'walkthrough_later_plan',tools:[['toolu_later_plan','present_walkthrough',{planJson:JSON.stringify(stopPlan)}]]},{text:'Synthetic stopped walkthrough.'}];
  if(!(await fetch(new URL('/respond',control),{method:'POST',body:JSON.stringify(stopAnswers)})).ok)throw Error('Synthetic provider refused the stop script.');
  await set('#assistant-question','Show this selection and pause.');await submit('#assistant-composer');
  await wait("!document.querySelector('.assistant-walkthrough-continue').hidden");
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
  const script=async answers=>{if(!(await fetch(new URL('/respond',control),{method:'POST',body:JSON.stringify(answers)})).ok)throw Error('Synthetic provider refused the script.');};
  await set('#assistant-question','Fill unsaved futures ticket\nSet ticket.lots = "2"\nSet ticket.executionPrice = "3.4"');
  await script([{tools:[['toolu_walk_digest','read_page_digest',{}],['toolu_walk_plan','present_walkthrough',{planJson:JSON.stringify(plan)}]]},{text:'Synthetic walkthrough presented.'}]);
  await submit('#assistant-composer');
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
}
