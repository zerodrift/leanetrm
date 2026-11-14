import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

test('swap settlement retains its read and P&L view while new actions open Lifecycle',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="editor-shelf"></div><div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {setupSwapSettlement,lifecycleMeasures}=await import('/swap-settlement.js');const sent=[];let opened=0;
  const detail={state:{tradeId:'synthetic-trade',revision:1,tradeReference:'TR-1'},context:{contextId:'synthetic-context'},asOf:'2026-11-02T16:00:00Z',canAmend:true,currencyLabel:'USD',decimalPlaces:2,
   settlement:{version:2,occurrences:[],determinations:[]},balances:[{determinationId:'synthetic-determination',membershipOrdinal:0,deliveryFrom:'2026-10-01',deliveryTo:'2026-11-01',ordinal:1,dueAt:'2026-11-02T16:00:00Z',amount:'100',applied:'0',outstanding:'100'}],
   cashMovements:[],valuation:{...Object.fromEntries(lifecycleMeasures.map(([,key])=>[key,'0'])),payments:[{membershipOrdinal:0,ordinal:0,deliveryFrom:'2026-10-01',deliveryTo:'2026-11-01',paymentAt:'2026-11-02T16:00:00Z',completionAt:'2026-11-01T00:00:00Z',state:'Undetermined',final:true,realized:true,contractualAmount:'100',remainingValue:'100'}]}};
  const view=setupSwapSettlement({api:async(path)=>{sent.push(path);if(path==='swaps/settlement/inspect')return detail;if(path==='workspace/navigation')return{frames:[]};throw Error('Unexpected operation '+path);},
   act:async work=>work(),busy:()=>false,zone:()=>'America/Chicago',context:()=>({snapshotId:'synthetic-frame'}),pending:()=>undefined,
   host:()=>document.querySelector('#host'),lifecycle(){opened++;}});
  await view.open('synthetic-trade');document.querySelector('#swap-open-lifecycle')?.click();
  return{opened,sent,legacy:!!document.querySelector('#swap-settlement-payment-form,#swap-net-form,#swap-settlement-determine'),
   pnl:!!document.querySelector('#swap-settlement-opening,#swap-settlement-reconcile'),text:document.querySelector('#host').textContent,unsaved:view.unsaved()};
 })()`);
 assert.equal(result.opened,1);assert.equal(result.legacy,false);assert.equal(result.pnl,true);assert.equal(result.unsaved,false);
 assert.deepEqual(result.sent,['swaps/settlement/inspect','workspace/navigation']);assert.match(result.text,/TR-1/);assert.match(result.text,/saved action policies/i);
}));
