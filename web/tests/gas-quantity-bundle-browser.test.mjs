import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';
import {fixture} from './gas-owned-fixture.mjs';

const prepare=fixture.replace("const api=async(path,body)=>{","const api=async(path,body)=>{if(path==='lifecycle/gas/setup')return memberSetup(body);")+`
offer.action='gas.owned.reconcile_quantity';offer.label='Correct linked title and futures delivery';
offer.target={handler:'gas_quantity_bundle',scopeId:'account-1',bindingId:'account-1'};setup.policies[0].action=offer.action;
window.memberSetup=body=>({...setup,target:body.target,forms:{[body.target.handler==='gas_owned_position_quantity_source'?offer.action:'gas.futures.reconcile_quantity']:{
 label:'Observed correction',permission:'RecordGasOwnedHoldings',fields:[
  {name:'observedMember',label:'Observed member revision',kind:'reference',nullable:false,
   members:body.target.handler==='gas_owned_position_quantity_source'?['positionId','expectedRevision']:['positionId','performanceId','expectedRevision'],
   choices:[{id:body.target.bindingId,label:'Synthetic selected member',value:body.target.handler==='gas_owned_position_quantity_source'?{positionId:body.target.bindingId,expectedRevision:1}:{positionId:body.target.scopeId,performanceId:body.target.bindingId,expectedRevision:1}}]},
  {name:'quantity',label:'Explicit corrected quantity',kind:'text',nullable:false}]}}});
setup.forms={[offer.action]:{label:offer.label,permission:'RecordGasOwnedHoldings',fields:[
 {name:'ordinary',label:'Owned title corrections',kind:'object',nullable:false,fields:[
  {name:'positions',label:'Owned titles',memberLabel:'Owned title',kind:'array',nullable:false,variants:[
   {id:'title-1',label:'Synthetic owned title',target:{handler:'gas_owned_position_quantity_source',scopeId:'account-1',bindingId:'title-1'}}]}]},
 {name:'physical',label:'Futures delivery corrections',kind:'object',nullable:false,fields:[
  {name:'corrections',label:'Observed delivery corrections',memberLabel:'Observed delivery',kind:'array',nullable:false,minItems:1,variants:[
   {id:'delivery-1',label:'TR-000101 · Synthetic delivery',action:'gas.futures.reconcile_quantity',target:{handler:'gas_futures_reconciliation',scopeId:'obligation-1',bindingId:'delivery-1'}}]}]}]}};
window.errors=[];options.act=async work=>{try{await work();}catch(error){errors.push(error.message);}};
document.querySelector('#gas-form').replaceChildren();window.editor=mountGasOwnedAction(document.querySelector('#gas-form'),offer,setup,options);
window.choose=(name,value)=>{const n=document.querySelector('[name="'+name+'"]');n.value=value;n.dispatchEvent(new Event('change',{bubbles:true}));};
window.add=(family,id)=>{choose(family,'entries');document.querySelector('[name="'+family+'"]').parentElement.querySelector(':scope > button').click();choose(family+'-member',id);};
`;

test('a composed gas form loads each member action under its exact captured source',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="gas-form"></div>',async evaluate=>{
 await evaluate(`(async()=>{${prepare}add('positions','title-1');add('corrections','delivery-1');})()`);
 await new Promise(resolve=>setTimeout(resolve,70));
 assert.deepEqual(await evaluate('errors'),[]);
 assert.equal(await evaluate("document.querySelectorAll('[name=quantity]').length"),2);
 assert.equal(await evaluate("[...document.querySelectorAll('[name=quantity]')].every(n=>n.value==='')"),true);
 assert.equal(await evaluate("document.querySelector('textarea')===null"),true);
 assert.deepEqual(await evaluate('sent'),[]);
}));

test('the linked correction review shows both product quantities and source allocations before one command',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="gas-form"></div>',async evaluate=>{
 await evaluate(`(async()=>{${prepare}
 const {setupLifecycle}=await import('/lifecycle-view.js');
 const discovery={context:setup.context,actions:[offer],policies:[],optionProfiles:[],swapProfiles:[],marginPolicies:[],choices:{actions:[{code:offer.action,automatic:false,configurationPermission:'ConfigureGasOwnedHoldings',executionPermission:'RecordGasOwnedHoldings'}],modes:['manual'],policyImplementation:'explicit_lifecycle_action_policy_v1',swapRounding:[],swapDue:[],swapRecognition:[],swapCorrection:[]}};
 const source=key=>({sources:[{sourceKey:key,oldCapacity:'1',capacity:'0.125',oldAllocated:'1',allocated:'0.125',previouslyUnused:'0',unused:'0',quantity:{code:'synthetic_quantity'}}]});
 const actual=async(path,body)=>{if(path==='lifecycle/view')return discovery;if(path==='lifecycle/gas/setup')return body.target.handler==='gas_quantity_bundle'?{...setup,target:body.target}:memberSetup(body);
  if(path==='lifecycle/preview'){sent.push({path,body});const physical={performanceId:'delivery-1',positionId:'obligation-1',acknowledgedRevision:2,currentRevision:2,currentQuantity:'0.25',changed:true};
   return{hash:'a'.repeat(64),offer,details:{willChange:true,outcome:{ordinary:{positions:[{positionId:'title-1',acknowledgedRevision:2,currentRevision:2,currentQuantity:'0.125',changed:true}],consumptions:[]},physical:{corrections:[physical]}},
    corrections:[physical],sourceAllocations:[source('Synthetic title source'),source('Synthetic delivery source')],principal:[]}};}
  if(path==='lifecycle/execute'){sent.push({path,body});return{operationId:body.operationId,changed:true};}throw Error('Unexpected '+path);};
 window.desk=setupLifecycle(document.querySelector('#gas-form'),{...options,api:actual,busy:()=>false,identity:()=> 'synthetic_operator',context:()=>({snapshotId:'snapshot-1'}),historical:()=>false});await desk.load();
 })()`);
 await new Promise(resolve=>setTimeout(resolve,50));await evaluate("document.querySelector('.tabulator-row').click()");await new Promise(resolve=>setTimeout(resolve,50));
 assert.equal(await evaluate("document.querySelector('[name=positions]')!==null"),true);
 await evaluate("add('positions','title-1');add('corrections','delivery-1')");await new Promise(resolve=>setTimeout(resolve,70));
 await evaluate("(()=>{choose('policyId','policy-1:2');for(const[family,quantity]of [['positions','0.125'],['corrections','0.25']]){const row=document.querySelector('[data-gas-entry=\"'+family+'\"]'),member=row.querySelector('[name=observedMember]');member.selectedIndex=1;member.dispatchEvent(new Event('change',{bubbles:true}));const n=row.querySelector('[name=quantity]');n.value=quantity;n.dispatchEvent(new Event('change',{bubbles:true}));}document.querySelector('[data-lifecycle=panel] form').requestSubmit();})()");
 await new Promise(resolve=>setTimeout(resolve,70));assert.deepEqual(await evaluate('errors'),[]);
 const review=await evaluate("document.querySelector('[data-lifecycle-review]').textContent");
 assert.match(review,/Owned title/);assert.match(review,/Observed performance/);assert.match(review,/0\.125/);assert.match(review,/0\.25/);
 assert.match(review,/Synthetic title source/);assert.match(review,/Synthetic delivery source/);
 await evaluate("document.querySelector('[data-lifecycle-execute]').click()");await new Promise(resolve=>setTimeout(resolve,70));
 const calls=await evaluate('sent');assert.deepEqual(calls.map(c=>c.path),['lifecycle/preview','lifecycle/execute']);
 assert.equal(calls[0].body.operationId,calls[1].body.operationId);assert.deepEqual(calls[0].body.selection,calls[1].body.selection);
}));
