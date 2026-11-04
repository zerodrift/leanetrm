import test from 'node:test';
import assert from 'node:assert/strict';
import {batchReads} from '../dist/batch-reads.js';
const tick=()=>new Promise(resolve=>setTimeout(resolve,0));
test('overlapping demand coalesces, stays within the batch size and never overlaps requests',async()=>{
 const calls=[],release=[],activity=[];
 const reader=batchReads({maximum:2,current:()=>true,active:value=>activity.push(value),read:keys=>{calls.push(keys);return new Promise(resolve=>release.push(()=>resolve(new Map(keys.map(key=>[key,key*10])))));}});
 const first=reader.get([1,2,1]),second=reader.get([2,3]);await tick();assert.deepEqual(calls,[[1,2]]);
 const third=reader.get([4]);await tick();assert.equal(calls.length,1);release.shift()();await tick();assert.deepEqual(calls,[[1,2],[3,4]]);
 release.shift()();assert.deepEqual([...await first],[[1,10],[2,20]]);assert.deepEqual([...await second],[[2,20],[3,30]]);assert.deepEqual([...await third],[[4,40]]);
 await reader.idle();assert.deepEqual(activity,[true,false]);await reader.get([1]);assert.equal(calls.length,2);
});
test('disposal resolves pending callers, clears retained keys and ignores a late reply',async()=>{
 let release,received=0;const activity=[];
 const reader=batchReads({maximum:2,current:()=>true,active:value=>activity.push(value),received:()=>received++,read:()=>new Promise(resolve=>release=resolve)});
 const result=reader.get(['a']);await tick();reader.cancel();assert.equal((await result).size,0);assert.equal(reader.has('a'),false);await reader.idle();
 release(new Map([['a',1]]));await tick();assert.equal(received,0);assert.deepEqual(activity,[true,false]);assert.equal((await reader.get(['b'])).size,0);
});
test('a changed context dispatches no queued work and failures remain failures',async()=>{
 let current=true,calls=0;const reader=batchReads({maximum:1,current:()=>current,read:async()=>{calls++;return new Map();}});
 const pending=reader.get([1]);current=false;assert.equal((await pending).size,0);assert.equal(calls,0);
 const failed=batchReads({maximum:1,current:()=>true,read:async()=>{throw Error('Synthetic read failure');}});
 await assert.rejects(failed.get([1]),/Synthetic read failure/);await failed.idle();assert.equal(failed.busy(),false);
});
test('cancelling mixed cached and pending demand returns no stale cached values',async()=>{
 let release;const reader=batchReads({maximum:1,current:()=>true,read:keys=>keys[0]===1?Promise.resolve(new Map([[1,10]])):new Promise(done=>release=done)});
 await reader.get([1]);const mixed=reader.get([1,2]);await tick();reader.cancel();assert.equal((await mixed).size,0);release(new Map([[2,20]]));
});
