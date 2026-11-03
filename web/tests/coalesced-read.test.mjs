import test from 'node:test';
import assert from 'node:assert/strict';
import {coalescedRead} from '../dist/coalesced-read.js';

test('a background verification and date change share the whole account/preference read',async()=>{
 let bootstrap,preferences,calls=0;
 const verify=coalescedRead(async()=>{calls++;await new Promise(r=>bootstrap=r);await new Promise(r=>preferences=r);return 'verified';});
 const background=verify();const follow=verify();assert.equal(calls,1);
 bootstrap();await new Promise(r=>setImmediate(r));const duringPreferences=verify();assert.equal(calls,1);
 preferences();assert.deepEqual(await Promise.all([background,follow,duringPreferences]),['verified','verified','verified']);
 const next=verify();assert.equal(calls,2);bootstrap();await new Promise(r=>setImmediate(r));preferences();await next;
});
test('a refused verification can be read again',async()=>{
 let calls=0;const verify=coalescedRead(async()=>{if(++calls===1)throw Error('denied');return 'new read';});
 await assert.rejects(verify(),/denied/);assert.equal(await verify(),'new read');
});
