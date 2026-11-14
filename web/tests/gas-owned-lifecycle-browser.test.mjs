import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';
const page='<div id="gas-form"></div>';
const fixture=`
const {mountGasOwnedAction}=await import('/gas-owned-lifecycle.js');
window.sent=[];window.gasCurrent=true;window.gasZone='America/Chicago';
const setup={context:{contextId:'source-1',asOf:'2026-11-06T22:00:00Z',businessDate:'2026-11-06',snapshotId:'snapshot-1'},
 accounts:[{id:'account-1',label:'Synthetic owned account'}],policies:[{id:'policy-1',revision:2,accountId:'account-1',action:'gas.owned.receive_title',label:'Observed title · revision 2'}],
 evidence:[{id:'evidence-1',label:'Synthetic observed title · source revision 3'}],lots:[],reservations:[],loans:[],positions:[],fees:[],parties:[],
 choices:{carrying:[{id:'nominal_mark_v1',label:'Nominal marked value'}],titleOrigins:[{id:'opening_owned_stock_v1',label:'Explicit owned opening stock'}]}};
setup.forms={'gas.owned.receive_title':{label:'Accept title to gas',permission:'RecordGasOwnedHoldings',fields:[
 {name:'evidenceId',label:'Observed title evidence',kind:'select',nullable:false,choices:setup.evidence},
 {name:'quantity',label:'Observed quantity',kind:'text',nullable:false},
 {name:'titleAt',label:'Title transferred at',kind:'instant',nullable:false},
 {name:'availableAt',label:'Available at',kind:'instant',nullable:false},
 {name:'originPolicy',label:'Title origin',kind:'select',nullable:false,choices:setup.choices.titleOrigins},
 {name:'carrying',label:'Carrying value',kind:'select',nullable:false,choices:setup.choices.carrying}]}};
setup.configuration={};
const offer={target:{handler:'gas_owned',scopeId:'account-1',bindingId:'account-1'},action:'gas.owned.receive_title',label:'Accept title to gas',
 occurrenceId:'account-1',at:setup.context.asOf,before:null,eligible:true,reason:null,revision:1,stateVersion:1,selection:null,requiredInputs:[]};
const api=async(path,body)=>{if(path!=='time/inspect')throw Error('Unexpected operation');return {candidates:[{instant:'2026-11-06T18:00:00Z',offset:'-06:00'}]};};
const options={api,zone:()=>gasZone,permission:()=>true,current:()=>gasCurrent,changed:()=>{},act:async work=>work(),queue:async(path,body)=>sent.push({path,body}),confirmDiscard:async()=>true};
window.editor=mountGasOwnedAction(document.querySelector('#gas-form'),offer,setup,options);
window.fillGas=()=>{for(const [key,value] of Object.entries({policyId:'policy-1:2',evidenceId:'evidence-1',quantity:'0.333333333333333333',
 titleAt:'2026-11-06T12:00:00',availableAt:'2026-11-06T12:00:00',originPolicy:'opening_owned_stock_v1',carrying:'nominal_mark_v1'})){
 const control=document.querySelector('[name="'+key+'"]');control.value=value;control.dispatchEvent(new Event('input',{bubbles:true}));control.dispatchEvent(new Event('change',{bubbles:true}));}};
`;
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
   const actual=async(path,body)=>{if(path==='lifecycle/view')return discovery;if(path==='lifecycle/gas/setup')return setup;
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
