// Returning to a page retains its location; completed Blotter reads require a fresh server check before reuse.
export async function run({command,evaluate,wait,click,persona,check,idle,url}){
  await command('Page.enable');await command('Runtime.enable');
  const installed=await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.__navigationReads=[];const original=window.fetch;window.fetch=async(...args)=>{const path=String(args[0]);if(path.startsWith('/api/trading/'))window.__navigationReads.push({path,body:args[1]?.body});const response=await original(...args);if(path.startsWith('/api/bootstrap')&&location.search==='?jobs-without-default=1'&&response.ok){const body=await response.clone().json();if(body.access)body.access.permissions=[];return new Response(JSON.stringify(body),{status:200,headers:{'Content-Type':'application/json'}});}return response;};`});
  const open=async hash=>{await command('Page.navigate',{url:'about:blank'});await command('Page.navigate',{url:url+'/'+hash});await wait(`document.querySelector('#nav-market')&&!document.querySelector('#nav-market').hidden&&${idle}`);};
  try{
    await open('');await persona('trader');await click('#nav-market');
    await evaluate('window.__beforeReload=true');await command('Page.reload');await wait(`!window.__beforeReload&&document.querySelector('#nav-market')&&!document.querySelector('#nav-market').hidden&&${idle}`);
    await check('reload reopens the current page instead of the first permitted page',"!document.querySelector('#market-page').hidden&&document.querySelector('#futures-page').hidden");
    await open('#reference/interest_rate_definitions');
    await check('Reference data accepts the explicitly selected category catalog',"!document.querySelector('#reference-page').hidden&&document.querySelector('#error').hidden&&document.querySelector('#reference tbody tr')!==null");
    await click('#nav-futures');
    await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    const reads=()=>evaluate("window.__navigationReads.filter(r=>['/api/trading/workspace','/api/trading/workspace/stream','/api/trading/directory','/api/trading/positions'].includes(r.path)).length");
    await check('the authorized Blotter has rows before cache validation',"document.querySelectorAll('#desk-positions .tabulator-row').length>0");
    const before=await reads(),context=await evaluate("document.querySelector('#futures-page').dataset.pricingContext");
    await click('#nav-market');
    await evaluate(`window.__navigationFetch=window.fetch;window.fetch=async(...args)=>{const response=await window.__navigationFetch(...args);if(String(args[0])==='/api/trading/freshness')await new Promise(resolve=>window.__releaseFreshness=resolve);return response;};document.querySelector('#nav-futures').click()`);
    await wait("typeof window.__releaseFreshness==='function'");
    await check('retained values stay hidden while current access is checked',"getComputedStyle(document.querySelector('#blotter-dock')).visibility==='hidden'");
    await evaluate('window.__releaseFreshness();window.fetch=window.__navigationFetch');await wait(idle);
    await check('returning to an unchanged Blotter checks the server without refetching positions or valuations',`window.__navigationReads.some(r=>r.path==='/api/trading/freshness')&&document.querySelector('#futures-page').dataset.pricingContext===${JSON.stringify(context)}&&document.querySelector('#error').hidden`);
    await check('an unchanged Blotter keeps all completed data reads',`${await reads()}===${before}`);
    // An asynchronous selection change while validation is in flight must not reveal the old values.
    const oldSnapshot=await evaluate("document.querySelector('#snapshot').value");
    await click('#nav-market');
    await evaluate(`delete window.__releaseFreshness;window.__navigationFetch=window.fetch;window.fetch=async(...args)=>{const response=await window.__navigationFetch(...args);if(String(args[0])==='/api/trading/freshness')await new Promise(resolve=>window.__releaseFreshness=resolve);return response;};document.querySelector('#nav-futures').click()`);
    await wait("typeof window.__releaseFreshness==='function'");
    await evaluate("document.querySelector('#snapshot').value=[...document.querySelector('#snapshot').options].find(o=>o.value&&o.value!==document.querySelector('#snapshot').value).value;window.__releaseFreshness();window.fetch=window.__navigationFetch");await wait(idle);
    await check('a late freshness reply for another selection cannot reveal retained values',"getComputedStyle(document.querySelector('#blotter-dock')).visibility==='hidden'");
    await evaluate(`document.querySelector('#snapshot').value=${JSON.stringify(oldSnapshot)}`);await click('#nav-futures');
    await click('#nav-market');
    await evaluate(`window.__navigationFetch=window.fetch;window.fetch=async(...args)=>{const response=await window.__navigationFetch(...args);if(String(args[0])!=='/api/trading/freshness')return response;const body=await response.clone().json();return new Response(JSON.stringify({...body,version:'0'.repeat(64)}),{status:200,headers:{'Content-Type':'application/json'}});}`);
    await click('#nav-futures');await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    await check('a changed server version reloads the Blotter',`${await reads()}>${before}&&document.querySelector('#futures-page').dataset.pricingContext!==${JSON.stringify(context)}&&document.querySelector('#error').hidden`);
    await evaluate('window.fetch=window.__navigationFetch');
    // A denied validation cannot redisplay previously authorized values.
    await click('#nav-market');
    await evaluate(`window.__navigationFetch=window.fetch;window.fetch=(...args)=>String(args[0])==='/api/trading/freshness'?Promise.resolve(new Response(JSON.stringify({code:'access_denied'}),{status:403,headers:{'Content-Type':'application/json'}})):window.__navigationFetch(...args)`);
    await click('#nav-futures');
    await check('failed freshness authorization clears retained Blotter rows',"!document.querySelector('#error').hidden&&document.querySelectorAll('#desk-positions .tabulator-row').length===0");
    await persona('admin');await click('#nav-jobs');
    await evaluate('window.__beforeReload=true');await command('Page.navigate',{url:url+'/?jobs-without-default=1'});
    await wait(`!window.__beforeReload&&document.querySelector('#jobs-page')&&${idle}`);
    await check('Jobs restores even when the account has no default business page',"!document.querySelector('#jobs-page').hidden&&document.querySelector('#no-access-page').hidden&&document.querySelector('#nav-market').hidden&&document.querySelector('#error').hidden");
  }finally{await command('Page.removeScriptToEvaluateOnNewDocument',{identifier:installed.identifier});}
}
