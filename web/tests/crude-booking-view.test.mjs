import test from 'node:test';
import assert from 'node:assert/strict';
import {showAcceptedCrudeTrade} from '../dist/physical-crude-editor.js';
const deferred=()=>{let resolve;const promise=new Promise(r=>{resolve=r;});return{promise,resolve};};
test('an accepted trade whose editor read completes after actor change cannot publish into the new panel',async()=>{
 const read=deferred(),calls=[];let actor='first';
 const done=showAcceptedCrudeTrade({current:()=>actor==='first',open:async()=>{calls.push('open');await read.promise;},directory:async()=>{calls.push('directory');},booked:async()=>{calls.push('booked');}});
 actor='second';read.resolve();await done;assert.deepEqual(calls,['open']);
});
test('a context change during directory refresh prevents accepted trade navigation',async()=>{
 const read=deferred(),calls=[];let epoch=1;
 const done=showAcceptedCrudeTrade({current:()=>epoch===1,open:async()=>{calls.push('open');},directory:async()=>{calls.push('directory');await read.promise;},booked:async()=>{calls.push('booked');}});
 await Promise.resolve();epoch++;read.resolve();await done;assert.deepEqual(calls,['open','directory']);
});
test('the unchanged accepted booking publishes only after both retained reads',async()=>{
 const calls=[];await showAcceptedCrudeTrade({current:()=>true,open:async()=>{calls.push('open');},directory:async()=>{calls.push('directory');},booked:async()=>{calls.push('booked');}});
 assert.deepEqual(calls,['open','directory','booked']);
});
