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
test('a check requested during an older read waits for one fresh read, including after a refusal',async()=>{
 let release,calls=0,account='trader';
 const verify=coalescedRead(async()=>{const read=account;calls++;await new Promise(r=>release=r);if(read==='refused')throw Error('denied');return read;});
 const first=verify();account='editor';
 const afterAction=verify.afterCurrent(),alsoAfterAction=verify.afterCurrent();
 assert.equal(calls,1);release();assert.equal(await first,'trader');
 await new Promise(r=>setImmediate(r));assert.equal(calls,2);release();
 assert.deepEqual(await Promise.all([afterAction,alsoAfterAction]),['editor','editor']);
 account='refused';const refused=verify();account='trader';const retry=verify.afterCurrent();release();
 await assert.rejects(refused,/denied/);await new Promise(r=>setImmediate(r));assert.equal(calls,4);release();assert.equal(await retry,'trader');
});
