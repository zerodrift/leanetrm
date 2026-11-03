import test from 'node:test';
import assert from 'node:assert/strict';
import {apiRefusal} from '../dist/api-refusal.js';
import {PendingCommand} from '../dist/pending-command.js';
test('a coded server failure cannot become a definite booking rejection',async()=>{
 const pending=new PendingCommand(code=>code==='lng_input');pending.begin('lng/book',{quantity:'980'},()=> 'original');const before=pending.sending();
 const error=await apiRefusal('lng/book',new Response(JSON.stringify({code:'lng_input',message:'Synthetic server failure'}),{status:500}),false);
 pending.failed(error.code,before);assert.equal(pending.command?.operationId,'original');assert.equal(pending.command?.uncertain,true);
});
test('a known first 4xx refusal permits editing while an uncertain retry remains retained',async()=>{
 const error=await apiRefusal('lng/book',new Response(JSON.stringify({code:'lng_input',message:'Choose compatible exact terms'}),{status:422}),false);
 const pending=new PendingCommand(code=>code==='lng_input');pending.begin('lng/book',{},()=> 'first');pending.failed(error.code,pending.sending());assert.equal(pending.command,undefined);
 pending.begin('lng/book',{},()=> 'unknown');pending.sending();pending.failed(error.code,pending.sending());assert.equal(pending.command.operationId,'unknown');
});
test('missing market data is not blamed on a curve, since a discount rate can be what is missing',async()=>{
 const error=await apiRefusal('swaps/price',new Response(JSON.stringify({code:'missing_observation',message:'A selected rate sample is missing.'}),{status:422}),false);
 assert.equal(error.code,'missing_observation');
 assert.doesNotMatch(error.message,/for this curve/);
 assert.match(error.message,/price or discount rate/);
});
