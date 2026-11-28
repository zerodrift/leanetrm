import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';
const view={expectedRevision:1,canWrite:true,profile:'gas_final_daily_fixed_cash_v1',roundingPolicy:'currency_minor_units_to_even_v1',quantityPolicies:['final_allocated_billing_v1'],correctionReasons:['quantity_correction'],claims:[{cashSlot:1,code:'payment',role:'consideration',stage:'delivery',sideCode:'gas',day:'2021-01-01',price:'3',currency:'usd',decimalPlaces:2,supported:true,completed:true}],determinations:[]};

test('billing owns readonly, global busy and retained-command controls without treating a read as an edit',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{const {setupGasBilling}=await import('/gas-billing.js');let busy=false,pending;
  const api=async()=>(${JSON.stringify(view)}),workflow={busy:()=>busy,pending:()=>pending,queue:async()=>{},changed(){}};
  const widget=setupGasBilling(document.querySelector('#host'),{api,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',workflow});
  const tick=()=>new Promise(r=>setTimeout(r,0));await tick();const root=document.querySelector('.gas-billing'),claim=root.querySelector('[data-billing=claim]');
  const initially=claim.disabled;busy=true;widget.sync();const blocked=claim.disabled;busy=false;widget.sync();const restored=!claim.disabled;
  pending={operationId:'retained'};widget.sync();const retained=claim.disabled;
  widget.destroy();pending=undefined;const readonly=setupGasBilling(document.querySelector('#host'),{api,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',readOnly:true,workflow});await tick();
  for(const n of document.querySelectorAll('input,select,button'))if(!n.closest('[data-own-controls]'))n.disabled=false;readonly.sync();
  return {initially,blocked,restored,retained,readonly:document.querySelector('[data-billing=claim]').disabled,edited:readonly.unsaved()};
 })()`);
 assert.deepEqual(result,{initially:false,blocked:true,restored:true,retained:true,readonly:true,edited:false});
}));

test('an indexed claim names its index, reviews the fixing, spread and unit price, and sends the reviewed fixing',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const indexed={...view,profiles:['gas_final_daily_fixed_cash_v1','gas_final_daily_entry_cash_v1','gas_final_daily_indexed_cash_v1'],claims:[{...view.claims[0],code:'daily',role:'indexed_consideration',price:null,
  profiles:['gas_final_daily_indexed_cash_v1'],index:{code:'synthetic_daily',convention:'named_index_daily_v1',spread:'0.05'}}]};
 const result=await evaluate(`(async()=>{const {setupGasBilling}=await import('/gas-billing.js');let sent;
  const api=async path=>path==='gas/billing/read'?${JSON.stringify(indexed)}:{previewHash:'reviewed',sourceId:'source',previousId:null,amount:'-328.3',contribution:'-328.3',currency:'usd',contractualDueAt:'2021-02-10T00:00:00Z',alreadyDetermined:false,
   fixing:{observationId:'fixing',value:'3.3',correctsId:null,index:'synthetic_daily',spread:'0.05',unitPrice:'3.35'}};
  setupGasBilling(document.querySelector('#host'),{api,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',workflow:{busy:()=>false,pending:()=>undefined,changed(){},async queue(command){sent=command.body;}}});
  const tick=()=>new Promise(r=>setTimeout(r,0));await tick();const root=document.querySelector('.gas-billing'),profile=root.querySelector('[data-billing=profile]');
  const claim=root.querySelector('[data-billing=claim]').options[1].textContent,pick=()=>[...profile.options].filter(o=>o.value).map(o=>[o.value,o.textContent]);
  for(const [name,value] of [['claim','0'],['policy','final_allocated_billing_v1'],['profile','gas_final_daily_indexed_cash_v1'],['rounding','currency_minor_units_to_even_v1'],['evidence','Synthetic review']]){const field=root.querySelector('[data-billing='+name+']');field.value=value;field.dispatchEvent(new Event('change',{bubbles:true}));}
  const profiles=pick();root.querySelector('form').requestSubmit();await tick();const review=root.querySelector('[data-billing=review]').textContent;
  root.querySelector('[data-billing=confirm]').click();await tick();
  return {claim,profiles,review,fixing:sent.fixingObservationId,keys:Object.keys(sent).sort()};
 })()`);
 assert.match(result.claim,/synthetic_daily index, spread 0\.05 usd/);assert.doesNotMatch(result.claim,/null|unavailable/);
 assert.deepEqual(result.profiles,[['gas_final_daily_indexed_cash_v1','Final daily indexed cash · final published fixing plus spread']]);
 assert.match(result.review,/^Final fixing: 3\.3 on synthetic_daily\nSpread: 0\.05\nUnit price: 3\.35\nRestated amount: -328\.3 usd/);
 assert.equal(result.fixing,'fixing');
 assert.deepEqual(result.keys,['correctionDueAt','evidence','expectedPreviousId','fixingObservationId','preview','previewHash','reason','sourceId']);
}));

test('a later review that offers nothing to accept withdraws the earlier review',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{const {setupGasBilling}=await import('/gas-billing.js');let already=false,sent=0;
  const reviewed={previewHash:'reviewed',sourceId:'source',previousId:null,amount:'-300',contribution:'-300',currency:'usd',contractualDueAt:'2021-02-02T00:00:00Z'};
  const api=async path=>path==='gas/billing/read'?${JSON.stringify(view)}:{...reviewed,alreadyDetermined:already};
  setupGasBilling(document.querySelector('#host'),{api,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',workflow:{busy:()=>false,pending:()=>undefined,changed(){},async queue(){sent++;}}});
  const tick=()=>new Promise(r=>setTimeout(r,0));await tick();const root=document.querySelector('.gas-billing'),confirm=root.querySelector('[data-billing=confirm]');
  for(const [name,value] of [['claim','0'],['policy','final_allocated_billing_v1'],['profile','gas_final_daily_fixed_cash_v1'],['rounding','currency_minor_units_to_even_v1'],['evidence','Synthetic review']])root.querySelector('[data-billing='+name+']').value=value;
  root.querySelector('form').requestSubmit();await tick();const first=!confirm.hidden;
  already=true;root.querySelector('form').requestSubmit();await tick();const second=!confirm.hidden,reviewHidden=root.querySelector('[data-billing=review]').hidden;confirm.click();await tick();
  return {first,second,reviewHidden,sent};})()`);
 assert.deepEqual(result,{first:true,second:false,reviewHidden:true,sent:0});
}));

test('a handed request winning at acceptance cannot strand the billing form busy',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{const {setupGasBilling}=await import('/gas-billing.js');let queued=0;
  const api=async path=>path==='gas/billing/read'?${JSON.stringify(view)}:{previewHash:'reviewed',sourceId:'source',previousId:null,amount:'-300',contribution:'-300',currency:'usd',contractualDueAt:'2021-02-02T00:00:00Z',alreadyDetermined:false};
  const widget=setupGasBilling(document.querySelector('#host'),{api,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=> 'UTC',workflow:{busy:()=>false,pending:()=>undefined,changed(){},queue(){queued++;throw Error('Reconcile the handed request first.');}}});
  const tick=()=>new Promise(r=>setTimeout(r,0));await tick();const root=document.querySelector('.gas-billing');
  for(const [name,value] of [['claim','0'],['policy','final_allocated_billing_v1'],['profile','gas_final_daily_fixed_cash_v1'],['rounding','currency_minor_units_to_even_v1'],['evidence','Synthetic review']])root.querySelector('[data-billing='+name+']').value=value;
  root.querySelector('form').requestSubmit();await tick();root.querySelector('[data-billing=confirm]').click();await tick();
  return {queued,message:root.querySelector('[data-billing=message]').textContent,enabled:!root.querySelector('[data-billing=preview]').disabled,held:widget.pending()};
 })()`);
 assert.equal(result.queued,1);assert.match(result.message,/handed request/);assert.equal(result.enabled,true);assert.equal(result.held,false);
}));

test('original gas payment dates stay contractual while correction due times use the display zone',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const results=await evaluate(`(async()=>{const {setupGasBilling}=await import('/gas-billing.js');const results=[];
  for(const zone of ['America/Los_Angeles','Pacific/Auckland']){
   const data=${JSON.stringify(view)};data.determinations=[{id:'original',cash_slot:1,day:'2021-01-01',ordinal:1,rounded_total:'-300',contribution:'-300',currency_code:'usd',due_at:'2021-02-02T00:00:00Z',reason:null,evidence:'Synthetic original'},
    {id:'correction',cash_slot:1,day:'2021-01-01',ordinal:2,rounded_total:'-330',contribution:'-30',currency_code:'usd',due_at:'2021-02-03T18:00:00Z',reason:'quantity_correction',evidence:'Synthetic correction'}];
   const api=async path=>path==='gas/billing/read'?data:{previewHash:'reviewed',sourceId:'source',previousId:null,amount:'-300',contribution:'-300',currency:'usd',contractualDueAt:'2021-02-02T00:00:00Z',alreadyDetermined:false};
   const widget=setupGasBilling(document.querySelector('#host'),{api,context:()=>({}),tradeId:'synthetic',actor:()=> 'operator',zone:()=>zone,workflow:{busy:()=>false,pending:()=>undefined,changed(){},async queue(){}}});
   const tick=()=>new Promise(r=>setTimeout(r,30));await tick();const root=document.querySelector('.gas-billing');
   for(const [name,value] of [['claim','0'],['policy','final_allocated_billing_v1'],['profile','gas_final_daily_fixed_cash_v1'],['rounding','currency_minor_units_to_even_v1'],['evidence','Synthetic review']])root.querySelector('[data-billing='+name+']').value=value;
   root.querySelector('form').requestSubmit();await tick();
   results.push({review:root.querySelector('[data-billing=review]').textContent,history:root.querySelector('[data-billing=history]').textContent});widget.destroy();
  }return results;
 })()`);
 for(const result of results){assert.match(result.review,/Due: Feb 2, 2021\n/);assert.match(result.history,/Feb 2, 2021/);}
 assert.notEqual(results[0].history,results[1].history,'Correction timestamps must still use the display zone.');
}));
