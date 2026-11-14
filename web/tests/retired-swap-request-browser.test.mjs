import test from 'node:test';
import assert from 'node:assert/strict';
import {browserAvailable,browserTestTimeout,withBrowserPage} from './browser-page.mjs';

test('earlier swap requests only read status and retain mismatched or missing outcomes until explicit dismissal',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {retiredSwapRequest}=await import('/retired-swap-request.js');let command={operationId:'earlier-op',path:'swaps/settlement/payment',body:{tradeId:'trade-a'}},reply={found:true,operationId:'other-op',tradeId:'trade-a'};
  const sent=[];let resolved=0,dismissed=0;
  const recovery=retiredSwapRequest(document.querySelector('#host'),{api:async(path,body)=>{sent.push([path,body]);return reply;},actor:()=>'actor-a',command:()=>command,busy:()=>false,act:async work=>work(),resolved:async()=>{resolved++;command=undefined;},dismissed(){dismissed++;command=undefined;}});
  await recovery.check();const wrong={kept:!!command,text:document.querySelector('#host').textContent};
  reply={found:false};await recovery.check();const missing={kept:!!command,canDismiss:!document.querySelector('#swap-earlier-dismiss').hidden};
  document.querySelector('#swap-earlier-dismiss').click();return{wrong,missing,sent,resolved,dismissed,kept:!!command};
 })()`);
 assert.equal(result.wrong.kept,true);assert.match(result.wrong.text,/exact earlier operation and trade/);
 assert.deepEqual(result.missing,{kept:true,canDismiss:true});assert.equal(result.resolved,0);assert.equal(result.dismissed,1);assert.equal(result.kept,false);
 assert.deepEqual(result.sent.map(x=>x[0]),['swaps/settlement/status','swaps/settlement/status']);
}));

test('an earlier actor response cannot release the active actor request',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {retiredSwapRequest}=await import('/retired-swap-request.js');let actor='actor-a',command={operationId:'op-a',path:'swaps/settlement/net',body:{preview:{tradeId:'trade-a'}}},complete,resolved=0;
  const recovery=retiredSwapRequest(document.querySelector('#host'),{api:()=>new Promise(resolve=>complete=resolve),actor:()=>actor,command:()=>command,busy:()=>false,act:async work=>work(),resolved:async()=>{resolved++;command=undefined;},dismissed(){command=undefined;}});
  const checking=recovery.check();actor='actor-b';command={operationId:'op-b',path:'swaps/settlement/determine',body:{preview:{tradeId:'trade-b'}}};recovery.sync();
  complete({found:true,operationId:'op-a',tradeId:'trade-a'});await checking;return{resolved,command,canDismiss:!document.querySelector('#swap-earlier-dismiss').hidden};
 })()`);
 assert.equal(result.resolved,0);assert.equal(result.command.operationId,'op-b');assert.equal(result.canDismiss,false);
}));

test('an exact accepted earlier swap result releases only that retained request',{skip:!browserAvailable,timeout:browserTestTimeout},()=>withBrowserPage('<div id="host"></div>',async evaluate=>{
 const result=await evaluate(`(async()=>{
  const {retiredSwapRequest}=await import('/retired-swap-request.js');let command={operationId:'earlier-op',path:'swaps/settlement/determine',body:{preview:{tradeId:'trade-a'}}},resolved;
  const sent=[];const recovery=retiredSwapRequest(document.querySelector('#host'),{api:async(path,body)=>{sent.push([path,body]);return{found:true,operationId:'earlier-op',tradeId:'trade-a'};},actor:()=>'actor-a',command:()=>command,busy:()=>false,act:async work=>work(),resolved:async original=>{resolved=original;command=undefined;},dismissed(){throw Error('Unexpected dismissal');}});
  await recovery.check();return{resolved,kept:!!command,hidden:document.querySelector('#retired-swap-request').hidden,sent};
 })()`);
 assert.equal(result.kept,false);assert.equal(result.hidden,true);assert.equal(result.resolved.operationId,'earlier-op');
 assert.deepEqual(result.sent,[['swaps/settlement/status',{operationId:'earlier-op'}]]);
}));
