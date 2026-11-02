import {setupWorkspaceProbe} from './workspace-probe.mjs';
import {chooseFutures} from './futures-selection.mjs';
import {newTrade} from './new-trade.mjs';
import {setupTradingReplies} from './trading-responses.mjs';
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,url}){
  await command('Page.enable');await command('Runtime.enable');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  // A second market example on offer, whose address differs from the page's by a fragment alone: choosing it is a page load that, here, leaves the
  // page itself in place, so that what it asked before it, and what it did, can be read from the address.
  await command('Page.addScriptToEvaluateOnNewDocument',{source:`(()=>{const send=window.fetch;window.fetch=async(input,init)=>{const path=typeof input==='string'?input:input.url;const response=await send(input,init);if(path==='/api/examples'&&response.ok){const data=await response.json();return new Response(JSON.stringify([...data,{id:'second',name:'Second example',url:'/#second-example'}]),{status:200,headers:{'Content-Type':'application/json'}});}return response;};})()`});
  await command('Page.navigate',{url});await wait(idle);
  await setupTradingReplies(evaluate);
  await setupWorkspaceProbe(evaluate);
  await evaluate(`(()=>{const send=window.fetch;window.__editorReplies=[];window.__amendCommands=[];window.fetch=window.__workspaceProbe(send,async(input,init,send)=>{
    const path=typeof input==='string'?input:input.url;
    if(path==='/api/futures/editor'&&window.__failEditor){window.__failEditor=false;return new Response('Synthetic outage',{status:503});}
    if(path==='/api/futures/editor'&&window.__denyEditor){window.__denyEditor=false;return new Response(JSON.stringify({code:'access_denied'}),{status:403});}
    if(path==='/api/futures/amend'&&window.__refuseAmend){window.__refuseAmend=false;window.__refusedAmend=JSON.parse(init.body);return new Response(JSON.stringify({code:'futures_revision_conflict',message:'Synthetic refusal'}),{status:409,headers:{'Content-Type':'application/json'}});}
    if(path==='/api/futures/amend')window.__amendCommands.push(JSON.parse(init.body));
    let response=await send(input,init);
    if((path==='/api/trading/workspace'||path==='/api/trading/blotter')&&response.ok){
      const data=await response.json(),report=path==='/api/trading/workspace'?data.blotter:data;
      const booked=window.__editorReplies.filter(r=>r.path==='/api/futures/book').at(-1)?.data.tradeId;
      const other=report.holdings.find(row=>row.tradeId!==booked);if(other)other.tradeReference='TR-990037';
      response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});
    }
    if(path==='/api/trading/positions'&&window.__revisionSort&&response.ok){const data=await response.json();if(JSON.parse(init.body).offset===0)window.__revisionSortBinding=data.positions[0].bindingId;data.positions=data.positions.map(h=>({...h,revision:h.bindingId===window.__revisionSortBinding?10:2}));response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}
    if((path==='/api/trading/workspace'||path==='/api/trading/blotter')&&window.__revisionSort&&response.ok){const data=await response.json(),report=path==='/api/trading/workspace'?data.blotter:data;report.holdings=report.holdings.map(h=>({...h,revision:h.bindingId===window.__revisionSortBinding?'10':'2'}));response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}
    if((path==='/api/futures/editor'&&window.__holdEditor)||(path==='/api/futures/amend-preview'&&window.__holdPreview)){
      window.__holdEditor=false;window.__holdPreview=false;await new Promise(resolve=>window.__releaseEditor=resolve);window.__releaseEditor=null;
    }
    try{window.__editorReplies.push({path,data:await response.clone().json()});}catch{}
    if(path==='/api/futures/amend'&&window.__loseAmend){window.__loseAmend=false;return new Response('incomplete',{status:502});}return response;
  });})()`);
  await persona('trader');
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");
  await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
  await check('Power TOUs and Reference data are in Settings',"document.querySelector('#nav-group-settings #nav-power')&&document.querySelector('#nav-group-settings #nav-reference')&&!document.querySelector('#nav-group-markets #nav-reference')");
  await click('#nav-power');await check('Power TOUs header identifies Settings',"document.querySelector('#page-section').textContent==='SETTINGS'");await click('#nav-reference');await check('Reference data header identifies Settings',"document.querySelector('#page-section').textContent==='SETTINGS'");await click('#nav-futures');
  await check('visible product branding is LeanETRM',"document.title.includes('LeanETRM')&&document.querySelector('.brand').textContent.includes('LeanETRM')");
  await check('working knowledge and actual today are explicit beside selected price date',"document.querySelector('#workspace-mode-label').textContent.includes('Working data')&&document.querySelector('#workspace-today').textContent.includes('Today:')&&document.querySelector('#workspace-today').textContent.includes('earlier market date')&&!document.querySelector('#futures-new').disabled");
  await newTrade({click,wait},'futures');await chooseFutures({evaluate,set,click});await evaluate("document.querySelector('#futures-book').selectedIndex=1;");
  await set('#futures-lots','37');await set('#futures-execution','2.9');await evaluate("for(const id of ['futures-legal-entity','futures-counterparty']){const s=document.getElementById(id);s.selectedIndex=1;s.dispatchEvent(new Event('input',{bubbles:true}));}");await submit('#futures-form');
  const bookedTrade=await evaluate("window.__editorReplies.filter(r=>r.path==='/api/futures/book').at(-1).data.tradeId");
  const tradeReference=await evaluate(`window.__tradingReports(window.__editorReplies).at(-1).data.holdings.find(row=>row.tradeId===${JSON.stringify(bookedTrade)}).tradeReference`);
  await check('row targeting distinguishes the booked quantity from a competing reference containing 37',`(()=>{const rows=window.__tradingReports(window.__editorReplies).at(-1).data.holdings;return rows.some(row=>row.tradeId!==${JSON.stringify(bookedTrade)}&&row.tradeReference==='TR-990037')&&rows.find(row=>row.tradeId===${JSON.stringify(bookedTrade)}).lots==='37';})()`);
  async function open(revision=1){
    await set('#blotter-filters .table-search',tradeReference);
    await wait(`(()=>{const rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(row=>row.getClientRects().length);return ${idle}&&rows.length===1&&rows[0].querySelector('.desk-reference')?.textContent===${JSON.stringify(tradeReference)};})()`);
    // A row opens its position in the details; Amend puts the trade's form in its Terms tab, in place of the read view.
    await click('#desk-positions .tabulator-row:not(.tabulator-group)');
    await wait(`document.querySelector('#position-details')?.hidden===false&&document.querySelector('#position-details-head .side-panel-reference').textContent===${JSON.stringify(tradeReference)}`);
    if(await evaluate("document.querySelector('#trade-editing').hidden"))await click('#side-panel-amend');
    await wait(`(()=>{const state=window.__editorReplies.filter(r=>r.path==='/api/futures/editor').at(-1)?.data.state;return !document.querySelector('#trade-editing').hidden&&state?.tradeId===${JSON.stringify(bookedTrade)}&&state.revision===${revision};})()`);
  }
  // The question a form with unsaved edits asks, and what keystrokes are: trusted input, which is what counts as an edit.
  const prompt="(document.querySelector('#position-details-prompt')??document.querySelector('#position-details-prompt'))";
  // Text on the page that a person can see: not hidden, and drawn.
  const shown=selector=>`(()=>{const n=document.querySelector(${JSON.stringify(selector)});return !n.hidden&&n.getClientRects().length>0;})()`;
  const asks=()=>evaluate(`new Promise(resolve=>{const start=Date.now(),poll=()=>{if(!${prompt}.hidden||Date.now()-start>1500)resolve();else setTimeout(poll,25);};poll();})`);
  const answer=async index=>{await evaluate(`${prompt}.querySelectorAll('button')[${index}]?.click()`);await wait(`${prompt}.hidden&&`+idle);};
  const typeInto=async(selector,text)=>{await evaluate(`(()=>{const n=document.querySelector(${JSON.stringify(selector)});n.focus();n.select();})()`);await command('Input.insertText',{text});};
  await open();
  await check('futures amendments expose preview and save together before a preview',"document.querySelector('#trade-preview').textContent==='Preview changes'&&document.querySelector('#trade-submit').textContent==='Save amendment'&&document.querySelector('#trade-submit').getClientRects().length>0&&document.querySelector('#trade-submit').disabled");
  await check('the desk shows each trade by its readable reference and offers instrument and revision columns',"[...document.querySelectorAll('#desk-positions .desk-reference')].every(n=>n.textContent.startsWith('TR-'))&&!!document.querySelector('#blotter-column-choices input[value=instrumentReference]')&&!!document.querySelector('#blotter-column-choices input[value=revision]')&&document.querySelector('#trade-editor-reference').textContent.includes('TR-')&&document.querySelector('#trade-editor-reference').textContent.includes('IN-')");
  await check('Amend puts the form in the panel\'s Terms tab in place of its read view, with no dialog on the page',"(()=>{const form=document.querySelector('#position-details-form'),root=document.querySelector('#trade-editing');return form.contains(root)&&!form.hidden&&document.querySelector('#position-details-view').hidden&&document.querySelector('#position-details-tabs-terms').getAttribute('aria-selected')==='true'&&document.querySelector('#side-panel-amend').textContent==='Close editor'&&!document.querySelector('dialog[open]')&&!document.querySelector('#trade-editor')&&!document.querySelector('#swap-editor');})()");
  await check('trade terms keep immutable booking facts above an instrument-owned quantity',"['trader','trade-date','executed'].every(id=>document.getElementById('trade-'+id).readOnly)&&!document.querySelector('#trade-trade-terms #trade-lots')&&document.querySelector('#trade-instrument #trade-lots')&&Boolean(document.querySelector('#trade-trade-terms').compareDocumentPosition(document.querySelector('#trade-instrument'))&Node.DOCUMENT_POSITION_FOLLOWING)");
  await check('filtered blotter row opens its exact trade revision and instrument terms',"document.querySelector('#trade-lots').value==='37'&&document.querySelector('#trade-revision').value==='1'&&!document.querySelector('#trade-amend-fields').disabled&&document.querySelector('#trade-side-fields').textContent.includes('per lot')");
  // The edit is typed, which is what counts as one: leaving the page then asks the browser to confirm, until the amendment has been sent.
  const unloadAsks="(()=>{const event=new Event('beforeunload',{cancelable:true});window.dispatchEvent(event);return event.defaultPrevented;})()";
  await typeInto('#trade-lots','38');await set('#trade-reason','Synthetic editor correction');
  await check('leaving the page asks the browser to confirm while the form holds the edit',unloadAsks);
  await submit('#trade-amend-form');
  await check('managed preview displays separate quantity units and monetary currency',"!document.querySelector('#trade-preview-result').hidden&&document.querySelector('#trade-preview-values').textContent.includes('USD')&&document.querySelector('#trade-preview-values').textContent.includes('MMBtu (IT)')&&document.querySelector('#trade-submit').disabled");
  // An amendment the backend refuses for good keeps its form and the edits in it, as a refused new booking does, and says why: what the person typed is
  // not dropped unasked. What was previewed for it is dropped, so that it is previewed again.
  await evaluate("window.__refuseAmend=true");await click('#trade-confirm');await click('#trade-submit');
  await check('a futures amendment the backend refuses keeps its form with what was typed in it, says why, and has to be previewed again',`(()=>{const panel=document.querySelector('#position-details');return window.__refusedAmend?.lots==='38'&&${shown('#trade-error')}&&document.querySelector('#trade-error').textContent==='This trade changed. Reload the current revision before amending.'&&!document.querySelector('#error').hidden&&document.querySelector('#error').textContent==='This trade changed. Reload the current revision before amending.'&&!panel.hidden&&panel.getAttribute('aria-label')==='Position details'&&!document.querySelector('#position-details-form').hidden&&document.querySelector('#position-details-form').contains(document.querySelector('#trade-editing'))&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value==='38'&&document.querySelector('#trade-reason').value==='Synthetic editor correction'&&document.querySelector('#futures-pending').hidden&&document.querySelector('#trade-preview-result').hidden&&!document.querySelector('#trade-confirm').checked&&document.querySelector('#trade-submit').disabled&&!document.querySelector('#trade-amend-fields').disabled;})()`,"[window.__refusedAmend?.lots,document.querySelector('#trade-error').textContent,document.querySelector('#error').textContent,document.querySelector('#position-details').dataset.mode,document.querySelector('#trade-editing').hidden,document.querySelector('#trade-lots').value,document.querySelector('#futures-pending').hidden]");
  await check('and the browser is still asked before the page is left, for the edit has not been sent',unloadAsks);
  await submit('#trade-amend-form');
  await check('previewed again from the same form, the amendment can be confirmed',"!document.querySelector('#trade-preview-result').hidden&&document.querySelector('#error').hidden&&document.querySelector('#trade-lots').value==='38'");
  await evaluate("window.__loseAmend=true");await click('#trade-confirm');await click('#trade-submit');
  await check('uncertain amendment retains original command and closes stale editor, and the panel is on its position\'s read view again',"document.querySelector('#trade-editing').hidden&&!document.querySelector('#futures-pending').hidden&&document.querySelector('#position-details')?.hidden===false&&!document.querySelector('#position-details-view').hidden&&document.querySelector('#position-details-form').hidden&&document.querySelector('#side-panel-amend').textContent==='Amend'");
  // The retained request is reachable whatever the panel shows, and says which request it is and which trade it belongs to.
  const retained=`(()=>{const card=document.querySelector('#futures-pending');return !card.hidden&&card.getClientRects().length>0&&card.textContent.includes(${JSON.stringify(tradeReference)})&&card.textContent.includes('Futures amendment')&&!document.querySelector('#futures-reconcile').disabled&&!document.querySelector('#futures-retry').disabled;})()`;
  await check('the retained amendment is named on the page by what it is and the trade it belongs to, with Check status and Retry',`document.querySelector('#futures-retained').contains(document.querySelector('#futures-pending'))&&${retained}`,"document.querySelector('#futures-pending').outerHTML");
  await click('#side-panel-amend');await wait("!document.querySelector('#trade-editing').hidden&&"+idle);
  await check('the form of the retained amendment waits for it, and the card is still reachable on the page',`document.querySelector('#futures-retained').contains(document.querySelector('#futures-pending'))&&document.querySelector('#trade-amend-fields').disabled&&${retained}`);
  await click('#position-details-tabs-history');
  await check('and on another tab',`document.querySelector('#futures-retained').contains(document.querySelector('#futures-pending'))&&${retained}`);
  await click('#details-collapse');await wait("(document.querySelector('#position-details')?.hidden!==false)");
  await check('with the panel closed the retained request is on the page, where it can still be checked or retried',`!document.querySelector('#futures-retained').closest('#position-details')&&document.querySelector('#futures-retained').contains(document.querySelector('#futures-pending'))&&${retained}`);
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("document.querySelector('#position-details')?.hidden===false");
  await check('and it remains reachable when a position is opened',`document.querySelector('#futures-retained').contains(document.querySelector('#futures-pending'))&&${retained}`);
  await click('#futures-retry');
  await check('amendment retry preserves exact operation and economics',"window.__amendCommands.length===2&&JSON.stringify(window.__amendCommands[0])===JSON.stringify(window.__amendCommands[1])&&document.querySelector('#futures-pending').hidden");
  await wait("!!document.querySelector('#position-details-view .side-panel-section')&&"+idle);
  await check('the desk shows the new revision, and the panel returns to the amended position at it, on its Terms, with no form open',`(()=>{const h=window.__tradingReports(window.__editorReplies).at(-1).data.holdings.find(row=>row.tradeId===${JSON.stringify(bookedTrade)});return h.revision==='2'&&h.lots==='38'&&document.querySelector('#position-details')?.hidden===false&&document.querySelector('#position-details-head .side-panel-reference').textContent===${JSON.stringify(tradeReference)}&&document.querySelector('#position-details-head .side-panel-meta').textContent.startsWith('Revision 2')&&document.querySelector('#position-details-tabs-terms').getAttribute('aria-selected')==='true'&&document.querySelector('#trade-editing').hidden&&!document.querySelector('#position-details-view').hidden&&document.querySelector('#desk-positions .open-row .desk-reference')?.textContent===${JSON.stringify(tradeReference)};})()`,"document.querySelector('#position-details-head').innerText");
  // Once the amendment has been sent the form holds nothing, and leaving the page asks nothing of the browser.
  await check('and the browser is asked nothing when the page is left, because the form holds nothing now',`!${unloadAsks}`);
  await open(2);
  await check('accepted amendment exposes the current revision in the editor',"document.querySelector('#trade-revision').value==='2'&&document.querySelector('#trade-lots').value==='38'");
  await click('#position-details-tabs-history');await wait("!!document.querySelector('#position-details-view .side-panel-section')&&"+idle);
  await check('and its immutable event history, in the panel\'s own width, in the History tab',"(()=>{const view=document.querySelector('#position-details-view'),titles=[...view.querySelectorAll('h3')].map(n=>n.textContent),panel=document.querySelector('#position-details').getBoundingClientRect();return titles.join()==='Revision 1 · Booked,Revision 2 · Amended'&&view.textContent.includes('Synthetic editor correction')&&!view.querySelector('table')&&view.scrollWidth<=view.clientWidth&&[...view.querySelectorAll('*')].every(n=>n.getBoundingClientRect().right<=panel.right+1)&&!document.querySelector('#trade-editing').hidden;})()","document.querySelector('#position-details-view').innerText");
  await click('#position-details-tabs-terms');
  await typeInto('#trade-lots','40');
  await set('#trade-revision','1');await asks();
  await check('choosing another revision with unsaved edits asks first, and keeping them leaves the form on its revision with them',`${prompt}.querySelectorAll('button').length===2&&document.querySelector('#trade-editor-reference').textContent.endsWith('Revision 2')&&document.querySelector('#trade-lots').value==='40'`,`[${prompt}.textContent,document.querySelector('#trade-editor-reference').textContent,document.querySelector('#trade-lots').value]`);
  await answer(0);
  await check('the revision selector is back on the revision the form shows',"document.querySelector('#trade-revision').value==='2'&&document.querySelector('#trade-lots').value==='40'&&!document.querySelector('#trade-editing').hidden");
  await set('#trade-revision','1');await asks();await answer(1);await wait(idle);
  await check('earlier revision is inspectable but cannot be edited',"document.querySelector('#trade-lots').value==='37'&&document.querySelector('#trade-amend-fields').disabled&&document.querySelector('#trade-editor-mode').textContent.includes('earlier trade revision')");
  await click('#trade-reload');await check('reload explicitly retrieves latest revision',"document.querySelector('#trade-revision').value==='2'&&!document.querySelector('#trade-amend-fields').disabled");
  // A revision that cannot be read says so, in the page's own message: the form does not vanish unexplained.
  await evaluate("window.__failEditor=true");await set('#trade-revision','1');await wait(idle);
  await check('a revision that cannot be read reports the failure and closes the form it replaced, and does not swallow the error',"!document.querySelector('#error').hidden&&document.querySelector('#error').textContent.length>0&&document.querySelector('#trade-editing').hidden","[document.querySelector('#error').hidden,document.querySelector('#error').textContent,document.querySelector('#trade-editing').hidden]");
  await open(2);
  // Another market example is another page load, which gives up what the panel holds: it asks first, and keeping the edits puts the select back.
  await typeInto('#trade-lots','40');
  await set('#example-dataset','second');await asks();
  await check('choosing another market example, which loads another page, asks first about the edits the panel holds',`${prompt}.querySelectorAll('button').length===2&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value==='40'&&location.hash===''`,`[${prompt}.textContent,location.hash]`);
  await answer(0);
  await check('keeping the edits puts the select back on the example the page is, and the page and its form stay',`document.querySelector('#example-dataset').value==='baseline'&&location.hash===''&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-lots').value==='40'`,`[document.querySelector('#example-dataset').value,location.hash,document.querySelector('#trade-lots').value]`);
  await set('#example-dataset','second');await asks();await answer(1);
  await check('and agreeing loads the other example, and the form is given up',`location.hash==='#second-example'&&document.querySelector('#trade-editing').hidden`,`[location.hash,document.querySelector('#trade-editing').hidden]`);
  await evaluate("history.replaceState(null,'',location.pathname);document.querySelector('#example-dataset').value='baseline'");
  await open(2);
  await shot('trade-editor');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  // A page that becomes narrow shows the details, which hold the form, in place of the positions; a phone's page scrolls down through them.
  await wait("document.querySelector('#position-details')?.hidden===false&&!document.querySelector('#blotter-pane-details').hidden&&document.querySelector('#blotter-pane-grid').hidden");
  await check('editor stays in the details at phone width without scrolling sideways',"(()=>{const r=document.querySelector('#position-details').getBoundingClientRect(),form=document.querySelector('#position-details-form');return r.width<=390&&form.scrollWidth<=form.clientWidth+1&&document.documentElement.scrollWidth<=document.documentElement.clientWidth+1;})()","(()=>{const r=document.querySelector('#position-details').getBoundingClientRect(),form=document.querySelector('#position-details-form');return [r.width,form.scrollWidth,form.clientWidth,document.documentElement.scrollWidth,document.documentElement.clientWidth];})()");await shot('trade-editor-mobile');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await evaluate("window.__holdEditor=true;document.querySelector('#trade-reload').click()");await wait('!!window.__releaseEditor');
  await evaluate("document.querySelector('#position-details').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));window.__releaseEditor()");await wait(idle);
  await check('delayed editor response cannot reopen a form the person closed with the panel',"document.querySelector('#trade-editing').hidden&&(document.querySelector('#position-details')?.hidden!==false)&&document.querySelector('#trade-book').options.length===0");
  await open(2);await set('#trade-lots','39');await set('#trade-reason','Synthetic delayed preview');
  await evaluate("window.__holdPreview=true;document.querySelector('#trade-amend-form').requestSubmit()");await wait('!!window.__releaseEditor');
  await evaluate("document.querySelector('#position-details').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));window.__releaseEditor()");await wait(idle);
  await check('late preview cannot restore cleared economics or prepared amendment',"document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-preview-result').hidden&&document.querySelector('#trade-preview-values').textContent===''&&document.querySelector('#trade-effective').textContent===''");
  // An amendment that changes the instrument, here the delivery month, gives the trade another binding. The panel follows the trade to it
  // and says so; the earlier revisions, of the first instrument, stay readable, each naming its own, and cannot be amended from here.
  const eventually=expression=>evaluate(`new Promise(resolve=>{const start=Date.now(),poll=()=>{let ok=false;try{ok=!!(${expression});}catch{}if(ok||Date.now()-start>4000)resolve();else setTimeout(poll,50);};poll();})`);
  const bindingOf=()=>evaluate(`window.__tradingReports(window.__editorReplies).at(-1).data.holdings.find(row=>row.tradeId===${JSON.stringify(bookedTrade)}).bindingId`);
  await open(2);
  const bindingBefore=await bindingOf(),contractBefore=await evaluate("document.querySelector('#trade-contract').value");
  await click('#trade-contract-date');
  const month=await evaluate(`(()=>{const calendar=document.querySelector('#trade-contract-date').parentElement;for(let n=0;n<24;n++){const cell=[...calendar.querySelectorAll('[data-date]:not(:disabled)')].find(b=>b.getAttribute('aria-pressed')!=='true');if(cell){cell.click();return cell.dataset.date;}const next=calendar.querySelector('[aria-label="Next month with data"]');if(next.disabled)return null;next.click();}return null;})()`);
  if(month===null)throw Error('The instrument-changing amendment needs a second delivery month.');
  // What a calendar sets for the person is an edit: this contract-only amendment asks before a row switch, and keeping it changes nothing.
  await set('#blotter-filters .table-search','');
  await evaluate(`[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].find(row=>row.querySelector('.desk-reference').textContent!==${JSON.stringify(tradeReference)}).click()`);await asks();
  await check('a contract-only amendment asks before a row switch: a delivery month chosen in the calendar is an edit',`${prompt}.querySelectorAll('button').length===2&&document.querySelector('#position-details-head .side-panel-reference').textContent===${JSON.stringify(tradeReference)}&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-contract').value!==${JSON.stringify(contractBefore)}`,`[${prompt}.textContent,document.querySelector('#trade-contract').value,${JSON.stringify(contractBefore)}]`);
  await answer(0);await set('#blotter-filters .table-search',tradeReference);
  // The month chosen offers its contract, which the person then chooses, as a booking does.
  const monthContract=await evaluate("document.querySelector('#trade-contract').options[1]?.value");
  if(!monthContract)throw Error('The delivery month chosen offers no contract.');
  await set('#trade-contract',monthContract);
  await set('#trade-reason','Synthetic delivery month correction');await submit('#trade-amend-form');
  await check('the preview of an amendment of the delivery month alone is offered for confirmation',`document.querySelector('#trade-contract').value!==${JSON.stringify(contractBefore)}&&!document.querySelector('#trade-preview-result').hidden`,"[document.querySelector('#trade-error').textContent,document.querySelector('#error').textContent]");
  await click('#trade-confirm');await click('#trade-submit');
  await eventually("document.querySelector('#position-details')?.hidden===false&&document.querySelector('#position-details-head .side-panel-meta')?.textContent.startsWith('Revision 3')");
  const bindingAfter=await bindingOf();
  await check('the amendment gave the trade another binding, at its third revision',`${JSON.stringify(bindingAfter)}!==${JSON.stringify(bindingBefore)}&&window.__tradingReports(window.__editorReplies).at(-1).data.holdings.find(row=>row.tradeId===${JSON.stringify(bookedTrade)}).revision==='3'`);
  await check('the panel follows the trade to that binding, on its Terms with no form open, and says that it has',`(()=>{return document.querySelector('#position-details')?.hidden===false&&document.querySelector('#position-details-head .side-panel-reference').textContent===${JSON.stringify(tradeReference)}&&document.querySelector('#position-details-head .side-panel-meta').textContent.startsWith('Revision 3')&&document.querySelector('#position-details-tabs-terms').getAttribute('aria-selected')==='true'&&document.querySelector('#trade-editing').hidden&&!document.querySelector('#notice').hidden&&document.querySelector('#notice').textContent.includes(${JSON.stringify(tradeReference+' now holds another instrument')});})()`,"[(document.querySelector('#position-details')?.hidden!==false),document.querySelector('#position-details-head').innerText,document.querySelector('#notice').textContent]");
  await click('#side-panel-amend');await wait("!document.querySelector('#trade-editing').hidden&&"+idle);
  await set('#trade-revision','1');await wait(idle);
  await check('an earlier revision of another instrument is read as it is: it names its instrument, and the form of this position cannot amend it',`(()=>{const read=window.__editorReplies.filter(r=>r.path==='/api/futures/editor').at(-1).data;return read.state.revision===1&&read.state.bindingId!==${JSON.stringify(bindingAfter)}&&document.querySelector('#error').hidden&&!document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-revision').value==='1'&&document.querySelector('#trade-amend-fields').disabled&&document.querySelector('#trade-editor-reference').textContent.includes(read.state.instrumentReference);})()`,"[document.querySelector('#error').textContent,document.querySelector('#trade-error').textContent,document.querySelector('#trade-revision').value,document.querySelector('#trade-editor-reference').textContent]");
  await click('#position-details-tabs-history');await eventually("document.querySelectorAll('#position-details-view .side-panel-section').length===3");
  await check('and the History tab lists the three revisions, each naming the instrument it was of',`(()=>{const sections=[...document.querySelectorAll('#position-details-view .side-panel-section')],instruments=sections.map(section=>[...section.querySelectorAll('.side-term')].find(row=>row.children[0].textContent==='Instrument')?.children[1].textContent);return sections.map(section=>section.querySelector('h3').textContent).join()==='Revision 1 · Booked,Revision 2 · Amended,Revision 3 · Amended'&&instruments.every(Boolean)&&instruments[0]!==instruments[2]&&instruments[0]===instruments[1];})()`,"[...document.querySelectorAll('#position-details-view .side-panel-section')].map(s=>s.innerText)");
  await click('#details-collapse');await wait("(document.querySelector('#position-details')?.hidden!==false)");
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await wait("document.querySelector('#position-details')?.hidden===false");await evaluate("window.__denyEditor=true");await click('#side-panel-amend');
  await check('denied editor clears prior business names and all trade surfaces',"document.querySelector('#trade-book').options.length===0&&document.querySelector('#trade-contract').options.length===0&&document.querySelector('#trade-revision').options.length===0&&document.querySelector('#desk-positions').children.length===0&&(document.querySelector('#position-details')?.hidden!==false)");
  // The panel's Terms read is the editor's read: when the account may not make it, the page is cleared as for the editor.
  await click('#futures-refresh');await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')");
  await evaluate("window.__denyEditor=true");await click('#desk-positions .tabulator-row:not(.tabulator-group)');
  await wait("document.querySelector('#desk-positions').children.length===0");
  await check('a denied Terms read clears the page and closes the panel, as a denied editor read does',"(document.querySelector('#position-details')?.hidden!==false)&&document.querySelector('#desk-positions').children.length===0&&!document.querySelector('#position-details-view')?.children.length&&!document.querySelector('#position-details-head')?.children.length");
  await click('#futures-refresh');await click('#date-context>summary');await set('#date-context-on','2020-12-18');await wait(idle);await click('#date-context-latest');await click('#apply-view-context');
  await check('historical mode visibly explains disabled booking and offers working return',`document.querySelector('#workspace-mode-label').textContent.includes('Historical view')&&!document.querySelector('#return-working').hidden&&document.querySelector('#futures-new').disabled&&document.querySelector('#futures-book-submit').disabled&&${shown('#new-trade-availability')}&&document.querySelector('#new-trade-availability').textContent.includes('Historical view')&&document.querySelector('#futures-new').getAttribute('aria-describedby')==='new-trade-availability'&&!document.querySelector('#futures-new').title`);
  // Reading a historical close, the desk reads its curves in that same context and draws them beside the positions.
  await set('#snapshot',await evaluate("[...document.querySelector('#snapshot').options].filter(o=>o.value).at(-1).value"));await wait(idle);await click('#futures-refresh');
  await wait("!!document.querySelector('#desk-positions .tabulator-row')&&document.querySelectorAll('#desk-positions .sparkline-loading').length===0");
  await check('under a historical context the desk reads its curves in that context and draws them',"(()=>{const shapes=window.__editorReplies.filter(r=>r.path==='/api/market/curve-shapes').at(-1),report=window.__tradingReports(window.__editorReplies).at(-1).data;return !!shapes&&shapes.data.context.contextId===report.context.contextId&&document.querySelectorAll('#desk-positions svg.sparkline:not(.sparkline-unavailable)').length>0;})()");
  // Returning to working data opens it on its newest close, as opening the workspace does (D217, D219), and the Blotter reads it there.
  const historicalRead=await evaluate("window.__tradingReports(window.__editorReplies).at(-1).data.context.contextId");
  await click('#return-working');await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')&&"+idle);
  await check('return to working opens the newest close, says it is the latest, and the Blotter reads it in working data, keeping nothing of the historical view',`document.querySelector('#workspace-mode-label').textContent.includes('Working data')&&document.querySelector('#return-working').hidden&&document.querySelector('#snapshot').value!==''&&document.querySelector('#market-date-badge').textContent==='Latest close'&&window.__tradingReports(window.__editorReplies).at(-1).data.context.contextId!==${JSON.stringify(historicalRead)}&&!document.querySelector('#futures-new').disabled`,"[document.querySelector('#workspace-mode-label').textContent,document.querySelector('#snapshot').value,document.querySelector('#market-date-badge').textContent,window.__tradingReports(window.__editorReplies).at(-1).data.context,document.querySelector('#futures-new').disabled]");
  await persona('risk');await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
  await check('New trade follows the backend\'s permission: an account that may not book is not offered it, and is told why in the backend\'s words on the page, with nothing of a new trade open',`document.querySelector('#futures-new').disabled&&${shown('#new-trade-availability')}&&document.querySelector('#new-trade-availability').textContent==='Your current role does not have Book Trade permission.'&&document.querySelector('#futures-new').getAttribute('aria-describedby')==='new-trade-availability'&&!document.querySelector('#futures-new').title&&document.querySelector('#position-details').hidden&&document.querySelector('#futures-new-trade').hidden&&document.querySelector('#swap-editing').hidden`);
  await evaluate("window.__revisionSort=true");await click('#futures-refresh');
  await check('revision sorting uses coherent booked facts and complete priced revisions',"(()=>{const facts=window.__editorReplies.filter(r=>r.path==='/api/trading/positions').at(-1).data,priced=window.__tradingReports(window.__editorReplies).at(-1).data;return facts.positions.every(f=>priced.holdings.some(h=>h.bindingId===f.bindingId&&h.revision===String(f.revision)))&&document.querySelector('#futures-page').dataset.pricingState==='complete';})()");
  // Revisions sort as numbers in a flat list: grouping would order the rows by book first.
  await set('#blotter-row-group','');await wait(idle);await click('#blotter-columns>summary');await click('#blotter-column-choices input[value="revision"]');await click('#blotter-columns>summary');
  await evaluate("[...document.querySelectorAll('#desk-positions [role=columnheader]')].find(n=>n.textContent.trim()==='Revision').click()");
  // Every row stays, one at revision 10 and the rest at 2: the desk refuses rows its summaries do not describe. Ascending, the first is 2 as a number, 10 as text.
  await check('revision column sorts numerically across multiple digits',"(()=>{const field=[...document.querySelectorAll('#desk-positions [role=columnheader]')].find(n=>n.textContent.trim()==='Revision').getAttribute('tabulator-field');const shown=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].map(row=>row.querySelector('[tabulator-field='+field+']').textContent.trim());return window.__tradingReports(window.__editorReplies).at(-1).data.holdings.some(h=>h.revision==='10')&&shown.length>1&&shown[0]==='2';})()");

}
