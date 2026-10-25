import test from 'node:test';
import assert from 'node:assert/strict';
import {latestFrame} from '../dist/context-defaults.js';
test('initial selection uses the latest instant, independent of catalog ordering',()=>{
 assert.equal(latestFrame([{id:'a',at:'2026-01-02T01:00:00Z'},{id:'b',at:'2026-01-03T01:00:00Z'},{id:'c',at:'2026-01-01T01:00:00Z'}]),'b');
 assert.equal(latestFrame([]),'');
});
test('several exact closes at the latest instant require an explicit choice',()=>{assert.equal(latestFrame([{id:'a',at:'2026-01-02T01:00:00Z'},{id:'b',at:'2026-01-02T01:00:00Z'}]),'');});
