export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
 await command('Runtime.enable');await command('Page.enable');
 const {identifier}=await command('Page.addScriptToEvaluateOnNewDocument',{source:`
  window.__navigationReads=[];window.__pricingReads=[];const send=window.fetch;window.fetch=async(input,init)=>{
   const path=new URL(typeof input==='string'?input:input.url,location.href).pathname;window.__navigationReads.push(path);
   const body=path==='/api/evaluate'?JSON.parse(init.body):null;if(body)window.__pricingReads.push(body);
   if(path==='/api/market/current'&&window.__denySampleReset){window.__denySampleReset=false;return new Response(JSON.stringify({code:'access_denied'}),{status:403,headers:{'Content-Type':'application/json'}});}
   let response=await send(input,init);
   const mode=window.__breakHydration;
   if(mode&&(mode==='sources'?body:path==='/api/workspace/catalog')){
    window.__breakHydration=null;const data=await response.json();
    if(mode==='missing')delete data.referenceScope;
    if(mode==='revision')data.definitions.revisions[0].id='mismatched';
    if(mode==='sources')delete data.observationScope;
    response=new Response(JSON.stringify(data),{status:response.status,headers:response.headers});
   }
   if(path==='/api/workspace/navigation'&&new URLSearchParams(location.search).has('hold-navigation'))await new Promise(resolve=>window.__releaseNavigation=resolve);
   if(path==='/api/workspace/catalog'&&window.__holdWorkspace){window.__holdWorkspace=false;await new Promise(resolve=>window.__releaseWorkspace=resolve);}
   return response;
  };`});
 try{
  await command('Page.navigate',{url:url+'/'});await wait(idle+"&&document.querySelectorAll('.curve-button').length>0&&performance.getEntriesByName('leanetrm:workspace-ready').length===1");
  // Start on Curves & prices whichever page the account opens on.
  await click('#nav-market');
  await check('initial directory reads only the explicit navigation contract',"window.__navigationReads.includes('/api/workspace/navigation')&&!window.__navigationReads.includes('/api/workspace/catalog')&&!window.__navigationReads.includes('/api/workspace')&&performance.getEntriesByName('leanetrm:workspace-ready').length===1");
  await set('#commodity','test_gas');await check('navigation filters do not hydrate pricing data',"!window.__navigationReads.includes('/api/workspace/catalog')&&document.querySelectorAll('.curve-button').length>0");
  await set('#commodity','');
  const selected=await evaluate("[...document.querySelector('#snapshot').options].find(o=>o.textContent.includes('Dec 18')).value");
  await set('#snapshot',selected);await wait(idle);
  await check('selecting a market date does not load unused contracts or reference data',"!window.__navigationReads.includes('/api/workspace/catalog')");
  await evaluate("window.__firstPriceStart=performance.now();[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.startsWith('Local power forwards')).click()");await wait(idle);
  await check('first pricing reads the catalog once before the compact evaluation',"window.__navigationReads.filter(p=>p==='/api/workspace/catalog').length===1&&window.__navigationReads.indexOf('/api/workspace/catalog')<window.__navigationReads.indexOf('/api/evaluate')&&window.__pricingReads.length===1&&window.__pricingReads[0].definitionScope==='curve_dependencies_v1'&&window.__pricingReads[0].includeWorkspace!==true&&document.querySelector('#curve-chart canvas')&&document.querySelector('#inputs tbody tr')&&document.querySelector('#error').hidden");
  await check('catalog hydration sets the selected curve units before rendering the chart',"document.querySelector('#unit').textContent.trim()&&document.querySelector('#curve-chart').dataset.chartUnit===document.querySelector('#unit').textContent");
  console.log('FIRST_PRICING_WITH_HYDRATION_MS='+await evaluate('performance.now()-window.__firstPriceStart'));
  await evaluate("[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.startsWith('Local power forwards')).click()");await wait(idle);
  await check('repeated pricing uses the ordinary evaluation response without reloading catalog metadata',"window.__pricingReads.length===2&&window.__pricingReads[1].includeWorkspace!==true&&window.__navigationReads.filter(p=>p==='/api/workspace/catalog').length===1&&document.querySelector('#error').hidden");
  await persona('viewer');
  await check('changing profiles selects the uniquely latest offered market date',"(async()=>{const frames=(await(await fetch('/api/workspace/navigation')).json()).frames,newest=Math.max(...frames.map(f=>Date.parse(f.at))),latest=frames.filter(f=>Date.parse(f.at)===newest);return document.querySelector('#snapshot').value===(latest.length===1?latest[0].id:'');})()");
  // This check is about a curve chosen with no date selected, so the date the person arrived on is cleared first.
  await set('#snapshot','');await wait(idle);
  await evaluate("[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.startsWith('Local power forwards')).click()");await wait(idle);
  await check('choosing a curve without a date loads its explicit details without manufacturing prices',"document.querySelector('#snapshot').value===''&&!document.querySelector('#curve-detail').hidden&&document.querySelector('#contracts').children.length===0&&document.querySelector('#error').hidden");
  await persona('viewer');await set('#snapshot',selected);await wait(idle);
  await evaluate("window.__holdWorkspace=true;[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.startsWith('Local power forwards')).click()");await wait("typeof window.__releaseWorkspace==='function'");
  await check('workspace hydration stays busy without publishing partial prices',"document.body.getAttribute('aria-busy')==='true'&&document.querySelector('#contracts').children.length===0");
  await evaluate("(()=>{const s=document.querySelector('#snapshot');s.value=[...s.options].find(o=>o.value&&o.value!==s.value).value;window.__releaseWorkspace();})()");await wait(idle);
  await check('late hydration cannot restore prices for a different selected snapshot',"!document.querySelector('#error').hidden&&document.querySelector('#contracts').children.length===0&&document.querySelector('#inputs').children.length===0");
  await evaluate("document.querySelector('#date-context-on').value='2020-12-17';document.querySelector('#date-context-on').dispatchEvent(new Event('input',{bubbles:true}))");
  await click('#profile-open');await click('#preferences-form button[type=submit]');
  await check('presentation refresh after a cancelled hydration never treats navigation as full pricing data',"document.querySelector('#contracts').children.length===0&&document.querySelector('#curve-detail').hidden&&document.querySelector('#error').hidden");
  for(const mode of ['missing','revision','sources']){
   await persona('viewer');await set('#snapshot',selected);await wait(idle);
   await evaluate(`window.__breakHydration=${JSON.stringify(mode)};[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.startsWith('Local power forwards')).click()`);await wait(idle);
   await check('invalid hydration '+mode+' rejects before publishing prices or sources',"!document.querySelector('#error').hidden&&document.querySelector('#contracts').children.length===0&&document.querySelector('#inputs').children.length===0");
   await evaluate("window.__catalogsBeforeRetry=window.__navigationReads.filter(p=>p==='/api/workspace/catalog').length;[...document.querySelectorAll('.curve-button')].find(b=>b.textContent.startsWith('Local power forwards')).click()");await wait(idle);
   await check('a rejected '+mode+' response cannot populate the workspace cache',"window.__navigationReads.filter(p=>p==='/api/workspace/catalog').length===window.__catalogsBeforeRetry+1&&window.__pricingReads.at(-1).includeWorkspace!==true&&document.querySelector('#curve-chart canvas')&&document.querySelector('#error').hidden");
  }
  await evaluate("window.__beforeDeniedSample=window.__pricingReads.length;window.__denySampleReset=true");await click('#example');
  await check('a denied sample context reset clears prior prices before requesting prices',"!document.querySelector('#error').hidden&&window.__pricingReads.length===window.__beforeDeniedSample&&document.querySelector('#contracts').children.length===0&&document.querySelector('#inputs').children.length===0");
  await persona('viewer');await evaluate('window.__referenceStart=performance.now()');await click('#reference-area-curves');await click('#nav-table-currencies');
  await check('first reference navigation explicitly hydrates its exact pin and unit metadata',"document.querySelector('#reference-page-status').textContent.startsWith('Rows ')&&document.querySelector('#reference tbody tr')&&document.querySelector('#error').hidden");
  console.log('FIRST_REFERENCE_WITH_HYDRATION_MS='+await evaluate('performance.now()-window.__referenceStart'));
  await command('Page.navigate',{url:url+'/?hold-navigation=true'});await wait("typeof window.__releaseNavigation==='function'");
  await evaluate("document.querySelector('#date-context-on').dispatchEvent(new Event('input',{bubbles:true}));window.__releaseNavigation()");await wait(idle);
  await check('a reset during navigation cannot publish a stale directory or startup ready mark',"!document.querySelector('#error').hidden&&document.querySelectorAll('.curve-button').length===0&&performance.getEntriesByName('leanetrm:workspace-ready').length===0&&!window.__navigationReads.includes('/api/workspace/catalog')");
 }finally{await command('Page.removeScriptToEvaluateOnNewDocument',{identifier});}
}
