import test from 'node:test';
import assert from 'node:assert/strict';
import {linkedFrame} from '../dist/close-sessions.js';

const close=(id,replacedBy)=>({id:'frame-'+id,at:'2026-10-16T21:00:00Z',originCloseId:id,close:{session:'london',sessionVersion:1,businessDate:'2026-10-16',replacedBy}});

test('a separate window opens the close it names while that close is in force',()=>{
 assert.equal(linkedFrame([close('a',null),close('b',null)],'b').id,'frame-b');
});
test('a replaced close is history and is not opened for a separate window',()=>{
 assert.equal(linkedFrame([close('a','b'),close('b',null)],'a'),undefined);
});
test('a close the view does not offer names no frame',()=>{
 assert.equal(linkedFrame([close('a',null)],'missing'),undefined);
});
