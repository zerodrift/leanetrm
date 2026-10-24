import {chooseFutures} from './futures-selection.mjs';
import {setupTradingReplies} from './trading-responses.mjs';
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,url}){
  await command('Page.enable');await command('Runtime.enable');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  await setupTradingReplies(evaluate);
  await evaluate(`(()=>{const send=window.fetch;window.__editorReplies=[];window.__amendCommands=[];window.fetch=async(input,init)=>{
    const path=typeof input==='string'?input:input.url;
    if(path==='/api/futures/editor'&&window.__denyEditor){window.__denyEditor=false;return new Response(JSON.stringify({code:'access_denied'}),{status:403});}
    if(path==='/api/futures/amend')window.__amendCommands.push(JSON.parse(init.body));
    let response=await send(input,init);
    if((path==='/api/trading/workspace'||path==='/api/trading/blotter')&&response.ok){
      const data=await response.json(),report=path==='/api/trading/workspace'?data.blotter:data;
      const booked=window.__editorReplies.filter(r=>r.path==='/api/futures/book').at(-1)?.data.tradeId;
      const other=report.holdings.find(row=>row.tradeId!==booked);if(other)other.tradeReference='TR-990037';
      response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});
    }
    if((path==='/api/trading/workspace'||path==='/api/trading/blotter')&&window.__revisionSort&&response.ok){const data=await response.json(),report=path==='/api/trading/workspace'?data.blotter:data;report.holdings=report.holdings.map((h,i)=>({...h,revision:i===0?'10':'2'}));response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}
    if((path==='/api/futures/editor'&&window.__holdEditor)||(path==='/api/futures/amend-preview'&&window.__holdPreview)){
      window.__holdEditor=false;window.__holdPreview=false;await new Promise(resolve=>window.__releaseEditor=resolve);window.__releaseEditor=null;
    }
    try{window.__editorReplies.push({path,data:await response.clone().json()});}catch{}
    if(path==='/api/futures/amend'&&window.__loseAmend){window.__loseAmend=false;return new Response('incomplete',{status:502});}return response;
  };})()`);
  await persona('trader');
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");
  await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
  await check('Power TOUs and Reference data are in Settings',"document.querySelector('#nav-group-settings #nav-power')&&document.querySelector('#nav-group-settings #nav-reference')&&!document.querySelector('#nav-group-markets #nav-reference')");
  await click('#nav-power');await check('Power TOUs header identifies Settings',"document.querySelector('#page-section').textContent==='SETTINGS'");await click('#nav-reference');await check('Reference data header identifies Settings',"document.querySelector('#page-section').textContent==='SETTINGS'");await click('#nav-futures');
  await check('visible product branding is LeanETRM',"document.title.includes('LeanETRM')&&document.querySelector('.brand').textContent.includes('LeanETRM')");
  await check('working knowledge and actual today are explicit beside selected price date',"document.querySelector('#workspace-mode-label').textContent.includes('Working data')&&document.querySelector('#workspace-today').textContent.includes('Today:')&&document.querySelector('#workspace-today').textContent.includes('earlier market date')&&!document.querySelector('#futures-new').disabled");
  await click('#futures-new');await click('#trade-kind-future');await chooseFutures({evaluate,set,click});await evaluate("document.querySelector('#futures-book').selectedIndex=1;");
  await set('#futures-lots','37');await set('#futures-execution','2.9');await evaluate("for(const id of ['futures-legal-entity','futures-counterparty']){const s=document.getElementById(id);s.selectedIndex=1;s.dispatchEvent(new Event('input',{bubbles:true}));}");await submit('#futures-form');
  const bookedTrade=await evaluate("window.__editorReplies.filter(r=>r.path==='/api/futures/book').at(-1).data.tradeId");
  const tradeReference=await evaluate(`window.__tradingReports(window.__editorReplies).at(-1).data.holdings.find(row=>row.tradeId===${JSON.stringify(bookedTrade)}).tradeReference`);
  await check('row targeting distinguishes the booked quantity from a competing reference containing 37',`(()=>{const rows=window.__tradingReports(window.__editorReplies).at(-1).data.holdings;return rows.some(row=>row.tradeId!==${JSON.stringify(bookedTrade)}&&row.tradeReference==='TR-990037')&&rows.find(row=>row.tradeId===${JSON.stringify(bookedTrade)}).lots==='37';})()`);
  async function open(revision=1){
    await set('#blotter-filters .table-search',tradeReference);
    await wait(`(()=>{const rows=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].filter(row=>row.getClientRects().length);return ${idle}&&rows.length===1&&rows[0].querySelector('.desk-reference')?.textContent===${JSON.stringify(tradeReference)};})()`);
    // A row opens its position in the side panel; Amend puts the trade's form in its Terms tab, in place of the read view.
    await click('#desk-positions .tabulator-row:not(.tabulator-group)');
    await wait(`!document.querySelector('#side-panel').hidden&&document.querySelector('#side-panel-head .side-panel-reference').textContent===${JSON.stringify(tradeReference)}`);
    if(await evaluate("document.querySelector('#trade-editing').hidden"))await click('#side-panel-amend');
    await wait(`(()=>{const state=window.__editorReplies.filter(r=>r.path==='/api/futures/editor').at(-1)?.data.state;return !document.querySelector('#trade-editing').hidden&&state?.tradeId===${JSON.stringify(bookedTrade)}&&state.revision===${revision};})()`);
  }
  await open();
  await check('the desk shows each trade by its readable reference and offers instrument and revision columns',"[...document.querySelectorAll('#desk-positions .desk-reference')].every(n=>n.textContent.startsWith('TR-'))&&!!document.querySelector('#blotter-column-choices input[value=instrumentReference]')&&!!document.querySelector('#blotter-column-choices input[value=revision]')&&document.querySelector('#trade-editor-reference').textContent.includes('TR-')&&document.querySelector('#trade-editor-reference').textContent.includes('IN-')");
  await check('Amend puts the form in the panel\'s Terms tab in place of its read view, with no dialog on the page',"(()=>{const form=document.querySelector('#side-panel-form'),root=document.querySelector('#trade-editing');return form.contains(root)&&!form.hidden&&document.querySelector('#side-panel-view').hidden&&document.querySelector('#side-panel-tabs-terms').getAttribute('aria-selected')==='true'&&document.querySelector('#side-panel-amend').textContent==='Close editor'&&!document.querySelector('dialog[open]')&&!document.querySelector('#trade-editor')&&!document.querySelector('#swap-editor');})()");
  await check('trade terms keep immutable booking facts above an instrument-owned quantity',"['trader','trade-date','executed'].every(id=>document.getElementById('trade-'+id).readOnly)&&!document.querySelector('#trade-trade-terms #trade-lots')&&document.querySelector('#trade-instrument #trade-lots')&&Boolean(document.querySelector('#trade-trade-terms').compareDocumentPosition(document.querySelector('#trade-instrument'))&Node.DOCUMENT_POSITION_FOLLOWING)");
  await check('filtered blotter row opens its exact trade revision and instrument terms',"document.querySelector('#trade-lots').value==='37'&&document.querySelector('#trade-revision').value==='1'&&!document.querySelector('#trade-amend-fields').disabled&&document.querySelector('#trade-side-fields').textContent.includes('per lot')");
  await set('#trade-lots','38');await set('#trade-reason','Synthetic editor correction');await submit('#trade-amend-form');
  await check('managed preview displays separate quantity units and monetary currency',"!document.querySelector('#trade-preview-result').hidden&&document.querySelector('#trade-preview-values').textContent.includes('USD')&&document.querySelector('#trade-preview-values').textContent.includes('MMBtu (IT)')&&document.querySelector('#trade-submit').disabled");
  await evaluate("window.__loseAmend=true");await click('#trade-confirm');await click('#trade-submit');
  await check('uncertain amendment retains original command and closes stale editor, and the panel is on its position\'s read view again',"document.querySelector('#trade-editing').hidden&&!document.querySelector('#futures-pending').hidden&&!document.querySelector('#side-panel').hidden&&!document.querySelector('#side-panel-view').hidden&&document.querySelector('#side-panel-form').hidden&&document.querySelector('#side-panel-amend').textContent==='Amend'");
  // The retained request keeps its Check status and Retry inside the panel: Amend shows them in the form, which waits for the request.
  await click('#side-panel-amend');await wait("!document.querySelector('#trade-editing').hidden&&"+idle);
  await check('a retained amendment is reconciled or retried from the panel\'s form, which waits for it',"(()=>{const pending=document.querySelector('#trade-editor-pending'),form=document.querySelector('#side-panel-form');return form.contains(pending)&&!pending.hidden&&pending.getClientRects().length>0&&document.querySelector('#trade-amend-fields').disabled&&!document.querySelector('#trade-editor-retry').disabled&&!document.querySelector('#trade-editor-reconcile').disabled;})()");
  await click('#trade-editor-retry');
  await check('amendment retry preserves exact operation and economics',"window.__amendCommands.length===2&&JSON.stringify(window.__amendCommands[0])===JSON.stringify(window.__amendCommands[1])&&document.querySelector('#futures-pending').hidden");
  await wait("!!document.querySelector('#side-panel-view .side-panel-section')&&"+idle);
  await check('the desk shows the new revision, and the panel returns to the amended position at it, on its Terms, with no form open',`(()=>{const h=window.__tradingReports(window.__editorReplies).at(-1).data.holdings.find(row=>row.tradeId===${JSON.stringify(bookedTrade)});return h.revision==='2'&&h.lots==='38'&&!document.querySelector('#side-panel').hidden&&document.querySelector('#side-panel-head .side-panel-reference').textContent===${JSON.stringify(tradeReference)}&&document.querySelector('#side-panel-head .side-panel-meta').textContent.startsWith('Revision 2')&&document.querySelector('#side-panel-tabs-terms').getAttribute('aria-selected')==='true'&&document.querySelector('#trade-editing').hidden&&!document.querySelector('#side-panel-view').hidden&&document.querySelector('#desk-positions .open-row .desk-reference')?.textContent===${JSON.stringify(tradeReference)};})()`,"document.querySelector('#side-panel-head').innerText");
  await open(2);
  await check('accepted amendment exposes the current revision in the editor',"document.querySelector('#trade-revision').value==='2'&&document.querySelector('#trade-lots').value==='38'");
  await click('#side-panel-tabs-history');await wait("!!document.querySelector('#side-panel-view .side-panel-section')&&"+idle);
  await check('and its immutable event history, in the panel\'s own width, in the History tab',"(()=>{const view=document.querySelector('#side-panel-view'),titles=[...view.querySelectorAll('h3')].map(n=>n.textContent),panel=document.querySelector('#side-panel').getBoundingClientRect();return titles.join()==='Revision 1 · Booked,Revision 2 · Amended'&&view.textContent.includes('Synthetic editor correction')&&!view.querySelector('table')&&view.scrollWidth<=view.clientWidth&&[...view.querySelectorAll('*')].every(n=>n.getBoundingClientRect().right<=panel.right+1)&&!document.querySelector('#trade-editing').hidden;})()","document.querySelector('#side-panel-view').innerText");
  await click('#side-panel-tabs-terms');
  await set('#trade-revision','1');await wait(idle);
  await check('earlier revision is inspectable but cannot be edited',"document.querySelector('#trade-lots').value==='37'&&document.querySelector('#trade-amend-fields').disabled&&document.querySelector('#trade-editor-mode').textContent.includes('earlier trade revision')");
  await click('#trade-reload');await check('reload explicitly retrieves latest revision',"document.querySelector('#trade-revision').value==='2'&&!document.querySelector('#trade-amend-fields').disabled");
  await shot('trade-editor');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  // The panel is placed again when the window's width has changed, a frame after it does.
  await wait("document.querySelector('#side-panel').dataset.placement==='overlay'&&document.querySelector('#side-panel').getBoundingClientRect().left===0");
  await check('editor fits a narrow viewport with bounded scrolling: the panel is a sheet and the form scrolls inside it',"(()=>{const r=document.querySelector('#side-panel').getBoundingClientRect(),form=document.querySelector('#side-panel-form');return r.left>=0&&r.right<=390&&r.height<=844&&form.scrollWidth<=form.clientWidth&&document.querySelector('#side-panel-body').scrollHeight>document.querySelector('#side-panel-body').clientHeight;})()");await shot('trade-editor-mobile');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await evaluate("window.__holdEditor=true;document.querySelector('#trade-reload').click()");await wait('!!window.__releaseEditor');
  await evaluate("document.querySelector('#side-panel').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));window.__releaseEditor()");await wait(idle);
  await check('delayed editor response cannot reopen a form the person closed with the panel',"document.querySelector('#trade-editing').hidden&&document.querySelector('#side-panel').hidden&&document.querySelector('#trade-book').options.length===0");
  await open(2);await set('#trade-lots','39');await set('#trade-reason','Synthetic delayed preview');
  await evaluate("window.__holdPreview=true;document.querySelector('#trade-amend-form').requestSubmit()");await wait('!!window.__releaseEditor');
  await evaluate("document.querySelector('#side-panel').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));window.__releaseEditor()");await wait(idle);
  await check('late preview cannot restore cleared economics or prepared amendment',"document.querySelector('#trade-editing').hidden&&document.querySelector('#trade-preview-result').hidden&&document.querySelector('#trade-preview-values').textContent===''&&document.querySelector('#trade-effective').textContent===''");
  await evaluate("window.__denyEditor=true");await click('#desk-positions .tabulator-row:not(.tabulator-group)');await click('#side-panel-amend');
  await check('denied editor clears prior business names and all trade surfaces',"document.querySelector('#trade-book').options.length===0&&document.querySelector('#trade-contract').options.length===0&&document.querySelector('#trade-revision').options.length===0&&document.querySelector('#desk-positions').children.length===0&&document.querySelector('#side-panel').hidden");
  // The panel's Terms read is the editor's read: when the account may not make it, the page is cleared as for the editor.
  await click('#futures-refresh');await wait("!!document.querySelector('#desk-positions .tabulator-row:not(.tabulator-group)')");
  await click('#desk-positions .tabulator-row:not(.tabulator-group)');await evaluate("window.__denyEditor=true");await click('#side-panel-tabs-terms');
  await wait("document.querySelector('#desk-positions').children.length===0");
  await check('a denied Terms read clears the page and closes the panel, as a denied editor read does',"document.querySelector('#side-panel').hidden&&document.querySelector('#desk-positions').children.length===0&&document.querySelector('#side-panel-view').children.length===0&&document.querySelector('#side-panel-head').children.length===0");
  await click('#futures-refresh');await click('#date-context>summary');await set('#date-context-on','2020-12-18');await click('#date-context-latest');await click('#apply-view-context');
  await check('historical mode visibly explains disabled booking and offers working return',"document.querySelector('#workspace-mode-label').textContent.includes('Historical view')&&!document.querySelector('#return-working').hidden&&document.querySelector('#futures-new').disabled&&document.querySelector('#futures-book-submit').disabled&&document.querySelector('#futures-booking-availability').textContent.includes('Historical view')");
  // Reading a historical close, the desk reads its curves in that same context and draws them beside the positions.
  await set('#snapshot',await evaluate("[...document.querySelector('#snapshot').options].filter(o=>o.value).at(-1).value"));await wait(idle);await click('#futures-refresh');
  await wait("!!document.querySelector('#desk-positions .tabulator-row')&&document.querySelectorAll('#desk-positions .sparkline-loading').length===0");
  await check('under a historical context the desk reads its curves in that context and draws them',"(()=>{const shapes=window.__editorReplies.filter(r=>r.path==='/api/market/curve-shapes').at(-1),report=window.__tradingReports(window.__editorReplies).at(-1).data;return !!shapes&&shapes.data.context.contextId===report.context.contextId&&document.querySelectorAll('#desk-positions svg.sparkline:not(.sparkline-unavailable)').length>0;})()");
  await click('#return-working');
  await check('return to working does not silently select a market date',"document.querySelector('#workspace-mode-label').textContent.includes('Working data')&&document.querySelector('#snapshot').value===''&&document.querySelector('#futures-new').disabled");
  await persona('risk');await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
  await check('draft entry stays available while booking permission is explained',"!document.querySelector('#futures-new').disabled&&document.querySelector('#futures-book-submit').disabled&&document.querySelector('#futures-booking-availability').textContent.includes('Book Trade permission')");
  await evaluate("window.__revisionSort=true");await click('#futures-refresh');
  // Revisions sort as numbers in a flat list: grouping would order the rows by book first.
  await set('#blotter-row-group','');await wait(idle);await click('#blotter-columns>summary');await click('#blotter-column-choices input[value="revision"]');await click('#blotter-columns>summary');
  await evaluate("[...document.querySelectorAll('#desk-positions [role=columnheader]')].find(n=>n.textContent.trim()==='Revision').click()");
  // Every row stays, one at revision 10 and the rest at 2: the desk refuses rows its summaries do not describe. Ascending, the first is 2 as a number, 10 as text.
  await check('revision column sorts numerically across multiple digits',"(()=>{const field=[...document.querySelectorAll('#desk-positions [role=columnheader]')].find(n=>n.textContent.trim()==='Revision').getAttribute('tabulator-field');const shown=[...document.querySelectorAll('#desk-positions .tabulator-row:not(.tabulator-group)')].map(row=>row.querySelector('[tabulator-field='+field+']').textContent.trim());return window.__tradingReports(window.__editorReplies).at(-1).data.holdings.some(h=>h.revision==='10')&&shown.length>1&&shown[0]==='2';})()");

}
