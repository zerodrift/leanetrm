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
