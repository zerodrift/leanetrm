import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';
const page='<div id="gas-form"></div>';
import {fixture,batchFixture} from './gas-owned-fixture.mjs';
test('linked correction rows lazy-read exact sources and keep stock-use revisions scoped to their performance',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${batchFixture}addMember('a');})()`);await new Promise(r=>setTimeout(r,30));
 await evaluate("addMember('b')");await new Promise(r=>setTimeout(r,30));
 assert.deepEqual((await evaluate('batchReads')).map(r=>r.target.bindingId),['a','b']);
 assert.deepEqual(await evaluate("[...document.querySelectorAll('[data-gas-entry=corrections]')].map(r=>[...r.querySelectorAll('[name=stockUse] option')].map(o=>o.value))"),[[],[]]);
 await evaluate("fillMember('a');fillMember('b');document.querySelector('[name=policyId]').value='policy-1:2'");
 assert.deepEqual(await evaluate("[...document.querySelectorAll('[data-gas-entry=corrections]')].map(r=>[...r.querySelectorAll('[name=stockUse] option')].map(o=>o.value))"),[['','use-a'],['','use-b']]);
 const draft=await evaluate('editor.read()');assert.deepEqual(draft.selection.corrections.map(c=>[c.performanceId,c.expectedRevision,c.expectedPrincipalVersion,c.supply[0].consumptionId,c.supply[0].expectedRevision,c.quantity]),[['a',3,4,'use-a',2,'0.125'],['b',3,4,'use-b',2,'0.125']]);
 assert.equal(await evaluate("document.querySelector('[name=corrections] option[value=empty]')===null"),true);assert.deepEqual(await evaluate('sent'),[]);
}));
test('late member fields cannot replace a newly selected observed performance',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${batchFixture}const original=batchRead;window.releaseA=null;window.batchRead=body=>body.target.bindingId==='a'?new Promise(resolve=>releaseA=()=>resolve(original(body))):original(body);addMember('a');})()`);
 await new Promise(r=>setTimeout(r,30));await evaluate("(()=>{const n=document.querySelector('[name=corrections-member]');n.value='b';n.dispatchEvent(new Event('change',{bubbles:true}));})()");await new Promise(r=>setTimeout(r,30));
 await evaluate('releaseA()');await new Promise(r=>setTimeout(r,30));assert.deepEqual(await evaluate("[...document.querySelectorAll('[name=observedPerformance] option')].map(o=>o.value)"),['','b']);
 assert.deepEqual(await evaluate('sent'),[]);
}));
test('linked correction source replies must match the exact member and original context',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${batchFixture}const original=batchRead;window.batchRead=async body=>({...await original(body),target:{...body.target,bindingId:'other'}});addMember('a');})()`);await new Promise(r=>setTimeout(r,30));
 assert.match(await evaluate("document.querySelector('[data-gas-entry=corrections]').textContent"),/do not match.*exact observed target/);
 assert.equal(await evaluate("document.querySelector('[name=observedPerformance]')===null"),true);assert.deepEqual(await evaluate('sent'),[]);
}));
test('a linked correction cannot include the same observed performance twice',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${batchFixture}addMember('a');})()`);await new Promise(r=>setTimeout(r,30));await evaluate("addMember('a');document.querySelector('[name=policyId]').value='policy-1:2'");await new Promise(r=>setTimeout(r,30));
 assert.match(await evaluate("editor.read().then(()=>'',error=>error.message)"),/each observed performance only once/);assert.deepEqual(await evaluate('sent'),[]);
}));
test('gas title form chooses exact native sources, keeps exact quantity text and resolves local observed times',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}})()`);
  assert.equal(await evaluate("document.querySelector('[name=carrying]').value"),'');
  assert.equal(await evaluate("document.querySelector('[name=originPolicy]').value"),'');
  await evaluate('fillGas()');
  const result=await evaluate('editor.read()');
  assert.deepEqual(result,{policyId:'policy-1',policyRevision:2,selection:{evidenceId:'evidence-1',quantity:'0.333333333333333333',
   titleAt:'2026-11-06T18:00:00Z',availableAt:'2026-11-06T18:00:00Z',originPolicy:'opening_owned_stock_v1',carrying:'nominal_mark_v1'}});
  assert.equal(await evaluate("document.querySelector('textarea')===null"),true);
  assert.deepEqual(await evaluate('sent'),[]);
 });
});
test('editing a gas quantity while its local clock resolves cannot return a mixed financial draft',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture.replace("const api=async(path,body)=>{", "const api=async(path,body)=>{await new Promise(resolve=>{(window.gasResolvers??=[]).push(resolve);});")}
   fillGas();window.result=editor.read().then(value=>({value}),error=>({error:error.message}));
   const quantity=document.querySelector('[name=quantity]');quantity.value='7';quantity.dispatchEvent(new Event('input',{bubbles:true}));
   for(const resolve of gasResolvers)resolve();
  })()`);
  assert.match((await evaluate('result')).error,/changed|current/i);
  assert.equal(await evaluate("document.querySelector('[name=quantity]').value"),'7');
  assert.deepEqual(await evaluate('sent'),[]);
 });
});
test('gas forms refuse incomplete backend field metadata instead of constructing empty objects',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  const result=await evaluate(`(async()=>{${fixture}
   setup.forms['gas.owned.receive_title'].fields=[{name:'cashout',label:'Cashout terms',kind:'object',nullable:false}];
   try{mountGasOwnedAction(document.querySelector('#gas-form'),offer,setup,options);return 'accepted';}catch(error){return error.message;}
  })()`);
  assert.match(result,/field|schema|metadata/i);
 });
});
test('gas cashout settings retain an explicit nested policy and its exact scenario reference',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}
   const {mountGasOwnedSettings}=await import('/gas-owned-lifecycle.js');
   setup.configuration={projection_profile:{label:'Quantity and cashout policy',permission:'ConfigureGasOwnedHoldings',fields:[
    {name:'implementation',label:'Model',kind:'select',nullable:false,choices:[{id:'gas_quantity_and_cashout_lifecycle_projection_v2',label:'Quantity and cashout model'}]},
    {name:'cashouts',label:'Cashout selections',kind:'array',nullable:false,fields:[
     {name:'positionId',label:'Native claim',kind:'select',nullable:false,choices:[{id:'claim-1',label:'Synthetic operator claim'}]},
     {name:'scenarioMark',label:'Expected cashout market',kind:'reference',nullable:false,members:['scenarioDefinitionRevisionId','scenarioCurve','scenarioContractId'],
      choices:[{id:'mark-1',label:'Synthetic exact cashout mark',value:{scenarioDefinitionRevisionId:'definition-1',scenarioCurve:'synthetic_cashout',scenarioContractId:'contract-1'}}]},
     {name:'cashCarrying',label:'Cash carrying',kind:'select',nullable:false,choices:[{id:'explicit_nominal_cashout_cash_v1',label:'Explicit nominal cash'}]}]}]}};
   mountGasOwnedSettings(document.querySelector('#gas-form'),setup,options);
   const command=document.querySelector('#gas-form>select');command.value='projection_profile';command.dispatchEvent(new Event('change'));await Promise.resolve();await Promise.resolve();
   const choose=(name,value)=>{const input=document.querySelector('[name='+name+']');input.value=value;input.dispatchEvent(new Event('change',{bubbles:true}));};
   choose('accountId','account-1');choose('implementation','gas_quantity_and_cashout_lifecycle_projection_v2');
   if(document.querySelector('[name=cashouts]').value!=='')throw Error('Cashout disposition was silently filled.');
   choose('cashouts','entries');[...document.querySelectorAll('button')].find(b=>b.textContent==='Add entry').click();
   choose('positionId','claim-1');choose('scenarioMark','mark-1');choose('cashCarrying','explicit_nominal_cashout_cash_v1');
   document.querySelector('form').requestSubmit();await new Promise(resolve=>setTimeout(resolve,0));
  })()`);
  const commands=await evaluate('sent');assert.equal(commands.length,1);
  assert.equal(commands[0].path,'lifecycle/gas/configure');assert.equal(commands[0].body.action,'projection_profile');
  assert.deepEqual(commands[0].body.selection,{implementation:'gas_quantity_and_cashout_lifecycle_projection_v2',cashouts:[{positionId:'claim-1',
   scenarioDefinitionRevisionId:'definition-1',scenarioCurve:'synthetic_cashout',scenarioContractId:'contract-1',cashCarrying:'explicit_nominal_cashout_cash_v1'}]});
 });
});
test('changing the gas account while an observed clock resolves cannot queue a mixed configuration',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}
   const {mountGasOwnedSettings}=await import('/gas-owned-lifecycle.js');
   setup.accounts.push({id:'account-2',label:'Another synthetic account'});
   setup.configuration={evidence:{label:'Observed evidence',permission:'RecordGasOwnedHoldings',fields:[{name:'occurredAt',label:'Observed at',kind:'instant',nullable:false}]}};
   const slow=async()=>{await new Promise(resolve=>{(window.configResolvers??=[]).push(resolve);});return {candidates:[{instant:'2026-11-06T18:00:00Z',offset:'-06:00'}]};};
   window.configErrors=[];mountGasOwnedSettings(document.querySelector('#gas-form'),setup,{...options,api:slow,act:async work=>{try{await work();}catch(error){configErrors.push(error.message);}}});
   const command=document.querySelector('#gas-form>select');command.value='evidence';command.dispatchEvent(new Event('change'));await Promise.resolve();await Promise.resolve();
   const account=document.querySelector('[name=accountId]');account.value='account-1';account.dispatchEvent(new Event('change',{bubbles:true}));
   const at=document.querySelector('[name=occurredAt]');at.value='2026-11-06T12:00:00';at.dispatchEvent(new Event('input',{bubbles:true}));
   document.querySelector('form').dispatchEvent(new Event('submit',{bubbles:true,cancelable:true}));await Promise.resolve();
   account.value='account-2';account.dispatchEvent(new Event('change',{bubbles:true}));for(const resolve of configResolvers)resolve();
  })()`);
  for(let i=0;i<10;i++){if(await evaluate('configErrors.length||sent.length'))break;await new Promise(r=>setTimeout(r,20));}
  assert.deepEqual(await evaluate('sent'),[]);
  assert.match((await evaluate('configErrors'))[0],/account|changed/i);
 });
});
test('Lifecycle reviews a gas observation through typed fields and shows the native effect before execution',{skip:!browserAvailable,timeout:browserTestTimeout},async()=>{
 await withBrowserPage(page,async evaluate=>{
  await evaluate(`(async()=>{${fixture}
   const {setupLifecycle}=await import('/lifecycle-view.js');window.gasErrors=[];
   const discovery={context:setup.context,actions:[offer],policies:[],optionProfiles:[],swapProfiles:[],marginPolicies:[],choices:{actions:[{code:offer.action,automatic:false,configurationPermission:'ConfigureGasOwnedHoldings',executionPermission:'RecordGasOwnedHoldings'}],modes:['manual','review'],policyImplementation:'explicit_lifecycle_action_policy_v1',swapRounding:[],swapDue:[],swapRecognition:[],swapCorrection:[]}};
   const actual=async(path,body)=>{if(path==='lifecycle/view')return discovery;if(path==='lifecycle/gas/setup')return {...setup,target:body.target};
    if(path==='lifecycle/preview'){sent.push({path,body});return {hash:'a'.repeat(64),offer,details:{selection:body.selection,willChange:true,effects:{kind:'receive_title',performedQuantity:'0.333333333333333333',quantitySpecification:'synthetic_energy',currency:'usd',effectiveAt:'2026-11-06T18:00:00Z'}}};}return api(path,body);};
   window.desk=setupLifecycle(document.querySelector('#gas-form'),{...options,api:actual,busy:()=>false,identity:()=> 'synthetic_operator',context:()=>({snapshotId:'snapshot-1'}),historical:()=>false,act:async work=>{try{await work();}catch(error){gasErrors.push(error.message);}}});await desk.load();
  })()`);
  await new Promise(r=>setTimeout(r,50));await evaluate("document.querySelector('.tabulator-row').click()");await new Promise(r=>setTimeout(r,50));
  assert.equal(await evaluate("document.querySelector('[name=quantity]')!==null"),true);
  await evaluate("fillGas();document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await new Promise(r=>setTimeout(r,50));
  assert.deepEqual(await evaluate('gasErrors'),[]);
  const text=await evaluate("document.querySelector('[data-lifecycle-review]').textContent");assert.match(text,/0\.333333333333333333/);assert.match(text,/synthetic_energy/);assert.match(text,/12:00 PM/);
  const sent=await evaluate('sent');assert.equal(sent.length,1);assert.equal(sent[0].path,'lifecycle/preview');assert.equal(sent[0].body.selection.quantity,'0.333333333333333333');assert.equal(sent[0].body.selection.titleAt,'2026-11-06T18:00:00Z');
 });
});
