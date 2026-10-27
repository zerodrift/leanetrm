// Agent access in the profile dialog and a workspace's grants in Users & access (docs/ai-assistant.md §7.1). The Agents
// scenario runs this at its end, in its own browser and with its pricing session, so it opens no session of its own: a
// preview holds at most 64. Its persona and agent are created through the page's own session; assertions look only at
// this run's rows.
export async function run({command,evaluate,wait,click,persona,check,idle,fitsWidth},{side,model,identifier,stamp}){
  const grantee='browser_grantee_'+stamp,person='Browser grantee '+stamp,guide='Browser grant guide '+stamp,titled=guide+' · version 1';
  const label=`Browser agent model · ${identifier} (Anthropic)`;
  async function call(path,body){const r=await evaluate(`__api(${JSON.stringify(path)},${JSON.stringify(body)})`);if(r.status!==200)throw Error('Synthetic setup failed: '+path+' '+JSON.stringify(r));return r.data;}
  const items="[...document.querySelectorAll('#profile-grants-list li')]";
  const kept="Object.keys(sessionStorage).find(k=>k.startsWith('openetrm.assistant.grants.request.'))";
  const sent="window.__requests.filter(r=>r.path==='/api/assistant/grants/revoke')";
  const status="document.querySelector('#profile-grants-status').textContent";
  const member=async()=>(await side.call('admin')).members.find(m=>m.login===grantee);
  const access=async enabled=>{const m=await member();await side.call('admin/update',{operationId:crypto.randomUUID(),id:m.id,expectedVersion:m.version,name:m.name,enabled,roles:m.roles});};

  await persona('admin');
  await call('admin/create',{operationId:crypto.randomUUID(),login:grantee,name:person,kind:'human',roles:['trader']});
  const modelVersion=(await side.call('settings/ai/models')).models.find(m=>m.id===model).capability.version;
  let agent=await call('settings/ai/agents/create',{operationId:crypto.randomUUID(),name:guide});
  agent=await call('settings/ai/agents/save',{operationId:crypto.randomUUID(),agentId:agent.id,expectedVersion:agent.version,description:'Reads the page it is opened on.',
    instructions:'Quote only values you read.',modelId:model,modelVersion,reasoning:'low',caching:false,tools:['read_market_data','read_trades'],allowedRoles:['trader'],
    limits:{toolSteps:4,inputTokens:100000,outputTokens:1024,elapsedSeconds:60,readSeconds:10,retries:1,historyTurns:10},grantDays:7});
  agent=await call('settings/ai/agents/publish',{operationId:crypto.randomUUID(),agentId:agent.id,expectedVersion:agent.version});
  const grant=()=>call('assistant/grants/grant',{operationId:crypto.randomUUID(),agentId:agent.id,version:1});

  // A person's grants are read only when asked, and Revoke names the agent it ends.
  await persona(grantee);const first=await grant();
  await click('#profile-open');await evaluate('window.__requests=[]');await click('#profile-grants-show');
  await check('Agent access reads only the person\'s grants when asked, naming the agent and version, model, reads and term, with a Revoke for the agent',
    `JSON.stringify(window.__requests.map(r=>r.path))==='["/api/assistant/grants"]'&&${items}.length===1`
    +`&&${items}[0].querySelector('strong').textContent===${JSON.stringify(titled)}&&${items}[0].textContent.includes(${JSON.stringify(label)})`
    +`&&${items}[0].textContent.includes('Reads trades and market data')&&/^Allowed .+ · until .+$/.test([...${items}[0].querySelectorAll('span')].at(-1).textContent)`
    +`&&${items}[0].querySelector('button').getAttribute('aria-label')===${JSON.stringify('Revoke '+guide)}&&${status}==='You have 1 agent grant in force.'`
    +"&&document.querySelector('#profile-grants-error').hidden&&document.querySelector('#profile-grants-show').textContent==='Refresh agent access'");

  // A lost answer after the server revoked keeps the request for its own identity; Retry reads the stored outcome.
  await evaluate("window.__dropRevoke=true;window.__trackedRevoke=window.fetch;window.fetch=async(...args)=>{const r=await window.__trackedRevoke(...args);"
    +"if(window.__dropRevoke&&String(args[0])==='/api/assistant/grants/revoke'){window.__dropRevoke=false;throw new TypeError('Synthetic dropped response after server commit');}return r;}");
  await click('#profile-grants-list li button');
  const retained=await evaluate(`JSON.parse(sessionStorage.getItem(${kept}))`);
  await check('a lost answer keeps the exact request, shows the grants in force now and offers Retry, which takes focus',
    `!document.querySelector('#profile-grants-pending').hidden&&document.querySelector('#profile-grants-pending-text').textContent.includes(${JSON.stringify(guide)})`
    +"&&document.querySelector('#profile-grants-error').textContent.startsWith('We could not confirm the revocation')"
    +`&&${items}.length===0&&${status}==='You have no agent grants in force.'&&document.activeElement===document.querySelector('#profile-grants-retry')`
    +`&&${JSON.stringify(retained?.path==='assistant/grants/revoke'&&JSON.stringify(retained?.body)===JSON.stringify({grantId:first.id}))}`);
  await click('#profile-close');await persona('trader');await click('#profile-open');
  await check('another identity is not offered the kept request',"document.querySelector('#profile-grants-pending').hidden");
  await click('#profile-close');await persona(grantee);await click('#profile-open');
  await check('the identity that sent it is offered it again before its grants are read',
    `!document.querySelector('#profile-grants-pending').hidden&&JSON.parse(sessionStorage.getItem(${kept})).operationId===${JSON.stringify(retained?.operationId)}&&${items}.length===0`);
  await evaluate('window.__requests=[]');await click('#profile-grants-retry');
  await check('Retry resends the identical request, which answers the revocation, and keeps nothing',
    `${sent}.length===1&&JSON.stringify(${sent}[0].body)===${JSON.stringify(JSON.stringify({grantId:first.id,operationId:retained?.operationId}))}`
    +`&&document.querySelector('#profile-grants-pending').hidden&&!${kept}&&${status}.startsWith(${JSON.stringify('Your grant to '+guide+' ended ')})`
    +"&&document.querySelector('#profile-grants-error').hidden");

  // A person who no longer uses agents still lists and revokes their grants; a read refused once access ends keeps none.
  const second=await grant();await side.replaceRoles(await member(),['viewer']);
  await click('#profile-grants-show');
  await check('a person who no longer uses agents still lists their grant',`${items}.length===1`);
  await access(false);await click('#profile-grants-show');
  await check('a read refused because access ended shows no grant and says why, keeping no request',
    `${items}.length===0&&${status}===''&&document.querySelector('#profile-grants-error').textContent==='Your current access cannot read or revoke agent grants.'`
    +`&&document.querySelector('#profile-grants-pending').hidden&&!${kept}`);
  await access(true);await click('#profile-grants-show');
  await check('restored, the person lists the grant again: a suspension revokes nothing',`${items}.length===1`);
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});
  await check('Agent access fits a phone and shows no internal identifier',
    `${fitsWidth}&&(d=>d.scrollWidth<=d.clientWidth+1)(document.querySelector('#profile-dialog'))`
    +"&&!/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(document.querySelector('#profile-dialog').innerText)");
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await evaluate('window.__requests=[]');await click('#profile-grants-list li button');
  await check('Revoke, which needs no UseAgents, sends only its request identity and grant, ends the grant and moves focus to Agent access',
    `${sent}.length===1&&Object.keys(${sent}[0].body).sort().join()==='grantId,operationId'&&${sent}[0].body.grantId===${JSON.stringify(second.id)}`
    +`&&${items}.length===0&&${status}.startsWith('Your grant to ')&&!${kept}&&document.activeElement===document.querySelector('#profile-grants-title')`);

  // Those who administer the workspace's access see every grant in force and the latest changes, read-only.
  await click('#profile-close');await persona('trader');await grant();
  const trader=(await side.call('admin')).members.find(m=>m.login==='trader').name;
  await persona('admin');await click('#nav-admin');
  const rows=table=>`[...document.querySelectorAll('#${table} tbody tr')].map(r=>[...r.cells].map(c=>c.textContent)).filter(r=>r.includes(${JSON.stringify(titled)}))`;
  await check('Users & access lists the grant in force by person, with its model and reads, and no Revoke',
    `JSON.stringify(${rows('admin-grants')}.map(r=>r.slice(0,4)))===${JSON.stringify(JSON.stringify([[trader,titled,label,'trades and market data']]))}`
    +"&&!document.querySelector('#admin-grants-card').hidden&&![...document.querySelectorAll('#admin-grants button,#admin-grant-history button')].some(b=>/Revoke/.test(b.textContent+b.getAttribute('aria-label')))"
    +"&&document.querySelector('#error').hidden");
  await check("Users & access lists the agent's changes newest first, each with its person and who made it",
    `JSON.stringify(${rows('admin-grant-history')}.map(r=>[r[1],r[6],r[7]]))===${JSON.stringify(JSON.stringify([[trader,'Granted',trader],
      [person,'Revoked',person],[person,'Granted',person],[person,'Revoked',person],[person,'Granted',person]]))}`
    +`&&${rows('admin-grant-history')}.every(r=>r[3]===${JSON.stringify(label)}&&r[4]==='trades and market data')`);
  await check('the kept request was recorded once, and the grant in force is the one this run made',
    `(async()=>{const w=await(await window.__originalFetch('/api/assistant/grants/workspace')).json();`
    +`return w.history.filter(r=>r.operationId===${JSON.stringify(retained?.operationId)}).length===1&&w.current.filter(g=>g.agentName===${JSON.stringify(guide)}).length===1`
    +`&&w.history.some(r=>r.operationId===w.current.find(g=>g.agentName===${JSON.stringify(guide)}).operationId&&r.outcome==='granted');})()`);
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await evaluate('window.scrollTo(0,0)');
  await check("a workspace's grants fit a phone and show no internal identifier",
    `${fitsWidth}&&!/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/i.test(document.body.innerText)`);
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await wait(idle);
}
