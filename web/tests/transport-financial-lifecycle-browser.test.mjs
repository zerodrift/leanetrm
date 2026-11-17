import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const context={contextId:'transport-context',asOf:'2026-11-02T15:00:00Z',businessDate:'2026-11-02',snapshotId:'transport-close'};
const target={handler:'gas_transport_financial',scopeId:'transport-agreement',bindingId:'transport-instrument'},action='gas.transport_financial.expire';
const offer={target,action,label:'Inspect Transport rights and fee',occurrenceId:'rights-expiry',at:context.asOf,before:null,eligible:false,reason:'lifecycle_event_not_due',revision:1,stateVersion:1,selection:null,requiredInputs:[],subject:{reference:'Synthetic Transport',product:'Transport',bookId:'transport-book',book:'Synthetic book'}};
const discovery={context,actions:[offer],policies:[],optionProfiles:[],swapProfiles:[],marginPolicies:[],choices:{actions:[{code:action,automatic:false,configurationPermission:'AmendTrade',executionPermission:'AmendTrade'}],modes:[],policyImplementation:'explicit_lifecycle_action_policy_v1',swapRounding:[],swapDue:[],swapRecognition:[],swapCorrection:[]}};
const commandForm={label:'Adopt an exact capacity version',permission:'AmendTrade',fields:[
 {name:'source',label:'Current financial state and capacity',kind:'reference',nullable:false,members:['kind','tradeId','expectedFinancialStateId','capacityVersionId'],choices:[{id:'capacity-source',label:'Retained rights → selected capacity revision',value:{kind:'adopt_capacity_version',tradeId:target.scopeId,expectedFinancialStateId:'financial-state-1',capacityVersionId:'capacity-version-2'}}]},
 {name:'effectiveAt',label:'Financial amendment time',kind:'instant',nullable:false},{name:'evidence',label:'Amendment evidence',kind:'text',nullable:false}]};
const setup={context,target,ownerId:'transport-owner',accounts:[],policies:[],forms:{},configuration:{adopt_capacity_version:commandForm}};
const boot=`(async()=>{
 window.errors=[];window.calls=[];window.actor='transport-operator';window.historical=false;window.canAmend=true;window.failRecord=false;window.statusFound=true;window.wrongStatus=false;window.badEcho=false;window.holdPreview=false;window.previews=[];window.changedSources=0;
 window.sourceSetup=${JSON.stringify(setup)};window.sourceDiscovery=${JSON.stringify(discovery)};
 const {setupLifecycle}=await import('/lifecycle-view.js');
 window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(r=>setTimeout(r,10));if(!test())throw Error('Financial Transport controls not ready');};
 window.api=async(path,body)=>{calls.push({path,body:structuredClone(body)});
  if(path==='lifecycle/view')return sourceDiscovery;
  if(path==='gas/transport/financial/setup')return sourceSetup;
  if(path==='lifecycle/preview')return{hash:'a'.repeat(64),offer:sourceDiscovery.actions[0],details:{overviewSections:[{label:'Explicit rights expiry',values:[{label:'Remaining rights',value:'0 USD'},{label:'Independent unpaid fee',value:'-12.5 USD remains owed'},{label:'Expiry effective',value:sourceSetup.context.asOf,kind:'instant'}]}]}};
  if(path==='lifecycle/execute')return{operationId:body.operationId,changed:true};
  if(path==='time/inspect')return{candidates:[{instant:'2026-11-02T15:00:00Z',offset:'-06:00'}]};
  if(path==='gas/transport/financial/preview'){
   const response={...structuredClone(body),tradeId:body.command.tradeId,kind:body.command.kind,requestedCommand:structuredClone(body.command),requestedEffectiveAt:body.effectiveAt,acknowledgement:false,outcome:{tradeId:body.command.tradeId,kind:body.command.kind,changed:true},overviewSections:[{label:'Financial effect',values:[{label:'Rights package',value:'One whole remaining-rights package'},{label:'Independent fee',value:'Unchanged'},{label:'Effective',value:body.effectiveAt,kind:'instant'}]}]};
   if(badEcho)response.requestedCommand.capacityVersionId='different-version';
   if(holdPreview)return new Promise(resolve=>previews.push({body:structuredClone(body),resolve:()=>resolve(response)}));return response;
  }
  if(path==='gas/transport/financial/record'){window.recorded=structuredClone(body);if(failRecord)throw new TypeError('Synthetic lost financial acknowledgement');return{found:true,...body,tradeId:body.command.tradeId,kind:body.command.kind,outcome:{tradeId:body.command.tradeId,kind:body.command.kind,changed:true}};}
  if(path==='gas/transport/financial/status')return statusFound?{found:true,...recorded,tradeId:wrongStatus?'another-agreement':recorded.command.tradeId,kind:recorded.command.kind,outcome:{tradeId:recorded.command.tradeId,kind:recorded.command.kind,changed:true}}:{found:false};
  if(path==='cash-payments/record'){window.payment=structuredClone(body);if(failRecord)throw new TypeError('Synthetic lost cash acknowledgement');return{operationId:body.operationId,tradeId:body.tradeId,bindingId:body.bindingId,allocationId:body.allocationId,revision:3};}
  if(path==='cash-payments/status')return{found:true,outcome:{operationId:body.operationId,tradeId:body.tradeId,bindingId:body.bindingId,allocationId:body.allocationId,revision:3}};
  throw Error('Unexpected operation '+path);
 };
 window.mount=()=>window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async work=>{try{await work();}catch(error){errors.push(error.message);}},busy:()=>false,identity:()=>actor,context:()=>({snapshotId:'header'}),historical:()=>historical,zone:()=> 'America/Chicago',permission:name=>name==='ReadTrades'||name==='ReadMarketData'||name==='AmendTrade'&&canAmend,changed(){},committed(){changedSources++;}});
 mount();await desk.load();await until(()=>document.querySelector('.tabulator-row'));document.querySelector('.tabulator-row').click();await new Promise(r=>setTimeout(r,50));
})()`;
const fill=`(()=>{const form=document.querySelector('[data-transport-financial-command]');form.closest('details').open=true;for(const[name,value]of Object.entries({source:'capacity-source',effectiveAt:'2026-11-02T09:00:00',evidence:'Synthetic explicit amendment'})){const input=form.querySelector('[name="'+name+'"]');input.value=value;input.dispatchEvent(new Event('change',{bubbles:true}));}form.requestSubmit();})()`;
const page='<div id="host"></div>',options={skip:!browserAvailable,timeout:browserTestTimeout};

test('financial Transport opens backend forms without owned or operating authority and records only after review',options,()=>withBrowserPage(page,async evaluate=>{
 await evaluate(boot);
 assert.equal(await evaluate("!!document.querySelector('[data-transport-financial-command]')"),true);
 assert.equal(await evaluate("document.querySelector('[data-lifecycle-execute]')===null"),true,'ineligible expiry must not offer a performance form');
 await evaluate(fill);await evaluate("until(()=>document.querySelector('[data-transport-financial-record]'))");
 assert.equal((await evaluate('calls')).some(call=>call.path==='gas/transport/financial/record'),false);
 assert.match(await evaluate("document.querySelector('[data-transport-financial-review]').textContent"),/One whole remaining-rights package.*Unchanged.*9:00 AM/);
 await evaluate("document.querySelector('[data-transport-financial-record]').click();until(()=>changedSources===1)");
 const calls=await evaluate('calls'),preview=calls.find(call=>call.path.endsWith('/preview')),record=calls.find(call=>call.path.endsWith('/record'));
 assert.deepEqual(record.body,preview.body);assert.equal(record.body.ownerId,'transport-owner');assert.deepEqual(record.body.context,context);
 assert.equal(record.body.command.capacityVersionId,'capacity-version-2');
 assert.equal(calls.some(call=>call.path==='lifecycle/gas/setup'),false);
 assert.deepEqual(await evaluate('errors'),[]);
}));

test('a phone keeps long described financial sources and the reviewed record control within its viewport',options,()=>withBrowserPage('<meta name="viewport" content="width=device-width,initial-scale=1"><link rel="stylesheet" href="/app.css"><main><div id="host"></div></main>',async(evaluate,command)=>{
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:1000,deviceScaleFactor:1,mobile:true});
 await evaluate(boot);
 await evaluate("(async()=>{sourceSetup.configuration.adopt_capacity_version.label='Adopt signed whole-rights market value';sourceSetup.configuration.adopt_capacity_version.fields[0].choices[0].label='Synthetic independent provider rights PV · -20 USD · synthetic_transport_marks';desk.reset();mount();await desk.load();await until(()=>document.querySelector('.tabulator-row'));document.querySelector('.tabulator-row').click();})()");
 await evaluate("until(()=>document.querySelector('[data-transport-financial-command]'))");await evaluate(fill);await evaluate("until(()=>document.querySelector('[data-transport-financial-record]'))");
 await evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
 await evaluate("document.querySelector('[data-transport-financial-record]').scrollIntoView({block:'end'})");
 const geometry=await evaluate("(()=>{const r=document.querySelector('[data-transport-financial-record]').getBoundingClientRect();return{viewport:document.documentElement.clientWidth,width:document.documentElement.scrollWidth,record:{left:r.left,right:r.right,top:r.top,bottom:r.bottom},height:innerHeight};})()");
 assert.ok(geometry.width<=geometry.viewport+1&&geometry.record.left>=0&&geometry.record.right<=geometry.viewport+1&&geometry.record.top>=0&&geometry.record.bottom<=geometry.height,JSON.stringify(geometry));
 assert.match(await evaluate("document.querySelector('[data-transport-financial-review]').textContent"),/One whole remaining-rights package/);
}));

test('a lost financial response retains canonical request and recovers by exact actor-bound status after remount',options,()=>withBrowserPage(page,async evaluate=>{
 await evaluate(boot);await evaluate('failRecord=true');await evaluate(fill);await evaluate("until(()=>document.querySelector('[data-transport-financial-record]'))");
 await evaluate("document.querySelector('[data-transport-financial-record]').click();until(()=>recorded&&document.querySelector('[data-lifecycle=pending]').hidden===false)");
 const original=await evaluate('recorded');
 await evaluate("actor='other-actor';desk.reset();desk.sync()");assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);
 await evaluate("actor='transport-operator';desk.reset();desk.sync();statusFound=false;document.querySelector('[data-lifecycle=check-status]').click()");
 await evaluate("until(()=>calls.some(call=>call.path.endsWith('/status')))");assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),false);
 await evaluate("wrongStatus=true;statusFound=true;document.querySelector('[data-lifecycle=check-status]').click();until(()=>errors.length===2)");
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),false);
 await evaluate("wrongStatus=false;document.querySelector('[data-lifecycle=check-status]').click();until(()=>document.querySelector('[data-lifecycle=pending]').hidden)");
 for(const call of (await evaluate('calls')).filter(call=>call.path.endsWith('/status')))assert.deepEqual(call.body,{operationId:original.operationId,ownerId:original.ownerId});
 assert.equal((await evaluate('calls')).filter(call=>call.path.endsWith('/record')).length,1);
 assert.equal(await evaluate('changedSources'),1);
}));

test('an altered financial input echo withholds Record rather than substituting another exact source',options,()=>withBrowserPage(page,async evaluate=>{
 await evaluate(boot);await evaluate('badEcho=true');await evaluate(fill);await evaluate('until(()=>errors.length===1)');
 assert.match((await evaluate('errors'))[0],/exact.*review|review.*exact/i);
 assert.equal(await evaluate("document.querySelector('[data-transport-financial-record]')===null"),true);
}));

test('a financial edit outruns an earlier preview and historical context keeps every write disabled',options,()=>withBrowserPage(page,async evaluate=>{
 await evaluate(boot);await evaluate('holdPreview=true');await evaluate(fill);await evaluate('until(()=>previews.length===1)');
 await evaluate("document.querySelector('[name=evidence]').value='A different explicit amendment';document.querySelector('[name=evidence]').dispatchEvent(new Event('input',{bubbles:true}));previews[0].resolve()");
 await new Promise(resolve=>setTimeout(resolve,60));assert.equal(await evaluate("document.querySelector('[data-transport-financial-record]')===null"),true);
 await evaluate('historical=true;desk.sync()');
 assert.equal(await evaluate("[...document.querySelectorAll('[data-transport-financial-command] button')].every(button=>button.disabled)"),true);
}));

const paymentForm={label:'Observed Transport fee payment',permission:'AmendTrade',fields:[
 {name:'cashOwner',label:'Independent fee cash',kind:'reference',nullable:false,members:['snapshotId','knowledgeMode','tradeId','bindingId'],choices:[{id:'fee-cash',label:'Independent fee -12.5 USD',value:{snapshotId:context.snapshotId,knowledgeMode:'current',tradeId:target.scopeId,bindingId:'independent-fee-cash'}}]},
 {name:'allocation',label:'Recorded payment',kind:'reference',nullable:false,members:['allocationId','expectedRevision'],choices:[{id:'payment-head',label:'Recorded payment · revision 2',value:{allocationId:'payment-allocation',expectedRevision:2}}]},
 {name:'paidAt',label:'Observed payment time',kind:'instant',nullable:false},{name:'amount',label:'Observed signed amount',kind:'text',nullable:false},
 {name:'policy',label:'Recorded cash treatment',kind:'select',nullable:false,choices:[{id:'explicit_paid_cash_nominal_due_v1',label:'Explicit recorded cash'}]}]};
test('closed Transport keeps its independent ordinary cash editor and recovers its complete retained payment body',options,()=>withBrowserPage(page,async evaluate=>{
 await evaluate(boot);
 await evaluate(`(async()=>{sourceSetup.configuration={payment:${JSON.stringify(paymentForm)}};sourceDiscovery.actions[0].reason='gas_transport_financial_closed';await desk.load();await until(()=>document.querySelector('.tabulator-row'));document.querySelector('.tabulator-row').click();await until(()=>document.querySelector('[data-transport-payment]'));})()`);
 assert.equal(await evaluate("document.querySelector('[name=accountId]')===null"),true);
 assert.equal(await evaluate("document.querySelector('[data-lifecycle-execute]')===null"),true);
 await evaluate(`(()=>{failRecord=true;const form=document.querySelector('[data-transport-payment]');form.closest('details').open=true;
  for(const[name,value]of Object.entries({cashOwner:'fee-cash',allocation:'payment-head',paidAt:'2026-11-02T09:00:00',amount:'-5.000000000000000001',policy:'explicit_paid_cash_nominal_due_v1'})){const input=form.querySelector('[name="'+name+'"]');input.value=value;input.dispatchEvent(new Event('change',{bubbles:true}));}form.requestSubmit();})()`);
 await evaluate("until(()=>window.payment&&document.querySelector('[data-lifecycle=pending]').hidden===false)");
 const original=await evaluate('payment');assert.equal(original.bindingId,'independent-fee-cash');assert.equal(original.amount,'-5.000000000000000001');
 assert.equal(Object.hasOwn(original,'ownerId'),false);assert.equal(Object.hasOwn(original,'command'),false);
 await evaluate("desk.reset();desk.sync();document.querySelector('[data-lifecycle=check-status]').click();until(()=>document.querySelector('[data-lifecycle=pending]').hidden)");
 assert.deepEqual((await evaluate('calls')).find(call=>call.path==='cash-payments/status').body,original);
 assert.equal((await evaluate('calls')).filter(call=>call.path==='cash-payments/record').length,1);
}));

test('explicit Transport expiry uses the saved lifecycle policy and backend financial review while independent cash survives',options,()=>withBrowserPage(page,async evaluate=>{
 await evaluate(boot);
 await evaluate(`(async()=>{sourceDiscovery.actions[0].eligible=true;sourceDiscovery.actions[0].reason=null;
  sourceSetup.configuration={};sourceSetup.policies=[{id:'expiry-policy',revision:3,action:${JSON.stringify(action)},accountId:${JSON.stringify(target.scopeId)},label:'Reviewed explicit expiry'}];
  sourceSetup.forms[${JSON.stringify(action)}]={label:'Expire whole rights',permission:'AmendTrade',fields:[{name:'state',label:'Financial state',kind:'reference',nullable:false,members:['expectedFinancialStateId'],choices:[{id:'state-head',label:'Current live rights',value:{expectedFinancialStateId:'financial-state-1'}}]},{name:'evidence',label:'Expiry evidence',kind:'text',nullable:false}]};
  await desk.load();await until(()=>document.querySelector('.tabulator-row'));document.querySelector('.tabulator-row').click();await until(()=>document.querySelector('[name=state]'));
  const form=document.querySelector('[data-lifecycle=panel] form');for(const[name,value]of Object.entries({policyId:'expiry-policy:3',state:'state-head',evidence:'Synthetic explicit expiry'})){const input=form.querySelector('[name="'+name+'"]');input.value=value;input.dispatchEvent(new Event('change',{bubbles:true}));}form.requestSubmit();await until(()=>document.querySelector('[data-lifecycle-execute]'));
 })()`);
 assert.match(await evaluate("document.querySelector('[data-lifecycle-review]').textContent"),/Remaining rights0 USD.*-12.5 USD remains owed.*9:00 AM/);
 await evaluate("document.querySelector('[data-lifecycle-execute]').click();until(()=>changedSources===1)");
 const request=(await evaluate('calls')).find(call=>call.path==='lifecycle/execute').body;
 assert.deepEqual(request.target,target);assert.equal(request.action,action);assert.equal(request.policyId,'expiry-policy');assert.equal(request.policyRevision,3);
 assert.equal(request.automatic,false);assert.equal(request.previewHash,'a'.repeat(64));assert.deepEqual(request.selection,{expectedFinancialStateId:'financial-state-1',evidence:'Synthetic explicit expiry'});
}));
