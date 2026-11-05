// Reads on a hidden settings tab must wait for the person to open that tab.
export async function run({command,evaluate,wait,click,persona,check,idle,url}){
  await command('Runtime.enable');await command('Page.enable');
  const {identifier}=await command('Page.addScriptToEvaluateOnNewDocument',{source:`
    window.__lazyRequests=[];window.__lazyShapes=[];window.__lazyDeskShapes=[];window.__deferLibraryCurve='future';window.__deferHeldShapes=true;
    const Observer=window.IntersectionObserver;
    window.IntersectionObserver=class extends Observer{constructor(callback,options){super((entries,self)=>callback(entries.filter(entry=>entry.target.dataset?.curve!==window.__deferLibraryCurve&&!(window.__deferHeldShapes&&entry.target.dataset?.shapeRow!==undefined)),self),options);}};
    const original=window.fetch;
    window.fetch=(input,init)=>{const path=new URL(typeof input==='string'?input:input.url,location.href).pathname;window.__lazyRequests.push(path);if(path==='/api/market/curve-shapes'){const body=JSON.parse(init.body);window.__lazyShapes.push(body);if(document.querySelector('#futures-page:not([hidden])'))window.__lazyDeskShapes.push(body);}return original(input,init);};
  `});
  try{
    await command('Page.navigate',{url:url+'/'});await wait(idle);
    await persona('viewer');await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);await click('#nav-market');await click('#curve-all-markets');
    await evaluate("[...document.querySelectorAll('.curve-button')].find(button=>button.querySelector('span')?.textContent==='Local power forwards').click()");await wait(idle);
    await check('an unrevealed library curve has no optional shape read',"!window.__lazyShapes.some(read=>read.curves.includes('future'))");
    await evaluate("(()=>{window.__deferLibraryCurve='';const button=document.querySelector('.curve-button[data-curve=future]');button.closest('details').open=false;})()");
    await evaluate('new Promise(resolve=>requestAnimationFrame(resolve))');
    await evaluate("(()=>{const button=document.querySelector('.curve-button[data-curve=future]');button.closest('details').open=true;button.scrollIntoView({block:'center'});})()");
    await wait("window.__lazyShapes.some(read=>read.curves.includes('future'))");
    await check('rendering curve detail rebinds newly visible library shape reads',"window.__lazyShapes.some(read=>read.curves.includes('future'))");
    await persona('trader');await click('#nav-futures');await wait(idle);
    await click('#nav-market');await evaluate('window.__lazyShapes=[];window.__lazyDeskShapes=[]');await click('#nav-futures');await wait(idle);
    await check('returning to a cached Blotter does not fetch hidden curve cells',"window.__lazyDeskShapes.length===0","({reads:window.__lazyShapes,positionHidden:document.querySelector('#position-details').hidden,details:document.querySelector('#position-details').textContent})");
    await click('#desk-positions .tabulator-row:not(.tabulator-group)');
    await wait("window.__lazyDeskShapes.length>0&&document.querySelector('#position-details').hidden===false");
    await check('a position opened after cached Blotter reuse loads only its demanded curve',"window.__lazyDeskShapes.length===1&&window.__lazyDeskShapes[0].curves.length===1",'window.__lazyDeskShapes');
    await persona('admin');await evaluate('window.__lazyRequests=[]');await click('#nav-ai');
    await check('opening Models does not read hidden AI tabs',"!window.__lazyRequests.some(path=>['/api/settings/ai/usage','/api/settings/ai/agents','/api/settings/ai/page-prompts'].includes(path))",'window.__lazyRequests');
    await click('#ai-tab-providers');await click('#ai-tab-models');
    await check('Models and Providers reuse their shared directory without reading it again on a tab switch',"window.__lazyRequests.filter(path=>path==='/api/settings/ai/models').length===1&&window.__lazyRequests.filter(path=>path==='/api/settings/ai/providers').length===1",'window.__lazyRequests');
    await evaluate('window.__lazyRequests=[]');await click('#ai-tab-agents');
    await check('opening Agents reads its own directory',"window.__lazyRequests.filter(path=>path==='/api/settings/ai/agents').length===1&&!window.__lazyRequests.includes('/api/settings/ai/usage')",'window.__lazyRequests');
  }finally{await persona('viewer');await click('#nav-market');await command('Page.removeScriptToEvaluateOnNewDocument',{identifier});}
}
