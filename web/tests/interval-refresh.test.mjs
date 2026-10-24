import test from 'node:test';
import assert from 'node:assert/strict';
import {IntervalRefresh} from '../dist/interval-refresh.js';
const deferred=()=>{let resolve,reject;const promise=new Promise((a,b)=>{resolve=a;reject=b;});return {promise,resolve,reject};};
test('an old response never crosses a scope change and requests never overlap',async()=>{
 const first=deferred(),second=deferred(),shown=[];let calls=0;
 const refresh=new IntervalRefresh(()=>++calls===1?first.promise:second.promise,x=>shown.push(x),()=>{});
 refresh.setActive(true);const pending=refresh.refresh();await refresh.refresh();assert.equal(calls,1);
 refresh.invalidate();first.resolve('old');await pending;assert.deepEqual(shown,[]);
 const next=refresh.refresh();second.resolve('new');await next;assert.deepEqual(shown,['new']);refresh.dispose();
});
test('hiding or disposing ignores pending responses; errors are reported without replacing values',async()=>{
 const pending=deferred(),shown=[],errors=[];const refresh=new IntervalRefresh(()=>pending.promise,x=>shown.push(x),e=>errors.push(e));
 refresh.setActive(true);const read=refresh.refresh();refresh.setActive(false);pending.resolve('hidden');await read;assert.deepEqual(shown,[]);
 const failing=new IntervalRefresh(async()=>{throw Error('Unavailable');},x=>shown.push(x),e=>errors.push(e));
 failing.setActive(true);await failing.refresh();assert.equal(errors[0].message,'Unavailable');assert.deepEqual(shown,[]);failing.dispose();refresh.dispose();
});
test('repeated visible notifications do not postpone an automatic refresh',async t=>{
 t.mock.timers.enable({apis:['setTimeout']});let calls=0;
 const refresh=new IntervalRefresh(async()=>++calls,()=>{},()=>{});
 refresh.setActive(true);refresh.setPeriod(5000);
 for(let i=0;i<5;i++){t.mock.timers.tick(1000);refresh.setActive(true);}
 assert.equal(calls,1);await Promise.resolve();refresh.dispose();
});
