import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';
import {fixture} from './gas-owned-fixture.mjs';

const page='<div id="gas-form"></div>';
const ordinary=fixture.replace("const api=async(path,body)=>{","const api=async(path,body)=>{if(path==='lifecycle/gas/setup')return ordinaryRead(body);")+`
offer.action='gas.owned.reconcile_quantity';offer.label='Correct observed gas title or use';offer.target={handler:'gas_owned_quantity_reconciliation',scopeId:'account-1',bindingId:'account-1'};
setup.policies[0].action=offer.action;
window.memberFields=target=>[
 {name:'observedQuantity',label:'Observed title revision',kind:'reference',nullable:false,members:['positionId','expectedRevision'],choices:[{id:target.bindingId,label:'Synthetic retired title · financial revision 3',value:{positionId:target.bindingId,expectedRevision:3}}]},
 {name:'quantity',label:'Explicit corrected quantity',kind:'text',nullable:false},
 {name:'retracted',label:'Quantity disposition',kind:'select',nullable:false,choices:[{id:'false',label:'Keep an active quantity',value:false},{id:'true',label:'Withdraw the recorded quantity',value:true}]},
 {name:'evidenceId',label:'Exact source revision',kind:'select',nullable:false,choices:[{id:'evidence-zero',label:'Synthetic title · source revision 2 · document quantity 0'}]},
 {name:'sourceAllocation',label:'Source allocation contract',kind:'select',nullable:false,choices:[{id:'withdraw',label:'Withdraw all source allocations',value:'withdraw_all_source_allocations_v1'}]}];
window.ordinaryRead=async body=>({...setup,target:body.target,forms:{[offer.action]:{label:offer.label,permission:'RecordGasOwnedHoldings',fields:memberFields(body.target)}}});
setup.forms={[offer.action]:{label:offer.label,permission:'RecordGasOwnedHoldings',fields:[
 {name:'positions',label:'Owned title corrections',memberLabel:'Owned title',kind:'array',nullable:false,variants:[{id:'title-1',label:'Synthetic retired title',target:{handler:'gas_owned_position_quantity_source',scopeId:'account-1',bindingId:'title-1'}}]},
 {name:'consumptions',label:'Recorded delivery and loss corrections',memberLabel:'Recorded use',kind:'array',nullable:false,variants:[]},
 {name:'evidence',label:'Correction evidence reference',kind:'text',nullable:false}]}};
document.querySelector('#gas-form').replaceChildren();window.editor=mountGasOwnedAction(document.querySelector('#gas-form'),offer,setup,options);
window.selectField=(host,name,value)=>{const n=host.querySelector('[name="'+name+'"]');n.value=value;n.dispatchEvent(new Event('change',{bubbles:true}));};
window.addTitle=()=>{selectField(document,'positions','entries');document.querySelector('[name=positions]').parentElement.querySelector(':scope > button').click();selectField(document,'positions-member','title-1');};
window.fillTitle=()=>{for(const[name,value]of Object.entries({policyId:'policy-1:2',observedQuantity:'title-1',quantity:'0',retracted:'true',evidenceId:'evidence-zero',sourceAllocation:'withdraw',consumptions:'empty',evidence:'Synthetic source withdrawal'}))selectField(document,name,value);};
`;

test('ordinary correction chooses exact retired title revisions without filling quantities or allocation choices',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${ordinary}addTitle();})()`);await new Promise(r=>setTimeout(r,50));
 assert.match(await evaluate("document.querySelector('[data-gas-entry=positions]').textContent"),/Owned title/);
 assert.equal(await evaluate("document.querySelector('[name=quantity]').value"),'');
 assert.equal(await evaluate("document.querySelector('[name=retracted]').value"),'');
 assert.equal(await evaluate("document.querySelector('[name=sourceAllocation]').value"),'');
 await evaluate('fillTitle()');
 assert.deepEqual((await evaluate('editor.read()')).selection,{positions:[{positionId:'title-1',expectedRevision:3,quantity:'0',retracted:true,evidenceId:'evidence-zero',sourceAllocation:'withdraw_all_source_allocations_v1'}],consumptions:[],evidence:'Synthetic source withdrawal'});
 assert.equal(await evaluate("document.querySelector('textarea')===null"),true);
 assert.deepEqual(await evaluate('sent'),[]);
}));

test('ordinary Lifecycle review shows acknowledged and current title quantities before the retained execution',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${ordinary}
 const {setupLifecycle}=await import('/lifecycle-view.js');window.errors=[];
 const discovery={context:setup.context,actions:[offer],policies:[],optionProfiles:[],swapProfiles:[],marginPolicies:[],choices:{actions:[{code:offer.action,automatic:false,configurationPermission:'ConfigureGasOwnedHoldings',executionPermission:'RecordGasOwnedHoldings'}],modes:['manual','review'],policyImplementation:'explicit_lifecycle_action_policy_v1',swapRounding:[],swapDue:[],swapRecognition:[],swapCorrection:[]}};
 const actual=async(path,body)=>{if(path==='lifecycle/view')return discovery;if(path==='lifecycle/gas/setup')return body.target.handler==='gas_owned_quantity_reconciliation'?{...setup,target:body.target}:ordinaryRead(body);
  if(path==='lifecycle/preview'){sent.push({path,body});return{hash:'a'.repeat(64),offer,details:{willChange:false,outcome:{positions:[{positionId:'title-1',acknowledgedRevision:1,currentRevision:3,currentQuantity:'0',changed:false}],consumptions:[]}}};}
  if(path==='lifecycle/execute'){sent.push({path,body});return{operationId:body.operationId,changed:false,semanticAcknowledgement:true};}throw Error('Unexpected '+path);};
 window.desk=setupLifecycle(document.querySelector('#gas-form'),{...options,api:actual,busy:()=>false,identity:()=> 'synthetic_operator',context:()=>({snapshotId:'snapshot-1'}),historical:()=>false,act:async work=>{try{await work();}catch(error){errors.push(error.message);}}});await desk.load();
 })()`);
 await new Promise(r=>setTimeout(r,50));await evaluate("document.querySelector('.tabulator-row').click()");await new Promise(r=>setTimeout(r,50));
 assert.equal(await evaluate("document.querySelector('[name=positions]')!==null"),true);
 await evaluate('addTitle()');await new Promise(r=>setTimeout(r,50));await evaluate("fillTitle();document.querySelector('[data-lifecycle=panel] form').requestSubmit()");await new Promise(r=>setTimeout(r,50));
 assert.deepEqual(await evaluate('errors'),[]);
 const review=await evaluate("document.querySelector('[data-lifecycle-review]').textContent");
 assert.match(review,/Acknowledged revision/);assert.match(review,/Current revision/);assert.match(review,/Current quantity/);assert.match(review,/Synthetic retired title/);
 await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await new Promise(r=>setTimeout(r,50));
 const calls=await evaluate('sent');assert.deepEqual(calls.map(c=>c.path),['lifecycle/preview','lifecycle/execute']);
 assert.equal(calls[0].body.operationId,calls[1].body.operationId);assert.deepEqual(calls[0].body.selection,calls[1].body.selection);
}));
