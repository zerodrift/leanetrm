import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

const context={contextId:'10000000-0000-0000-0000-000000000001',asOf:'2026-11-02T15:00:00Z',businessDate:'2026-11-02',snapshotId:'20000000-0000-0000-0000-000000000001'};
const offer={target:{handler:'listed_option',scopeId:'30000000-0000-0000-0000-000000000001',bindingId:'40000000-0000-0000-0000-000000000001'},action:'option.expiry',label:'Determine expiry',occurrenceId:'50000000-0000-0000-0000-000000000001',at:context.asOf,before:null,eligible:true,reason:null,revision:1,stateVersion:2,subject:{reference:'TR-000001',product:'Synthetic gas call',bookId:'60000000-0000-0000-0000-000000000001',book:'Synthetic book'},policyId:'70000000-0000-0000-0000-000000000001',policyRevision:3,selection:{evidenceId:'30000000-0000-0000-0000-000000000001',lots:null,expectedEventVersion:2,profileRevision:4},requiredInputs:[],inputChoices:{}};
const discovery={context,actions:[offer],policies:[{id:offer.policyId,revision:3,action:offer.action,mode:'review',from_at:'2026-01-01T00:00:00Z',until_at:'2027-01-01T00:00:00Z'}],optionProfiles:[],swapProfiles:[],marginPolicies:[],choices:{actions:[{code:'option.expiry',automatic:true,configurationPermission:'AmendTrade',executionPermission:'BookTrade'}],modes:['manual','review','automatic'],policyImplementation:'explicit_lifecycle_action_policy_v1',swapRounding:[],swapDue:[],swapRecognition:[],swapCorrection:[]}};
const settle=()=>new Promise(r=>setTimeout(r,40));
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
