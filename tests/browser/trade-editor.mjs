export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,url}){
  await command('Page.enable');await command('Runtime.enable');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  await evaluate(`(()=>{const send=window.fetch;window.__editorReplies=[];window.__amendCommands=[];window.fetch=async(input,init)=>{
    const path=typeof input==='string'?input:input.url;
    if(path==='/api/futures/editor'&&window.__denyEditor){window.__denyEditor=false;return new Response(JSON.stringify({code:'access_denied'}),{status:403});}
    if(path==='/api/futures/amend')window.__amendCommands.push(JSON.parse(init.body));
    let response=await send(input,init);
    if(path==='/api/trading/blotter'&&window.__revisionSort&&response.ok){const data=await response.json();data.holdings=data.holdings.slice(0,2).map((h,i)=>({...h,revision:i===0?'10':'2'}));response=new Response(JSON.stringify(data),{status:200,headers:{'Content-Type':'application/json'}});}
    if((path==='/api/futures/editor'&&window.__holdEditor)||(path==='/api/futures/amend-preview'&&window.__holdPreview)){
      window.__holdEditor=false;window.__holdPreview=false;await new Promise(resolve=>window.__releaseEditor=resolve);window.__releaseEditor=null;
    }
    try{window.__editorReplies.push({path,data:await response.clone().json()});}catch{}
    if(path==='/api/futures/amend'&&window.__loseAmend){window.__loseAmend=false;return new Response('incomplete',{status:502});}return response;
  };})()`);
  await persona('trader');
  const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')).id;})()");
  await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
  await check('Power TOUs and Reference data are in Settings',"document.querySelector('#settings-menu #nav-power')&&document.querySelector('#settings-menu #nav-reference')&&!document.querySelector('#market-menu #nav-reference')");
  await click('#nav-power');await check('Power TOUs header identifies Settings',"document.querySelector('#page-section').textContent==='WORKSPACE SETTINGS'");await click('#nav-reference');await check('Reference data header identifies Settings',"document.querySelector('#page-section').textContent==='WORKSPACE SETTINGS'");await click('#nav-futures');
  await check('visible product branding is LeanETRM',"document.title.includes('LeanETRM')&&document.querySelector('.brand').textContent.includes('LeanETRM')");
  await check('working knowledge and actual today are explicit beside selected price date',"document.querySelector('#workspace-mode-label').textContent.includes('Working data')&&document.querySelector('#workspace-today').textContent.includes('Today:')&&document.querySelector('#workspace-today').textContent.includes('earlier market date')&&!document.querySelector('#futures-new').disabled");
  await click('#futures-new');await click('#trade-kind-future');await evaluate("document.querySelector('#futures-contract').selectedIndex=1;document.querySelector('#futures-contract').dispatchEvent(new Event('change',{bubbles:true}));document.querySelector('#futures-book').selectedIndex=1;");
  await set('#futures-lots','37');await set('#futures-execution','2.9');await evaluate("for(const id of ['futures-legal-entity','futures-counterparty']){const s=document.getElementById(id);s.selectedIndex=1;s.dispatchEvent(new Event('input',{bubbles:true}));}");await submit('#futures-form');
  async function open(){await set('#futures-holdings .table-search','37');await click('#futures-holdings .tabulator-row');await wait("document.querySelector('#trade-editor').open");}
  await open();
  await check('blotter exposes readable trade instrument and revision columns',"document.querySelector('#futures-holdings').textContent.includes('Trade ID')&&document.querySelector('#futures-holdings').textContent.includes('Instrument ID')&&document.querySelector('#trade-editor-reference').textContent.includes('TR-')&&document.querySelector('#trade-editor-reference').textContent.includes('IN-')");
  await check('trade terms keep immutable booking facts above an instrument-owned quantity',"['trader','trade-date','executed'].every(id=>document.getElementById('trade-'+id).readOnly)&&!document.querySelector('#trade-trade-terms #trade-lots')&&document.querySelector('#trade-instrument #trade-lots')&&Boolean(document.querySelector('#trade-trade-terms').compareDocumentPosition(document.querySelector('#trade-instrument'))&Node.DOCUMENT_POSITION_FOLLOWING)");
  await check('filtered blotter row opens its exact trade revision and instrument terms',"document.querySelector('#trade-lots').value==='37'&&document.querySelector('#trade-revision').value==='1'&&!document.querySelector('#trade-amend-fields').disabled&&document.querySelector('#trade-side-fields').textContent.includes('per lot')");
  await set('#trade-lots','38');await set('#trade-reason','Synthetic editor correction');await submit('#trade-amend-form');
  await check('managed preview displays separate quantity units and monetary currency',"!document.querySelector('#trade-preview-result').hidden&&document.querySelector('#trade-preview-values').textContent.includes('USD')&&document.querySelector('#trade-preview-values').textContent.includes('MMBtu (IT)')&&document.querySelector('#trade-submit').disabled");
  await evaluate("window.__loseAmend=true");await click('#trade-confirm');await click('#trade-submit');
  await check('uncertain amendment retains original command and closes stale editor',"!document.querySelector('#trade-editor').open&&!document.querySelector('#futures-pending').hidden");
  await click('#futures-retry');
  await check('amendment retry preserves exact operation and economics',"window.__amendCommands.length===2&&JSON.stringify(window.__amendCommands[0])===JSON.stringify(window.__amendCommands[1])&&document.querySelector('#futures-pending').hidden");
  await set('#futures-holdings .table-search','38');await click('#futures-holdings .tabulator-row');
  await check('accepted amendment exposes current revision and immutable event history',"document.querySelector('#trade-revision').value==='2'&&document.querySelector('#trade-lots').value==='38'&&document.querySelector('#trade-history').textContent.includes('Synthetic editor correction')");
  await set('#trade-revision','1');await wait(idle);
  await check('earlier revision is inspectable but cannot be edited',"document.querySelector('#trade-lots').value==='37'&&document.querySelector('#trade-amend-fields').disabled&&document.querySelector('#trade-editor-mode').textContent.includes('earlier trade revision')");
  await click('#trade-reload');await check('reload explicitly retrieves latest revision',"document.querySelector('#trade-revision').value==='2'&&!document.querySelector('#trade-amend-fields').disabled");
  await shot('trade-editor');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  await check('editor fits a narrow viewport with bounded scrolling',"(()=>{const r=document.querySelector('#trade-editor').getBoundingClientRect();return r.left>=0&&r.right<=390&&r.height<=844;})()");await shot('trade-editor-mobile');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await evaluate("window.__holdEditor=true;document.querySelector('#trade-reload').click()");await wait('!!window.__releaseEditor');
  await evaluate("document.querySelector('#trade-editor').dispatchEvent(new Event('cancel'));window.__releaseEditor()");await wait(idle);
  await check('delayed editor response cannot reopen a cancelled dialog',"!document.querySelector('#trade-editor').open&&document.querySelector('#trade-book').options.length===0");
  await set('#futures-holdings .table-search','38');await click('#futures-holdings .tabulator-row');await set('#trade-lots','39');await set('#trade-reason','Synthetic delayed preview');
  await evaluate("window.__holdPreview=true;document.querySelector('#trade-amend-form').requestSubmit()");await wait('!!window.__releaseEditor');
  await evaluate("document.querySelector('#trade-editor').dispatchEvent(new Event('cancel'));window.__releaseEditor()");await wait(idle);
  await check('late preview cannot restore cleared economics or prepared amendment',"!document.querySelector('#trade-editor').open&&document.querySelector('#trade-preview-result').hidden&&document.querySelector('#trade-preview-values').textContent===''&&document.querySelector('#trade-effective').textContent===''");
  await evaluate("window.__denyEditor=true");await click('#futures-holdings .tabulator-row');
  await check('denied editor clears prior business names and all trade surfaces',"document.querySelector('#trade-book').options.length===0&&document.querySelector('#trade-contract').options.length===0&&document.querySelector('#trade-revision').options.length===0&&document.querySelector('#futures-holdings').children.length===0");
  await click('#futures-refresh');await click('#date-context>summary');await set('#date-context-on','2020-12-18');await click('#date-context-latest');await click('#apply-view-context');
  await check('historical mode visibly explains disabled booking and offers working return',"document.querySelector('#workspace-mode-label').textContent.includes('Historical view')&&!document.querySelector('#return-working').hidden&&document.querySelector('#futures-new').disabled&&document.querySelector('#futures-book-submit').disabled&&document.querySelector('#futures-booking-availability').textContent.includes('Historical view')");
  await click('#return-working');
  await check('return to working does not silently select a market date',"document.querySelector('#workspace-mode-label').textContent.includes('Working data')&&document.querySelector('#snapshot').value===''&&document.querySelector('#futures-new').disabled");
  await persona('risk');await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
  await check('draft entry stays available while booking permission is explained',"!document.querySelector('#futures-new').disabled&&document.querySelector('#futures-book-submit').disabled&&document.querySelector('#futures-booking-availability').textContent.includes('Book Trade permission')");
  await evaluate("window.__revisionSort=true");await click('#futures-refresh');
  await evaluate("[...document.querySelectorAll('#futures-holdings [role=columnheader]')].find(n=>n.textContent.trim()==='Revision').click()");
  await check('revision column sorts numerically across multiple digits',"(()=>{const field=[...document.querySelectorAll('#futures-holdings [role=columnheader]')].find(n=>n.textContent.trim()==='Revision').getAttribute('tabulator-field');return JSON.stringify([...document.querySelectorAll('#futures-holdings .tabulator-row')].map(row=>row.querySelector('[tabulator-field='+field+']').textContent.trim()))===JSON.stringify(['2','10']);})()");

}
