import test from 'node:test';
import assert from 'node:assert/strict';
import {latestFrame,frameAt} from '../dist/context-defaults.js';
test('initial selection uses the latest instant, independent of catalog ordering',()=>{
 assert.equal(latestFrame([{id:'a',at:'2026-01-02T01:00:00Z'},{id:'b',at:'2026-01-03T01:00:00Z'},{id:'c',at:'2026-01-01T01:00:00Z'}]),'b');
 assert.equal(latestFrame([]),'');
});
test('several exact closes at the latest instant require an explicit choice',()=>{assert.equal(latestFrame([{id:'a',at:'2026-01-02T01:00:00Z'},{id:'b',at:'2026-01-02T01:00:00Z'}]),'');});
test('a product ticket selects only a unique frame at the desk instant, otherwise it asks for an explicit choice',()=>{
 const frames=[{id:'a',at:'2026-01-02T01:00:00Z'},{id:'b',at:'2026-01-03T01:00:00Z'}];
 assert.equal(frameAt(frames,'2026-01-02T01:00:00+00:00'),'a');
 assert.equal(frameAt(frames,'2026-01-04T01:00:00Z'),'');
 assert.equal(frameAt([...frames,{id:'c',at:frames[0].at}],frames[0].at),'');
 assert.equal(frameAt([{id:'fine',at:'2026-01-02T01:00:00.0000001Z'}],'2026-01-02T01:00:00.0000002Z'),'');
});
