import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';
import {fixture} from './gas-owned-fixture.mjs';

const effects={accruedThrough:'2026-11-06T18:00:00Z',previousCumulativeRoundedFee:'-2.88',
 cumulativeRoundedFee:'-4.69',independentCash:'-1.81',exactTotal:'-4.687501',currency:'usd',dueAt:'2026-11-08T18:00:00Z',principalUnchanged:true};
async function openReview(evaluate,value){
 await evaluate(`(async()=>{${fixture}
  const {setupLifecycle}=await import('/lifecycle-view.js');window.gasErrors=[];
  offer.action='gas.park_loan.fee_determine';offer.label='Determine accrued service fee';offer.target.handler='gas_park_loan';
  setup.policies[0].action=offer.action;setup.forms={[offer.action]:{label:offer.label,permission:'RecordGasOwnedHoldings',fields:[
   {name:'through',label:'Observed accrual through',kind:'instant',nullable:false},
   {name:'evidence',label:'Determination evidence',kind:'text',nullable:false}]}};
  const discovery={context:setup.context,actions:[offer],policies:[],optionProfiles:[],swapProfiles:[],marginPolicies:[],choices:{actions:[{code:offer.action,automatic:false,configurationPermission:'ConfigureGasOwnedHoldings',executionPermission:'RecordGasOwnedHoldings'}],modes:['manual','review'],policyImplementation:'explicit_lifecycle_action_policy_v1',swapRounding:[],swapDue:[],swapRecognition:[],swapCorrection:[]}};
  const actual=async(path,body)=>{if(path==='lifecycle/view')return discovery;if(path==='lifecycle/gas/setup')return {...setup,target:body.target};
   if(path==='lifecycle/preview'){sent.push({path,body});return {hash:'a'.repeat(64),offer,details:{selection:body.selection,effects:${JSON.stringify(value)}}};}return api(path,body);};
  window.desk=setupLifecycle(document.querySelector('#gas-form'),{...options,api:actual,busy:()=>false,identity:()=> 'synthetic_operator',context:()=>({snapshotId:'snapshot-1'}),historical:()=>false,act:async work=>{try{await work();}catch(error){gasErrors.push(error.message);}}});await desk.load();
 })()`);
 for(let attempt=0;attempt<50&&!await evaluate("!!document.querySelector('.tabulator-row')");attempt++)await new Promise(resolve=>setTimeout(resolve,20));
 await evaluate("document.querySelector('.tabulator-row').click()");
 for(let attempt=0;attempt<50&&!await evaluate("!!document.querySelector('[name=through]')");attempt++)await new Promise(resolve=>setTimeout(resolve,20));
 await evaluate(`(()=>{for(const[name,value]of Object.entries({policyId:'policy-1:2',through:'2026-11-06T12:00:00',evidence:'Synthetic reviewed fee'})){
  const input=document.querySelector('[name='+name+']');input.value=value;input.dispatchEvent(new Event('input',{bubbles:true}));input.dispatchEvent(new Event('change',{bubbles:true}));}
  document.querySelector('[data-lifecycle=panel] form').requestSubmit();})()`);
 for(let attempt=0;attempt<50&&!await evaluate("gasErrors.length||!!document.querySelector('[data-lifecycle-review]')");attempt++)await new Promise(resolve=>setTimeout(resolve,20));
}

test('fee determination review presents backend monetary totals and explicit through/due times in the user zone',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="gas-form"></div>',async evaluate=>{
 await openReview(evaluate,effects);
 assert.deepEqual(await evaluate('gasErrors'),[]);
 const facts=await evaluate("Object.fromEntries([...document.querySelectorAll('[data-lifecycle-review] dt')].map(label=>[label.textContent,label.nextElementSibling.textContent]))");
 assert.equal(facts['Previous rounded cumulative fee'],'-2.88');assert.equal(facts['New rounded cumulative fee'],'-4.69');
 assert.equal(facts['Signed fee contribution'],'-1.81');assert.match(facts['Observed accrual through'],/Nov 6, 2026, 12:00 PM/);
 assert.match(facts['Fee due'],/Nov 8, 2026, 12:00 PM/);assert.equal(facts.Currency,'usd');
 assert.equal(await evaluate("document.querySelector('[data-lifecycle-execute]').disabled"),false);
 assert.equal((await evaluate('sent')).filter(call=>call.path==='lifecycle/execute').length,0);
}));

for(const missing of ['previousCumulativeRoundedFee','cumulativeRoundedFee','independentCash','accruedThrough','dueAt']){
 test(`a fee review missing ${missing} withholds execution`,{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="gas-form"></div>',async evaluate=>{
  const incomplete={...effects};delete incomplete[missing];await openReview(evaluate,incomplete);
  assert.match((await evaluate('gasErrors')).join(' '),/complete cumulative fee review/i);
  assert.equal(await evaluate("document.querySelector('[data-lifecycle-execute]')===null"),true);
 }));
}
