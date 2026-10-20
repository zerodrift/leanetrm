import {sideSession} from './side-session.mjs';

// Settings › AI against the assistant check's preview, after its HTTP checks: identifiers are
// unique to this run and assertions look only at this run's rows.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,errors,url}){
  await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
  await evaluate(`window.__requests=[];window.__originalFetch=window.fetch;window.fetch=async(...args)=>{window.__requests.push({path:String(args[0]),body:args[1]?.body?JSON.parse(args[1].body):null});return window.__originalFetch(...args);};window.__api=async(path,body)=>{const boot=await(await window.__originalFetch('/api/bootstrap')).json();const r=await window.__originalFetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify(body)});return {status:r.status,data:await r.json()};};`);
  await check('a viewer is offered no AI settings',"document.querySelector('#nav-ai').hidden&&document.querySelector('#ai-page').hidden");
  const stamp=Date.now(),manager='browser_ai_admin_'+stamp,pricer='browser_ai_pricing_'+stamp,anthropic='browser-model-'+stamp,openai='browser-openai-'+stamp;
  await persona('admin');
  for(const [login,name,roles] of [[manager,'Browser AI administrator',['access_administrator']],[pricer,'Browser pricing administrator',['access_administrator','viewer']]]){
    const created=await evaluate(`__api('admin/create',${JSON.stringify({operationId:crypto.randomUUID(),login,name,kind:'human',roles})})`);
    if(created.status!==200)throw Error('Synthetic persona setup failed: '+JSON.stringify(created));
  }
  const rows=table=>`[...document.querySelectorAll('#${table} tbody tr')].map(r=>[...r.cells].map(c=>c.textContent))`;
  const row=(table,text)=>`${rows(table)}.find(r=>r.some(c=>c.includes(${JSON.stringify(text)})))`;
  // A model's name opens it.
  const manage=text=>evaluate(`[...document.querySelectorAll('#ai-model-table tbody tr')].find(r=>r.textContent.includes(${JSON.stringify(text)})).cells[1].querySelector('button').click()`).then(()=>wait(idle));
  const sent=path=>`window.__requests.filter(r=>r.path==='/api/settings/ai/${path}')`;
  const shown="!document.querySelector('#error').hidden&&document.querySelector('#error').textContent";

  await persona(manager);
  await check('an administrator without market data opens AI settings from Settings',"!document.querySelector('#nav-ai').hidden&&document.querySelector('#nav-ai').closest('#settings-menu')!==null");
  await click('#nav-ai');
  await check('AI settings open on Models & prices with the date controls hidden',"!document.querySelector('#ai-page').hidden&&!document.querySelector('#ai-models').hidden&&document.querySelector('#ai-providers').hidden&&document.querySelector('#page-title').textContent==='AI'&&document.querySelector('#snapshot').closest('.date-control').hidden&&document.querySelector('#error').hidden");
  await click('#ai-tab-providers');
  // The HTTP checks before this scenario may have qualified a provider, so the tab must show what the directory reports.
  await check('Providers lists every supported vendor with its protocol and the qualification its directory reports',`(async()=>{const d=await(await window.__originalFetch('/api/settings/ai/providers')).json(),r=${rows('ai-provider-table')};return r.length===5&&['Anthropic','OpenAI','Google','xAI','Meta'].every(n=>d.providers.some(p=>p.name===n))&&d.providers.every(p=>r.some(c=>c[0]===p.name&&c[1]===p.protocol&&c[2]===(p.qualified?'Qualified':'Not qualified')))&&r.some(c=>c[0]==='Google'&&c[1]==='generateContent')&&document.querySelector('#ai-providers').textContent.includes('only cumulative cap');})()`);
  await click('#ai-tab-models');
  await set('#ai-model-vendor','anthropic');await set('#ai-model-identifier',anthropic);await set('#ai-model-name','Browser model');await submit('#ai-model-form');
  await check('Add model lists the model without a price, and the audit records it',`(()=>{const r=${row('ai-model-table',anthropic)};return r&&r[0]==='Anthropic'&&r[1]==='Browser model'&&r[3]==='No price';})()&&${row('ai-model-audit',anthropic)}?.[3]==='Model added'&&document.querySelector('#error').hidden`);
  await manage(anthropic);
  await check('without market data the price form explains why no currency can be chosen, while withdrawal stays available',"!document.querySelector('#ai-model-card').hidden&&document.querySelector('#ai-model-title').textContent==='Browser model'&&!document.querySelector('#ai-price-currency-note').hidden&&document.querySelector('#ai-price-currency').disabled&&document.querySelector('#ai-price-form button[type=submit]').disabled&&!document.querySelector('#ai-withdraw-form button[type=submit]').disabled");

  await persona(pricer);await click('#nav-ai');await manage(anthropic);
  const choices=await evaluate("[...document.querySelector('#ai-price-currency').options].map(o=>({value:o.value,label:o.textContent}))");
  const usd=choices.filter(c=>c.label.startsWith('USD'));
  await check('currency choices start unchosen and tell repeated currencies apart by catalog and revision',JSON.stringify(choices[0].value===''&&usd.length>=2&&new Set(usd.map(c=>c.label)).size===usd.length&&usd.every(c=>/model_price_alternate|\(/.test(c.label))));
  await check('cache read may be not offered on a protocol that caches only on request',"!document.querySelector('#ai-none-cacheRead').closest('label').hidden");
  await set('#ai-price-currency',usd[0].value);await set('#ai-rate-uncachedInput','1.234');await set('#ai-rate-cacheWrite','2.5');await set('#ai-rate-output','9.87');
  await evaluate("document.querySelector('#ai-price-open').checked=true");await set('#ai-price-from','2026-01-01T00:00:00');
  const before=await evaluate(sent('models/price')+'.length');
  await submit('#ai-price-form');
  await check('a rate left blank is neither priced nor assumed not offered',`${shown}.includes('cache read')&&${sent('models/price')}.length===${before}`);
  await evaluate("document.querySelector('#ai-none-cacheRead').checked=true;document.querySelector('#ai-none-cacheRead').dispatchEvent(new Event('change',{bubbles:true}))");
  await submit('#ai-price-form');
  await check('a price names its tier choice explicitly',`${shown}.includes('long-prompt tier')&&${sent('models/price')}.length===${before}`);
  await evaluate("document.querySelector('#ai-tier-on').checked=true;document.querySelector('#ai-tier-on').dispatchEvent(new Event('change',{bubbles:true}))");
  await check('a tier offers exactly the rates the base set offers',"document.querySelector('#ai-tier-cacheRead').disabled&&!document.querySelector('#ai-tier-cacheWrite').disabled&&!document.querySelector('#ai-tier-threshold').disabled");
  await set('#ai-tier-threshold','123456');await set('#ai-tier-uncachedInput','2.468');await set('#ai-tier-cacheWrite','5');await set('#ai-tier-output','19.74');
  await submit('#ai-price-form');
  await check('Add price sends every member, null only where chosen',`(()=>{const b=${sent('models/price')}.at(-1)?.body;return b&&b.currency.code==='usd'&&JSON.stringify(b.rates)===JSON.stringify({uncachedInput:'1.234',cacheRead:null,cacheWrite:'2.5',output:'9.87'})&&JSON.stringify(b.tier)===JSON.stringify({threshold:123456,rates:{uncachedInput:'2.468',cacheRead:null,cacheWrite:'5',output:'19.74'}})&&b.effectiveTo===null&&Date.parse(b.effectiveFrom)===Date.parse('2026-01-01T00:00:00Z')&&b.expectedSequence===0;})()&&document.querySelector('#error').hidden`);
  await check('the model shows the revision in force with its rates and tier',`(()=>{const r=${row('ai-model-table',anthropic)};return r[3].startsWith('Revision 1 · USD')&&r.slice(4,9).join('|')==='1.234|Not offered|2.5|9.87|Above 123,456 tokens'&&r[9]==='None scheduled';})()`);
  await check('the price history shows the tier as its own rate row',`(()=>{const r=${rows('ai-price-history')};return r.length===2&&r[0][1]==='Prompts up to 123,456 tokens'&&r[1][1]==='Prompts over 123,456 tokens'&&r[1].slice(3,7).join('|')==='2.468|Not offered|5|19.74'&&r[0][8]==='Open-ended';})()&&${row('ai-model-audit',anthropic)}[3]==='Price added'`);

  await set('#ai-withdraw-from','2099-01-01T00:00:00');await submit('#ai-withdraw-form');
  await check('a withdrawal names its end explicitly',`${shown}.includes('open-ended')&&${sent('models/withdraw')}.length===0`);
  await evaluate("document.querySelector('#ai-withdraw-open').checked=true");await submit('#ai-withdraw-form');
  await check('a withdrawal schedules the end of pricing without a currency',`(()=>{const r=${row('ai-model-table',anthropic)};const b=${sent('models/withdraw')}.at(-1).body;return r[9].includes('No price')&&r[3].startsWith('Revision 1')&&b.expectedSequence===1&&b.effectiveTo===null&&!('currency' in b);})()&&${rows('ai-price-schedule')}.at(-1)[2].startsWith('No price')&&${row('ai-model-audit',anthropic)}[3]==='Price withdrawn'`);
  await set('#ai-rename-name','Browser model renamed');await submit('#ai-rename-form');
  await check('a rename changes the display name and keeps the identifier',`${row('ai-model-table',anthropic)}[1]==='Browser model renamed'&&document.querySelector('#ai-model-title').textContent==='Browser model renamed'&&${row('ai-model-audit',anthropic)}[3]==='Display name changed'`);

  await evaluate('window.scrollTo(0,0)');await shot('ai-models-desktop');
  await set('#ai-model-vendor','openai');await set('#ai-model-identifier',openai);await set('#ai-model-name','Browser automatic cache');await submit('#ai-model-form');
  await manage(openai);
  await check('cache read must be priced where caching is automatic',"document.querySelector('#ai-none-cacheRead').closest('label').hidden&&document.querySelector('#ai-price-currency').value===''&&document.querySelector('#ai-rate-uncachedInput').value===''");
  await evaluate("(()=>{window.__dropPrice=true;const send=window.fetch;window.fetch=async(...args)=>{const r=await send(...args);if(window.__dropPrice&&String(args[0])==='/api/settings/ai/models/price'){window.__dropPrice=false;throw new TypeError('Synthetic lost response after commit');}return r;};})()");
  await set('#ai-price-currency',usd[0].value);for(const [field,value] of [['uncachedInput','1'],['cacheRead','0.1'],['output','2']])await set('#ai-rate-'+field,value);
  await evaluate("for(const id of ['#ai-none-cacheWrite','#ai-tier-none','#ai-price-open']){const n=document.querySelector(id);n.checked=true;n.dispatchEvent(new Event('change',{bubbles:true}));}");
  await set('#ai-price-from','2026-02-01T00:00');await submit('#ai-price-form');
  const retained=await evaluate("JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.ai.models.request.'))))");
  await check('a lost response retains the exact price request for reconciliation',`!document.querySelector('#ai-models-pending').hidden&&${JSON.stringify(retained?.path)}==='settings/ai/models/price'&&!document.querySelector('#error').hidden&&document.querySelector('#ai-price-form button[type=submit]').disabled`);
  await check('after an uncertain outcome the tab shows the committed price and its audit',`${rows('ai-price-history')}.length===1&&${rows('ai-model-audit')}.filter(r=>r[2].includes(${JSON.stringify(openai)})).length===2`);
  await click('#ai-models-retry');
  await check('retry resends the identical request, which the server replays once',`(()=>{const sent=${sent('models/price')}.filter(r=>r.body.operationId===${JSON.stringify(retained.operationId)});return sent.length===2&&JSON.stringify(sent[0].body)===JSON.stringify(sent[1].body);})()&&document.querySelector('#ai-models-pending').hidden&&document.querySelector('#error').hidden&&${rows('ai-price-history')}.length===1&&${rows('ai-model-audit')}.filter(r=>r[2].includes(${JSON.stringify(openai)})).length===2&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.ai.models.request.'))`);

  const first='sk-browser-synthetic-key-0001',second='sk-browser-synthetic-key-0002';
  await click('#ai-tab-providers');
  await evaluate(`[...document.querySelectorAll('#ai-provider-table tbody tr')].find(r=>r.cells[0].textContent==='Anthropic').querySelector('button').click()`);await wait(idle);
  await check('Replace key names its provider',"!document.querySelector('#ai-key-form').hidden&&document.querySelector('#ai-key-title').textContent.includes('Anthropic')");
  await set('#ai-key-value',first);await submit('#ai-key-form');
  await check('a replaced key shows only its last four characters, who set it and when',`(()=>{const r=${row('ai-provider-table','Anthropic')};return r[3]==='Ends in 0001'&&r[4]==='Browser pricing administrator'&&r[5]!=='';})()&&${rows('ai-provider-audit')}[0][3]==='Key replaced'&&document.querySelector('#ai-key-value').value===''&&document.querySelector('#error').hidden`);
  await evaluate(`[...document.querySelectorAll('#ai-provider-table tbody tr')].find(r=>r.cells[0].textContent==='Anthropic').querySelector('button').click()`);await wait(idle);
  await evaluate("(()=>{window.__dropKey=true;const send=window.fetch;window.fetch=async(...args)=>{const r=await send(...args);if(window.__dropKey&&String(args[0])==='/api/settings/ai/providers/key'){window.__dropKey=false;throw new TypeError('Synthetic lost response after commit');}return r;};})()");
  await set('#ai-key-value',second);await submit('#ai-key-form');
  const keyed=await evaluate("JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.ai.key.request.'))))");
  const stores="[...Object.values(sessionStorage),...Object.values(localStorage),location.href].join('\\n')";
  await check('an unconfirmed key replacement retains its identity but never the key',`!document.querySelector('#ai-key-pending').hidden&&${JSON.stringify(Object.keys(keyed?.body??{}).sort())}.join()==='expectedVersion,vendor'&&!${stores}.includes('sk-browser-synthetic')&&document.querySelector('#ai-key-value').value===''`);
  await check('after an uncertain outcome the tab shows the current key status',`${row('ai-provider-table','Anthropic')}[3]==='Ends in 0002'`);
  // An identity switch and a failed directory read keep the unconfirmed replacement and its field,
  // and an unrelated change afterwards leaves it retained.
  await persona(manager);await persona(pricer);
  await evaluate("(()=>{window.__failModels=true;const send=window.fetch;window.fetch=async(...args)=>{if(window.__failModels&&String(args[0])==='/api/settings/ai/models'){window.__failModels=false;return new Response(JSON.stringify({code:'synthetic_unavailable'}),{status:503,headers:{'Content-Type':'application/json'}});}return send(...args);};})()");
  await click('#nav-ai');await click('#ai-tab-providers');
  await check('a failed directory read keeps the unconfirmed key replacement and its field, and refuses new changes',"!document.querySelector('#error').hidden&&!document.querySelector('#ai-key-pending').hidden&&!document.querySelector('#ai-key-form').hidden&&!document.querySelector('#ai-key-value').disabled&&document.querySelector('#ai-model-form button[type=submit]').disabled&&document.querySelector('#ai-provider-table').children.length===0");
  await click('#ai-providers-refresh');await click('#ai-tab-models');
  await set('#ai-model-vendor','openai');await set('#ai-model-identifier','browser-third-'+stamp);await set('#ai-model-name','Browser third model');await submit('#ai-model-form');
  await click('#ai-tab-providers');
  await check('an unrelated change leaves the unconfirmed key replacement retained',`!document.querySelector('#ai-key-pending').hidden&&JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.ai.key.request.')))).operationId===${JSON.stringify(keyed.operationId)}&&${row('ai-model-table','browser-third-'+stamp)}!==undefined&&document.querySelector('#error').hidden`);
  const attempts=await evaluate(sent('providers/key')+'.length'),replaced=await evaluate(rows('ai-provider-audit')+".filter(r=>r[3]==='Key replaced').length");
  await click('#ai-key-retry');
  await check('retry asks for the key again rather than storing it',`${shown}.includes('same key')&&${sent('providers/key')}.length===${attempts}`);
  await set('#ai-key-value',second);await click('#ai-key-retry');
  await check('a retry with the same key replays the original replacement once',`(()=>{const sent=${sent('providers/key')}.filter(r=>r.body.operationId===${JSON.stringify(keyed.operationId)});const audit=${rows('ai-provider-audit')}.filter(r=>r[3]==='Key replaced');return sent.length===2&&JSON.stringify(sent[0].body)===JSON.stringify(sent[1].body)&&audit.length===${replaced};})()&&document.querySelector('#ai-key-pending').hidden&&document.querySelector('#error').hidden&&document.querySelector('#ai-key-value').value===''&&!${stores}.includes('sk-browser-synthetic')`);

  await evaluate('window.scrollTo(0,0)');await shot('ai-settings-desktop');
  await click('#ai-tab-models');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate('window.scrollTo(0,0)');
  await check('AI settings avoid page-wide horizontal overflow on a phone',"document.documentElement.scrollWidth<=window.innerWidth+1");await shot('ai-settings-mobile');
  await check('AI settings expose no internal GUIDs',"!/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(document.body.innerText)");
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});

  // An independent administrator removes this one's access while the page stays open.
  const authority=await sideSession(url);
  const member=(await authority.call('admin')).members.find(m=>m.login===pricer);
  await authority.replaceRoles(member,['viewer']);
  await click('#ai-models-refresh');
  await check('a revoked administrator keeps no model, price or audit rows on screen',`!document.querySelector('#error').hidden&&['ai-model-table','ai-model-audit','ai-price-history','ai-provider-table','ai-provider-audit'].every(id=>document.querySelector('#'+id).children.length===0)&&document.querySelector('#ai-model-card').hidden`);
  await persona('viewer');
  await check('switching to a viewer hides AI settings',"document.querySelector('#nav-ai').hidden&&document.querySelector('#ai-page').hidden");
  await check('no uncaught JavaScript exceptions',JSON.stringify(errors.length===0));
}
