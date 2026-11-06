import {gasDeliveryFixture} from './physical-delivery-fixture.mjs';
import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
const account={id:'account',trade_id:'trade',origin_obligation_id:'obligation',version_id:'version',holder_role:'legal_entity',external_reference:'Synthetic service',active:true,revision:1,valid_from:'2021-01-01',valid_to:'2021-02-01',statement_policy:'final_statement_balance_v1'};
const daily={expectedRevision:1,canWrite:true,rows:[{obligationId:'obligation',sideCode:'receipt',pipeline:'synthetic_pipe',zone:'synthetic_zone',deliveryPoint:'synthetic_point',kind:'contractual',revisionId:null,day:'2021-01-01',start:'2021-01-01T06:00:00Z',end:'2021-01-02T06:00:00Z',quantitySpecification:{code:'gas_unit'},contractual:{numerator:'100',denominator:'1'},selected:{numerator:'100',denominator:'1'},heads:[]}]};
daily.delivery=gasDeliveryFixture(daily);
const view={permissions:{canConfigure:true,canRecord:true},nominationProfile:'manual_confirmed_ceiling_v1',imbalanceProfile:'paired_offsetting_imbalance_v1',roundingPolicy:'currency_minor_units_to_even_v1',statementPolicies:['final_statement_balance_v1'],balances:[{accountId:'account',amount:null,refusal:'Missing statement'}],state:{accounts:[account],nominations:[],statements:[],agreements:[],responses:[],movements:[],nominationApplications:[],imbalanceApplications:[],heads:[]}};

test('the manual desk groups four stages, selects sole references and pins every read to its supplied context',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{const {setupGasOperations}=await import('/gas-operations.js');const calls=[];let busy=false,pending,mode='current';
 const api=async(path,body)=>{calls.push({path,body});return path==='gas/quantities/read'?${JSON.stringify(daily)}:${JSON.stringify(view)}};
 const widget=setupGasOperations(document.querySelector('#host'),{api,context:()=>({snapshotId:'header',knowledgeMode:mode}),tradeId:'trade',actor:()=> 'actor',zone:()=> 'UTC',workflow:{busy:()=>busy,pending:()=>pending,queue:async()=>{},changed(){}}});
 await new Promise(r=>setTimeout(r,20));const root=document.querySelector('.gas-operations');root.querySelector('[data-operation="nomination"]').click();
 const selected=root.querySelector('[name=accountId]').value;busy=true;widget.sync();const blocked=root.querySelector('[name=quantity]').disabled;busy=false;pending={operationId:'held'};widget.sync();
 const retained=root.querySelector('[name=quantity]').disabled;pending=undefined;mode='retained';widget.sync();const historical=[...root.querySelectorAll('[data-operation]')].every(b=>b.disabled);
 return {stages:[...root.querySelectorAll('[data-stage-title]')].map(n=>n.textContent),selected,blocked,retained,historical,edited:widget.unsaved(),contexts:calls.every(c=>c.body.context.snapshotId==='header'),tbd:root.textContent.includes('Live pipeline connection · TBD')};})()`);
 assert.deepEqual(result,{stages:['Before flow','Scheduling','After flow','Reconciliation'],selected:'account',blocked:true,retained:true,historical:true,edited:false,contexts:true,tbd:true});
}));

test('changing evidence invalidates a reviewed operation and an accepted command uses global transport',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{const {setupGasOperations}=await import('/gas-operations.js');let queued;const tick=()=>new Promise(r=>setTimeout(r,20));
 const api=async(path)=>path==='time/inspect'?{candidates:[{instant:'2021-01-03T00:00:00Z'}]}:path==='time/resolve'?{instant:'2021-01-03T00:00:00Z'}:path==='gas/quantities/read'?${JSON.stringify(daily)}:path==='gas/operations/preview'?{previewHash:'review',review:{action:'Record submitted request'}}:${JSON.stringify(view)};
 const widget=setupGasOperations(document.querySelector('#host'),{api,context:()=>({snapshotId:'header',knowledgeMode:'current'}),tradeId:'trade',actor:()=> 'actor',zone:()=> 'UTC',workflow:{busy:()=>false,pending:()=>undefined,queue:async c=>{queued=c},changed(){}}});await tick();const root=document.querySelector('.gas-operations');root.querySelector('[data-operation="nomination"]').click();
 const set=(name,value)=>{const input=root.querySelector('[name='+name+']');input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}))};
 for(const [k,v] of [['externalReference','Synthetic request'],['quantity','100'],['submittedAt','2021-01-03T00:00:00'],['channel','portal'],['reason','Synthetic retro request'],['evidence','Synthetic evidence']])set(k,v);
 // The server remains the time resolver; this stub supplies its single explicit candidate.
 root.querySelector('form').dispatchEvent(new Event('submit',{cancelable:true}));await tick();const visible=!root.querySelector('[data-ops=confirm]').hidden;
 set('evidence','Synthetic revised evidence');const invalidated=root.querySelector('[data-ops=confirm]').hidden;
 root.querySelector('form').dispatchEvent(new Event('submit',{cancelable:true}));await tick();root.querySelector('[data-ops=confirm]').click();await tick();return {visible,invalidated,edited:widget.unsaved(),queued:queued?.path};})()`);
 assert.equal(result.invalidated,true);assert.equal(result.visible,true);assert.equal(result.queued,'gas/operations/record');
}));

test('losing private operations authority clears private history while native allocation remains available',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{const {setupGasOperations}=await import('/gas-operations.js');let denied=false;const tick=()=>new Promise(r=>setTimeout(r,20));
 const api=async path=>{if(path==='gas/quantities/read')return ${JSON.stringify(daily)};if(denied)throw Error('access_denied');return ${JSON.stringify(view)}};
 const widget=setupGasOperations(document.querySelector('#host'),{api,context:()=>({knowledgeMode:'current'}),tradeId:'trade',actor:()=> 'operator',zone:()=> 'UTC',workflow:{busy:()=>false,pending:()=>undefined,queue:async()=>{},changed(){}}});await tick();const root=document.querySelector('.gas-operations');const before=root.querySelector('[data-ops=history]').textContent.includes('Synthetic service');denied=true;root.querySelector('[data-ops=refresh]').click();await tick();const cleared=root.querySelector('[data-ops=history]').textContent==='';root.querySelector('[data-stage=\"2\"]').click();root.querySelector('[data-operation=daily_quantity]').click();return {before,cleared,allocation:!!root.querySelector('[name=quantity]')&&!root.querySelector('[name=quantity]').disabled,privateInputs:!!root.querySelector('[name=accountId]'),edited:widget.unsaved()};})()`);
 assert.deepEqual(result,{before:true,cleared:true,allocation:true,privateInputs:false,edited:false});
}));

test('stage navigation reveals only its actions and refuses to hide an unfinished operation',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async(evaluate,command)=>{
 await evaluate(`(async()=>{const {setupGasOperations}=await import('/gas-operations.js');const api=async path=>path==='gas/quantities/read'?${JSON.stringify(daily)}:${JSON.stringify(view)};
 window.widget=setupGasOperations(document.querySelector('#host'),{api,context:()=>({snapshotId:'header',knowledgeMode:'current'}),tradeId:'trade',actor:()=> 'operator',zone:()=> 'UTC',workflow:{busy:()=>false,pending:()=>undefined,queue:async()=>{},changed(){}}});await new Promise(r=>setTimeout(r,20));})()`);
 const stage=await evaluate(`(()=>{const root=document.querySelector('.gas-operations');return {navigation:!!root.querySelector('[data-ops=stages][role=tablist]'),visible:[...root.querySelectorAll('.gas-stage')].filter(n=>!n.hidden).length,editor:root.querySelector('[data-ops=editor]').hidden};})()`);
 assert.deepEqual(stage,{navigation:true,visible:1,editor:true});
 await evaluate("document.querySelector('[data-stage=\"1\"]').click();document.querySelector('[data-operation=response]').click();document.querySelector('[name=externalReference]').focus()");
 await command('Input.insertText',{text:'Synthetic acknowledgment in progress'});
 await evaluate("document.querySelector('[data-stage=\"2\"]').click()");
 assert.equal(await evaluate("document.querySelector('[data-stage=\"1\"]').getAttribute('aria-selected')==='true'&&document.querySelector('[name=externalReference]').value==='Synthetic acknowledgment in progress'&&!document.querySelector('[data-ops=editor]').hidden"),true);
 await evaluate("document.querySelector('[data-ops=refresh]').click()");
 await evaluate("(async()=>{for(let i=0;i<50&&document.querySelector('[data-stage=\"2\"]').disabled;i++)await new Promise(r=>setTimeout(r,10));document.querySelector('[data-stage=\"2\"]').click();})()");
 assert.deepEqual(await evaluate("[...document.querySelectorAll('.gas-stage:not([hidden]) [data-operation]')].map(n=>n.dataset.operation)"),['daily_quantity','statement']);
 assert.equal(await evaluate("document.querySelector('[data-ops=editor]').hidden"),true);
}));
