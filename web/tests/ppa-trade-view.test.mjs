import test from 'node:test';
import assert from 'node:assert/strict';
import {ppaCutoff} from '../dist/ppa-trade-view.js';
test('PPA inspection retains database microseconds when adapting a common context cutoff',()=>{
 assert.equal(ppaCutoff('2026-10-26T12:34:56.1234560+00:00'),'2026-10-26T12:34:56.123456+00:00');
 assert.equal(ppaCutoff('2026-10-26T12:34:56.123456Z'),'2026-10-26T12:34:56.123456Z');
});
