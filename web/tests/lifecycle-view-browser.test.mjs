import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
import {batchFixture} from './gas-owned-fixture.mjs';

const context={contextId:'10000000-0000-0000-0000-000000000001',asOf:'2026-11-02T15:00:00Z',businessDate:'2026-11-02',snapshotId:'20000000-0000-0000-0000-000000000001'};
const offer={target:{handler:'listed_option',scopeId:'30000000-0000-0000-0000-000000000001',bindingId:'40000000-0000-0000-0000-000000000001'},action:'option.expiry',label:'Determine expiry',occurrenceId:'50000000-0000-0000-0000-000000000001',at:context.asOf,before:null,eligible:true,reason:null,revision:1,stateVersion:2,subject:{reference:'TR-000001',product:'Synthetic gas call',bookId:'60000000-0000-0000-0000-000000000001',book:'Synthetic book'},policyId:'70000000-0000-0000-0000-000000000001',policyRevision:3,selection:{evidenceId:'30000000-0000-0000-0000-000000000001',lots:null,expectedEventVersion:2,profileRevision:4},requiredInputs:[],inputChoices:{}};
const discovery={context,actions:[offer],policies:[{id:offer.policyId,revision:3,action:offer.action,mode:'review',from_at:'2026-01-01T00:00:00Z',until_at:'2027-01-01T00:00:00Z'}],optionProfiles:[],swapProfiles:[],marginPolicies:[],choices:{actions:[{code:'option.expiry',automatic:true,configurationPermission:'AmendTrade',executionPermission:'BookTrade'}],modes:['manual','review','automatic'],policyImplementation:'explicit_lifecycle_action_policy_v1',swapRounding:[],swapDue:[],swapRecognition:[],swapCorrection:[]}};
const settle=()=>new Promise(r=>setTimeout(r,40));
test('initial gas form metadata must echo the exact requested native target',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const gasOffer={...offer,target:{handler:'gas_owned',scopeId:'account',bindingId:'account'},action:'gas.owned.receive_title',selection:null};
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.errors=[];const api=async(path)=>path==='lifecycle/view'?${JSON.stringify({...discovery,actions:[gasOffer]})}:{context:${JSON.stringify(context)},target:{...${JSON.stringify(gasOffer.target)},scopeId:'another-account'},accounts:[],policies:[],forms:{'gas.owned.receive_title':{label:'Title',permission:'RecordGasOwnedHoldings',fields:[{name:'quantity',label:'Quantity',kind:'text',nullable:false}]}},configuration:{}};window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(error){errors.push(error.message);}},busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>true,changed(){}});await desk.load();})()`);
 await settle();await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=panel] form')===null"),true);assert.match((await evaluate('errors')).join(' '),/target|exact source/i);
}));
test('linked correction review displays each principal prefix, source allocation and member revision before execution',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="gas-form"></div><div id="host"></div>',async evaluate=>{
 const principal=id=>({positionId:'delivery-'+id,originalQuantity:'1',finalPrice:'6',currency:{code:'usd'},principalDueAt:'2026-12-02T12:00:00Z',performances:[{businessReference:'Synthetic performance '+id,at:context.asOf,quantity:'0.125',contribution:'0.75',adjustment:'0.75'}]});
 const allocations=id=>({policy:'preserve_unallocated_source_quantity_v1',sources:[{sourceKey:'synthetic_source_'+id,oldCapacity:'1',capacity:'0.875',oldAllocated:'0.25',allocated:'0.125',previouslyUnused:'0.75',unused:'0.75',quantity:{code:'synthetic_unit'}}]});
 const details={willChange:true,principal:[principal('a'),principal('b')],sourceAllocations:[allocations('a'),allocations('b')],corrections:['a','b'].map(id=>({performanceId:id,positionId:'delivery-'+id,acknowledgedRevision:4,currentRevision:4,currentQuantity:'0.125',changed:true}))};
 await evaluate(`(async()=>{${batchFixture}
 setup.forms[offer.action].fields[0].variants.forEach((variant,index)=>{variant.label='Synthetic shared reference · originating trade '+(index+101);variant.parentLabel='TR-000'+(index+101)+' · Synthetic monthly future';});
 document.querySelector('#gas-form').remove();window.errors=[];window.lifecycleSent=[];
 const {setupLifecycle}=await import('/lifecycle-view.js');const capturedOffer={...offer,subject:{reference:'Synthetic linked account',product:'Physical quantities',bookId:'book',book:'Synthetic book'}};
 const catalogue={...${JSON.stringify(discovery)},context:setup.context,actions:[capturedOffer],choices:{...${JSON.stringify(discovery.choices)},actions:[{code:offer.action,automatic:false,configurationPermission:'ConfigureGasOwnedHoldings',executionPermission:'RecordGasOwnedHoldings'}]}};
 const lifeApi=async(path,body)=>{if(path==='lifecycle/view')return catalogue;if(path==='lifecycle/gas/setup')return body.target.handler==='gas_futures_reconciliation_batch'?{...setup,target:body.target}:batchRead(body);if(path==='lifecycle/preview')return{hash:'a'.repeat(64),offer:capturedOffer,details:${JSON.stringify(details)}};if(path==='lifecycle/execute'){lifecycleSent.push(body);return{operationId:body.operationId,changed:true};}throw Error('Unexpected '+path);};
 window.desk=setupLifecycle(document.querySelector('#host'),{api:lifeApi,act:async f=>{try{await f();}catch(e){errors.push(e.message);}},busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=>gasZone,permission:()=>true,changed(){}});await desk.load();})()`);
 await settle();await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();
 await evaluate("addMember('a')");await settle();await evaluate("addMember('b')");await settle();
 await evaluate("fillMember('a');fillMember('b');document.querySelector('[name=policyId]').value='policy-1:2';document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await settle();
 assert.deepEqual(await evaluate('errors'),[]);const text=await evaluate("document.querySelector('[data-lifecycle-review]').textContent");
 for(const value of ['Synthetic performance a','Synthetic performance b','synthetic_source_a','synthetic_source_b','Accepted revision','Current revision','0.875','0.125'])assert.ok(text.includes(value),value);
 assert.equal(await evaluate("[...document.querySelectorAll('[data-lifecycle-review] h4')].filter(h=>h.textContent==='Physical delivery principal').length"),2);
 const parents=await evaluate("[...document.querySelectorAll('[data-lifecycle-review] [data-physical-parent]')].map(p=>p.textContent)");
 assert.deepEqual(parents,['TR-000101 · Synthetic monthly future','TR-000102 · Synthetic monthly future']);
 assert.doesNotMatch(text,/No accepted performance/);assert.deepEqual(await evaluate('lifecycleSent'),[]);
 await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await settle();assert.equal(await evaluate('lifecycleSent.length'),1);assert.deepEqual((await evaluate('lifecycleSent[0].selection.corrections')).map(c=>c.performanceId),['a','b']);
}));
test('physical quantity correction chooses native versions and reviews source capacity and principal before sending',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const action='gas.futures.reconcile_quantity',policy='preserve_unallocated_source_quantity_v1';
 const corrected={...offer,target:{...offer.target,handler:'gas_futures_reconciliation'},action,label:'Correct observed physical quantity',selection:null};
 const reference=(name,members,value)=>({name,label:name,kind:'reference',nullable:false,members,choices:[{id:'current',label:'Selected native revision',value}]});
 const fields=[reference('observedPerformance',['positionId','performanceId','expectedRevision'],{positionId:corrected.target.scopeId,performanceId:corrected.target.bindingId,expectedRevision:3}),
  reference('principalVersion',['expectedPrincipalVersion'],{expectedPrincipalVersion:5}),{name:'quantity',label:'Corrected quantity',kind:'text',nullable:false},
  {name:'retracted',label:'Withdraw quantity',kind:'select',nullable:false,choices:[{id:'keep',label:'Keep active quantity',value:false},{id:'withdraw',label:'Withdraw quantity',value:true}]},
  {name:'titleEvidenceId',label:'Title evidence',kind:'select',nullable:true,choices:[{id:'title',label:'Synthetic corrected title',value:offer.occurrenceId}]},
  {name:'supply',label:'Identified supply',kind:'array',nullable:false,fields:[{name:'quantity',label:'Quantity',kind:'text',nullable:false}]},
  {name:'sourceAllocation',label:'Unused source quantity',kind:'select',nullable:false,choices:[{id:policy,label:'Preserve unused source quantity',value:policy}]},
  {name:'evidence',label:'Correction evidence',kind:'text',nullable:false}];
 const setup={context,accounts:[],policies:[{id:offer.policyId,label:'Synthetic correction policy',revision:3,action,accountId:corrected.target.scopeId}],
  forms:{[action]:{label:corrected.label,permission:'RecordGasOwnedHoldings',fields}},configuration:{}};
 const details={willChange:true,principal:{originalQuantity:'1',finalPrice:'6.03',currency:{code:'usd'},principalDueAt:'2026-12-02T12:00:00Z',
  performances:[{businessReference:'Synthetic corrected title',at:context.asOf,quantity:'0.125',contribution:'-0.75',adjustment:'0.75'}]},
  sourceAllocations:{policy,sources:[{sourceKey:'synthetic_title_capacity',oldCapacity:'1',capacity:'0.875',oldAllocated:'0.25',allocated:'0.125',previouslyUnused:'0.75',unused:'0.75',quantity:{code:'synthetic_unit'}}]}};
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.sent=[];window.errors=[];window.correctionReview=${JSON.stringify(details)};const api=async(path,body)=>{
  if(path==='lifecycle/view')return ${JSON.stringify({...discovery,actions:[corrected],choices:{...discovery.choices,actions:[{code:action,automatic:false,configurationPermission:'ConfigureGasOwnedHoldings',executionPermission:'RecordGasOwnedHoldings'}]}})};if(path==='lifecycle/gas/setup')return {...${JSON.stringify(setup)},target:body.target};
  if(path==='lifecycle/preview')return {hash:'a'.repeat(64),offer:${JSON.stringify(corrected)},details:correctionReview};
  if(path==='lifecycle/execute'){sent.push(body);return {operationId:body.operationId,changed:true};}throw Error('Unexpected operation '+path);};
  window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(e){errors.push(e.message);}},busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>true,changed(){}});await desk.load();})()`);
 await settle();await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();
 assert.equal(await evaluate("!!document.querySelector('[name=observedPerformance]')"),true);
 const fill=`(()=>{for(const [name,value]of Object.entries({policyId:'${offer.policyId}:3',observedPerformance:'current',principalVersion:'current',quantity:'0.125',retracted:'keep',titleEvidenceId:'title',supply:'empty',sourceAllocation:'${policy}',evidence:'Synthetic reviewed correction'})){
  const field=document.querySelector('[name="'+name+'"]');field.value=value;field.dispatchEvent(new Event('input',{bubbles:true}));field.dispatchEvent(new Event('change',{bubbles:true}));}document.querySelector('[data-lifecycle=panel] form').requestSubmit();})()`;
 await evaluate(fill);
 await settle();assert.deepEqual(await evaluate('errors'),[]);
 const text=await evaluate("document.querySelector('[data-lifecycle-review]').textContent");
 for(const expected of ['Observed source allocations','Previous capacity','Previously unused','synthetic_title_capacity','0.875','0.75','synthetic_unit','Physical delivery principal','Synthetic corrected title'])assert.ok(text.includes(expected),expected);
 assert.equal(await evaluate("document.querySelectorAll('[data-lifecycle=panel] input[type=datetime-local]').length"),0);
 await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await settle();assert.deepEqual(await evaluate('errors'),[]);
 const sent=await evaluate('sent');assert.equal(sent.length,1);assert.deepEqual(sent[0].context,context);assert.deepEqual(sent[0].target,corrected.target);
 assert.deepEqual(sent[0].selection,{positionId:corrected.target.scopeId,performanceId:corrected.target.bindingId,expectedRevision:3,expectedPrincipalVersion:5,
  quantity:'0.125',retracted:false,titleEvidenceId:offer.occurrenceId,supply:[],sourceAllocation:policy,evidence:'Synthetic reviewed correction'});
 await evaluate("(async()=>{correctionReview={willChange:false,acknowledgedRevision:1,currentRevision:3};await desk.load();})()");await settle();
 await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();await evaluate(fill);await settle();
 const acknowledged=await evaluate("Object.fromEntries([...document.querySelectorAll('[data-lifecycle-review] dt')].map(term=>[term.textContent,term.nextElementSibling.textContent]))");
 assert.equal(acknowledged['Acknowledged financial revision'],'1');assert.equal(acknowledged['Current financial revision'],'3');assert.equal(acknowledged['Acknowledged quantity'],'0.125');
 assert.equal(await evaluate('sent.length'),1);
}));
test('an older fact acknowledgement names the accepted and current revisions without claiming activation',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');const api=async(path,body)=>path==='lifecycle/view'?${JSON.stringify(discovery)}:path==='lifecycle/preview'?{hash:'a'.repeat(64),offer:${JSON.stringify(offer)},details:{}}:{operationId:body.operationId,changed:false,semanticAcknowledgement:true,acknowledgedRevision:1,currentRevision:2};window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>f(),busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>true,changed(){}});await desk.load();})()`);await settle();
 await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();await evaluate("document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await settle();await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await settle();
 assert.match(await evaluate("document.querySelector('[data-lifecycle=message]').textContent"),/Existing fact acknowledged.*revision 1.*current revision 2/i);
}));
test('a preview for a different native occurrence cannot become an executable review',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.errors=[];const api=async(path)=>path==='lifecycle/view'?${JSON.stringify(discovery)}:{hash:'a'.repeat(64),offer:{...${JSON.stringify(offer)},occurrenceId:'different-occurrence'},details:{amount:'100'}};window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(e){errors.push(String(e));}},busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>true,changed(){}});await desk.load();})()`);await settle();
 await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();await evaluate("document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await settle();
 assert.equal(await evaluate("!!document.querySelector('[data-lifecycle-execute]')"),false);assert.equal(await evaluate('errors.length'),1);
}));
test('read-only lifecycle inspection never offers an enabled financial action or settings save',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.desk=setupLifecycle(document.querySelector('#host'),{api:async()=>(${JSON.stringify(discovery)}),act:async f=>f(),busy:()=>false,identity:()=> 'reader',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>false,changed(){}});await desk.load();})()`);await settle();
 await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();
 assert.equal(await evaluate("Array.from(document.querySelectorAll('[data-lifecycle=panel] button[type=submit]')).some(b=>!b.disabled)"),false);
 await evaluate("document.querySelector('[data-lifecycle=products]').click()");await settle();
 await evaluate("(()=>{const select=document.querySelector('[data-lifecycle=panel] select');select.value='action:0';select.dispatchEvent(new Event('change'));desk.sync();})()");await settle();
 assert.equal(await evaluate("Array.from(document.querySelectorAll('[data-lifecycle=panel] button[type=submit]')).some(b=>!b.disabled)"),false);
}));
test('lifecycle retries a lost response with the exact reviewed source after remount, while another actor sees no retained request',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.actor='operator-a';window.sent=[];window.errors=[];window.fail=true;window.header='first-header';window.viewCalls=[];
 const api=async(path,body)=>{if(path==='lifecycle/view'){viewCalls.push(structuredClone(body));return ${JSON.stringify(discovery)};}if(path==='lifecycle/preview')return {hash:'a'.repeat(64),offer:${JSON.stringify(offer)},details:{decision:'exercised',openLots:'2',lots:'2',futureLots:'2'}};if(path==='lifecycle/execute'){sent.push(structuredClone(body));if(fail)throw new TypeError('Lost response');return {operationId:body.operationId};}throw Error('Unexpected '+path);};
 window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(e){errors.push(String(e));}},busy:()=>false,identity:()=>actor,context:()=>({snapshotId:header,convention:'trading_workspace_v1',knowledgeMode:'current'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>true,changed(){}});await desk.load();})()`);
 await settle();await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();
 await evaluate("document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await settle();
 assert.equal(await evaluate("document.querySelector('[data-lifecycle-review]').textContent.includes('9:00 AM')"),true);
 await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await settle();
 assert.equal(await evaluate("!document.querySelector('[data-lifecycle=pending]').hidden&&sent.length===1"),true);
 const original=await evaluate('sent[0]');assert.deepEqual(original.context,context);assert.equal(original.previewHash,'a'.repeat(64));
 await evaluate("header='changed-header';desk.reset();desk.sync();actor='operator-b';desk.reset();desk.sync()");
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);
 await evaluate("actor='operator-a';desk.reset();desk.sync();fail=false;document.querySelector('[data-lifecycle=retry]').click()");await settle();
 assert.deepEqual(await evaluate('sent'),[original,original]);assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);
 assert.equal(await evaluate("document.querySelector('[data-lifecycle=refresh]').disabled"),false);
}));

test('a late lifecycle read cannot expose another actor or retain a prior action after denied refresh',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');let actor='operator-a',release;const api=()=>new Promise(resolve=>release=resolve);const desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>f(),busy:()=>false,identity:()=>actor,context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'UTC',permission:()=>true,changed(){}});const reading=desk.load();actor='operator-b';desk.reset();release(${JSON.stringify(discovery)});await reading;return {rows:document.querySelector('[data-lifecycle=rows]').textContent,panel:document.querySelector('[data-lifecycle=panel]').textContent};})()`);
 assert.deepEqual(result,{rows:'',panel:''});
}));

test('an account switch while resolving a policy time cannot send the old actor’s draft under the new account',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.actor='operator-a';window.sent=[];window.releases=[];window.errors=[];
 const api=async(path,body)=>{if(path==='lifecycle/view')return ${JSON.stringify(discovery)};if(path==='time/inspect')return new Promise(resolve=>releases.push(()=>resolve({candidates:[{instant:'2026-11-02T15:00:00Z',offset:'-06:00'}]})));sent.push(path);return {};};
 window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(e){errors.push(String(e));}},busy:()=>false,identity:()=>actor,context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>true,changed(){}});await desk.load();document.querySelector('[data-lifecycle=settings]').click();})()`);await settle();
 await evaluate("(()=>{const form=document.querySelector('[data-lifecycle=panel] form'),selects=form.querySelectorAll('select');selects[0].value='option.expiry';selects[0].dispatchEvent(new Event('change'));selects[2].value='review';for(const input of form.querySelectorAll('input[type=datetime-local]'))input.value='2026-11-02T09:00:00';form.requestSubmit();})()");await settle();
 assert.equal(await evaluate('releases.length'),1);
 await evaluate("actor='operator-b';desk.reset();desk.sync();releases.shift()()");await settle();
 assert.equal(await evaluate('releases.length'),1);await evaluate('releases.shift()()');await settle();
 assert.deepEqual(await evaluate('sent'),[]);assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);
}));

test('editing a policy while its times resolve cannot submit a mixed draft',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.releases=[];window.sent=[];window.errors=[];
 const api=async(path,body)=>{if(path==='lifecycle/view')return ${JSON.stringify(discovery)};if(path==='time/inspect')return new Promise(resolve=>releases.push(()=>resolve({candidates:[{instant:'2026-11-02T15:00:00Z',offset:'-06:00'}]})));sent.push(path);return {};};
 window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(e){errors.push(String(e));}},busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>true,changed(){}});await desk.load();document.querySelector('[data-lifecycle=settings]').click();})()`);await settle();
 await evaluate("(()=>{const form=document.querySelector('[data-lifecycle=panel] form'),selects=form.querySelectorAll('select');selects[0].value='option.expiry';selects[0].dispatchEvent(new Event('change'));selects[2].value='review';for(const input of form.querySelectorAll('input[type=datetime-local]'))input.value='2026-11-02T09:00:00';form.requestSubmit();})()");await settle();
 await evaluate("(()=>{const mode=document.querySelector('[data-lifecycle=panel] form').querySelectorAll('select')[2];mode.value='manual';mode.dispatchEvent(new Event('change',{bubbles:true}));releases.shift()();})()");await settle();
 await evaluate('releases.shift()()');await settle();assert.deepEqual(await evaluate('sent'),[]);
 assert.equal(await evaluate('desk.edited()'),true);
}));

test('a physical final review shows every signed principal correction before recording',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');const api=async(path)=>path==='lifecycle/view'?${JSON.stringify(discovery)}:{hash:'a'.repeat(64),offer:${JSON.stringify(offer)},details:{physicalPrincipal:{originalQuantity:'200',finalPrice:'6',currency:{code:'usd'},principalDueAt:'2026-12-02T12:00:00Z',performances:[{id:'synthetic-performance',businessReference:'Synthetic title receipt',at:'2026-11-02T15:00:00Z',quantity:'75',contribution:'-450',adjustment:'-75'}]}}};window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>f(),busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>true,changed(){}});await desk.load();})()`);await settle();
 await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();await evaluate("document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await settle();
 const text=await evaluate("document.querySelector('[data-lifecycle-review]').textContent");assert.match(text,/Physical delivery principal/);assert.match(text,/Synthetic title receipt/);assert.match(text,/6 USD/);assert.match(text,/-450/);assert.match(text,/-75/);
}));

test('a margin review displays the backend exact and rounded cash amounts with currency',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');const api=async(path)=>path==='lifecycle/view'?${JSON.stringify(discovery)}:{hash:'a'.repeat(64),offer:${JSON.stringify(offer)},details:{marginRows:[{at:'2026-11-02T15:00:00Z',exactVariation:'40000',variation:'40000',adjustment:'-0.01',dueAt:'2026-11-03T15:00:00Z',currency:'usd'}]}};window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>f(),busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>true,changed(){}});await desk.load();})()`);await settle();
 await evaluate("document.querySelector('.tabulator-row').click()");await settle();await evaluate("document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await settle();
 const text=await evaluate("document.querySelector('[data-lifecycle-review]').textContent");assert.match(text,/40000/);assert.match(text,/-0\.01/);assert.match(text,/USD/);assert.doesNotMatch(text,/40000\/1/);
}));

test('only a matched same-account financial receipt invalidates other views, and a refresh failure keeps that receipt confirmed',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.effects=0;window.errors=[];window.executions=0;const api=async(path,body)=>{if(path==='lifecycle/view')return ${JSON.stringify(discovery)};if(path==='lifecycle/preview')return {hash:'a'.repeat(64),offer:${JSON.stringify(offer)},details:{}};if(path==='lifecycle/execute'){executions++;return {operationId:body.operationId};}throw Error(path);};window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(e){errors.push(String(e));}},busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'UTC',permission:()=>true,changed(){},committed:async()=>{effects++;throw Error('Synthetic current-view refresh failure');}});await desk.load();})()`);await settle();
 await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();await evaluate("document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await settle();assert.equal(await evaluate('effects'),0);
 await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await settle();assert.equal(await evaluate('effects'),1);assert.equal(await evaluate('executions'),1);assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);assert.equal(await evaluate("document.querySelector('[data-lifecycle=retry]').disabled"),true);assert.match(await evaluate("document.querySelector('[data-lifecycle=message]').textContent"),/recorded.*updated views.*could not/i);
}));

test('a confirmed reply arriving after an account switch cannot invalidate the new account views',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.actor='operator-a';window.effects=0;window.errors=[];const api=async(path,body)=>path==='lifecycle/view'?${JSON.stringify(discovery)}:path==='lifecycle/preview'?{hash:'a'.repeat(64),offer:${JSON.stringify(offer)},details:{}}:new Promise(resolve=>window.release=()=>resolve({operationId:body.operationId}));window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(e){errors.push(String(e));}},busy:()=>false,identity:()=>actor,context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'UTC',permission:()=>true,changed(){},committed(){effects++;}});await desk.load();})()`);await settle();
 await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();await evaluate("document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await settle();await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await settle();await evaluate("actor='operator-b';desk.reset();desk.sync();release()");await settle();assert.equal(await evaluate('effects'),0);assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);
}));

test('an async committed callback cannot refresh or label another account after it resumes',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.actor='operator-a';window.views=[];window.errors=[];const api=async(path,body)=>{if(path==='lifecycle/view'){views.push(actor);return ${JSON.stringify(discovery)};}if(path==='lifecycle/preview')return {hash:'a'.repeat(64),offer:${JSON.stringify(offer)},details:{}};return {operationId:body.operationId};};window.desk=setupLifecycle(document.querySelector('#host'),{api,act:async f=>{try{await f();}catch(e){errors.push(String(e));}},busy:()=>false,identity:()=>actor,context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'UTC',permission:()=>true,changed(){},committed:()=>new Promise(resolve=>window.releaseCommitted=resolve)});await desk.load();})()`);await settle();
 await evaluate("document.querySelector('[data-lifecycle=rows] .tabulator-row').click()");await settle();await evaluate("document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await settle();await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await settle();await evaluate("actor='operator-b';desk.reset();desk.sync();releaseCommitted()");await settle();assert.deepEqual(await evaluate('views'),['operator-a']);assert.equal(await evaluate("document.querySelector('[data-lifecycle=message]').textContent"),'');assert.equal(await evaluate("document.querySelector('[data-lifecycle=pending]').hidden"),true);
}));

test('Lifecycle keeps wide native action columns inside its own table at desktop and phone widths',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<link rel="stylesheet" href="/app.css"><main><section id="host"></section></main>',async(evaluate,command)=>{
 await command('Emulation.setDeviceMetricsOverride',{width:1440,height:1050,deviceScaleFactor:1,mobile:false});
 const full={...discovery,actions:[{...offer,eligible:false,reason:'lifecycle_policy_required',label:'Review scheduled margin',subject:{...offer.subject,product:'Synthetic natural gas physically delivered monthly future'}}]};
 await evaluate(`(async()=>{const {setupLifecycle}=await import('/lifecycle-view.js');window.desk=setupLifecycle(document.querySelector('#host'),{api:async()=>(${JSON.stringify(full)}),act:async f=>f(),busy:()=>false,identity:()=> 'operator',context:()=>({snapshotId:'header'}),historical:()=>false,zone:()=> 'America/Chicago',permission:()=>true,changed(){}});await desk.load();})()`);await settle();
 assert.equal(await evaluate('document.documentElement.scrollWidth<=document.documentElement.clientWidth+1'),true);
 await command('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:true});await settle();
 assert.equal(await evaluate('document.documentElement.scrollWidth<=document.documentElement.clientWidth+1'),true);
}));
