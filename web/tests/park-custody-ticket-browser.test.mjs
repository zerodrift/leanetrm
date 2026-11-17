import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,withBrowserPage,browserTestTimeout} from './browser-page.mjs';

const page='<div id="park-loan-page"></div><div id="terms"></div>';
const fixture=`
const {setupParkLoanTicket,readParkLoanView}=await import('/park-loan-trade-view.js');
window.calls=[];window.bookings=[];window.saved=[];window.historical=false;window.actor='synthetic-actor';window.wrongContext=false;window.wrongProfile=false;window.wrongTarget=false;window.overviewOverride=null;
const product='physical_gas_park_loan',nativeProduct='physical_gas_park_custody',profile='physical_gas_park_custody_v1';
const choices={profile:[{value:profile,label:'Retained-title Park custody'}],perspective:[{value:'customer',label:'Customer'},{value:'provider',label:'Provider'}],
 reference_bundle:[{value:'synthetic-catalog@synthetic-revision',label:'Synthetic exact bundle'}],point:[{value:'synthetic-point',label:'Synthetic custody point'}],
 quantity_specification:[{value:'synthetic-quantity',label:'Synthetic native quantity'}],currency:[{value:'synthetic-currency',label:'Synthetic currency'}],fee_direction:[{value:'pay',label:'Pay'},{value:'receive',label:'Receive'}]};
const field=id=>({id,label:id,meaning:'Explicit synthetic custody term.',type:id==='fixed_fee_amount'?'decimal':'choice',unit:null,state:'editable',required:true,band:null,choices:id==='fixed_fee_amount'?null:id,dependsOn:id==='owned_account'?['perspective']:[],lookup:null,soon:null,rule:null,...id==='profile'?{refreshDescription:true}:{}});
const common=['profile','perspective','reference_bundle','point','quantity_specification','currency','owned_account','fee_direction'];
const description=selected=>({product,version:1,rowShape:'flow',tradeFields:[...common,...selected?['fixed_fee_amount']:[]].map(field),selection:selected?{profile}:{},sideLimits:[],levels:['term'],modes:['new','booked'],tabs:[],tradeColumns:[],sides:[]});
const schedule={levels:['term'],valuationAt:'2041-01-01T12:00:00Z',rows:[{key:'term',parent:null,level:'term',start:'2041-02-01T12:00:00Z',end:'2041-02-08T12:00:00Z',operatingDate:null,cells:{},delivered:false}],totals:{},delivered:null,remaining:null,deliveredLeaves:0,leaves:1};
const preview={source:{definitionRevisionId:'synthetic-definition',referenceCatalogId:'synthetic-catalog',referenceRevisionId:'synthetic-revision'},schedule,summaries:[],links:[],scheduleRefusal:null,quantityUnit:'Synthetic unit'};
const draft={product,version:1,trade:{profile,perspective:'provider',reference_bundle:'synthetic-catalog@synthetic-revision',point:'synthetic-point',quantity_specification:'synthetic-quantity',currency:'synthetic-currency',owned_account:'none',fee_direction:'receive',fixed_fee_amount:'12.34567890123456789'},sides:[]};
window.custodyDraft=draft;
const trade={...preview,draft,snapshotId:'synthetic-snapshot',context:{contextId:'synthetic-cutoff',effectiveOn:'2041-01-01',knownAt:'2041-01-01T19:00:00Z'},actions:{amend:{allowed:false,reason:'Custody continuity is not qualified.'},closeOut:{allowed:false,reason:'Use custody lifecycle.'},record:{allowed:false,reason:'Use custody lifecycle.'}},facilityAllocations:[],measureLabel:'Remaining custody service PV',ownershipNote:'Customer stock remains independently owned.',overviewSections:[{label:'Custody',values:[{label:'In custody',value:'450 Synthetic unit'},{label:'Reserved inventory',value:'None for provider'}]}],lifecycleTarget:{handler:'gas_park_custody',scopeId:'synthetic-trade',bindingId:'synthetic-binding'}};
const api=async(path,body)=>{calls.push({path,body:structuredClone(body)});
 if(path==='tickets/description')return description(!!body.draft?.trade.profile);
 if(path==='tickets/choices')return {choices:body.field==='owned_account'?(body.draft.trade.perspective==='provider'?[{value:'none',label:'Not applicable for provider'}]:body.draft.trade.perspective==='customer'?[{value:'synthetic-account',label:'Synthetic inventory'}]:[]):choices[body.field],starting:null};
 if(path==='tickets/preview')return preview;
 if(path==='tickets/trade')return {...trade,overviewSections:overviewOverride??trade.overviewSections,draft:wrongProfile?{...draft,trade:{...draft.trade,profile:'physical_gas_park_loan_v1'}}:draft,lifecycleTarget:wrongTarget?{...trade.lifecycleTarget,bindingId:'other-binding'}:trade.lifecycleTarget,context:{...trade.context,contextId:wrongContext?'other-cutoff':trade.context.contextId}};
 if(path==='tickets/book'){bookings.push(structuredClone(body));throw Error('Synthetic lost custody reply');}
 if(path==='tickets/status')return {operationId:bookings[0].operationId,tradeId:'synthetic-trade',instrumentId:'synthetic-binding',revision:1};
 throw Error('Unexpected custody operation '+path);
};
window.until=async test=>{for(let i=0;i<300&&!test();i++)await new Promise(resolve=>setTimeout(resolve,10));if(!test())throw Error('Custody condition not ready');};
window.editor=setupParkLoanTicket({api,source:()=>({snapshotId:'synthetic-snapshot'}),historical:()=>historical,actor:()=>actor,instant:at=>at,zone:()=>'UTC',onBooked:async id=>saved.push(id)});
window.setTerm=(id,value)=>{const input=document.querySelector('[data-field="'+id+'"] .ticket-control');input.value=value;input.dispatchEvent(new Event('change'));};
window.holding={tradeId:'synthetic-trade',bindingId:'synthetic-binding',revision:'1',tradeReference:'Synthetic custody',instrumentReference:'Synthetic agreement',book:'Synthetic book',executedAt:'2040-12-01T12:00:00Z',trader:'Synthetic trader',booking:{tradeDate:'2040-12-01'},instrumentType:nativeProduct,priceNavigation:false,curve:'',projectionCurves:[]};
window.readCustody=context=>readParkLoanView(api,context,holding,{actor:()=>actor});
`;

test('provider custody selects independent references and explicit no inventory, then recovers its original booking',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${fixture}await editor.begin();await until(()=>document.querySelector('[data-field=profile] select')?.options.length===2);})()`);
 assert.equal(await evaluate("document.querySelector('[data-field=fixed_fee_amount]')===null"),true);
 assert.equal(await evaluate("[...document.querySelectorAll('.ticket-control')].every(input=>input.value==='')"),true);
 await evaluate("(async()=>{setTerm('profile',custodyDraft.trade.profile);await until(()=>!!document.querySelector('[data-field=fixed_fee_amount] input'));setTerm('perspective','provider');await until(()=>!!document.querySelector('[data-field=owned_account] option[value=none]'));for(const[id,value]of Object.entries(custodyDraft.trade))if(id!=='profile')setTerm(id,value);await until(()=>document.querySelector('.ticket-book')?.disabled===false);document.querySelector('.ticket-book').click();await until(()=>bookings.length===1&&editor.pending());editor.reset();await editor.begin();await until(()=>document.querySelectorAll('.ticket-booking button').length===2);})()");
 assert.equal(await evaluate('bookings[0].draft.trade.owned_account'),'none');assert.equal(await evaluate('bookings[0].draft.trade.fixed_fee_amount'),'12.34567890123456789');
 assert.equal(await evaluate("document.querySelector('[data-field=mark_curve]')===null"),true);
 await evaluate("(async()=>{[...document.querySelectorAll('.ticket-booking button')].find(button=>button.textContent==='Check booking status').click();await until(()=>saved.length===1);})()");
 assert.equal(await evaluate("calls.find(call=>call.path==='tickets/status').body.product"),'physical_gas_park_loan');
 assert.equal(await evaluate("calls.find(call=>call.path==='tickets/status').body.profile"),'physical_gas_park_custody_v1');
 assert.deepEqual(await evaluate('saved'),['synthetic-trade']);assert.equal(await evaluate('bookings.length'),1);
}));

test('custody retained inspection renders backend balances without a commodity market read',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${fixture}window.detail=await readParkLoanView(api,{snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'},holding,{actor:()=>actor});detail.view('UTC').termsContent(document.querySelector('#terms'));await until(()=>document.querySelector('#terms [data-field=owned_account] .ticket-value')?.textContent==='Not applicable for provider');})()`);
 assert.match(await evaluate("document.querySelector('#terms').textContent"),/450 Synthetic unit/);
 assert.equal(await evaluate("calls.every(call=>['tickets/trade','tickets/description','tickets/choices'].includes(call.path))"),true);
 assert.deepEqual(await evaluate("calls.filter(call=>call.path==='tickets/trade').map(call=>call.body.product)"),['physical_gas_park_custody','physical_gas_park_custody']);
 assert.equal(await evaluate("document.querySelectorAll('#terms input,#terms select').length"),0);
}));

test('custody historical entry and mismatched retained context withhold the ticket',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${fixture}historical=true;window.refusal=await editor.begin().then(()=>'',error=>error.message);})()`);
 assert.match(await evaluate('refusal'),/Return to working data/);assert.deepEqual(await evaluate('calls'),[]);
 const message=await evaluate("(async()=>{wrongContext=true;return readCustody({snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'}).then(()=>'',error=>error.message);})()");
 assert.match(message,/does not match/);
}));

test('custody inspection refuses a different profile or immutable lifecycle target under the same retained context',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${fixture}})()`);
 for(const flag of ['wrongProfile','wrongTarget']){
  const message=await evaluate(`(async()=>{wrongProfile=false;wrongTarget=false;window[${JSON.stringify(flag)}]=true;return readCustody({snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'}).then(()=>'',error=>error.message);})()`);
  assert.match(message,/custody.*profile.*target/i);
 }
}));


test('retained custody history formats only explicitly marked instants in each user zone and preserves exact titles', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 const raw='2041-01-01T00:00:01.1234560+00:00';
 await evaluate(`(async()=>{${fixture}overviewOverride=[{label:'Recorded custody observations',values:[{label:'Observed time',value:${JSON.stringify(raw)},kind:'instant'},{label:'Evidence',value:${JSON.stringify(raw)}},{label:'Accepted time',value:'Synthetic note: the operator wrote 16:00 local'}]}];window.custodyHistory=await readCustody({snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'});window.dispose=custodyHistory.view('UTC').termsContent(document.querySelector('#terms'));})()`);
 const row=label=>`document.querySelector('#terms .physical-instrument [data-field="${label}"] dd')`;
 assert.deepEqual(await evaluate(`({text:${row('Observed time')}.textContent,title:${row('Observed time')}.title})`),{text:'2041-01-01 00:00:01.1234560 UTC',title:raw});
 assert.deepEqual(await evaluate(`({text:${row('Evidence')}.textContent,title:${row('Evidence')}.title})`),{text:raw,title:''});
 assert.equal(await evaluate(`${row('Accepted time')}.textContent`),'Synthetic note: the operator wrote 16:00 local');
 await evaluate("dispose();dispose=custodyHistory.view('America/Chicago').termsContent(document.querySelector('#terms'))");
 assert.deepEqual(await evaluate(`({text:${row('Observed time')}.textContent,title:${row('Observed time')}.title})`),{text:'2040-12-31 18:00:01.1234560 UTC-06:00 · America/Chicago',title:raw});
 assert.equal(await evaluate(`${row('Evidence')}.textContent`),raw);
}));

test('invalid or unsupported overview instant markers withhold retained custody history instead of guessing a value', {skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage(page,async evaluate=>{
 await evaluate(`(async()=>{${fixture}})()`);
 for(const row of [{label:'Observed',value:'2041-01-01T00:00:00Z',kind:'date'},{label:'Observed',value:'2041-01-01T00:00:00Z',kind:null},{label:'Observed',value:'2041-01-01T00:00:00Z',kind:true},{label:'Observed',value:'Synthetic evidence only',kind:'instant'},{label:'Observed',value:'2041-01-01T00:00:00.00000001Z',kind:'instant'}]){
  const message=await evaluate(`(async()=>{overviewOverride=[{label:'Raw observations',values:[${JSON.stringify(row)}]}];return readCustody({snapshotId:'synthetic-snapshot',knowledgeMode:'retained',contextId:'synthetic-cutoff'}).then(()=>'',error=>error.message);})()`);
  assert.match(message,/overview|instant|marker/i,JSON.stringify(row));
 }
 assert.equal(await evaluate("document.querySelector('#terms').childElementCount"),0);
}));
