import {sideSession} from './side-session.mjs';

// Settings › AI against the assistant check's preview, after its HTTP checks: identifiers are
// unique to this run and assertions look only at this run's rows.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,errors,url}){
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
  await check('Providers lists every supported vendor with its protocol, the state and the usage mapping its directory reports',`(async()=>{const d=await(await window.__originalFetch('/api/settings/ai/providers')).json(),r=${rows('ai-provider-table')};const state=p=>p.enabled?(p.qualified?'Enabled':'Enabled · no longer qualified'):(p.qualified?'Disabled · qualified':'Disabled · not qualified');return r.length===5&&['Anthropic','OpenAI','Google','xAI','Meta'].every(n=>d.providers.some(p=>p.name===n))&&d.providers.every(p=>r.some(c=>c[0]===p.name&&c[1]===p.protocol&&c[2]===state(p)&&c[3]===(p.mapping?p.mapping.mapping+' · confirmed '+p.mapping.confirmedOn:'None confirmed')))&&r.some(c=>c[0]==='Google'&&c[1]==='generateContent'&&c[3]==='None confirmed')&&document.querySelector('#ai-providers').textContent.includes('only cumulative cap');})()`);
  await click('#ai-tab-models');
  await set('#ai-model-vendor','anthropic');await set('#ai-model-identifier',anthropic);await set('#ai-model-name','Browser model');await submit('#ai-model-form');
  await check('Add model lists the model without a price, and the audit records it',`(()=>{const r=${row('ai-model-table',anthropic)};return r&&r[0]==='Anthropic'&&r[1]==='Browser model'&&r[3]==='Disabled · not qualified'&&r[4]==='No price';})()&&${row('ai-model-audit',anthropic)}?.[3]==='Model added'&&document.querySelector('#error').hidden`);
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
  await check('the model shows the revision in force with its rates and tier',`(()=>{const r=${row('ai-model-table',anthropic)};return r[4].startsWith('Revision 1 · USD')&&r.slice(5,10).join('|')==='1.234|Not offered|2.5|9.87|Above 123,456 tokens'&&r[10]==='None scheduled';})()`);
  await check('the price history shows the tier as its own rate row',`(()=>{const r=${rows('ai-price-history')};return r.length===2&&r[0][1]==='Prompts up to 123,456 tokens'&&r[1][1]==='Prompts over 123,456 tokens'&&r[1].slice(3,7).join('|')==='2.468|Not offered|5|19.74'&&r[0][8]==='Open-ended';})()&&${row('ai-model-audit',anthropic)}[3]==='Price added'`);

  await set('#ai-withdraw-from','2099-01-01T00:00:00');await submit('#ai-withdraw-form');
  await check('a withdrawal names its end explicitly',`${shown}.includes('open-ended')&&${sent('models/withdraw')}.length===0`);
  await evaluate("document.querySelector('#ai-withdraw-open').checked=true");await submit('#ai-withdraw-form');
  await check('a withdrawal schedules the end of pricing without a currency',`(()=>{const r=${row('ai-model-table',anthropic)};const b=${sent('models/withdraw')}.at(-1).body;return r[10].includes('No price')&&r[4].startsWith('Revision 1')&&b.expectedSequence===1&&b.effectiveTo===null&&!('currency' in b);})()&&${rows('ai-price-schedule')}.at(-1)[2].startsWith('No price')&&${row('ai-model-audit',anthropic)}[3]==='Price withdrawn'`);
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
  // The retained request belongs to its identity, as the key replacement and provider requests below do.
  await persona(manager);
  await check('another identity is not offered a model request it did not send',"document.querySelector('#ai-models-pending').hidden");
  await persona(pricer);await click('#nav-ai');
  await check('a retained model request is offered again after an identity switch',`!document.querySelector('#ai-models-pending').hidden&&JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.ai.models.request.')))).operationId===${JSON.stringify(retained.operationId)}`);
  await manage(openai);
  await click('#ai-models-retry');
  await check('retry resends the identical request, which the server replays once',`(()=>{const sent=${sent('models/price')}.filter(r=>r.body.operationId===${JSON.stringify(retained.operationId)});return sent.length===2&&JSON.stringify(sent[0].body)===JSON.stringify(sent[1].body);})()&&document.querySelector('#ai-models-pending').hidden&&document.querySelector('#error').hidden&&${rows('ai-price-history')}.length===1&&${rows('ai-model-audit')}.filter(r=>r[2].includes(${JSON.stringify(openai)})).length===2&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.ai.models.request.'))`);

  const first='sk-browser-synthetic-key-0001',second='sk-browser-synthetic-key-0002';
  // A provider's name opens its card, which holds its actions.
  const openProvider=name=>evaluate(`[...document.querySelectorAll('#ai-provider-table tbody tr')].find(r=>r.cells[0].textContent===${JSON.stringify(name)}).cells[0].querySelector('button').click()`).then(()=>wait(idle));
  await click('#ai-tab-providers');
  await openProvider('Anthropic');await click('#ai-provider-key');
  await check('Replace key names its provider',"!document.querySelector('#ai-key-form').hidden&&document.querySelector('#ai-key-title').textContent.includes('Anthropic')");
  await set('#ai-key-value',first);await submit('#ai-key-form');
  await check('a replaced key shows only its last four characters, who set it and when',`(()=>{const r=${row('ai-provider-table','Anthropic')};return r[6]==='Ends in 0001'&&r[7]==='Browser pricing administrator'&&r[8]!=='';})()&&${rows('ai-provider-audit')}[0][3]==='Key replaced'&&document.querySelector('#ai-key-value').value===''&&document.querySelector('#error').hidden`);
  await click('#ai-provider-key');
  await evaluate("(()=>{window.__dropKey=true;const send=window.fetch;window.fetch=async(...args)=>{const r=await send(...args);if(window.__dropKey&&String(args[0])==='/api/settings/ai/providers/key'){window.__dropKey=false;throw new TypeError('Synthetic lost response after commit');}return r;};})()");
  await set('#ai-key-value',second);await submit('#ai-key-form');
  const keyed=await evaluate("JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.ai.key.request.'))))");
  const stores="[...Object.values(sessionStorage),...Object.values(localStorage),location.href].join('\\n')";
  await check('an unconfirmed key replacement retains its identity but never the key',`!document.querySelector('#ai-key-pending').hidden&&${JSON.stringify(Object.keys(keyed?.body??{}).sort())}.join()==='expectedVersion,vendor'&&!${stores}.includes('sk-browser-synthetic')&&document.querySelector('#ai-key-value').value===''`);
  await check('after an uncertain outcome the tab shows the current key status',`${row('ai-provider-table','Anthropic')}[6]==='Ends in 0002'`);
  // An identity switch and a failed directory read keep the unconfirmed replacement and its field,
  // and an unrelated change afterwards leaves it retained.
  await persona(manager);
  await check('another identity is not offered a key replacement it did not send',"document.querySelector('#ai-key-pending').hidden&&document.querySelector('#ai-key-form').hidden");
  await persona(pricer);
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

  // The provider's card records terms, attests and enables; a lost response is retained like a model change.
  await openProvider('Anthropic');
  await check('the provider card shows its usage mapping and key, and offers Enable once the replaced key leaves it unattested',
    "!document.querySelector('#ai-provider-card').hidden&&document.querySelector('#ai-provider-facts').textContent.includes('messages_v1')&&document.querySelector('#ai-provider-facts').textContent.includes('key ends in 0002')&&!document.querySelector('#ai-provider-enable').hidden&&document.querySelector('#ai-provider-disable').hidden");
  const terms={'#ai-terms-account':'Synthetic browser organization','#ai-terms-retention':'Synthetic: inputs are deleted within 7 days.','#ai-terms-retention-source':'https://example.invalid/browser/retention','#ai-terms-retention-on':'2026-10-01',
    '#ai-terms-training':'Synthetic: content is not used for training.','#ai-terms-training-source':'https://example.invalid/browser/training','#ai-terms-training-on':'2026-10-01',
    '#ai-terms-owner':'Synthetic: owners can view request logs.','#ai-terms-owner-source':'https://example.invalid/browser/owner','#ai-terms-owner-on':'2026-10-01'};
  for(const [id,value] of Object.entries(terms))if(id!=='#ai-terms-training-source')await set(id,value);
  await submit('#ai-terms-form');
  await check('terms name every source before they are sent',`${shown}.includes('source of the training-use terms')&&${sent('providers/terms')}.length===0`);
  await set('#ai-terms-training-source',terms['#ai-terms-training-source']);await submit('#ai-terms-form');
  await check('recorded terms show each term with its source and date, and leave the provider unattested',`(()=>{const b=${sent('providers/terms')}.at(-1).body,r=${rows('ai-terms-table')};return b.accountType==='Synthetic browser organization'&&b.trainingCheckedOn==='2026-10-01'&&r.length===3&&r[1][0]==='Training use'&&r[1][2]==='https://example.invalid/browser/training'&&document.querySelector('#ai-terms-summary').textContent.includes('Synthetic browser organization');})()&&${row('ai-provider-table','Anthropic')}[5]==='Not attested'&&${rows('ai-provider-audit')}[0][3]==='Terms recorded'&&document.querySelector('#error').hidden`);
  await submit('#ai-attest-form');
  await check('Attest needs its statement confirmed',`${shown}.includes('Confirm the attestation')&&${sent('providers/attest')}.length===0`);
  // A confirmation belongs to the key and terms it was given for: replacing the key clears it.
  await evaluate("document.querySelector('#ai-attest-confirm').checked=true");
  await click('#ai-provider-key');await set('#ai-key-value','sk-browser-synthetic-key-0003');await submit('#ai-key-form');
  const statement=`(async()=>{const a=(await(await window.__originalFetch('/api/settings/ai/providers')).json()).providers.find(p=>p.vendor==='anthropic'),s=document.querySelector('#ai-attest-statement').textContent;return s.includes('key, version '+a.credential.version+', ends in 0003')&&s.includes('terms version '+a.terms.version+' ')&&s.includes('“Synthetic browser organization”');})()`;
  await check('a new key clears the confirmation, and the statement names the new key and the terms version it binds',
    `(async()=>!document.querySelector('#ai-attest-confirm').checked&&document.querySelector('#ai-attest-confirm').getAttribute('aria-describedby')==='ai-attest-statement'&&await ${statement}&&document.querySelector('#error').hidden)()`);
  await evaluate("document.querySelector('#ai-attest-confirm').checked=true");await submit('#ai-attest-form');
  await check('an attested provider is qualified, stays disabled until Enable and offers no second attestation',`${row('ai-provider-table','Anthropic')}[2]==='Disabled · qualified'&&${row('ai-provider-table','Anthropic')}[5]!=='Not attested'&&${rows('ai-provider-audit')}[0][3]==='Attested'&&!document.querySelector('#ai-attest-confirm').checked&&document.querySelector('#ai-attest-form button[type=submit]').disabled&&document.querySelector('#ai-attest-statement').textContent.includes('are attested')&&document.querySelector('#error').hidden`);
  const enabledBefore=await evaluate(`${rows('ai-provider-audit')}.filter(r=>r[2]==='Anthropic'&&r[3]==='Enabled').length`);
  await evaluate("(()=>{window.__dropEnable=true;const send=window.fetch;window.fetch=async(...args)=>{const r=await send(...args);if(window.__dropEnable&&String(args[0])==='/api/settings/ai/providers/enable'){window.__dropEnable=false;throw new TypeError('Synthetic lost response after commit');}return r;};})()");
  await click('#ai-provider-enable');
  const enabling=await evaluate("JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.ai.providers.request.'))))");
  await check('a lost Enable response retains the request while the tab shows the provider as saved',`!document.querySelector('#ai-providers-pending').hidden&&${JSON.stringify(enabling?.path)}==='settings/ai/providers/enable'&&${row('ai-provider-table','Anthropic')}[2]==='Enabled'&&document.querySelector('#ai-provider-disable').disabled&&document.querySelector('#ai-terms-form button[type=submit]').disabled`);
  // The retained request belongs to its identity: another identity is not offered it, and it returns with its own.
  await persona(manager);
  await check("another identity is not offered a provider request it did not send","document.querySelector('#ai-providers-pending').hidden");
  await persona(pricer);await click('#nav-ai');await click('#ai-tab-providers');
  await check('a retained provider request is offered again after an identity switch',`!document.querySelector('#ai-providers-pending').hidden&&JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.ai.providers.request.')))).operationId===${JSON.stringify(enabling.operationId)}`);
  await click('#ai-providers-retry');
  await check('retry replays the original Enable once, and the audit records one Enable',`(()=>{const s=${sent('providers/enable')}.filter(r=>r.body.operationId===${JSON.stringify(enabling.operationId)});return s.length===2&&JSON.stringify(s[0].body)===JSON.stringify(s[1].body);})()&&${rows('ai-provider-audit')}.filter(r=>r[2]==='Anthropic'&&r[3]==='Enabled').length===${enabledBefore}+1&&document.querySelector('#ai-providers-pending').hidden&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.ai.providers.request.'))&&document.querySelector('#error').hidden`);

  // A model's card records its capabilities under the efforts its provider's mapping states, then enables it.
  // The efforts offered come from the directory: a directory stating others offers those instead.
  const offered="JSON.stringify([...document.querySelectorAll('#ai-capability-efforts input')].map(i=>i.value))";
  await evaluate("(()=>{window.__stubEfforts=true;const send=window.fetch;window.fetch=async(...args)=>{const r=await send(...args);if(window.__stubEfforts&&String(args[0])==='/api/settings/ai/providers'&&!args[1]?.body){const d=await r.json();for(const p of d.providers)if(p.mapping)p.mapping.efforts=['max','low'];return new Response(JSON.stringify(d),{status:200,headers:{'Content-Type':'application/json'}});}return r;};})()");
  await click('#ai-tab-models');await click('#ai-models-refresh');await manage(anthropic);
  await check("the model card offers the efforts its directory states",`${offered}===JSON.stringify(['max','low'])`);
  await evaluate('window.__stubEfforts=false');await click('#ai-models-refresh');await manage(anthropic);
  await check("the model card offers exactly the efforts its provider's applied mapping states and records nothing yet",`(async()=>{const d=await(await window.__originalFetch('/api/settings/ai/providers')).json();return ${offered}===JSON.stringify(d.providers.find(p=>p.vendor==='anthropic').mapping.efforts)&&document.querySelector('#ai-capability-summary').textContent.includes('No capabilities')&&document.querySelectorAll('#ai-capability-table tbody tr').length===0&&!document.querySelector('#ai-model-enable').hidden;})()`);
  await click('#ai-model-enable');
  await check('Enable refuses a model without capabilities, retains nothing and keeps focus on Enable',`${shown}.includes('capabilities are recorded')&&document.querySelector('#ai-models-pending').hidden&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.ai.models.request.'))&&document.activeElement===document.querySelector('#ai-model-enable')`);
  await set('#ai-capability-limit','64000');
  await evaluate("for(const effort of ['low','high'])[...document.querySelectorAll('#ai-capability-efforts input')].find(i=>i.value===effort).checked=true");
  for(const fact of ['outputLimit','reasoning','identifierStability','acceptedServed','tools','streaming']){await set('#ai-source-'+fact,'https://example.invalid/browser/'+fact);await set('#ai-checked-'+fact,'2026-10-01');}
  await submit('#ai-capability-form');
  await check("capabilities name the other served identifiers or that the vendor reports only the model's own",`${shown}.includes('reports only its own')&&${sent('models/edit')}.length===0`);
  await evaluate("document.querySelector('#ai-capability-only-own').checked=true");await submit('#ai-capability-form');
  await check('recorded capabilities show each fact with its value, source and date',`(()=>{const b=${sent('models/edit')}.at(-1).body,r=${rows('ai-capability-table')};return b.outputLimit===64000&&JSON.stringify(b.reasoningEfforts)==='["low","high"]'&&JSON.stringify(b.acceptedServed)==='[]'&&b.streamingSource==='https://example.invalid/browser/streaming'&&r.length===6&&r[0][1]==='64,000 tokens'&&r[1][1]==='low, high'&&r[3][1]==='Only its own'&&r[5][2]==='https://example.invalid/browser/streaming';})()&&document.querySelector('#ai-capability-summary').textContent.includes('Version 1')&&${row('ai-model-table',anthropic)}[3]==='Disabled · qualified'&&${row('ai-model-audit',anthropic)}[3]==='Capabilities recorded'&&document.querySelector('#error').hidden`);
  await click('#ai-model-enable');
  await check('Enable enables a qualified model on an enabled, qualified provider and moves focus to Disable',`${row('ai-model-table',anthropic)}[3]==='Enabled'&&${row('ai-model-audit',anthropic)}[3]==='Enabled'&&!document.querySelector('#ai-model-disable').hidden&&document.querySelector('#ai-model-enable').hidden&&document.activeElement===document.querySelector('#ai-model-disable')`);
  await click('#ai-model-disable');
  await check('Disable turns the model off, keeps its capabilities and moves focus to Enable',`${row('ai-model-table',anthropic)}[3]==='Disabled · qualified'&&${row('ai-model-audit',anthropic)}[3]==='Disabled'&&${rows('ai-capability-table')}.length===6&&document.activeElement===document.querySelector('#ai-model-enable')`);
  await manage(openai);
  await check('a model whose vendor has no confirmed usage mapping offers no efforts and says why',"document.querySelectorAll('#ai-capability-efforts input').length===0&&!document.querySelector('#ai-capability-note').hidden&&document.querySelector('#ai-capability-form').getAttribute('aria-describedby')==='ai-capability-note'&&document.querySelector('#ai-capability-form button[type=submit]').disabled");
  await click('#ai-tab-providers');await openProvider('Anthropic');await click('#ai-provider-disable');
  await check('Disable turns the provider off, keeps it qualified and moves focus to Enable',`${row('ai-provider-table','Anthropic')}[2]==='Disabled · qualified'&&${rows('ai-provider-audit')}[0][3]==='Disabled'&&!document.querySelector('#ai-provider-enable').hidden&&document.activeElement===document.querySelector('#ai-provider-enable')&&document.querySelector('#error').hidden`);

  // Test, from the provider's card, whether or not the provider and model are enabled. The fake provider answers
  // each test as the model it names, and the launcher counts what reaches it.
  const fact=name=>`${rows('ai-test-table')}.find(r=>r[0]===${JSON.stringify(name)})?.[1]`;
  const anthropicDirectory="(async()=>(await(await window.__originalFetch('/api/settings/ai/providers')).json()).providers.find(p=>p.vendor==='anthropic'))()";
  const retainedTest="JSON.parse(sessionStorage.getItem(Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.ai.test.request.'))))";
  const testOffered="document.querySelector('#ai-test-pending').checkVisibility()";
  await check('the provider card and table name the check endpoint its requests reach, and a vendor without an adapter has none',
    `document.querySelector('#ai-provider-facts').textContent.includes('/v1/messages (check endpoint)')&&${row('ai-provider-table','Anthropic')}[9].endsWith('/v1/messages (check endpoint)')&&${row('ai-provider-table','Google')}[9]==='No adapter in this release'`);
  const testChoices=await evaluate(`(async()=>{const d=await(await window.__originalFetch('/api/settings/ai/models')).json();return {model:d.models.find(m=>m.identifier===${JSON.stringify(anthropic)}).id,
    excluded:d.models.filter(m=>m.vendor!=='anthropic'||!m.capability).map(m=>m.id),unrecorded:d.models.some(m=>m.vendor==='anthropic'&&!m.capability)};})()`);
  const modelId=testChoices.model;
  await set('#ai-test-model',modelId);
  await check("the test offers only this vendor's models with capabilities, and the chosen model's efforts",
    `(()=>{const o=[...document.querySelector('#ai-test-model').options];return ${JSON.stringify(testChoices.unrecorded)}&&o.some(x=>x.value===${JSON.stringify(modelId)}&&x.textContent.includes(${JSON.stringify(anthropic)}))`
    +`&&!o.some(x=>${JSON.stringify(testChoices.excluded)}.includes(x.value));})()&&JSON.stringify([...document.querySelector('#ai-test-effort').options].map(o=>o.value))==='["","low","high"]'`);
  await submit('#ai-test-form');
  await check('a test names its reasoning effort before it is sent',`${shown}.includes('reasoning effort')&&${sent('providers/test')}.length===0`);
  await set('#ai-test-effort','low');await submit('#ai-test-form');
  await check('a test sends only its identity, model and effort, and the card shows its outcome, usage and exact cost, unmarked under the attested key and terms',
    `(()=>{const b=${sent('providers/test')}.at(-1)?.body;return JSON.stringify(Object.keys(b).sort())==='["modelId","operationId","reasoning"]'&&b.modelId===${JSON.stringify(modelId)}&&b.reasoning==='low';})()`
    +`&&${fact('Outcome')}==='Succeeded'&&${fact('Model')}===${JSON.stringify(anthropic)}&&${fact('Served as')}===${JSON.stringify(anthropic)}&&${fact('Reasoning effort')}==='low'`
    +`&&${fact('Uncached input')}==='21 tokens'&&${fact('Output')}==='9 tokens'&&/^0\\.000114744 USD/.test(${fact('Cost')})&&document.querySelector('#error').hidden`
    +`&&(r=>r.startsWith('Succeeded · ')&&!r.endsWith('earlier key or terms'))(${row('ai-provider-table','Anthropic')}[10])&&document.querySelector('#ai-test-summary').textContent===''`);
  // A lost response retains the test's request; Retry reads its outcome under the same identity and never tests again.
  await evaluate("(()=>{window.__dropTest=true;const send=window.fetch;window.fetch=async(...args)=>{const r=await send(...args);if(window.__dropTest&&String(args[0])==='/api/settings/ai/providers/test'){window.__dropTest=false;throw new TypeError('Synthetic lost test response');}return r;};})()");
  await set('#ai-test-effort','high');await submit('#ai-test-form');
  const testing=await evaluate(retainedTest);
  const recorded=await evaluate(anthropicDirectory+".then(p=>p.lastTest)");
  await check('a lost test response retains the request while the card shows the test the server recorded',
    `${testOffered}&&${JSON.stringify(testing?.path)}==='settings/ai/providers/test'&&${JSON.stringify(recorded?.operationId)}===${JSON.stringify(testing?.operationId)}`
    +`&&${fact('Reasoning effort')}==='high'&&${fact('Outcome')}==='Succeeded'&&document.querySelector('#ai-test-form button[type=submit]').disabled`);
  await click('#ai-test-retry');
  await check('retry resends the identical request and reads the same test, whose record is unchanged',
    `(async()=>{const s=${sent('providers/test')}.filter(r=>r.body.operationId===${JSON.stringify(testing.operationId)}),t=(await ${anthropicDirectory}).lastTest;return s.length===2&&JSON.stringify(s[0].body)===JSON.stringify(s[1].body)&&JSON.stringify(t)===${JSON.stringify(JSON.stringify(recorded))}&&!${testOffered}&&document.querySelector('#error').hidden;})()`);
  // A refusal proves nothing was opened, so it releases the request: here a directory stating an effort the model's version does not accept.
  await evaluate(`(()=>{window.__stubTestEfforts=true;const send=window.fetch;window.fetch=async(...args)=>{const r=await send(...args);if(window.__stubTestEfforts&&String(args[0])==='/api/settings/ai/models'&&!args[1]?.body){const d=await r.json();d.models.find(m=>m.identifier===${JSON.stringify(anthropic)}).capability.reasoningEfforts.push('medium');return new Response(JSON.stringify(d),{status:200,headers:{'Content-Type':'application/json'}});}return r;};})()`);
  await click('#ai-providers-refresh');await openProvider('Anthropic');await set('#ai-test-model',modelId);await set('#ai-test-effort','medium');await submit('#ai-test-form');
  await check('a test the server refuses is not retained, and the page says why',
    `${sent('providers/test')}.at(-1)?.body.reasoning==='medium'&&${shown}.includes('capability version records')&&!${testOffered}&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.ai.test.request.'))`);
  await evaluate('window.__stubTestEfforts=false');await click('#ai-providers-refresh');await openProvider('Anthropic');
  // Answers that leave a test's outcome unknown keep its request, for its own identity only, until Retry or Discard: a test
  // still in progress, and a refusal read after the test may have run. These answers are synthetic, so nothing reaches the server.
  const synthetic=(code,status)=>evaluate(`(()=>{window.__synthetic=true;const send=window.fetch;window.fetch=async(...args)=>{if(window.__synthetic&&String(args[0])==='/api/settings/ai/providers/test'){window.__synthetic=false;`
    +`return new Response(JSON.stringify({code:${JSON.stringify(code)}}),{status:${status},headers:{'Content-Type':'application/json'}});}return send(...args);};})()`);
  await synthetic('test_in_progress',409);
  await set('#ai-test-model',modelId);await set('#ai-test-effort','high');await submit('#ai-test-form');
  await check('a test still in progress is retained, and the page names it and says its outcome is not recorded yet',
    `${testOffered}&&${shown}.includes('no recorded outcome yet')&&document.querySelector('#ai-test-pending-text').textContent.includes(${JSON.stringify(`(Anthropic · ${anthropic})`)})&&${retainedTest}.body.reasoning==='high'`);
  await persona(manager);await click('#nav-ai');await click('#ai-tab-providers');
  await check('another identity is not offered a test it did not send',"document.querySelector('#ai-test-pending').hidden");
  await persona(pricer);await click('#nav-ai');await click('#ai-tab-providers');
  await check('a retained test is offered again after an identity switch, before any provider card opens',`${testOffered}&&document.querySelector('#ai-provider-card').hidden`);
  await evaluate("(()=>{window.__failProviders=true;const send=window.fetch;window.fetch=async(...args)=>{if(window.__failProviders&&String(args[0])==='/api/settings/ai/providers'&&!args[1]?.body){window.__failProviders=false;"
    +"return new Response(JSON.stringify({code:'synthetic_unavailable'}),{status:503,headers:{'Content-Type':'application/json'}});}return send(...args);};})()");
  await click('#ai-providers-refresh');
  await check('a failed directory read leaves the retained test with its Retry and Discard in reach',
    `${testOffered}&&!document.querySelector('#error').hidden&&document.querySelector('#ai-test-retry').checkVisibility()&&document.querySelector('#ai-test-discard').checkVisibility()`);
  await click('#ai-test-discard');
  await check('Discard forgets the retained test without sending it',`!${testOffered}&&!Object.keys(sessionStorage).some(k=>k.startsWith('openetrm.ai.test.request.'))`);
  await click('#ai-providers-refresh');await openProvider('Anthropic');
  await synthetic('access_denied',403);
  await set('#ai-test-model',modelId);await set('#ai-test-effort','low');await submit('#ai-test-form');
  await check('a refusal the server can send after a test ran keeps its request',`${testOffered}&&${retainedTest}?.body.reasoning==='low'`);
  await click('#ai-test-discard');await click('#ai-providers-refresh');await openProvider('OpenAI');
  await check('a vendor without an adapter offers no model to test, and its card says why',
    "document.querySelector('#ai-test-model').options.length===1&&!document.querySelector('#ai-test-note').hidden&&document.querySelector('#ai-test-note').textContent.includes('No adapter for OpenAI')");
  await openProvider('Anthropic');
  // A new key ends the attestation the last test ran under, and the page says so.
  await click('#ai-provider-key');await set('#ai-key-value','sk-browser-synthetic-key-0004');await submit('#ai-key-form');
  await check('a last test that ran under a key or terms no longer attested is marked, and the card says to attest before testing again',
    `${row('ai-provider-table','Anthropic')}[10].endsWith('earlier key or terms')&&document.querySelector('#ai-test-summary').textContent.includes('Attest the current key and terms, then test again')`);

  await evaluate('window.scrollTo(0,0)');await shot('ai-settings-desktop');
  await click('#ai-tab-models');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate('window.scrollTo(0,0)');
  await check('AI settings avoid page-wide horizontal overflow on a phone',fitsWidth);await shot('ai-settings-mobile');
  await click('#ai-tab-providers');await evaluate('window.scrollTo(0,0)');
  await check('the provider card avoids page-wide horizontal overflow on a phone',`!document.querySelector('#ai-provider-card').hidden&&${fitsWidth}`);await shot('ai-providers-mobile');
  await click('#ai-tab-models');
  await check('AI settings expose no internal GUIDs',"!/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(document.body.innerText)");
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});

  // An independent administrator removes this one's access while the page stays open.
  const authority=await sideSession(url);
  const member=(await authority.call('admin')).members.find(m=>m.login===pricer);
  await authority.replaceRoles(member,['viewer']);
  await click('#ai-models-refresh');
  await check('a revoked administrator keeps no model, price, capability, terms, test or audit rows on screen',`!document.querySelector('#error').hidden&&['ai-model-table','ai-model-audit','ai-price-history','ai-capability-table','ai-provider-table','ai-terms-table','ai-test-table','ai-provider-audit'].every(id=>document.querySelector('#'+id).children.length===0)&&document.querySelector('#ai-model-card').hidden&&document.querySelector('#ai-provider-card').hidden`);
  await persona('viewer');
  await check('switching to a viewer hides AI settings',"document.querySelector('#nav-ai').hidden&&document.querySelector('#ai-page').hidden");
  await check('no uncaught JavaScript exceptions',JSON.stringify(errors.length===0));
}
