import test from 'node:test';
import assert from 'node:assert/strict';
import {requestDeadlines} from '../dist/request-deadline.js';

test('business requests require server deadlines; startup alone has a bounded guard',()=>{
 const deadlines=requestDeadlines();
 assert.equal(deadlines.milliseconds('bootstrap?includeFrames=false'),20000);
 assert.equal(deadlines.milliseconds('examples'),20000);
 assert.throws(()=>deadlines.milliseconds('trading/blotter'),/deadline metadata/);
});
test('all windows use the server ordinary and extended request budgets',()=>{
 const deadlines=requestDeadlines();deadlines.read({normalMilliseconds:7000,extendedMilliseconds:11000});
 assert.equal(deadlines.milliseconds('workspace/navigation'),7000);
 assert.equal(deadlines.milliseconds('ppa/preview'),11000);
 assert.equal(deadlines.milliseconds('trading/position'),11000);
 assert.equal(deadlines.milliseconds('ppa-other'),7000);
 for(const value of [null,{}, {normalMilliseconds:0,extendedMilliseconds:1},
  {normalMilliseconds:3,extendedMilliseconds:2},{normalMilliseconds:1.1,extendedMilliseconds:2},
  {normalMilliseconds:20001,extendedMilliseconds:25001}])assert.throws(()=>deadlines.read(value),/deadline metadata/);
});
