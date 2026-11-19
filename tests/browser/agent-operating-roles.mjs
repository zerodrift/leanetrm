import {writeFile} from 'node:fs/promises';
import {dirname,join} from 'node:path';
import {withOwnedBrowser} from './owned-browser.mjs';
import {qualifyProvider,pricedModel} from './model-fixture.mjs';

async function run({command,evaluate,wait,click,set,submit,persona,check,shot,idle,fitsWidth,url,fixture}){
  await command('Page.enable');await command('Runtime.enable');
  await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait("!!document.querySelector('#profile-open')&&"+idle,30000);
  await evaluate(`window.observed=[];const original=window.fetch;window.fetch=async(...args)=>{const r=await original(...args);if(r.headers.get('content-type')?.includes('application/json'))observed.push({path:String(args[0]),body:args[1]?.body?JSON.parse(args[1].body):null,status:r.status,value:await r.clone().json()});if(window.loseRoleSave&&String(args[0])==='/api/settings/ai/agents/save-operating-role'&&r.ok){window.loseRoleSave=false;throw new TypeError('Synthetic lost role-save response');}return r;};window.__api=async(path,body)=>{const boot=await(await window.workspaceFetch('/api/bootstrap',undefined,original)).json();const r=await window.workspaceFetch('/api/'+path,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json','X-Preview-CSRF':boot.csrfToken},...body===undefined?{}:{body:JSON.stringify(body)}},original);const data=await r.json();return{status:r.status,data};};`);
  const api=async(path,body)=>{const result=await evaluate(`__api(${JSON.stringify(path)},${JSON.stringify(body)})`);if(result.status!==200)throw Error(path+' '+JSON.stringify(result));return result.data;};
  await persona('admin');
  for(const [login,roles] of [[fixture.manager,['access_administrator','viewer']],[fixture.reader,['risk_analyst']]])
    await api('admin/create',{operationId:crypto.randomUUID(),login,name:'Synthetic agent settings '+login,kind:'human',roles});
  await persona(fixture.manager);
  const side={call:api};await qualifyProvider(side,'synthetic-agent-operating-role-key');
  const model=await pricedModel(side,{identifier:fixture.name.toLowerCase().replaceAll(' ','-'),displayName:'Synthetic operating role model',efforts:['low']});await model.enable();
  await click('#nav-ai');await click('#ai-tab-agents');
  await set('#ai-agent-name',fixture.name);await submit('#ai-agent-create-form');
  await check('A new agent requires an explicit additional operating-role choice',"document.querySelector('#ai-agent-operating-role').value===''&&[...document.querySelector('#ai-agent-operating-role').options].map(o=>o.value).join()==='\u002cnone,gas_lifecycle_operator'");
  await check('Agent-only gas authority is separate from roles allowed to delegate',"![...document.querySelectorAll('#ai-agent-roles input')].some(n=>n.value==='gas_lifecycle_operator')&&[...document.querySelector('#ai-agent-operating-role').options].find(n=>n.value==='gas_lifecycle_operator').textContent.includes('RecordGasOwnedHoldings')");
  async function fill(role){
    for(const [field,value] of Object.entries({description:'Explicitly reviewed native gas operations.',instructions:'Read the exact selected source and request manual approval for every financial change.',
      model:model.id,effort:'low',caching:'off','operating-role':role,'tool-steps':'8','input-tokens':'200000','output-tokens':'2048',elapsed:'120',read:'15',retries:'1',history:'20','grant-days':'1'}))
      await set('#ai-agent-'+field,value);
    await evaluate("for(const n of document.querySelectorAll('#ai-agent-tools input'))n.checked=['read_workspace_page','read_page_operation','propose_page_action'].includes(n.value);for(const n of document.querySelectorAll('#ai-agent-roles input'))n.checked=['trader','risk_analyst'].includes(n.value);");
  }
  await fill('');const before=await evaluate("observed.filter(r=>r.path==='/api/settings/ai/agents/save-operating-role').length");await submit('#ai-agent-form');
  await check('A blank operating role sends no save and acquires no implicit capability',`document.querySelector('#error').textContent.includes('Choose an additional operating role')&&observed.filter(r=>r.path==='/api/settings/ai/agents/save-operating-role').length===${before}`);
  await set('#ai-agent-operating-role','gas_lifecycle_operator');await evaluate('window.loseRoleSave=true');await submit('#ai-agent-form');
  await check('An uncertain role save retains its exact explicit request',"!document.querySelector('#ai-agents-pending').hidden&&observed.some(r=>r.path==='/api/settings/ai/agents/save-operating-role'&&r.status===200&&r.body.operatingRole==='gas_lifecycle_operator'&&r.value.newest.operatingRole==='gas_lifecycle_operator')");
  await click('#ai-agents-retry');
  await check('Retry returns the first role-version outcome without saving a second version',"(()=>{const a=observed.filter(r=>r.path==='/api/settings/ai/agents/save-operating-role');return a.length===2&&JSON.stringify(a[0].body)===JSON.stringify(a[1].body)&&JSON.stringify(a[0].value)===JSON.stringify(a[1].value)&&a[1].value.newest.version===1&&document.querySelector('#ai-agents-pending').hidden;})()");
  const accepted=await evaluate("observed.filter(r=>r.path==='/api/settings/ai/agents/save-operating-role').at(-1).body");
  for(const [path,body,status,code] of [
    ['settings/ai/agents/save',{...accepted,operationId:crypto.randomUUID()},400,'request_format'],
    ['settings/ai/agents/save-operating-role',Object.fromEntries(Object.entries({...accepted,operationId:crypto.randomUUID()}).filter(([key])=>key!=='operatingRole')),400,'request_format'],
    ['settings/ai/agents/save-operating-role',{...accepted,operationId:crypto.randomUUID(),operatingRole:'swap_settlement_operator'},422,'model_admin_input'],
    ['settings/ai/agents/save-operating-role',{...accepted,operatingRole:'none'},409,'model_admin_conflict']]){
    const result=await evaluate(`__api(${JSON.stringify(path)},${JSON.stringify(body)})`);
    await check('Closed role-save admission refuses '+(path.endsWith('/save')?'an operating role on the legacy endpoint':body.operatingRole??'a missing explicit role'),JSON.stringify(result.status===status&&result.data.code===code));
  }
  await click('#ai-agent-publish');
  const state=async()=>{const d=await api('settings/ai/agents');return d.agents.find(a=>a.name===fixture.name);};
  let agent=await state();
  await check('Publish displays the exact additional operating role separately from allowed users',"document.querySelector('#ai-agent-published').textContent.includes('Additional operating role')&&document.querySelector('#ai-agent-published').textContent.includes('Gas lifecycle operator')&&document.querySelector('#ai-agent-published').textContent.includes('Risk analyst, Trader')");
  await persona(fixture.reader);
  const denied=await evaluate(`__api('assistant/grants/grant',${JSON.stringify({operationId:crypto.randomUUID(),agentId:agent.id,version:agent.publishedVersion})})`);
  await check('An allowed delegator without the four gas permissions cannot grant the operating role',JSON.stringify(denied.status===403&&denied.data.code==='grant_permission_missing'));
  await persona('trader');
  const oldGas=await api('assistant/grants/grant',{operationId:crypto.randomUUID(),agentId:agent.id,version:agent.publishedVersion});
  const expected=['ReadGasOwnedHoldings','ReadMarketData','ReadTrades','RecordGasOwnedHoldings','UseAssistantWorkspace'];
  await check('A user grants only the saved version’s explicit gas and tool permissions',JSON.stringify(JSON.stringify(oldGas.permissions)===JSON.stringify(expected)));
  await persona(fixture.manager);await click('#nav-ai');await click('#ai-tab-agents');
  const open=async()=>{await evaluate(`[...document.querySelectorAll('#ai-agent-table tbody tr')].find(r=>r.cells[0].textContent===${JSON.stringify(fixture.name)}).cells[0].querySelector('button').click()`);await wait(idle);};
  await open();await fill('none');await submit('#ai-agent-form');await click('#ai-agent-publish');agent=await state();
  await persona('trader');
  await check('Publishing None ends the earlier gas delegation',JSON.stringify(!(await api('assistant/grants')).grants.some(g=>g.id===oldGas.id)));
  const none=await api('assistant/grants/grant',{operationId:crypto.randomUUID(),agentId:agent.id,version:agent.publishedVersion});
  await check('The reader-only version requests no gas permissions',JSON.stringify(JSON.stringify(none.permissions)==='["UseAssistantWorkspace"]'));
  await persona(fixture.manager);await click('#nav-ai');await click('#ai-tab-agents');await open();await fill('gas_lifecycle_operator');await submit('#ai-agent-form');await click('#ai-agent-publish');agent=await state();
  await persona('trader');
  await check('Publishing Gas again leaves old reader and gas grants ended',JSON.stringify(!(await api('assistant/grants')).grants.some(g=>[oldGas.id,none.id].includes(g.id))));
  const grant=await api('assistant/grants/grant',{operationId:crypto.randomUUID(),agentId:agent.id,version:agent.publishedVersion});
  await check('Only a fresh explicit grant enables the newly published gas version',JSON.stringify(JSON.stringify(grant.permissions)===JSON.stringify(expected)&&![oldGas.id,none.id].includes(grant.id)));
  await persona(fixture.manager);await click('#nav-ai');await click('#ai-tab-agents');await open();
  await shot('agent-operating-role-desktop');await check('Operating-role settings fit the desktop viewport',fitsWidth);
  await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await check('Operating-role settings fit a narrow viewport',fitsWidth);await shot('agent-operating-role-mobile');
  await writeFile(join(dirname(process.argv[2]),'agent-operating-role-result.json'),JSON.stringify({agentId:agent.id,delegation:grant.id,retiredGasDelegation:oldGas.id}));
}

if(process.argv.length!==4)throw Error('Use the owned agent-operating-role fixture.');
console.log(`${await withOwnedBrowser(process.argv[2],process.argv[3],run)} full-app agent operating-role checks passed.`);
