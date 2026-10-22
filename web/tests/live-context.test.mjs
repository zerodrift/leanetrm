import test from 'node:test';
import assert from 'node:assert/strict';
import {LiveUpdates} from '../dist/live-context.js';

test('bursts keep one latest update and never run two screen refreshes together',async()=>{
 const scheduled=[],states=[],seen=[];let finish;
 const updates=new LiveUpdates({schedule:work=>{scheduled.push(work);return()=>{};},busy:()=>false,status:s=>states.push(s),
  apply:async value=>{seen.push(value.generation);await new Promise(resolve=>finish=resolve);}});
 updates.offer({generation:1});scheduled.shift()();await Promise.resolve();
 updates.offer({generation:2});updates.offer({generation:1000});assert.deepEqual(seen,[1]);
 finish();await new Promise(resolve=>setImmediate(resolve));scheduled.shift()();await Promise.resolve();
 assert.deepEqual(seen,[1,1000]);finish();await new Promise(resolve=>setImmediate(resolve));
 assert.equal(states.at(-1).state,'live');assert.ok(states.at(-1).updatedAt);
});
test('failed refresh stays visibly stale and a reconnect can retry the same generation',async()=>{
 const scheduled=[],states=[];let fail=true;
 const updates=new LiveUpdates({schedule:work=>{scheduled.push(work);return()=>{};},busy:()=>false,status:s=>states.push(s),apply:async()=>{if(fail)throw Error('offline');}});
 updates.offer({generation:3});scheduled.shift()();await new Promise(resolve=>setImmediate(resolve));
 assert.equal(states.at(-1).state,'stale');assert.equal(states.at(-1).updatedAt,undefined);
 fail=false;updates.offer({generation:3});scheduled.shift()();await new Promise(resolve=>setImmediate(resolve));
 assert.equal(states.at(-1).state,'live');
 updates.disconnected();assert.equal(states.at(-1).state,'stale');assert.ok(states.at(-1).updatedAt);
 updates.offer({generation:3});assert.equal(states.at(-1).state,'stale');assert.equal(scheduled.length,0);
 updates.reconnected();assert.equal(states.at(-1).state,'live');
});
test('stopped or obsolete callbacks cannot mark another selection live',async()=>{
 const scheduled=[],states=[];let finish;
 const updates=new LiveUpdates({schedule:work=>{scheduled.push(work);return()=>{};},busy:()=>false,status:s=>states.push(s),apply:()=>new Promise(resolve=>finish=resolve)});
 updates.offer({generation:2});scheduled.shift()();await Promise.resolve();updates.stop();const count=states.length;
 finish();await new Promise(resolve=>setImmediate(resolve));updates.offer({generation:1});assert.equal(states.length,count);
});
