// Returning to a page retains its location; completed Blotter reads require a fresh server check before reuse.
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
  await command('Page.enable');await command('Runtime.enable');
  const installed=await command('Page.addScriptToEvaluateOnNewDocument',{source:`window.__navigationReads=[];const original=window.fetch;window.fetch=async(...args)=>{const path=String(args[0]);if(path.startsWith('/api/trading/'))window.__navigationReads.push({path,body:args[1]?.body});const response=await original(...args);if(path.startsWith('/api/bootstrap')&&location.search==='?jobs-without-default=1'&&response.ok){const body=await response.clone().json();if(body.access)body.access.permissions=[];return new Response(JSON.stringify(body),{status:200,headers:{'Content-Type':'application/json'}});}return response;};`});
  const open=async hash=>{await command('Page.navigate',{url:'about:blank'});await command('Page.navigate',{url:url+'/'+hash});await wait(`document.querySelector('#nav-market')&&!document.querySelector('#nav-market').hidden&&${idle}`);};
  try{
    await open('');await persona('risk');await click('#nav-market');
    await evaluate('window.__beforeReload=true');await command('Page.reload');await wait(`!window.__beforeReload&&document.querySelector('#nav-market')&&!document.querySelector('#nav-market').hidden&&${idle}`);
    await check('reload reopens the current page instead of the first permitted page',"!document.querySelector('#market-page').hidden&&document.querySelector('#futures-page').hidden");
    await open('#reference/interest_rate_definitions');
    await check('Reference data accepts the explicitly selected category catalog',"!document.querySelector('#reference-page').hidden&&document.querySelector('#error').hidden&&document.querySelector('#reference tbody tr')!==null");
    const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')&&w.initialFrameIds.includes(f.id)).id;})()");
    await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
    await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    const reads=()=>evaluate("window.__navigationReads.filter(r=>['/api/trading/workspace','/api/trading/workspace/stream','/api/trading/directory','/api/trading/positions'].includes(r.path)).length");
    await check('the authorized Blotter has rows before cache validation',"document.querySelectorAll('#desk-positions .tabulator-row').length>0");
    let before=await reads();const context=await evaluate("document.querySelector('#futures-page').dataset.pricingContext");
    await click('#nav-market');
    await evaluate(`window.__navigationFetch=window.fetch;window.fetch=async(...args)=>{const response=await window.__navigationFetch(...args);if(String(args[0])==='/api/trading/freshness')await new Promise(resolve=>window.__releaseFreshness=resolve);return response;};document.querySelector('#nav-futures').click()`);
    await wait("typeof window.__releaseFreshness==='function'");
    await check('retained values stay hidden while current access is checked',"getComputedStyle(document.querySelector('#blotter-dock')).visibility==='hidden'");
    await evaluate('window.__releaseFreshness();window.fetch=window.__navigationFetch');await wait(idle);
    await check('returning to an unchanged Blotter checks the server without refetching positions or valuations',`window.__navigationReads.some(r=>r.path==='/api/trading/freshness')&&document.querySelector('#futures-page').dataset.pricingContext===${JSON.stringify(context)}&&document.querySelector('#error').hidden`);
    await check('an unchanged Blotter keeps all completed data reads',`${await reads()}===${before}`);
    await set('#blotter-name','Synthetic retained desk');await evaluate("document.querySelector('#blotter-sidebar').checked=true");await click('#blotter-save');await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    await click('#blotter-risk');await wait("!document.querySelector('#risk-fields').disabled");
    await click('input[name=risk-choice][value=held]');
    const sizes=await evaluate("document.querySelectorAll('#risk-sizes input').length");
    for(let n=0;n<sizes;n++)await set(`#risk-size-${n}`,'0.05');
    await submit('#risk-form',60000);
    await wait("!document.querySelector('#desk-run').hidden&&document.querySelector('#desk-lens [data-lens=risk]')?.getAttribute('aria-pressed')==='true'&&"+idle,60000);
    await click('#desk-lens [data-lens=ladder]');await wait(idle);
    await evaluate("document.querySelector('#desk-risk-full').open=true");await wait("document.querySelectorAll('#desk-risk-full-results [data-risk-factor]').length>0");
    await evaluate("[...document.querySelectorAll('#desk-risk-full-results [data-risk-factor]')].at(-1).click()");
    const factor=await evaluate("document.querySelector('#desk-risk-full-results .risk-factor-detail h3').textContent");
    const riskReads=await reads(),runTitle=await evaluate("document.querySelector('#desk-run-title').textContent");
    await click('#nav-market');await click('#nav-futures');await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    await check('leaving and returning retains the completed risk run and selected delta ladder without valuing positions again',
      `${await reads()}===${riskReads}&&!document.querySelector('#desk-run').hidden&&document.querySelector('#desk-run-title').textContent===${JSON.stringify(runTitle)}&&document.querySelector('#desk-lens [data-lens=ladder]')?.getAttribute('aria-pressed')==='true'`,
      "({title:document.querySelector('#desk-run-title').textContent,runHidden:document.querySelector('#desk-run').hidden,notice:document.querySelector('#notice').textContent,reads:window.__navigationReads})");
    await click('#nav-market');await click('#nav-blotter-views button[aria-label="Blotter view · Synthetic retained desk"]');await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    await check('returning through the currently selected saved view retains risk and the delta ladder without reloading the Blotter',
      `${await reads()}===${riskReads}&&!document.querySelector('#desk-run').hidden&&document.querySelector('#desk-lens [data-lens=ladder]')?.getAttribute('aria-pressed')==='true'`,
      "({runHidden:document.querySelector('#desk-run').hidden,notice:document.querySelector('#notice').textContent})");
    before=await reads();
    await check('returning keeps the expanded full report on its selected factor',`document.querySelector('#desk-risk-full').open&&document.querySelector('#desk-risk-full-results .risk-factor-detail h3')?.textContent===${JSON.stringify(factor)}`);
    await click('#nav-market');
    await evaluate(`delete window.__releaseFreshness;window.__navigationFetch=window.fetch;window.fetch=async(...args)=>{const response=await window.__navigationFetch(...args);if(String(args[0])==='/api/trading/freshness')await new Promise(resolve=>window.__releaseFreshness=resolve);return response;};document.querySelector('#nav-futures').click()`);
    await wait("typeof window.__releaseFreshness==='function'");
    await check('completed risk and full report remain hidden during current authorization validation',"getComputedStyle(document.querySelector('#blotter-dock')).visibility==='hidden'&&getComputedStyle(document.querySelector('#desk-risk-full-results')).visibility==='hidden'");
    await evaluate('window.__releaseFreshness();window.fetch=window.__navigationFetch');await wait(idle);
    // An asynchronous selection change while validation is in flight must not reveal the old values.
    const oldSnapshot=await evaluate("document.querySelector('#snapshot').value");
    await click('#nav-market');
    await evaluate(`delete window.__releaseFreshness;window.__navigationFetch=window.fetch;window.fetch=async(...args)=>{const response=await window.__navigationFetch(...args);if(String(args[0])==='/api/trading/freshness')await new Promise(resolve=>window.__releaseFreshness=resolve);return response;};document.querySelector('#nav-futures').click()`);
    await wait("typeof window.__releaseFreshness==='function'");
    await evaluate("document.querySelector('#snapshot').value=[...document.querySelector('#snapshot').options].find(o=>o.value&&o.value!==document.querySelector('#snapshot').value).value;window.__releaseFreshness();window.fetch=window.__navigationFetch");await wait(idle);
    await check('a late freshness reply for another selection cannot reveal retained values',"getComputedStyle(document.querySelector('#blotter-dock')).visibility==='hidden'");
    await evaluate(`document.querySelector('#snapshot').value=${JSON.stringify(oldSnapshot)}`);await click('#nav-futures');
    await click('#nav-market');
    await evaluate(`window.__navigationFetch=window.fetch;window.fetch=async(...args)=>{const response=await window.__navigationFetch(...args);if(!['/api/trading/freshness','/api/trading/changes'].includes(String(args[0])))return response;const body=await response.clone().json();return new Response(JSON.stringify({...body,version:'0'.repeat(64)}),{status:200,headers:{'Content-Type':'application/json'}});}`);
    await click('#nav-futures');await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
    await check('a changed server version reloads the Blotter',`${await reads()}>${before}&&document.querySelector('#futures-page').dataset.pricingContext!==${JSON.stringify(context)}&&document.querySelector('#error').hidden`);
    await evaluate('window.fetch=window.__navigationFetch');
    // A denied validation cannot redisplay previously authorized values.
    await click('#nav-market');
    await evaluate(`window.__navigationFetch=window.fetch;window.fetch=(...args)=>String(args[0])==='/api/trading/freshness'?Promise.resolve(new Response(JSON.stringify({code:'access_denied'}),{status:403,headers:{'Content-Type':'application/json'}})):window.__navigationFetch(...args)`);
    await click('#nav-futures');
    await check('failed freshness authorization clears retained Blotter rows',"!document.querySelector('#error').hidden&&document.querySelectorAll('#desk-positions .tabulator-row').length===0&&document.querySelector('#desk-risk-full-results').childElementCount===0");
    await persona('admin');await click('#nav-jobs');
    await evaluate('window.__beforeReload=true');await command('Page.navigate',{url:url+'/?jobs-without-default=1'});
    await wait(`!window.__beforeReload&&document.querySelector('#jobs-page')&&${idle}`);
    await check('Jobs restores even when the account has no default business page',"!document.querySelector('#jobs-page').hidden&&document.querySelector('#no-access-page').hidden&&document.querySelector('#nav-market').hidden&&document.querySelector('#error').hidden");
  }finally{await command('Page.removeScriptToEvaluateOnNewDocument',{identifier:installed.identifier});}
}
