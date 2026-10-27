import {sideSession} from './side-session.mjs';
import {qualifyProvider,pricedModel} from './model-fixture.mjs';
import {run as agentGrants} from './agent-grants.mjs';

// The Agents tab of Settings › AI, after the providers scenario in the assistant check's preview. The scenario
// qualifies the provider and adds a priced, enabled model through an independent session, so it relies on no
// earlier state; identifiers are unique to this run and assertions look only at this run's rows.
export async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,errors,url}){
  await command('Runtime.enable');await command('Page.enable');await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url:url+'/'});await wait("document.querySelectorAll('.curve-button').length>0&&"+idle);
  await evaluate(`window.__requests=[];window.__originalFetch=window.fetch;window.fetch=async(...args)=>{window.__requests.push({path:String(args[0]),body:args[1]?.body?JSON.parse(args[1].body):null});return window.__originalFetch(...args);};`
    +"window.__api=async(path,body)=>{const boot=await(await window.__originalFetch('/api/bootstrap')).json();const r=await window.__originalFetch('/api/'+path,{method:'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},body:JSON.stringify(body)});return {status:r.status,data:await r.json()};};");
  const stamp=Date.now(),manager='browser_agents_admin_'+stamp,pricer='browser_agents_pricing_'+stamp,agentsOnly='browser_agents_only_'+stamp,modelsOnly='browser_models_only_'+stamp;
  const identifier='browser-agent-model-'+stamp,name='Browser page guide '+stamp,other='Browser second guide '+stamp;
  // A session can sign in only as the accounts it created: the page creates its administrators, the side session its pricer.
  // The preview adds a role for each of Settings › AI's three permissions alone.
  await persona('admin');
  for(const [login,display,roles] of [[manager,'Browser agents administrator',['access_administrator']],[agentsOnly,'Browser agents-only administrator',['synthetic_agents_admin']],
    [modelsOnly,'Browser models-only administrator',['synthetic_models_admin']]]){
    const created=await evaluate(`__api('admin/create',${JSON.stringify({operationId:crypto.randomUUID(),login,name:display,kind:'human',roles})})`);
    if(created.status!==200)throw Error('Synthetic persona setup failed: '+JSON.stringify(created));
  }
  const side=await sideSession(url);
  await side.call('admin/create',{operationId:crypto.randomUUID(),login:pricer,name:'Browser agents pricing',kind:'human',roles:['access_administrator','viewer']});
  await side.call('profile',{profile:pricer});
  await qualifyProvider(side,'sk-synthetic-browser-agents-'+stamp);
  const added=await pricedModel(side,{identifier,displayName:'Browser agent model',efforts:['low','high']}),{capabilities}=added;await added.enable();

  const rows=table=>`[...document.querySelectorAll('#${table} tbody tr')].map(r=>[...r.cells].map(c=>c.textContent))`;
  const row=(table,text)=>`${rows(table)}.find(r=>r.some(c=>c.includes(${JSON.stringify(text)})))`;
  const sent=path=>`window.__requests.filter(r=>r.path==='/api/settings/ai/agents/${path}')`;
  const shown="!document.querySelector('#error').hidden&&document.querySelector('#error').textContent";
  const retained="Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.ai.agents.request.'))";
  const tabs="['usage','models','providers','agents'].filter(t=>!document.querySelector('#ai-tab-'+t).hidden).join()";
  const open=(agent=name)=>evaluate(`[...document.querySelectorAll('#ai-agent-table tbody tr')].find(r=>r.cells[0].textContent===${JSON.stringify(agent)}).cells[0].querySelector('button').click()`).then(()=>wait(idle));
  const tick=(group,values)=>evaluate(`(()=>{for(const box of document.querySelectorAll('#${group} input[type=checkbox]'))box.checked=${JSON.stringify(values)}.includes(box.value);})()`);
  const agentsTab=async()=>{await click('#nav-ai');await click('#ai-tab-agents');};
  async function fill(changes={}){
    const entry={description:'Answers questions about the page it is opened on.',instructions:'Quote only values you read.\nSay when something is unavailable.',
      model:added.id,effort:'low',caching:'off',tools:['read_current_page','read_trades'],roles:['trader','risk_analyst'],toolSteps:'8',inputTokens:'200000',outputTokens:'2048',
      elapsed:'120',read:'15',retries:'2',grant:'28',...changes};
    for(const [id,value] of [['description',entry.description],['instructions',entry.instructions],['model',entry.model],['effort',entry.effort],['caching',entry.caching],
      ['tool-steps',entry.toolSteps],['input-tokens',entry.inputTokens],['output-tokens',entry.outputTokens],['elapsed',entry.elapsed],['read',entry.read],['retries',entry.retries],['grant-days',entry.grant]])
      await set('#ai-agent-'+id,value);
    await tick('ai-agent-tools',entry.tools);await tick('ai-agent-roles',entry.roles);
  }

  // Each tab is offered by its own permission.
  await persona('trader');
  await check('a trader is offered no AI settings',"document.querySelector('#nav-ai').hidden&&document.querySelector('#ai-page').hidden");
  await evaluate('window.__requests=[]');await persona(agentsOnly);
  await check('an identity holding only ManageAgents lands on Settings › AI, is offered the Agents tab alone and reads only the agents directory',
    `!document.querySelector('#ai-page').hidden&&${tabs}==='agents'&&!document.querySelector('#ai-agents').hidden&&document.querySelector('#ai-models').hidden&&document.querySelector('#error').hidden`
    +"&&window.__requests.filter(r=>r.path.startsWith('/api/settings/ai/')).every(r=>r.path==='/api/settings/ai/agents')&&window.__requests.some(r=>r.path==='/api/settings/ai/agents')");
  await persona(modelsOnly);await click('#nav-ai');
  await check('an identity holding only ManageModelProviders is offered Models & prices and Providers alone',
    `${tabs}==='models,providers'&&!document.querySelector('#ai-models').hidden&&document.querySelector('#ai-agents').hidden&&document.querySelector('#error').hidden`);
  await persona(manager);await click('#nav-ai');
  await check('an access administrator is offered all four tabs, opening on Models & prices',
    `${tabs}==='usage,models,providers,agents'&&!document.querySelector('#ai-models').hidden&&document.querySelector('#ai-agents').hidden`);
  await click('#ai-tab-agents');
  await check('the Agents tab states what a save, Publish and Retire do',
    "!document.querySelector('#ai-agents').hidden&&document.querySelector('#ai-models').hidden&&/draft/.test(document.querySelector('#ai-agents').textContent)&&/Retire is permanent/.test(document.querySelector('#ai-agents').textContent)");

  await set('#ai-agent-name',' Padded name');await submit('#ai-agent-create-form');
  await check('a refused create explains its refusal and keeps no request',`${sent('create')}.length===1&&${shown}.startsWith('Check the entries')&&!${retained}`);
  await set('#ai-agent-name',name);await submit('#ai-agent-create-form');
  await check('Create lists the agent as active with nothing saved or published, and the audit records it',
    `(()=>{const r=${row('ai-agent-table',name)};return r&&r[1]==='Active'&&r[2]==='None saved'&&r[3]==='Not published'&&document.querySelector('#error').hidden;})()&&${row('ai-agent-audit',name)}?.[3]==='Agent created'`);
  await check('the create sent only its request identity and the name, and opened the new agent',
    `${sent('create')}.at(-1).body&&Object.keys(${sent('create')}.at(-1).body).sort().join()==='name,operationId'&&document.querySelector('#ai-agent-title').textContent===${JSON.stringify(name)}`);
  await check("the agent's card offers exactly the directory's models, each at its current capability version, and the roles that use agents",
    `(async()=>{const d=await(await window.__originalFetch('/api/settings/ai/agents')).json(),o=[...document.querySelector('#ai-agent-model').options];`
    +"return o[0].value===''&&o.length===d.models.length+1&&d.models.every((m,i)=>o[i+1].value===m.id&&o[i+1].textContent.endsWith('capability version '+m.version))"
    +`&&o.some(x=>x.value===${JSON.stringify(added.id)}&&x.textContent==='Browser agent model · ${identifier} (Anthropic) · capability version 1')`
    +"&&JSON.stringify([...document.querySelectorAll('#ai-agent-roles input')].map(b=>b.value))==='[\"market_data_editor\",\"risk_analyst\",\"trader\"]'"
    +"&&document.querySelectorAll('#ai-agent-tools input').length===10&&document.querySelector('#ai-agent-caching').value==='';})()");
  await check('the form states the limits the API passes',"document.querySelector('#ai-agent-instructions-help').textContent==='Up to 6,000 characters, in lines.'&&document.querySelector('#ai-agent-grant-help').textContent==='Up to 90 days.'");
  await fill({caching:''});
  const before=await evaluate(`${sent('save')}.length`);
  await submit('#ai-agent-form');
  await check('nothing blank is inferred: a save without a caching choice is refused before any request',`${shown}.includes('Choose whether prompt caching is on')&&${sent('save')}.length===${before}`);
  await set('#ai-agent-model',added.id);
  await check("choosing a model offers its version's reasoning efforts, chosen anew, and states its output limit",
    "JSON.stringify([...document.querySelector('#ai-agent-effort').options].map(o=>o.value))==='[\"\",\"low\",\"high\"]'&&document.querySelector('#ai-agent-effort').value===''"
    +"&&document.querySelector('#ai-agent-output-help').textContent==='Up to 4,096 tokens per call for this capability version.'");
  await set('#ai-agent-effort','low');await tick('ai-agent-tools',['read_market_data']);await click('#ai-agents-refresh');
  const entered=`!document.querySelector('#ai-agent-card').hidden&&document.querySelector('#ai-agent-model').value===${JSON.stringify(added.id)}&&document.querySelector('#ai-agent-effort').value==='low'`
    +"&&JSON.stringify([...document.querySelectorAll('#ai-agent-tools input:checked')].map(b=>b.value))==='[\"read_market_data\"]'"
    +"&&document.querySelector('#ai-agent-description').value==='Answers questions about the page it is opened on.'";
  await check('a refresh keeps what the open card has entered and chosen',entered);
  await evaluate("window.__failLoad=true;window.__tracked0=window.fetch;window.fetch=async(...args)=>{if(window.__failLoad&&String(args[0])==='/api/settings/ai/agents'){window.__failLoad=false;throw new TypeError('Synthetic network failure');}return window.__tracked0(...args);}");
  await click('#ai-agents-refresh');
  await check('a refresh that fails hides the card and says so',`${shown}&&document.querySelector('#ai-agent-card').hidden`);
  await click('#ai-agents-refresh');
  await check('the next refresh shows the card with what it had entered and chosen',`document.querySelector('#error').hidden&&${entered}`);
  await fill({tools:['read_trades'],roles:['market_data_editor']});await submit('#ai-agent-form');
  await check('an allowed role lacking a permission the tools need is refused by name, and nothing is kept',
    `${shown}.includes('The allowed role Market data editor lacks ReadTrades')&&!${retained}`);
  // The server refuses a request it cannot read, or one larger than it accepts, before any write: the request is
  // released, and the page states the ranges the entries keep.
  const refused=await evaluate(`${sent('save')}.length`);
  const unsaved=`document.querySelector('#ai-agents-pending').hidden&&!${retained}`
    +`&&(await(await window.__originalFetch('/api/settings/ai/agents')).json()).agents.find(a=>a.name===${JSON.stringify(name)}).newest===null`;
  await fill({toolSteps:'99999999999'});await submit('#ai-agent-form');
  await check('tool steps beyond what the server can read are refused with the ranges the entries keep, and nothing is kept',
    `(async()=>${shown}.includes('1 to 100 tool steps')&&${sent('save')}.at(-1).body.limits.toolSteps===99999999999&&${sent('save')}.length===${refused+1}&&${unsaved})()`);
  await fill({instructions:'Quote only values you read.\n'+'x'.repeat(32*1024)});await submit('#ai-agent-form');
  await check('instructions past the request size limit are refused the same way',
    `(async()=>${shown}.includes('instructions within the limit stated with them')&&${sent('save')}.length===${refused+2}&&${unsaved})()`);
  await fill();await submit('#ai-agent-form');
  await check('a save creates a draft version that the table and card show, and the audit records it',
    `(()=>{const r=${row('ai-agent-table',name)};return r&&r[2]==='Version 1 · draft'&&r[3]==='Not published'&&document.querySelector('#error').hidden;})()`
    +`&&document.querySelector('#ai-agent-draft').textContent.includes('Quote only values you read.')&&${row('ai-agent-audit',name)}?.[3]==='Version saved'`);
  await check('the save sent every field, with the exact model version the directory listed',
    `(()=>{const b=${sent('save')}.at(-1).body;return b.modelId===${JSON.stringify(added.id)}&&b.modelVersion===1&&b.reasoning==='low'&&b.caching===false`
    +"&&JSON.stringify(b.tools)==='[\"read_current_page\",\"read_trades\"]'&&JSON.stringify(b.allowedRoles)==='[\"risk_analyst\",\"trader\"]'"
    +"&&JSON.stringify(b.limits)==='{\"toolSteps\":8,\"inputTokens\":200000,\"outputTokens\":2048,\"elapsedSeconds\":120,\"readSeconds\":15,\"retries\":2}'&&b.grantDays===28"
    +"&&b.instructions==='Quote only values you read.\\nSay when something is unavailable.';})()");
  // A chosen role that no longer uses agents is named rather than offered, after a refresh as when its agent opens, and
  // is chosen again once it is offered again; a stubbed directory drops one.
  await evaluate("window.__dropRole=true;window.__tracked1=window.fetch;window.fetch=async(...args)=>{const r=await window.__tracked1(...args);"
    +"if(!window.__dropRole||String(args[0])!=='/api/settings/ai/agents')return r;const d=await r.json();d.roles=d.roles.filter(x=>x.code!=='risk_analyst');"
    +"return new Response(JSON.stringify(d),{status:200,headers:{'Content-Type':'application/json'}});}");
  const offered="JSON.stringify([...document.querySelectorAll('#ai-agent-roles input')].map(b=>[b.value,b.checked]))";
  const leftOut="!document.querySelector('#ai-agent-roles-note').hidden&&document.querySelector('#ai-agent-roles-note').textContent==='A save leaves out risk_analyst: only roles that use agents can be allowed.'"
    +`&&${offered}==='[["market_data_editor",false],["trader",true]]'`;
  await click('#ai-agents-refresh');
  await check('a refresh names a chosen role that no longer uses agents, and no longer offers it',leftOut);
  await open();
  await check('so does opening the agent whose newest version allowed it, whose rows name the role as saved',
    `${leftOut}&&document.querySelector('#ai-agent-draft').textContent.includes('risk_analyst (no longer uses agents)')`);
  await evaluate('window.__dropRole=false');await click('#ai-agents-refresh');
  await check('a role that uses agents again is offered still chosen, and the note is gone',
    `document.querySelector('#ai-agent-roles-note').hidden&&${offered}==='[["market_data_editor",false],["risk_analyst",true],["trader",true]]'`);
  await click('#ai-agent-publish');
  await check('Publish publishes the newest version, and the audit records it',
    `(()=>{const r=${row('ai-agent-table',name)};return r&&r[1]==='Active'&&r[2]==='Version 1'&&r[3]==='Version 1'&&r[4]==='Browser agent model · ${identifier} (Anthropic)';})()`
    +`&&${row('ai-agent-audit',name)}?.[3]==='Version published'&&document.querySelector('#ai-agent-publish').hidden`);

  // A capability edit after publication leaves the published version on an earlier capability version, and a choice
  // made at that version is never moved to the new one.
  await capabilities(['low','high']);
  await click('#ai-agents-refresh');
  await check("an agent whose published model version is no longer current is marked until a newer version is published",
    `${row('ai-agent-table',name)}?.[1]==='Active · model changed'&&document.querySelector('#ai-agent-published').textContent.includes('no longer current')`);
  await check('the form offers the model at its new capability version, and the model chosen at the earlier one is unchosen, saying why',
    `[...document.querySelector('#ai-agent-model').options].some(o=>o.value===${JSON.stringify(added.id)}&&o.textContent.endsWith('capability version 2'))`
    +"&&document.querySelector('#ai-agent-model').value===''&&document.querySelector('#ai-agent-effort').value===''&&!document.querySelector('#ai-agent-model-pin-note').hidden");
  await open();
  await check('reopened, the card leaves unchosen the model its newest version names at an earlier capability version',
    "document.querySelector('#ai-agent-model').value===''&&!document.querySelector('#ai-agent-model-pin-note').hidden"
    +"&&document.querySelector('#ai-agent-description').value==='Answers questions about the page it is opened on.'");

  // A lost answer after the server saved keeps the request for the identity that sent it; Retry reads the stored outcome.
  await evaluate("window.__dropSave=true;window.__tracked=window.fetch;window.fetch=async(...args)=>{const r=await window.__tracked(...args);if(window.__dropSave&&String(args[0])==='/api/settings/ai/agents/save'){window.__dropSave=false;throw new TypeError('Synthetic dropped response after server commit');}return r;}");
  await fill({description:'Answers questions about the page, on the current model version.'});await submit('#ai-agent-form');
  const kept=await evaluate(`JSON.parse(sessionStorage.getItem(${retained}))`);
  // The version the reload shows is newer than the form, and may be this request's own, so the form does not call it newer.
  await check('a lost answer keeps the exact request and offers Retry and Discard',
    `!document.querySelector('#ai-agents-pending').hidden&&${shown}&&document.querySelector('#ai-agents-retry').checkVisibility()&&${JSON.stringify(kept?.body?.modelVersion)}===2`
    +`&&${row('ai-agent-table',name)}?.[2]==='Version 2 · draft'&&document.querySelector('#ai-agent-stale').hidden`);
  await persona('admin');await agentsTab();
  await check('another identity is not offered the kept request',
    `!document.querySelector('#ai-agents').hidden&&!!${row('ai-agent-table',name)}&&document.querySelector('#ai-agents-pending').hidden`);
  await persona(manager);await agentsTab();
  await check('the identity that sent it is offered it again',
    `!document.querySelector('#ai-agents-pending').hidden&&JSON.parse(sessionStorage.getItem(${retained})).operationId===${JSON.stringify(kept?.operationId)}`);
  // A refusal after an uncertain attempt says nothing about that attempt, so the request stays kept.
  await evaluate("window.__refuseSave=true;window.__tracked3=window.fetch;window.fetch=async(...args)=>{if(window.__refuseSave&&String(args[0])==='/api/settings/ai/agents/save'){window.__refuseSave=false;"
    +"return new Response(JSON.stringify({code:'model_admin_conflict',message:'Synthetic'}),{status:409,headers:{'Content-Type':'application/json'}});}return window.__tracked3(...args);}");
  await click('#ai-agents-retry');
  await check('a refusal after an uncertain attempt keeps the request',
    `${shown}.startsWith('This agent changed')&&!document.querySelector('#ai-agents-pending').hidden&&JSON.parse(sessionStorage.getItem(${retained})).operationId===${JSON.stringify(kept?.operationId)}`);
  await click('#ai-agents-retry');
  await check('Retry resends the same request, which answers with the stored version, recorded once',
    `(()=>{const same=${sent('save')}.filter(r=>r.body.operationId===${JSON.stringify(kept?.operationId)});return same.length===2&&JSON.stringify(same[0].body)===JSON.stringify(same[1].body);})()`
    +`&&document.querySelector('#ai-agents-pending').hidden&&!${retained}&&${row('ai-agent-table',name)}?.[2]==='Version 2 · draft'`);

  // A save confirmed before its reload failed refills the form from the version it made at the next read, which is
  // then not newer than the form.
  await evaluate("window.__failReload='armed';window.__tracked5=window.fetch;window.fetch=async(...args)=>{if(window.__failReload==='due'&&String(args[0])==='/api/settings/ai/agents'){"
    +"window.__failReload='spent';throw new TypeError('Synthetic network failure');}const r=await window.__tracked5(...args);"
    +"if(window.__failReload==='armed'&&String(args[0])==='/api/settings/ai/agents/save'&&r.ok)window.__failReload='due';return r;}");
  await open();
  const answered=await evaluate(`${sent('save')}.length`);
  await fill({description:'Saved before its reload failed.'});await submit('#ai-agent-form');
  await check('a save confirmed before its reload failed keeps no request, and the card waits for the next read',
    `${sent('save')}.length===${answered+1}&&${shown}.includes('could not connect')&&document.querySelector('#ai-agent-card').hidden&&!${retained}`);
  await click('#ai-agents-refresh');
  await check('the next read fills the form from the version that save made, and does not call it newer',
    `!document.querySelector('#ai-agent-card').hidden&&document.querySelector('#ai-agent-title').textContent===${JSON.stringify(name)}`
    +`&&${row('ai-agent-table',name)}?.[2]==='Version 3 · draft'&&document.querySelector('#ai-agent-description').value==='Saved before its reload failed.'`
    +"&&document.querySelector('#ai-agent-effort').value==='low'&&document.querySelector('#ai-agent-stale').hidden");
  // Another administrator saves a newer version while this form is open: saving the form is refused until it starts again.
  await open();
  const listing=(await side.call('settings/ai/agents')).agents.find(a=>a.name===name);
  await side.call('settings/ai/agents/save',{operationId:crypto.randomUUID(),agentId:listing.id,expectedVersion:listing.version,description:'Saved by another administrator.',
    instructions:'Answer only about market data.',modelId:added.id,modelVersion:2,reasoning:'high',caching:true,tools:['read_market_data'],allowedRoles:['trader'],
    limits:{toolSteps:4,inputTokens:100000,outputTokens:1024,elapsedSeconds:60,readSeconds:10,retries:1},grantDays:7});
  await set('#ai-agent-description','Edited while another administrator saved.');await click('#ai-agents-refresh');
  const saves=await evaluate(`${sent('save')}.length`);
  await submit('#ai-agent-form');
  await check('a form filled before another administrator saved a newer version says so, and its save is refused before any request',
    `!document.querySelector('#ai-agent-stale').hidden&&${shown}.includes('A newer version of this agent was saved')&&${sent('save')}.length===${saves}`);
  await click('#ai-agent-restart');
  await check('starting again fills the form from the newest version',
    "document.querySelector('#ai-agent-description').value==='Saved by another administrator.'&&document.querySelector('#ai-agent-effort').value==='high'"
    +"&&document.querySelector('#ai-agent-caching').value==='on'&&document.querySelector('#ai-agent-grant-days').value==='7'&&document.querySelector('#ai-agent-stale').hidden");
  await set('#ai-agent-description','Unsaved description in progress.');
  await click('#ai-agent-publish');
  await check('publishing a version on the current model version clears the mark and keeps what the form holds unsaved',
    `${row('ai-agent-table',name)}?.[1]==='Active'&&${row('ai-agent-table',name)}?.[3]==='Version 4'&&document.querySelector('#ai-agent-description').value==='Unsaved description in progress.'`);

  // A request whose answer never came is kept until Discard, which sends nothing.
  await evaluate("window.__failRetire=true;window.__tracked4=window.fetch;window.fetch=async(...args)=>{if(window.__failRetire&&String(args[0])==='/api/settings/ai/agents/retire'){window.__failRetire=false;throw new TypeError('Synthetic network failure before sending');}return window.__tracked4(...args);}");
  await evaluate("document.querySelector('#ai-agent-retire-confirm').checked=true");await click('#ai-agent-retire');
  await check('an unanswered Retire is kept with Retry and Discard',`!document.querySelector('#ai-agents-pending').hidden&&!!${retained}&&${sent('retire')}.length===0`);
  await click('#ai-agents-discard');
  await check('Discard forgets the kept request without sending it',`document.querySelector('#ai-agents-pending').hidden&&!${retained}&&${sent('retire')}.length===0&&${row('ai-agent-table',name)}?.[1]==='Active'`);

  // A Retire confirmation never carries over to another agent's card.
  await evaluate("document.querySelector('#ai-agent-retire-confirm').checked=true");
  await set('#ai-agent-name',other);await submit('#ai-agent-create-form');
  await check("a created agent's card opens without the confirmation given on another agent's",
    `document.querySelector('#ai-agent-title').textContent===${JSON.stringify(other)}&&!document.querySelector('#ai-agent-retire-confirm').checked`);
  await open();
  await click('#ai-agent-retire');
  await check('Retire asks for confirmation that it is permanent',`${shown}.includes('permanent')&&${row('ai-agent-table',name)}?.[1]==='Active'`);
  await evaluate("document.querySelector('#ai-agent-retire-confirm').checked=true");await click('#ai-agent-retire');
  await check('a retired agent is listed as retired, offers no change, and the audit records it',
    `${row('ai-agent-table',name)}?.[1]==='Retired'&&document.querySelector('#ai-agent-form').hidden&&document.querySelector('#ai-agent-publish').hidden`
    +`&&document.querySelector('#ai-agent-retire').hidden&&${row('ai-agent-audit',name)}?.[3]==='Agent retired'`);

  await evaluate('window.scrollTo(0,0)');await shot('ai-agents-desktop');
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate('window.scrollTo(0,0)');
  await open(other);
  await check('the Agents tab and card avoid page-wide horizontal overflow on a phone',`!document.querySelector('#ai-agent-card').hidden&&!document.querySelector('#ai-agent-form').hidden&&${fitsWidth}`);await shot('ai-agents-mobile');
  await check('the Agents tab exposes no internal GUIDs',"!/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(document.body.innerText)");
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});

  // An independent administrator removes access while the page stays open: an identity refused at its next change,
  // like one refused at its next read, keeps no request and no agent or form data on screen or in the page.
  const wiped=`['ai-agent-table','ai-agent-audit','ai-agent-published','ai-agent-draft','ai-agent-tools','ai-agent-roles'].every(id=>document.getElementById(id).children.length===0)`
    +"&&document.querySelector('#ai-agent-card').hidden&&document.querySelector('#ai-agent-model').options.length===1&&document.querySelector('#ai-agent-effort').options.length===1"
    +"&&['ai-agent-title','ai-agent-facts','ai-agent-output-help','ai-agent-roles-note'].every(id=>document.getElementById(id).textContent==='')"
    +"&&['ai-agent-name','ai-agent-description','ai-agent-instructions','ai-agent-tool-steps','ai-agent-grant-days'].every(id=>document.getElementById(id).value==='')"
    +"&&!document.querySelector('#ai-agent-retire-confirm').checked";
  // The pricing session administers access too, so the run opens no third session: a preview holds at most 64.
  const revoke=async login=>side.replaceRoles((await side.call('admin')).members.find(m=>m.login===login),['viewer']);
  await persona(agentsOnly);await agentsTab();await open(other);await fill({description:'Entered before a change was refused.'});
  await revoke(agentsOnly);
  const attempted=await evaluate(`${sent('save')}.length`);
  await submit('#ai-agent-form');
  await check('a change refused because access ended keeps no request, and the administrator keeps no agent or form data',
    `${sent('save')}.length===${attempted+1}&&${shown}.includes('make that change')&&!${retained}&&${wiped}`);
  await persona(manager);await agentsTab();await open(other);await set('#ai-agent-description','Entered before access ended.');
  await revoke(manager);
  await click('#ai-agents-refresh');
  await check('a read refused because access ended leaves the administrator no agent, version, audit or form data',`${shown}.includes('make that change')&&${wiped}`);
  // Agent access and a workspace's grants follow in this browser and with the pricing session, opening no session.
  await agentGrants({command,evaluate,wait,click,persona,check,idle,fitsWidth},{side,model:added.id,identifier,stamp});
  await check('no uncaught JavaScript exceptions',JSON.stringify(errors.length===0));
}
