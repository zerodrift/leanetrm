import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const context={contextId:'custody-context',asOf:'2026-11-02T15:00:00Z',businessDate:'2026-11-02',snapshotId:'custody-close'};
const target={handler:'gas_park_custody',scopeId:'custody-agreement',bindingId:'custody-instrument'};
const action='gas.park_custody.accept';
const offer={target,action,label:'Accept custody',occurrenceId:'custody-acceptance',at:context.asOf,before:null,eligible:true,reason:null,revision:1,stateVersion:0,selection:null,requiredInputs:[],subject:{reference:'Synthetic custody',product:'Park custody',bookId:'custody-book',book:'Synthetic book'}};
const discovery={context,actions:[offer],policies:[],optionProfiles:[],swapProfiles:[],marginPolicies:[],choices:{actions:[{code:action,automatic:false,configurationPermission:'ConfigureGasOperations',executionPermission:'RecordGasOperations'}],modes:[],policyImplementation:'explicit_lifecycle_action_policy_v1',swapRounding:[],swapDue:[],swapRecognition:[],swapCorrection:[]}};
const setup={context,target,accounts:[],policies:[{id:'custody-policy',label:'Explicit custody acceptance',revision:2,action,accountId:target.scopeId}],forms:{[action]:{label:'Accept custody',permission:'RecordGasOperations',fields:[{name:'serviceVersion',label:'Agreement head',kind:'reference',nullable:false,members:['expectedVersion'],choices:[{id:'head-0',label:'Unaccepted agreement',value:{expectedVersion:0}}]}]}},configuration:{evidence:{label:'Custody observation',permission:'RecordGasOperations',fields:[{name:'businessKey',label:'Observation reference',kind:'text',nullable:false},{name:'quantity',label:'Observed quantity',kind:'text',nullable:false},{name:'observedAt',label:'Observed custody time',kind:'instant',nullable:false}]}}};
const effects={acknowledgementOutsideSource:false,performedQuantity:'720.000000000000000001',acknowledgedQuantity:null,quantitySpecification:'synthetic_native_quantity',custodyBefore:'0',custodyAfter:'720.000000000000000001',reservedBefore:'0',reservedAfter:'0',ownedQuantityChange:'0',feeAmount:'-12.500000000000000001',currency:'usd',effectiveAt:context.asOf,returnAt:'2026-11-04T15:00:00Z',feeDueAt:'2026-11-05T15:00:00Z',titleRetainedByCustomer:true,physicalActivityUnchanged:true,feeCashUnchanged:false,allocations:[]};
const boot=`(async()=>{window.errors=[];window.calls=[];window.actor='custody-operator';window.historical=false;window.fail=false;window.statusFound=true;window.statusWrong=false;window.effects=${JSON.stringify(effects)};window.changedSources=0;
 const {setupLifecycle}=await import('/lifecycle-view.js');window.until=async test=>{for(let i=0;i<250&&!test();i++)await new Promise(r=>setTimeout(r,10));if(!test())throw Error('Custody controls not ready');};
 const api=async(path,body)=>{calls.push({path,body:structuredClone(body)});if(path==='lifecycle/view')return ${JSON.stringify(discovery)};if(path==='gas/park-custody/setup')return ${JSON.stringify(setup)};
 if(path==='time/inspect')return{candidates:[{instant:'2026-11-02T15:00:00Z',offset:'-06:00'}]};
 if(path==='lifecycle/preview')return{hash:'a'.repeat(64),offer:${JSON.stringify(offer)},details:{willChange:true,effects}};
 if(path==='gas/park-custody/evidence'){if(fail)throw new TypeError('Synthetic lost observation response');return{operationId:body.operationId,tradeId:body.target.scopeId,instrumentId:body.target.bindingId,action:'evidence',id:'observation',revision:1,changed:true};}
 if(path==='gas/park-custody/evidence/status')return statusFound?{found:true,outcome:{operationId:body.operationId,tradeId:statusWrong===true?'different-agreement':body.target.scopeId,instrumentId:statusWrong==='binding'?'different-instrument':body.target.bindingId,action:'evidence',id:'observation',revision:1,changed:true}}:{found:false};
 if(path==='lifecycle/execute')return{operationId:body.operationId,changed:true};throw Error('Unexpected '+path);};
 window.mount=()=>window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(e){errors.push(e.message);}},busy:()=>false,identity:()=>actor,context:()=>({snapshotId:'header'}),historical:()=>historical,zone:()=> 'America/Chicago',permission:name=>['ReadGasOperations','RecordGasOperations'].includes(name),changed(){},committed(){changedSources++;}});mount();await desk.load();await until(()=>!!document.querySelector('.tabulator-row'));document.querySelector('.tabulator-row').click();await new Promise(r=>setTimeout(r,80));})()`;
const fillReview=`(()=>{for(const[name,value]of Object.entries({policyId:'custody-policy:2',serviceVersion:'head-0'})){const input=document.querySelector('[name="'+name+'"]');input.value=value;input.dispatchEvent(new Event('change',{bubbles:true}));}document.querySelector('[data-lifecycle=panel] form').requestSubmit();})()`;
const fillEvidence=`(()=>{const form=document.querySelector('[data-custody-observation]');for(const[name,value]of Object.entries({businessKey:'synthetic-custody-receipt',quantity:'720.000000000000000001',observedAt:'2026-11-02T09:00:00'})){const input=form.querySelector('[name="'+name+'"]');input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));}form.requestSubmit();})()`;
const settle=()=>new Promise(r=>setTimeout(r,80));

test('provider custody reviews exact service balances and title boundaries without an owned-account permission', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(boot);assert.deepEqual(await evaluate('errors'),[]);
 assert.equal(await evaluate("!!document.querySelector('[name=serviceVersion]')"),true,'The provider-safe custody form must open.');
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=panel] form').inert"),false);
 assert.equal(await evaluate("document.querySelector('[name=accountId]')"),null);
 await evaluate(fillReview);await settle();assert.deepEqual(await evaluate('errors'),[]);
 const facts=await evaluate("Object.fromEntries([...document.querySelectorAll('[data-lifecycle-review] dt')].map(t=>[t.textContent,t.nextElementSibling.textContent]))");
 assert.equal(facts['Performed custody quantity'],effects.performedQuantity);assert.equal(facts['Custody after'],effects.custodyAfter);assert.equal(facts['Reserved after'],'0');assert.equal(facts['Owned quantity change'],'0');assert.equal(facts['New signed fee cash'],effects.feeAmount);assert.equal(facts.Currency,'USD');assert.match(facts['Title ownership'],/retained by customer/i);assert.match(facts['Physical activity'],/unchanged/i);assert.match(facts['Observed custody time'],/9:00 AM/);
 assert.equal(await evaluate("document.querySelector('[data-lifecycle-execute]').disabled"),false);
 await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await settle();assert.equal(await evaluate('changedSources'),1);
 const execution=(await evaluate('calls')).find(c=>c.path==='lifecycle/execute');assert.deepEqual(execution.body.target,target);assert.deepEqual(execution.body.selection,{expectedVersion:0});assert.deepEqual(execution.body.context,context);
}));

test('custody observation recovers its exact actor-bound request by status after remount and refuses a wrong target receipt', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(boot);assert.equal(await evaluate("!!document.querySelector('[data-custody-observation]')"),true);
 await evaluate('fail=true');await evaluate(fillEvidence);await settle();
 const original=(await evaluate('calls')).find(c=>c.path==='gas/park-custody/evidence');assert.ok(original);assert.deepEqual(original.body.target,target);assert.deepEqual(original.body.context,context);assert.deepEqual(original.body.selection,{businessKey:'synthetic-custody-receipt',quantity:'720.000000000000000001',observedAt:context.asOf});assert.equal(Object.hasOwn(original.body,'accountId'),false);
 await evaluate("(async()=>{desk.reset();mount();await desk.load();})()");await evaluate("statusWrong=true;document.querySelector('[data-lifecycle=check-status]').click()");await settle();assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),false);assert.equal(await evaluate('changedSources'),0);
 await evaluate("statusWrong='binding';document.querySelector('[data-lifecycle=check-status]').click()");await settle();assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),false);
 await evaluate("statusWrong=false;statusFound=false;document.querySelector('[data-lifecycle=check-status]').click()");await settle();assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),false);
 await evaluate("actor='another-operator';desk.reset();desk.sync()");assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);
 await evaluate("actor='custody-operator';desk.reset();desk.sync();statusFound=true;document.querySelector('[data-lifecycle=check-status]').click()");await settle();assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);assert.equal(await evaluate('changedSources'),1);
 for(const request of (await evaluate('calls')).filter(c=>c.path==='gas/park-custody/evidence/status'))assert.deepEqual(request.body,original.body);
 assert.equal((await evaluate('calls')).filter(c=>c.path==='gas/park-custody/evidence').length,1,'Status must not resubmit the observation.');
}));

test('custody review withholds execution when exact effect fields are absent or numeric values are lossy', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(boot);assert.equal(await evaluate("!!document.querySelector('[name=serviceVersion]')"),true);
 for(const change of ["delete effects.feeDueAt","effects={..."+JSON.stringify(effects)+",custodyAfter:720}"]){await evaluate(change);await evaluate(fillReview);await settle();assert.equal(await evaluate("document.querySelector('[data-lifecycle-execute]')===null||document.querySelector('[data-lifecycle-execute]').disabled"),true);}
 assert.equal((await evaluate('calls')).some(c=>c.path==='lifecycle/execute'),false);
}));

const paymentForm={label:'Observed fee payment',permission:'AmendTrade',fields:[
 {name:'cashOwner',label:'Accepted independent cash',kind:'reference',nullable:false,members:['snapshotId','knowledgeMode','tradeId','bindingId'],choices:[{id:'accepted-fee',label:'Accepted fee -12.5 USD',value:{snapshotId:context.snapshotId,knowledgeMode:'current',tradeId:target.scopeId,bindingId:'accepted-cash-binding'}}]},
 {name:'allocation',label:'Recorded payment',kind:'reference',nullable:false,members:['allocationId','expectedRevision'],choices:[{id:'new-payment',label:'New recorded payment',value:{allocationId:'synthetic-new-payment',expectedRevision:0}},{id:'recorded-payment-2',label:'Existing recorded payment · revision 2',value:{allocationId:'synthetic-existing-payment',expectedRevision:2}}]},
 {name:'paidAt',label:'Observed payment time',kind:'instant',nullable:false},{name:'amount',label:'Observed signed amount',kind:'text',nullable:false},
 {name:'policy',label:'Recorded cash treatment',kind:'select',nullable:false,choices:[{id:'explicit_paid_cash_nominal_due_v1',label:'Explicit recorded cash'}]}]};
const paymentBoot=boot.replace(JSON.stringify(setup),JSON.stringify({...setup,configuration:{...setup.configuration,payment:paymentForm}}))
 .replace("window.errors=[];","window.errors=[];window.canPay=true;window.failPayment=true;window.wrongPaymentReceipt=false;window.cashStatusFound=true;window.cashStatusRevision=3;")
 .replace("permission:name=>['ReadGasOperations','RecordGasOperations'].includes(name)","permission:name=>['ReadGasOperations','RecordGasOperations'].includes(name)||name==='AmendTrade'&&canPay")
 .replace("if(path==='lifecycle/execute')", "if(path==='cash-payments/status')return cashStatusFound?{found:true,outcome:{operationId:body.operationId,allocationId:body.allocationId,tradeId:body.tradeId,bindingId:wrongPaymentReceipt?'other-cash':body.bindingId,revision:cashStatusRevision}}:{found:false};if(path==='cash-payments/record'){if(failPayment)throw new TypeError('Synthetic lost bank payment acknowledgement');return{operationId:body.operationId,allocationId:body.allocationId,tradeId:body.tradeId,bindingId:wrongPaymentReceipt?'other-cash':body.bindingId,revision:3};}if(path==='lifecycle/execute')");
const fillPayment=`(()=>{const form=document.querySelector('[data-custody-payment]');form.closest('details').open=true;for(const[name,value]of Object.entries({cashOwner:'accepted-fee',allocation:'recorded-payment-2',paidAt:'2026-11-02T09:00:00',amount:'-5.000000000000000001',policy:'explicit_paid_cash_nominal_due_v1'})){const input=form.querySelector('[name="'+name+'"]');input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));}form.requestSubmit();})()`;

test('custody cash records an explicit existing allocation and retains its full shared request after a lost acknowledgement', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(paymentBoot);assert.equal(await evaluate("!!document.querySelector('[data-custody-payment]')"),true);
 assert.deepEqual(await evaluate("[...document.querySelectorAll('[data-custody-payment] select[name]')].map(n=>n.value)"),['','','']);
 await evaluate(fillPayment);await settle();
 const original=(await evaluate('calls')).find(c=>c.path==='cash-payments/record');assert.ok(original);
 const {operationId,...body}=original.body;assert.ok(operationId);assert.deepEqual(body,{snapshotId:context.snapshotId,knowledgeMode:'current',tradeId:target.scopeId,bindingId:'accepted-cash-binding',allocationId:'synthetic-existing-payment',expectedRevision:2,paidAt:context.asOf,amount:'-5.000000000000000001',policy:'explicit_paid_cash_nominal_due_v1'});
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),false);
 await evaluate("(async()=>{desk.reset();mount();await desk.load();})()");assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),false);
 await evaluate("actor='another-operator';desk.reset();desk.sync()");assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);
 await evaluate("actor='custody-operator';desk.reset();desk.sync();failPayment=false;wrongPaymentReceipt=true;document.querySelector('[data-lifecycle=retry]').click()");await settle();assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),false);assert.equal(await evaluate('changedSources'),0);
 await evaluate("wrongPaymentReceipt=false;document.querySelector('[data-lifecycle=retry]').click()");await settle();assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);assert.equal(await evaluate('changedSources'),1);
 for(const call of (await evaluate('calls')).filter(c=>c.path==='cash-payments/record'))assert.deepEqual(call,original);
}));

test('custody observations do not grant bank-payment authority and historical views disable payment controls', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(paymentBoot.replace('window.canPay=true','window.canPay=false'));assert.equal(await evaluate("document.querySelector('[data-custody-payment]')?.inert"),true);
 await evaluate(fillPayment);await settle();assert.equal((await evaluate('calls')).some(c=>c.path==='cash-payments/record'),false);
 await evaluate('canPay=true;historical=true;desk.sync()');assert.equal(await evaluate("document.querySelector('[data-custody-payment]')?.inert"),true);assert.equal(await evaluate("document.querySelector('[data-custody-payment] button').disabled"),true);
}));


test('cash status checks the original request without a live frame and refuses absent, malformed or mismatched receipts', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(paymentBoot);await evaluate(fillPayment);await settle();
 const original=(await evaluate('calls')).find(c=>c.path==='cash-payments/record');assert.ok(original);
 await evaluate('desk.reset();mount();desk.sync();historical=true;desk.sync()');
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=retry]').disabled"),true);
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=check-status]').hidden"),false);
 for(const change of ['cashStatusFound=false','cashStatusFound=true;cashStatusRevision=0','cashStatusRevision=undefined','cashStatusRevision=3;wrongPaymentReceipt=true']){
  await evaluate(change+";document.querySelector('[data-lifecycle=check-status]').click()");await settle();assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),false);assert.equal(await evaluate('changedSources'),0);
 }
 // A saved fact acknowledgement can name an older revision than the selected
 // allocation head. The native command reader, not revision arithmetic here,
 // attests that original outcome against the retained request.
 await evaluate("cashStatusRevision=1;wrongPaymentReceipt=false;document.querySelector('[data-lifecycle=check-status]').click()");await settle();
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);assert.equal(await evaluate('changedSources'),1);
 for(const call of (await evaluate('calls')).filter(c=>c.path==='cash-payments/status'))assert.deepEqual(call.body,original.body);
 assert.equal((await evaluate('calls')).filter(c=>c.path==='cash-payments/record').length,1);
}));

test('fully returned custody keeps its exact payment and observation workbench without a new performance form', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const closed={...offer,action:'gas.park_custody.return',label:'Inspect completed custody and fee cash',eligible:false,reason:'gas_custody_complete',availableQuantity:'0'};
 const completedSetup={...setup,forms:{},configuration:{...setup.configuration,payment:paymentForm}};
 const closedBoot=paymentBoot.replace(JSON.stringify(discovery),JSON.stringify({...discovery,actions:[closed],choices:{...discovery.choices,actions:[{...discovery.choices.actions[0],code:closed.action}]}}))
  .replace(JSON.stringify({...setup,configuration:{...setup.configuration,payment:paymentForm}}),JSON.stringify(completedSetup));
 await evaluate(closedBoot);
 assert.deepEqual(await evaluate('errors'),[]);
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=panel] > form')"),null);
 assert.equal(await evaluate("document.querySelector('[data-lifecycle-execute]')"),null);
 assert.equal(await evaluate("[...document.querySelectorAll('[data-lifecycle=panel] button')].some(button=>button.textContent==='Review action')"),false);
 assert.equal(await evaluate("!!document.querySelector('[data-custody-observation]')&&!!document.querySelector('[data-custody-payment]')&&!document.querySelector('[data-custody-payment]').inert"),true);
 assert.match(await evaluate("document.querySelector('[data-lifecycle=panel]').textContent"),/fully returned.*independent fee cash.*observation history/i);
 await evaluate(fillPayment);await settle();
 const recorded=(await evaluate('calls')).find(call=>call.path==='cash-payments/record');assert.ok(recorded);assert.equal(recorded.body.bindingId,'accepted-cash-binding');assert.equal(recorded.body.tradeId,target.scopeId);
 assert.equal((await evaluate('calls')).some(call=>call.path==='lifecycle/preview'||call.path==='lifecycle/execute'),false);
}));

test('a custody action awaiting explicit configuration retains its input form rather than becoming a completed workbench', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const configuring={...offer,eligible:false,reason:'lifecycle_configuration_required'};
 await evaluate(boot.replace(JSON.stringify(discovery),JSON.stringify({...discovery,actions:[configuring]})));
 assert.deepEqual(await evaluate('errors'),[]);
 assert.equal(await evaluate("!!document.querySelector('[data-lifecycle=panel] > form [name=serviceVersion]')&&!!document.querySelector('[data-lifecycle=panel] > form [name=policyId]')"),true);
 assert.equal(await evaluate("[...document.querySelectorAll('[data-lifecycle=panel] > form button')].some(button=>button.textContent==='Review action')"),true);
 assert.equal((await evaluate('calls')).some(call=>call.path==='lifecycle/execute'),false);
}));


test('an outside-source custody acknowledgement explains the pinned view and keeps the selected source while invalidating live financial inputs', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const outside={...effects,acknowledgementOutsideSource:true,effectiveAt:'2026-11-03T15:00:00Z',performedQuantity:'0',acknowledgedQuantity:'720',custodyBefore:'0',custodyAfter:'0',reservedBefore:'0',reservedAfter:'0',feeAmount:'0',feeCashUnchanged:true,allocations:[]};
 const outsideBoot=boot.replace(JSON.stringify(effects),JSON.stringify(outside)).replace('details:{willChange:true,effects}','details:{willChange:false,effects}').replace("if(path==='lifecycle/execute')return{operationId:body.operationId,changed:true}","if(path==='lifecycle/execute')return{operationId:body.operationId,changed:false,semanticAcknowledgement:true}");
 await evaluate(outsideBoot);await evaluate(fillReview);await settle();
 const facts=await evaluate("Object.fromEntries([...document.querySelectorAll('[data-lifecycle-review] dt')].map(t=>[t.textContent,t.nextElementSibling.textContent]))");
 assert.match(facts['Selected view'],/acknowledged beyond this selected business or knowledge view; this view was not refreshed/);
 assert.equal(facts['Custody after'],'0');assert.equal(facts['New signed fee cash'],'0');assert.match(facts['Observed custody time'],/Nov 3, 2026.*9:00 AM/);assert.match(facts['As of'],/Nov 2, 2026.*9:00 AM/);
 await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await settle();
 assert.equal(await evaluate('changedSources'),1);
 const calls=await evaluate('calls');assert.deepEqual(calls.find(call=>call.path==='lifecycle/execute').body.context,context);
 assert.deepEqual(calls.filter(call=>call.path==='lifecycle/view').map(call=>call.body),[{snapshotId:'header'},{snapshotId:'header'}]);
 assert.equal(calls.some(call=>!['lifecycle/view','gas/park-custody/setup','lifecycle/preview','lifecycle/execute'].includes(call.path)),false);
}));
